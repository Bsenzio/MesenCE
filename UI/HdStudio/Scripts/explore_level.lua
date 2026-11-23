-- HD Studio level runner: used by --levels to see as much as possible of each level.
-- It runs to the right, shoots and jumps; when the picture stops moving (title screen, menu,
-- death/continue, pause) it navigates the menus: cursor to the top option, then Start.
-- The game is reset before every level, so the script keeps no state that depends on the game.

local SAMPLE_STEP = 61          -- sample 1 pixel out of 61 (~1000 pixels)
local CHECK_INTERVAL = 10       -- frames between two samples
local STILL_THRESHOLD = 0.05    -- less than 5% of the sampled pixels changed = "still"
local STILL_CHECKS_NEEDED = 6   -- 6 checks x 10 frames = 1 second without movement

local frame = 0
local previousSample = nil
local stillChecks = 0
local queue = {}                -- pending {b = buttons, f = frames} steps
local waitFrames = 0
local menuStep = 0

local function takeSample()
  local buffer = emu.getScreenBuffer()
  local sample = {}
  for i = 1, #buffer, SAMPLE_STEP do
    sample[#sample + 1] = buffer[i]
  end
  return sample
end

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
    if not moved and changed / #sample < STILL_THRESHOLD then
      stillChecks = stillChecks + 1
    else
      stillChecks = 0
    end
  end
  previousSample = sample
end

local function push(buttons, frames)
  queue[#queue + 1] = { b = buttons, f = frames }
end

-- Title screen / menu / continue screen. Most NES games start with the cursor on the first
-- option, so moving up and pressing Start chooses "START GAME" instead of "PASSWORD"/"OPTIONS".
local function pushMenuActions()
  menuStep = menuStep + 1
  local phase = (menuStep - 1) % 3
  if phase == 0 then
    push({ up = true }, 5)
    push({}, 10)
    push({ up = true }, 5)
    push({}, 10)
    push({ start = true }, 6)
  elseif phase == 1 then
    push({ start = true }, 6)
  else
    push({ a = true }, 6)
    push({}, 12)
    push({ start = true }, 6)
  end
  push({}, 45)
end

local function onInputPolled()
  frame = frame + 1

  if frame % CHECK_INTERVAL == 0 then
    updateStillness()
  end

  -- Run the pending menu actions first
  if #queue > 0 then
    local step = queue[1]
    step.f = step.f - 1
    if step.f <= 0 then
      table.remove(queue, 1)
    end
    return emu.setInput(step.b, 0)
  end

  if waitFrames > 0 then
    waitFrames = waitFrames - 1
    return emu.setInput({ right = true }, 0)
  end

  if stillChecks >= STILL_CHECKS_NEEDED then
    stillChecks = 0
    pushMenuActions()
    return
  end

  -- Gameplay: run right and shoot. The cycle backs up every few seconds and then takes a
  -- running jump, which is how a blocked character gets over walls and gaps.
  local input = { right = true, b = true }
  local cycle = frame % 360
  if cycle >= 250 and cycle < 300 then
    input.right = false
    input.left = true
  elseif cycle >= 300 and cycle < 320 then
    input.a = true
  elseif frame % 75 < 13 then
    input.a = true
  end

  -- Every few seconds look up/down, which is how most games enter doors and elevators
  local scenery = frame % 420
  if scenery < 10 then
    input.up = true
  elseif scenery >= 10 and scenery < 20 then
    input.down = true
  end

  emu.setInput(input, 0)
end

emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)
