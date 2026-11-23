-- HD Studio camera sweep: reveals a whole level without playing it.
--
-- 1) Gets into the game (title screen / menus) by pressing Start when the picture is still.
-- 2) Finds the RAM byte that holds the player position: the player runs right and then left, and
--    the byte must go up and then down (that removes counters, pointers and timers).
-- 3) Verifies the candidate by writing it and looking at the picture two frames later. If the game
--    overwrites the value immediately (it was a copy, not the real variable) it is discarded.
-- 4) Teleports the player forward in steps so the game scrolls and loads every column of the level.
--    Every frame the PPU draws is recorded by the HD pack builder.
-- 5) If no usable address is found, it falls back to running right (the level runner).
--
-- Nothing here is game specific: the address is found by observation, so it works on any NES game.

local SAMPLE_STEP = 61          -- sample 1 pixel out of 61 (~1000 pixels) for the screen hash
local STILL_CHECKS = 6          -- 6 checks x 10 frames = 1 second without movement
local STILL_THRESHOLD = 0.05
local MAX_CANDIDATES = 16

local frame = 0
local lastEmuFrame = -1
local state = "menu"
local stateFrames = 0

local previousSample = nil
local stillChecks = 0
local tapButton = nil
local tapFramesLeft = 0
local menuCooldown = 0
local useStartNext = true

local snapA, snapB, snapC
local candidates = {}
local candidateIndex = 1
local testDeltas = { 1, 2, 4, 8, 16, -1, -2, -4, -8, -16 }
local testDeltaIndex = 1
local baseValue = 0
local bestAddress = nil
local test16Offsets = { -1, 0 }
local test16Index = 1
local test16Base = 0
local bestAddress16 = nil
local sweep16Base = 0
local sweep16Value = 0
local sweepBase = 0
local sweepDirection = 1
local sweepValue = 0
local lastHash = 0
local unchangedSteps = 0
local distinctScreens = 0
local triedSweeps = 0

local function log(msg)
  emu.log("sweep: " .. msg)
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
  emu.write(addr, value, emu.memType.nesInternalRam)
end

local function readRam16(addr)
  return readRam(addr) | (readRam(addr + 1) << 8)
end

local function writeRam16(addr, value)
  emu.write16(addr, value % 65536, emu.memType.nesInternalRam)
end

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

local function resetState()
  state = "menu"
  stateFrames = 0
  snapA, snapB, snapC = nil, nil, nil
  candidates = {}
  candidateIndex = 1
  baseValue = 0
  bestAddress = nil
  sweepBase = 0
  sweepDirection = 1
  sweepValue = 0
  lastHash = 0
  unchangedSteps = 0
  distinctScreens = 0
  triedSweeps = 0
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

