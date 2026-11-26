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
-- Memory mutation (learned): random play rarely finishes a level, so at the start of some episodes the explorer
-- changes 1 to 3 random bytes of the game's RAM (level number, lives, position, timers...). When a discovery
-- follows a mutation, the mutated addresses earn credit (a lot for a new palette, i.e. a new level/area), and
-- addresses with credit are chosen more often: the explorer learns by itself which bytes select the level.
-- States reached that way are saved like any other discovery, so exploration continues from the new area.
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
local MUTATION_CHANCE = 0.5      -- part of the episodes that start with a RAM mutation
local MUTATION_CREDIT = { P = 20, S = 3, L = 1 }
local BOOT_LIMIT_FRAMES = 3000   -- keep trying to leave the title/menu before Go-Explore saves cells
local GAMEPLAY_CHECKS_NEEDED = 3
local WEAK_GAMEPLAY_CHECKS_NEEDED = 8
local MENU_ESCAPE_INTERVAL = 240 -- while gameplay is unconfirmed, periodically try Start/A/Down

local nesDebug = emu.memType.nesDebug
local ramType = emu.memType.nesInternalRam

-- Mutable addresses: internal RAM except the CPU stack page ($0100-$01FF)
local mutableAddresses = {}
for addr = 0, 0x7FF do
  if addr < 0x100 or addr > 0x1FF then
    mutableAddresses[#mutableAddresses + 1] = addr
  end
end
local addressCredit = {}         -- addr -> learned credit
local episodeMutations = {}      -- addresses mutated at the start of the current episode
local pendingMutations = nil     -- { {addr, value}, ... } applied right after the next state load
local mutationEpisodes = 0
local productiveMutations = 0

-- Exploration state
local cells = {}          -- array of { key, state, visits, discoveries }
local knownKeys = {}      -- key -> true
local episode = 0
local episodeFrames = 0
local currentCell = nil
local pendingSave = nil   -- key of a cell to save at the next NMI
local pendingLoad = nil   -- cell to restore at the next NMI
local totalFrames = 0
local gameplayStarted = false
local gameplayConfirmed = false
local gameplayChecks = 0
local weakGameplayChecks = 0
local bootFrames = 0
local menuEscapeFrame = 0

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
    local kind = key:sub(1, 1)
    if currentCell then
      currentCell.discoveries = currentCell.discoveries + KEY_WEIGHT[kind]
    end
    -- Credit the RAM mutations of this episode (if any) for the discovery
    if #episodeMutations > 0 then
      if episodeMutations.credited ~= true then
        productiveMutations = productiveMutations + 1
        episodeMutations.credited = true
      end
      for _, addr in ipairs(episodeMutations) do
        addressCredit[addr] = (addressCredit[addr] or 0) + MUTATION_CREDIT[kind]
      end
    end
    if #cells < MAX_CELLS and gameplayStarted then
      pendingSave = key
    end
  end
end

-- Stillness (menus, pause, "continue" screens) -----------------------------------------------------

local function spritesMoved()
  local oam = {}
  local moved = false
  local movedCount = 0
  local count = 0
  for i = 0, 63 do
    local y = emu.read(i * 4, emu.memType.nesSpriteRam)
    local v = y * 256 + emu.read(i * 4 + 3, emu.memType.nesSpriteRam)
    oam[i] = v
    if y < 0xEF then
      count = count + 1
    end
    if previousOam and previousOam[i] ~= v then
      moved = true
      movedCount = movedCount + 1
    end
  end
  previousOam = oam
  return moved, count, movedCount
end

local previousScrollX = nil
local previousScrollY = nil

local function look()
  local buffer = emu.getScreenBuffer()
  local sample = {}
  for i = 1, #buffer, 61 do
    sample[#sample + 1] = buffer[i]
  end
  local moved, spriteCount, movedCount = spritesMoved()
  local ppu = emu.getPpuInfo()
  local scrollMoved = false
  if ppu then
    if previousScrollX ~= nil and (ppu.scrollX ~= previousScrollX or ppu.scrollY ~= previousScrollY) then
      scrollMoved = true
    end
    previousScrollX = ppu.scrollX
    previousScrollY = ppu.scrollY
  end
  local changedRatio = 0
  if previousSample and #previousSample == #sample then
    local changed = 0
    for i = 1, #sample do
      if sample[i] ~= previousSample[i] then
        changed = changed + 1
      end
    end
    changedRatio = changed / #sample
    if not moved and changedRatio < STILL_THRESHOLD then
      stillChecks = stillChecks + 1
    else
      stillChecks = 0
    end
  end
  previousSample = sample

  --Gameplay detection: a scrolling or clearly changing playfield with sprites.
  --A minimum boot time keeps animated logos from being mistaken for gameplay.
  --A weaker sustained signal covers static-screen games (puzzles, RPG battles)
  --and games whose title animation never becomes fully still.
  local active = totalFrames >= 900 and (scrollMoved or changedRatio > 0.08 or
    (spriteCount >= 2 and changedRatio > 0.03) or
    (spriteCount >= 6 and movedCount >= 2 and changedRatio > 0.01))
  if active then
    gameplayChecks = gameplayChecks + 1
  else
    gameplayChecks = math.max(0, gameplayChecks - 1)
  end
  local weakActive = totalFrames >= 900 and spriteCount >= 4 and (moved or changedRatio > 0.005)
  if weakActive then
    weakGameplayChecks = weakGameplayChecks + 1
  else
    weakGameplayChecks = math.max(0, weakGameplayChecks - 1)
  end
  local detected = gameplayChecks >= GAMEPLAY_CHECKS_NEEDED or weakGameplayChecks >= WEAK_GAMEPLAY_CHECKS_NEEDED
  if detected and not gameplayConfirmed then
    gameplayConfirmed = true
    gameplayStarted = true
    episodeFrames = 0
    stillChecks = 0
    cooldown = 0
    emu.log(string.format("HD Studio explorer: gameplay detected at frame %d (%d sprites, strong=%d weak=%d)", totalFrames, spriteCount, gameplayChecks, weakGameplayChecks))
  end

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

-- Picks an address: 70% among the addresses that already produced discoveries (weighted by their credit),
-- 30% any address (keeps searching for new useful bytes)
local function pickAddress(salt)
  local totalCredit = 0
  for _, credit in pairs(addressCredit) do
    totalCredit = totalCredit + credit
  end
  if totalCredit > 0 and rnd(salt) < 0.7 then
    local r = rnd(salt + 1) * totalCredit
    for addr, credit in pairs(addressCredit) do
      r = r - credit
      if r <= 0 then
        return addr
      end
    end
  end
  return mutableAddresses[1 + math.floor(rnd(salt + 2) * #mutableAddresses)]
end

local function planMutations()
  episodeMutations = {}
  if rnd(11) >= MUTATION_CHANCE then
    return nil
  end
  local count = 1 + math.floor(rnd(12) * 3)
  local mutations = {}
  for i = 1, count do
    local addr = pickAddress(100 + i * 10)
    local r = rnd(200 + i)
    local value
    if r < 0.4 then
      value = math.floor(rnd(300 + i) * 16)         -- small values: level/area/stage numbers
    elseif r < 0.7 then
      value = -1                                      -- current value + 1 (next level, next room...)
    else
      value = math.floor(rnd(400 + i) * 256)         -- any value
    end
    mutations[#mutations + 1] = { addr, value }
    episodeMutations[#episodeMutations + 1] = addr
  end
  mutationEpisodes = mutationEpisodes + 1
  return mutations
end

local function topAddresses(n)
  local list = {}
  for addr, credit in pairs(addressCredit) do
    list[#list + 1] = { addr = addr, credit = credit }
  end
  table.sort(list, function(a, b) return a.credit > b.credit end)
  local parts = {}
  for i = 1, math.min(n, #list) do
    parts[#parts + 1] = string.format("$%04X:%d", list[i].addr, list[i].credit)
  end
  return table.concat(parts, " ")
end

local function endEpisode()
  local cell = chooseCell()
  episode = episode + 1
  if episode % 10 == 0 then
    emu.log(string.format("episode %d: %d saved cells, %d discoveries, mutations %d/%d productive, best addresses: %s",
      episode, #cells, countKeys(), productiveMutations, mutationEpisodes, topAddresses(5)))
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
    pendingMutations = planMutations()
  else
    episodeMutations = {}
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
    if pendingMutations then
      for _, m in ipairs(pendingMutations) do
        local addr, value = m[1], m[2]
        if value < 0 then
          value = (emu.read(addr, ramType) + 1) & 0xFF
        end
        emu.write(addr, value, ramType)
      end
      pendingMutations = nil
    end
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

local function bootInput()
  --Deterministic title/menu progression. Every 60 frames taps A, every 150 taps
  --Start, every 240 taps Down, and a slower cycle presses Start then A. This is
  --enough to clear logos, "PRESS START" screens and simple menus without any
  --game-specific knowledge. It is not used once gameplay is detected.
  bootFrames = bootFrames + 1
  local input = { a = false, b = false, select = false, start = false, up = false, down = false, left = false, right = false }
  --Aggressive, deterministic boot sequence: short Start taps every half second,
  --A taps in between, and periodic Down/Right for menus. This clears "PRESS
  --START" screens, logos and simple menus without game-specific knowledge.
  if bootFrames % 30 < 8 then input.start = true end
  if bootFrames % 60 >= 32 and bootFrames % 60 < 38 then input.a = true end
  if bootFrames % 240 < 5 then input.down = true end
  if bootFrames % 300 >= 150 and bootFrames % 300 < 156 then input.right = true end
  if bootFrames % 360 >= 356 then input.select = true end
  if bootFrames == BOOT_LIMIT_FRAMES then
    emu.log("HD Studio explorer: no gameplay signature after " .. BOOT_LIMIT_FRAMES .. " boot frames; starting exploration from the current state")
    gameplayStarted = true
    episodeFrames = 0
    stillChecks = 0
    cooldown = 0
  end
  emu.setInput(input, 0)
end

local function onInputPolled()
  totalFrames = totalFrames + 1
  episodeFrames = episodeFrames + 1

  if not gameplayStarted then
    if episodeFrames % CHECK_INTERVAL == 0 then
      look()
    end
    bootInput()
    return
  end

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

  --A forced start (BOOT_LIMIT_FRAMES) is exploration, not proof of gameplay.
  --While the signature is still unconfirmed, keep tapping menu controls so a
  --title/attract screen that survived the boot sequence can eventually be left.
  if not gameplayConfirmed then
    menuEscapeFrame = menuEscapeFrame + 1
    local phase = menuEscapeFrame % MENU_ESCAPE_INTERVAL
    if phase < 5 then
      input.start = true
    elseif phase >= 30 and phase < 35 then
      input.a = true
    elseif phase >= 60 and phase < 65 then
      input.down = true
    elseif phase >= 90 and phase < 95 then
      input.right = true
    end
  end

  emu.setInput(input, 0)
end

local nmiVector = emu.read(0xFFFA, nesDebug) | (emu.read(0xFFFB, nesDebug) << 8)
emu.addMemoryCallback(onNmi, emu.callbackType.exec, nmiVector)
emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)
emu.log(string.format("HD Studio deep explorer: NMI handler at $%04X", nmiVector))
