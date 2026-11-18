-- HD Studio deep explorer (used by --generate): explores the whole game automatically with savestates,
-- without knowing anything about the game, so the HD pack capture sees as many graphics as possible.
--
-- Idea ("Go-Explore"): every time something new appears on screen, the current state is saved as a "cell".
-- Exploration runs in short episodes of random play; at the end of each episode the explorer goes back to
-- one of the saved cells (preferring the least visited and the most productive ones) and tries again from there.
-- Dying or getting stuck does not matter: the next episode starts from somewhere else.
--
-- Something is "new" when the game shows:
--  * a palette never seen before (usually a new level, area or cutscene)
--  * a sprite tile + palette never seen before (a new enemy, item, boss, animation...)
--  * a screen layout never seen before (a new room or background, from a coarse 4x4 picture of the screen)
--
-- Savestates can only be created/loaded inside an "exec" callback, so the explorer hooks the game's NMI
-- routine (address read from the $FFFA vector), which runs once per frame.
-- All random choices are seeded, so two runs on the same game explore exactly the same way.

local SEED = 20261118
local EPISODE_FRAMES = 300       -- random play before going back to a saved cell (5 seconds)
local CHECK_INTERVAL = 10        -- frames between two looks at the screen
local STILL_THRESHOLD = 0.05
local STILL_CHECKS_NEEDED = 12   -- nothing moved for 2 seconds: press Start/A
local STUCK_CHECKS = 60          -- nothing moved for 10 seconds even after pressing buttons: end the episode
local TAP_FRAMES = 5
local COOLDOWN_FRAMES = 90
local MAX_CELLS = 400

local nesDebug = emu.memType.nesDebug

-- Exploration state
local cells = {}          -- array of { key, state, visits, discoveries }
local knownKeys = {}      -- key -> true
local episode = 0
local episodeFrames = 0
local currentCell = nil
local pendingSave = nil   -- key of a cell to save at the next NMI
local pendingLoad = nil   -- cell to restore at the next NMI
local totalFrames = 0

-- Input state
local previousSample = nil
local previousOam = nil
local stillChecks = 0
local tapButton = nil
local tapFramesLeft = 0
local cooldown = 0
local useStartNext = true
local action = {}
local actionFramesLeft = 0

local function rnd(salt)
  math.randomseed(SEED + episode * 100003 + episodeFrames * 31 + (salt or 0))
  return math.random()
end

-- Novelty detection -----------------------------------------------------------------------------