-- The player went right (A->B) and then left (B->C): keep the bytes that followed that movement
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

  if #candidates > MAX_CANDIDATES then
    for i = #candidates, MAX_CANDIDATES + 1, -1 do
      table.remove(candidates, i)
    end
  end

  local parts = {}
  for i, c in ipairs(candidates) do
    parts[#parts + 1] = string.format("$%04X(%d)", c.addr, c.score)
    if i >= 8 then
      break
    end
  end
  log(string.format("%d candidates: %s", #candidates, table.concat(parts, " ")))
end

local function beginTest()
  if candidateIndex > #candidates then
    log("no usable address, falling back to running right")
    state = "fallback"
    stateFrames = 0
    return
  end

  local addr = candidates[candidateIndex].addr
  baseValue = snapB[addr]
  lastHash = screenHash()
  testDeltaIndex = 1
  log(string.format("trying $%04X (value %d)", addr, baseValue))
  state = "test"
  stateFrames = 0
end

local function acceptSweep(direction)
  bestAddress = candidates[candidateIndex].addr
  sweepBase = baseValue
  sweepDirection = direction
  sweepValue = 0
  distinctScreens = 0
  unchangedSteps = 0
  lastHash = screenHash()
  log(string.format("$%04X works (direction %+d): looking for a 16-bit position", bestAddress, direction))

  -- A 16-bit player position ($xx low byte, $xx+1 high byte) needs to be moved in one piece:
  -- moving only the low byte wraps every 256 pixels and the level stops scrolling.
  test16Offsets = { -1, 0 }
  test16Index = 1
  state = "test16"
  stateFrames = 0
end

local function pushMenuTap()
  menuCooldown = 40
  stillChecks = 0
  tapButton = useStartNext and "start" or "a"
  useStartNext = not useStartNext
  tapFramesLeft = 6
end

local function onInputPolled()
  frame = frame + 1

  -- A reset (the level sweep does one per level) restarts the whole search
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

  if state == "menu" then
    if stillChecks >= STILL_CHECKS and menuCooldown == 0 then
      pushMenuTap()
    else
      emu.setInput({ right = true }, 0)
    end
    if stateFrames > 900 then
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
      candidateIndex = 1
      beginTest()
    end

  elseif state == "test" then
    -- Try small moves in both directions: a big jump can be clamped by the game (level bounds)
    -- or overflow a 16-bit coordinate, and then the picture never changes.
    local addr = candidates[candidateIndex].addr
    local delta = testDeltas[testDeltaIndex]
    if delta == nil then
      log(string.format("$%04X discarded", addr))
      writeRam(addr, baseValue)
      candidateIndex = candidateIndex + 1
      testDeltaIndex = 1
      beginTest()
    elseif stateFrames == 1 then
      writeRam(addr, (baseValue + delta) % 256)
      emu.setInput({}, 0)
    elseif stateFrames == 3 then
      if screenHash() ~= lastHash then
        acceptSweep(delta > 0 and 1 or -1)
      else
        writeRam(addr, baseValue)
        testDeltaIndex = testDeltaIndex + 1
        stateFrames = 0
      end
    else
      emu.setInput({}, 0)
    end

  elseif state == "test16" then
    local a = bestAddress + test16Offsets[test16Index]
    if stateFrames == 1 then
      test16Base = readRam16(a)
      writeRam16(a, test16Base + 768)
      emu.setInput({}, 0)
    elseif stateFrames == 4 then
      if screenHash() ~= lastHash then
        log(string.format("$%04X is a working 16-bit position (base %d): sweeping", a, test16Base))
        bestAddress16 = a
        sweep16Base = test16Base
        sweep16Value = 0
        distinctScreens = 0
        unchangedSteps = 0
        lastHash = screenHash()
        state = "sweep16"
        stateFrames = 0
      else
        writeRam16(a, test16Base)
        test16Index = test16Index + 1
        if test16Index > #test16Offsets then
          log("no 16-bit position, sweeping the single byte")
          state = "sweep"
          stateFrames = 0
        else
          stateFrames = 0
        end
      end
    else
      emu.setInput({}, 0)
    end

  elseif state == "sweep16" then
    -- One step is 96 pixels: the game loads the new screen and the PPU draws it
    if stateFrames % 3 == 1 then
      sweep16Value = sweep16Value + 96
      if sweep16Value > 12000 then
        log(string.format("16-bit sweep finished: %d screens", distinctScreens))
        state = "done"
        stateFrames = 0
      else
        writeRam16(bestAddress16, sweep16Base + sweep16Value)
        local h = screenHash()
        if h ~= lastHash then
          distinctScreens = distinctScreens + 1
          unchangedSteps = 0
          lastHash = h
        else
          unchangedSteps = unchangedSteps + 1
          if unchangedSteps > 25 then
            log(string.format("the level stops at %d pixels (%d screens)", sweep16Value, distinctScreens))
            state = "done"
            stateFrames = 0
          end
        end
      end
    end
    emu.setInput({ right = true }, 0)

  elseif state == "sweep" then
    if stateFrames % 3 == 1 then
      sweepValue = sweepValue + 4
      if sweepValue > 1000 then
        log(string.format("sweep finished: %d different screens", distinctScreens))
        if distinctScreens < 4 and triedSweeps < 3 then
          triedSweeps = triedSweeps + 1
          candidateIndex = candidateIndex + 1
          beginTest()
        else
          state = "done"
          stateFrames = 0
        end
      else
        writeRam(bestAddress, (sweepBase + sweepDirection * sweepValue) % 256)
        local h = screenHash()
        if h ~= lastHash then
          distinctScreens = distinctScreens + 1
          unchangedSteps = 0
          lastHash = h
        else
          unchangedSteps = unchangedSteps + 1
          if unchangedSteps > 30 then
            log(string.format("no more scrolling after %d steps (%d screens)", sweepValue, distinctScreens))
            if distinctScreens < 4 and triedSweeps < 3 then
              triedSweeps = triedSweeps + 1
              candidateIndex = candidateIndex + 1
              beginTest()
            else
              state = "done"
              stateFrames = 0
            end
          end
        end
      end
    end
    emu.setInput({ right = true }, 0)

  elseif state == "done" then
    if stillChecks >= STILL_CHECKS and menuCooldown == 0 then
      pushMenuTap()
    else
      emu.setInput({ right = true }, 0)
    end

  else -- fallback
    if stillChecks >= STILL_CHECKS and menuCooldown == 0 then
      pushMenuTap()
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
