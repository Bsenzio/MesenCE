-- HD Studio camera sweep: reveals a whole level without playing it.
--
-- How it works (nothing here is game specific):
--  1) Gets into the game (title screen / menus) pressing Start when the picture is still.
--  2) Finds the RAM bytes that hold the player position: the player runs right and then left, and
--     only the bytes that follow both movements are kept (that removes counters, timers, pointers).
--  3) Scores every candidate: it moves the value a few steps and counts how many different screens
--     appear. Copies of the position (OAM shadows, velocities...) score 0; the real position or the
--     camera variable scores high. Both 8-bit and 16-bit (little endian) positions are tried.
--  4) Walks the best one through the whole level, so the game scrolls and the PPU draws every column.
--     Every drawn frame is recorded by the HD pack builder.
--  5) If nothing works, it falls back to running right (the level runner).

local SAMPLE_STEP = 61          -- sample 1 pixel out of 61 (~1000 pixels) for the screen hash
local STILL_CHECKS = 6
local STILL_THRESHOLD = 0.05
local MAX_CANDIDATES = 5
local MINI_STEPS = 10
local MINI_FRAMES = 2
local MAX_SCAN_CANDIDATES = 12

local frame = 0
local lastEmuFrame = -1
local state = "menu"
local stateFrames = 0

local previousSample = nil
local stillChecks = 0
local movingChecks = 0
local tapButton = nil
local tapFramesLeft = 0
local menuCooldown = 0
local useStartNext = true

local snapA, snapB, snapC
local candidates = {}
local candidateIndex = 1

local variants = {}             -- { mode = "byte"|"word", addr = n, base = v, direction = +1|-1 }
local variantIndex = 1
local bestVariant = nil
local bestScore = 0
local miniStep = 0
local miniDistinct = 0
local miniBands = nil
local miniScroll = 0

local fullStep = 0
local fullDistinct = 0
local fullScroll = 0
local fullUnchanged = 0

local function log(msg)
  emu.log("sweep: " .. msg)
end

-- Horizontal scroll of the PPU (0-511). Writing the real camera/player variable changes it;
-- writing a copy of the position does not, no matter what the picture does.
local hasScroll = type(emu.getScroll) == "function"
local function getScroll()
  if hasScroll then
    return emu.getScroll()
  end
  return 0
end

local function screenHash()
  local buffer = emu.getScreenBuffer()
  local h = 0
  for i = 1, #buffer, SAMPLE_STEP do
    h = (h * 33 + buffer[i]) % 2147483647
  end
  return h
end

local function readRam(addr)
  return emu.read(addr, emu.memType.nesInternalRam)
end

local function writeRam(addr, value)
  emu.write(addr, value % 256, emu.memType.nesInternalRam)
end

local function readValue(v)
  if v.mode == "word" then
    return readRam(v.addr) | (readRam(v.addr + 1) << 8)
  end
  return readRam(v.addr)
end

local function writeValue(v, value)
  if v.mode == "word" then
    emu.write16(v.addr, value % 65536, emu.memType.nesInternalRam)
  else
    writeRam(v.addr, value % 256)
  end
end

local function variantStride(v)
  return v.mode == "word" and 192 or 8
end

local function variantLimit(v)
  return v.mode == "word" and 64 or 120
end