local function paletteKey()
  local parts = {}
  for i = 0, 31 do
    parts[#parts + 1] = string.format("%02X", emu.read(i, emu.memType.nesPaletteRam))
  end
  return "P" .. table.concat(parts)
end

local function newSpriteKey()
  -- First sprite (tile + palette) of this frame that was never seen before, if any
  for i = 0, 63 do
    local y = emu.read(i * 4, emu.memType.nesSpriteRam)
    if y < 0xEF then
      local tile = emu.read(i * 4 + 1, emu.memType.nesSpriteRam)
      local attr = emu.read(i * 4 + 2, emu.memType.nesSpriteRam)
      local key = string.format("S%02X%d", tile, attr & 0x03)
      if not knownKeys[key] then
        return key
      end
    end
  end
  return nil
end

local function layoutKey(buffer)
  -- Coarse picture of the screen: 3x3 blocks, average brightness of 16 samples per block, 3 levels
  -- (coarse enough that scrolling inside the same area does not look like a new place)
  local width = 256
  local parts = {}
  for by = 0, 2 do
    for bx = 0, 2 do
      local sum = 0
      for sy = 0, 3 do
        for sx = 0, 3 do
          local x = bx * 85 + 8 + sx * 20
          local y = by * 80 + 8 + sy * 18
          local c = buffer[y * width + x + 1] or 0
          sum = sum + ((c >> 16) & 0xFF) + ((c >> 8) & 0xFF) + (c & 0xFF)
        end
      end
      parts[#parts + 1] = math.floor(sum / (16 * 3 * 86))
    end
  end
  return "L" .. table.concat(parts)
end

-- How interesting a discovery is: a new palette usually means a new level/area
local KEY_WEIGHT = { P = 4, S = 2, L = 1 }

local function checkNovelty(buffer)
  local key = paletteKey()
  if knownKeys[key] then
    key = newSpriteKey()
  end
  if not key then
    key = layoutKey(buffer)
    if knownKeys[key] then
      key = nil
    end
  end
  if key then
    knownKeys[key] = true
    if currentCell then
      currentCell.discoveries = currentCell.discoveries + KEY_WEIGHT[key:sub(1, 1)]
    end
    if #cells < MAX_CELLS then
      pendingSave = key
    end
  end
end

-- Stillness (menus, pause, "continue" screens) -----------------------------------------------------

local function spritesMoved()
  local oam = {}
  local moved = false
  for i = 0, 63 do
    local v = emu.read(i * 4, emu.memType.nesSpriteRam) * 256 + emu.read(i * 4 + 3, emu.memType.nesSpriteRam)
    oam[i] = v
    if previousOam and previousOam[i] ~= v then
      moved = true
    end
  end
  previousOam = oam
  return moved
end

local function look()
  local buffer = emu.getScreenBuffer()
  local sample = {}
  for i = 1, #buffer, 61 do
    sample[#sample + 1] = buffer[i]
  end
  local moved = spritesMoved()
  if previousSample and #previousSample == #sample then
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
  checkNovelty(buffer)
end

-- Choosing where to continue from ----------------------------------------------------------------

local function chooseCell()
  if #cells == 0 then
    return nil
  end
  -- Weight: rarely visited cells and cells that led to discoveries are preferred; recent cells get a bonus
  local total = 0
  local weights = {}
  for i, cell in ipairs(cells) do
    local w = (1 + cell.discoveries) / math.sqrt(1 + cell.visits)
    w = w * (1 + i / #cells)
    weights[i] = w
    total = total + w
  end
  local r = rnd(7) * total
  for i, w in ipairs(weights) do
    r = r - w
    if r <= 0 then
      return cells[i]
    end
  end
  return cells[#cells]
end

local function countKeys()
  local n = 0
  for _ in pairs(knownKeys) do n = n + 1 end
  return n
end

local function endEpisode()
  local cell = chooseCell()
  episode = episode + 1
  if episode % 10 == 0 then
    emu.log(string.format("episode %d: %d saved cells, %d discoveries", episode, #cells, countKeys()))
  end
  episodeFrames = 0
  stillChecks = 0
  tapFramesLeft = 0
  cooldown = 0
  actionFramesLeft = 0
  previousSample = nil
  previousOam = nil
  if cell then
    cell.visits = cell.visits + 1
    currentCell = cell
    pendingLoad = cell
  end
end

-- Hooks ------------------------------------------------------------------------------------------

local function onNmi()
  if pendingSave then
    cells[#cells + 1] = { key = pendingSave, state = emu.createSavestate(), visits = 0, discoveries = KEY_WEIGHT[pendingSave:sub(1, 1)] }
    pendingSave = nil
  end
  if pendingLoad then
    local cell = pendingLoad
    pendingLoad = nil
    emu.loadSavestate(cell.state)
  end
end

local function pickAction()
  local r = rnd(1)
  local a = {}
  if r < 0.50 then
    a.right = true
  elseif r < 0.70 then
    a.left = true
  elseif r < 0.80 then
    a.up = true
  elseif r < 0.88 then
    a.down = true
  end
  if rnd(2) < 0.45 then a.a = true end
  if rnd(3) < 0.45 then a.b = true end
  action = a
  actionFramesLeft = 12 + math.floor(rnd(4) * 34)
end

local function onInputPolled()
  totalFrames = totalFrames + 1
  episodeFrames = episodeFrames + 1
  local input = { a = false, b = false, select = false, start = false, up = false, down = false, left = false, right = false }

  if episodeFrames % CHECK_INTERVAL == 0 then
    look()
  end
  if cooldown > 0 then
    cooldown = cooldown - 1
  end

  if (episodeFrames >= EPISODE_FRAMES and #cells > 0) or stillChecks >= STUCK_CHECKS then
    endEpisode()
  elseif tapFramesLeft > 0 then
    input[tapButton] = true
    tapFramesLeft = tapFramesLeft - 1
  elseif stillChecks >= STILL_CHECKS_NEEDED and cooldown == 0 then
    tapButton = useStartNext and "start" or "a"
    useStartNext = not useStartNext
    tapFramesLeft = TAP_FRAMES
    cooldown = COOLDOWN_FRAMES
    input[tapButton] = true
  else
    if actionFramesLeft <= 0 then
      pickAction()
    end
    actionFramesLeft = actionFramesLeft - 1
    for k, v in pairs(action) do
      input[k] = v
    end
    if episodeFrames % 8 >= 6 then
      input.a = false
      input.b = false
    end
  end

  emu.setInput(input, 0)
end

local nmiVector = emu.read(0xFFFA, nesDebug) | (emu.read(0xFFFB, nesDebug) << 8)
emu.addMemoryCallback(onNmi, emu.callbackType.exec, nmiVector)
emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)
emu.log(string.format("HD Studio deep explorer: NMI handler at $%04X", nmiVector))
