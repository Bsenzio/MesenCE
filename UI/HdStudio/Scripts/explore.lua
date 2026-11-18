-- HD Studio explorer: plays the game with semi-random inputs so the HD pack capture
-- sees gameplay graphics (not only the title screen) when no movie (.mmo/.bk2) is available.
--
-- Usage: hdstudio.cmd game.nes UI\HdStudio\Scripts\explore.lua --frames=18000
--
-- How it decides what to do:
--  * every 10 frames it checks whether the sprites moved and how much the picture changed (~1000 sampled pixels)
--  * when nothing moves for 2 seconds (title screen, menu, pause, "continue" screen)
--    it taps Start or A (alternating) to move on; a pause caused by mistake is undone the same way
--  * otherwise it plays: mostly walks right, jumps and attacks in bursts, sometimes goes left/up/down
-- The random choices are seeded with the frame number, so every run plays exactly the same way.

local SEED = 20261118

local SAMPLE_STEP = 61          -- sample 1 pixel out of 61 (~1000 pixels)
local CHECK_INTERVAL = 10       -- frames between two samples
local STILL_THRESHOLD = 0.05    -- less than 5% of the sampled pixels changed (and no sprite moved) = "still"
local STILL_CHECKS_NEEDED = 12  -- 12 checks x 10 frames = 2 seconds without movement
local TAP_FRAMES = 5
local COOLDOWN_FRAMES = 90      -- wait after a tap before deciding again

-- Use the emulator's own frame counter: the script may be attached a few frames after the game started,
-- and all decisions must happen on the same game frames in every run (capture and --verify must match)
local frame = emu.getState().frameCount or 0
local previousSample = nil
local stillChecks = 0
local tapButton = nil
local tapFramesLeft = 0
local cooldown = 0
local useStartNext = true

local action = {}
local actionFramesLeft = 0

local function takeSample()
  local buffer = emu.getScreenBuffer()
  local sample = {}
  local n = 0
  for i = 1, #buffer, SAMPLE_STEP do
    n = n + 1
    sample[n] = buffer[i]
  end
  return sample
end

-- Sprite positions (X/Y of the 64 OAM entries): a paused game keeps them fixed,
-- even when it animates the colors of a "PAUSE" sign
local previousOam = nil
local function spritesMoved()
  local oam = {}
  local moved = false
  for i = 0, 63 do
    local y = emu.read(i * 4, emu.memType.nesSpriteRam)
    local x = emu.read(i * 4 + 3, emu.memType.nesSpriteRam)
    oam[i] = y * 256 + x
    if previousOam and previousOam[i] ~= oam[i] then
      moved = true
    end
  end
  previousOam = oam
  return moved
end

local function updateStillness()
  local sample = takeSample()
  local moved = spritesMoved()
  if previousSample then
    local changed = 0
    for i = 1, #sample do
      if sample[i] ~= previousSample[i] then
        changed = changed + 1
      end
    end
    -- "still": sprites did not move and the picture barely changed (no scrolling)
    if not moved and changed / #sample < STILL_THRESHOLD then
      stillChecks = stillChecks + 1
    else
      stillChecks = 0
    end
  end
  previousSample = sample
end

local function pickAction()
  math.randomseed(SEED + frame)
  local r = math.random()
  local a = {}
  if r < 0.55 then
    a.right = true
  elseif r < 0.70 then
    a.left = true
  elseif r < 0.78 then
    a.up = true
  elseif r < 0.86 then
    a.down = true
  end
  if math.random() < 0.45 then a.a = true end
  if math.random() < 0.45 then a.b = true end
  action = a
  actionFramesLeft = math.random(12, 45)
end

local function onInputPolled()
  frame = frame + 1
  local input = { a = false, b = false, select = false, start = false, up = false, down = false, left = false, right = false }

  if frame % CHECK_INTERVAL == 0 then
    updateStillness()
  end

  if cooldown > 0 then
    cooldown = cooldown - 1
  end

  if tapFramesLeft > 0 then
    -- Finishing a Start/A tap
    input[tapButton] = true
    tapFramesLeft = tapFramesLeft - 1
  elseif stillChecks >= STILL_CHECKS_NEEDED and cooldown == 0 then
    -- The picture is not moving: menu, pause or "continue" screen
    tapButton = useStartNext and "start" or "a"
    useStartNext = not useStartNext
    tapFramesLeft = TAP_FRAMES
    cooldown = COOLDOWN_FRAMES
    stillChecks = 0
    input[tapButton] = true
  else
    -- Gameplay
    if actionFramesLeft <= 0 then
      pickAction()
    end
    actionFramesLeft = actionFramesLeft - 1
    for k, v in pairs(action) do
      input[k] = v
    end
    -- release jump/attack buttons regularly so they can be pressed again
    if frame % 8 >= 6 then
      input.a = false
      input.b = false
    end
  end

  emu.setInput(input, 0)
end

emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)