local function takeSample()
  local buffer = emu.getScreenBuffer()
  local sample = {}
  for i = 1, #buffer, SAMPLE_STEP do
    sample[#sample + 1] = buffer[i]
  end
  return sample
end

-- The screen buffer is 256x240 (the NES resolution): three rows are enough to measure a
-- horizontal scroll. A real scroll keeps the same pixels, shifted; a glitch does not.
local SCREEN_WIDTH = 256
local BAND_ROWS = { 100, 120, 140 }

local function readBands()
  local buffer = emu.getScreenBuffer()
  local bands = {}
  for i, y in ipairs(BAND_ROWS) do
    local row = {}
    local offset = (y - 1) * SCREEN_WIDTH
    for x = 1, SCREEN_WIDTH do
      row[x] = buffer[offset + x]
    end
    bands[i] = row
  end
  return bands
end

-- Returns the horizontal shift (in pixels) that best aligns two frames and how well they match
local function detectScroll(previous, current)
  local bestShift, bestMatch, total = 0, 0, 0
  for shift = -12, 12 do
    local match, compared = 0, 0
    for _, row in ipairs(BAND_ROWS) do
      local prevRow = previous[_]
      local nowRow = current[_]
      for x = 1, SCREEN_WIDTH do
        local other = x - shift
        if other >= 1 and other <= SCREEN_WIDTH then
          compared = compared + 1
          if nowRow[x] == prevRow[other] then
            match = match + 1
          end
        end
      end
    end
    if compared > 0 then
      local ratio = match / compared
      if ratio > bestMatch then
        bestMatch = ratio
        bestShift = shift
      end
    end
    total = total + 1
  end
  return bestShift, bestMatch
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
      movingChecks = math.max(0, movingChecks - 1)
    else
      stillChecks = 0
      movingChecks = movingChecks + 1
    end
  end
  previousSample = sample
end

local function resetState()
  state = "menu"
  stateFrames = 0
  snapA, snapB, snapC = nil, nil, nil
  candidates = {}
  candidateIndex = 1
  variants = {}
  variantIndex = 1
  bestVariant = nil
  bestScore = 0
  stillChecks = 0
  previousSample = nil
  previousOam = nil
end

local function snapshot()
  local t = {}
  for addr = 0, 0x7FF do
    t[addr] = readRam(addr)
  end
  return t
end

local function findCandidates()
  candidates = {}
  for addr = 0, 0x7FF do
    local a, b, c = snapA[addr], snapB[addr], snapC[addr]
    local right = b - a
    local left = b - c
    if right >= 2 and left >= 2 and math.min(right, left) <= 120 then
      candidates[#candidates + 1] = { addr = addr, score = math.min(right, left) }
    end
  end

  table.sort(candidates, function(x, y)
    if x.score ~= y.score then
      return x.score > y.score
    end
    return x.addr > y.addr
  end)

  if #candidates > MAX_SCAN_CANDIDATES then
    for i = #candidates, MAX_SCAN_CANDIDATES + 1, -1 do
      table.remove(candidates, i)
    end
  end

  local parts = {}
  for i, c in ipairs(candidates) do
    parts[#parts + 1] = string.format("$%04X(%d)", c.addr, c.score)
    if i >= 6 then
      break
    end
  end
  log(string.format("%d candidates: %s", #candidates, table.concat(parts, " ")))
end

-- Build the variants to score: the byte itself and the 16-bit value it belongs to, both directions
local function buildVariants()
  variants = {}
  for i = 1, math.min(#candidates, MAX_CANDIDATES) do
    local addr = candidates[i].addr
    for _, direction in ipairs({ 1, -1 }) do
      variants[#variants + 1] = { mode = "byte", addr = addr, direction = direction }
      if addr >= 1 then
        variants[#variants + 1] = { mode = "word", addr = addr - 1, direction = direction }
      end
      variants[#variants + 1] = { mode = "word", addr = addr, direction = direction }
    end
  end
  variantIndex = 1
  miniStep = 0
  miniDistinct = 0
  miniBands = nil
end

local function describe(v)
  return string.format("%s $%04X%+d", v.mode, v.addr, v.direction)
end

-- Scores the current variant: moves it MINI_STEPS times and counts how many times the PPU
-- horizontal scroll really changed (that is the camera moving, not a glitch)
local function scoreVariant()
  local v = variants[variantIndex]
  if miniStep == 0 then
    v.base = readValue(v)
    miniDistinct = 0
    miniScroll = getScroll()
    miniBands = readBands()
    log(string.format("scoring %s (base %d, scroll %d)", describe(v), v.base, miniScroll))
  end

  local scroll = getScroll()
  if miniStep > 0 and scroll ~= miniScroll then
    miniDistinct = miniDistinct + 1
  end
  local bands = readBands()
  local shift, match = detectScroll(miniBands, bands)
  if match >= 0.5 then
    miniBands = bands
  end
  miniScroll = scroll

  miniStep = miniStep + 1
  writeValue(v, v.base + v.direction * miniStep * variantStride(v))

  if miniStep >= MINI_STEPS then
    writeValue(v, v.base)
    local penalty = (v.mode == "word" and v.base > 16384) and 100 or 0
    log(string.format("  %s -> %d scroll changes (last shift %d, match %.2f)", describe(v), miniDistinct, shift, match))
    if miniDistinct > bestScore and penalty == 0 then
      bestScore = miniDistinct
      bestVariant = { mode = v.mode, addr = v.addr, direction = v.direction, base = v.base }
    end
    variantIndex = variantIndex + 1
    miniStep = 0
    if variantIndex > #variants then
      if bestVariant == nil or bestScore < 2 then
        log("no address scrolls the picture, falling back to running right")
        state = "fallback"
        stateFrames = 0
      else
        -- The game may have moved the value while the other candidates were scored: take it again
        bestVariant.base = readValue(bestVariant)
        log(string.format("best: %s (%d screens, base now %d), sweeping the level",
          describe(bestVariant), bestScore, bestVariant.base))
        fullStep = 0
        fullDistinct = 0
        fullUnchanged = 0
        fullScroll = getScroll()
        state = "full"
        stateFrames = 0
      end
    end
  end
end

-- Menus: move the cursor to the top option and press Start (the runner does the same: some games
-- show a password screen if the cursor is left on the second option)
local queue = {}
local function push(buttons, frames)
  queue[#queue + 1] = { b = buttons, f = frames }
end

local function pushMenuActions()
  menuCooldown = 60
  stillChecks = 0
  push({ up = true }, 5)
  push({}, 8)
  push({ up = true }, 5)
  push({}, 8)
  push({ start = true }, 6)
  push({}, 40)
end

local function onInputPolled()
  frame = frame + 1

  local emuFrame = emu.getState().frameCount or 0
  if emuFrame < lastEmuFrame then
    resetState()
  end
  lastEmuFrame = emuFrame

  if frame % 10 == 0 then
    updateStillness()
  end

  if menuCooldown > 0 then
    menuCooldown = menuCooldown - 1
  end

  if tapFramesLeft > 0 then
    tapFramesLeft = tapFramesLeft - 1
    return emu.setInput({ [tapButton] = true }, 0)
  end

  stateFrames = stateFrames + 1

  if #queue > 0 then
    local step = queue[1]
    step.f = step.f - 1
    if step.f <= 0 then
      table.remove(queue, 1)
    end
    return emu.setInput(step.b, 0)
  end

  if state == "menu" then
    if stillChecks >= STILL_CHECKS and menuCooldown == 0 then
      pushMenuActions()
    else
      emu.setInput({ right = true }, 0)
    end
    -- Start looking for the position only once the game is clearly moving (gameplay, not menus)
    if movingChecks >= 6 or stateFrames > 1800 then
      log(string.format("gameplay detected after %d frames (moving=%d)", stateFrames, movingChecks))
      snapA = snapshot()
      state = "probeRight"
      stateFrames = 0
    end

  elseif state == "probeRight" then
    emu.setInput({ right = true }, 0)
    if stateFrames >= 25 then
      snapB = snapshot()
      state = "probeLeft"
      stateFrames = 0
    end

  elseif state == "probeLeft" then
    emu.setInput({ left = true }, 0)
    if stateFrames >= 25 then
      snapC = snapshot()
      findCandidates()
      buildVariants()
      state = "score"
      stateFrames = 0
    end

  elseif state == "score" then
    -- One write every MINI_FRAMES frames, so the game has time to react
    if stateFrames % MINI_FRAMES == 1 then
      scoreVariant()
    end
    emu.setInput({}, 0)

  elseif state == "full" then
    local v = bestVariant
    if stateFrames % 3 == 1 then
      local limit = variantLimit(v)
      if fullStep >= limit then
        log(string.format("sweep finished: %d scroll changes", fullDistinct))
        state = "done"
        stateFrames = 0
      else
        local scroll = getScroll()
        if fullStep > 0 and scroll ~= fullScroll then
          fullDistinct = fullDistinct + 1
          fullUnchanged = 0
        else
          fullUnchanged = fullUnchanged + 1
        end
        fullScroll = scroll

        fullStep = fullStep + 1
        writeValue(v, v.base + v.direction * fullStep * variantStride(v))
        if fullUnchanged > 12 then
          log(string.format("no more scrolling at step %d (%d scroll changes)", fullStep, fullDistinct))
          state = "done"
          stateFrames = 0
        end
      end
    end
    emu.setInput({ right = true }, 0)

  elseif state == "done" then
    if stillChecks >= STILL_CHECKS and menuCooldown == 0 then
      pushMenuActions()
    else
      emu.setInput({ right = true }, 0)
    end

  else -- fallback
    if stillChecks >= STILL_CHECKS and menuCooldown == 0 then
      pushMenuActions()
    else
      local input = { right = true, b = true }
      if frame % 75 < 13 then
        input.a = true
      end
      emu.setInput(input, 0)
    end
  end
end

emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)
