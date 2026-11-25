-- RetroArcDo HD Studio v0.4 - live interactive capture.
--
-- This script is loaded into the running emulator while the user plays normally.
-- It does NOT change input, memory or PPU state. At the end of each sampled frame it
-- records the hardware evidence of what was actually displayed:
--
--   * mapper-resolved sprite patterns (8x8 and 8x16, flips, palette, priority)
--   * OAM occurrences (x/y, OAM slot, part, tile, attribute)
--   * the visible background nametable window (scroll-aware, mirroring via the mapper)
--   * attribute palettes, scroll, sprite mode, mirroring and a screen hash
--
-- Output is plain TSV/JSON in HdStudio/live so the Studio can rebuild semantic assets
-- and so the raw observations survive even when automatic grouping fails.
--
-- The UI writes HdStudio/live/control.txt ("<id>|pause|resume|stop") and the script
-- reads it every 10 frames. Pausing only stops sampling; the game keeps running.

local OUTPUT_DIR = "__OUTPUT__"
local CONTROL_FILE = "__CONTROL__"
local SAMPLE_EVERY = __SAMPLE_EVERY__
local BG_SAMPLE_EVERY = __BG_SAMPLE_EVERY__
local MAX_PATTERNS = 40000

local spritesPath = OUTPUT_DIR .. "/live-sprites.tsv"
local patternsPath = OUTPUT_DIR .. "/live-patterns.tsv"
local framesPath = OUTPUT_DIR .. "/live-frames.tsv"
local blocksPath = OUTPUT_DIR .. "/live-backgrounds.tsv"
local statusPath = OUTPUT_DIR .. "/live-status.json"
local pausePath = OUTPUT_DIR .. "/pause-frame.json"
local logPath = OUTPUT_DIR .. "/live-capture.log"

local ntType = emu.memType.nesNametableRam
local palType = emu.memType.nesPaletteRam
local spriteType = emu.memType.nesSpriteRam

local frame = 0
local samples = 0
local gameplayFrames = 0
local stopped = false
local paused = false
local lastControlId = 0
local startedAt = os.time()
local sessionTag = os.date("!%Y%m%d-%H%M%S")
local spriteCount = 0
local blockCount = 0
local screenHash = ""

local patterns = {}
local patternOrder = {}
local blocks = {}
local blockOrder = {}

local function log(message)
  local f = io.open(logPath, "a")
  if f then
    f:write(os.date("!%Y-%m-%dT%H:%M:%SZ") .. " " .. tostring(message) .. "\n")
    f:close()
  end
end

local function fileExists(path)
  local f = io.open(path, "r")
  if f then f:close(); return true end
  return false
end

local function ensureHeader(path, header)
  if not fileExists(path) then
    local f = io.open(path, "w")
    if f then f:write(header .. "\n"); f:close() end
  end
end

local function loadExistingPatterns()
  local f = io.open(patternsPath, "r")
  if not f then return end
  for line in f:lines() do
    if line:sub(1, 1) ~= "#" then
      local key = line:match("^([^\t]+)")
      if key and key ~= "" and not patterns[key] then
        patterns[key] = { uses = 0, chrType = "", chrAddr = -1, data = "" }
        patternOrder[#patternOrder + 1] = key
      end
    end
  end
  f:close()
end

local function loadExistingBlocks()
  local f = io.open(blocksPath, "r")
  if not f then return end
  for line in f:lines() do
    if line:sub(1, 1) ~= "#" then
      local signature = line:match("^[^\t]+\t[^\t]+\t(.+)$")
      --The signature is not stored separately; rebuild dedup lazily from the tile keys.
      local palette, k1, k2, k3, k4 = line:match("^([^\t]+)\t%d+\t([^\t]+)\t([^\t]+)\t([^\t]+)\t([^\t]+)")
      if palette and k1 then
        local sig = palette .. "|" .. k1 .. "|" .. k2 .. "|" .. k3 .. "|" .. k4
        if not blocks[sig] then
          blocks[sig] = { uses = 0 }
          blockOrder[#blockOrder + 1] = sig
        end
      end
    end
  end
  f:close()
end

local function paletteKey(index)
  local base = index * 4
  return string.format("%02X%02X%02X%02X",
    emu.read(base, palType), emu.read(base + 1, palType),
    emu.read(base + 2, palType), emu.read(base + 3, palType))
end

local function spritePalette(attr)
  local base = 0x10 + ((attr & 3) * 4)
  return string.format("%02X%02X%02X%02X",
    emu.read(base, palType), emu.read(base + 1, palType),
    emu.read(base + 2, palType), emu.read(base + 3, palType))
end

local function patternInfo(ppuAddress)
  local info = emu.getChrTileInfo(ppuAddress)
  if not info or not info.hasPattern then
    return nil, nil
  end
  local key
  if info.type == "NesChrRam" then
    key = info.data
  else
    key = string.format("%X", math.floor(info.address / 16))
  end
  if not patterns[key] then
    if #patternOrder >= MAX_PATTERNS then
      return key, info
    end
    patterns[key] = {
      uses = 0,
      chrType = info.type,
      chrAddr = info.address,
      data = info.data or "",
      firstFrame = frame
    }
    patternOrder[#patternOrder + 1] = key
  end
  patterns[key].uses = patterns[key].uses + 1
  return key, info
end

local function writePatterns()
  local f = io.open(patternsPath, "w")
  if not f then return end
  f:write("#key\tchrType\tchrAddress\ttileData\tfirstFrame\tuses\n")
  for _, key in ipairs(patternOrder) do
    local p = patterns[key]
    f:write(string.format("%s\t%s\t%d\t%s\t%d\t%d\n", key, p.chrType or "", p.chrAddr or -1,
      p.data or "", p.firstFrame or 0, p.uses or 0))
  end
  f:close()
end

local function writeBlocks()
  local f = io.open(blocksPath, "w")
  if not f then return end
  f:write("#palette\tuses\tkey0\tkey1\tkey2\tkey3\tdata0\tdata1\tdata2\tdata3\tfirstFrame\tlastFrame\n")
  for _, sig in ipairs(blockOrder) do
    local b = blocks[sig]
    f:write(string.format("%s\t%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%d\t%d\n",
      b.palette, b.uses or 0, b.keys[1], b.keys[2], b.keys[3], b.keys[4],
      b.data[1], b.data[2], b.data[3], b.data[4], b.firstFrame or 0, b.lastFrame or 0))
  end
  f:close()
end

local function writeStatus()
  local f = io.open(statusPath, "w")
  if not f then return end
  f:write(string.format(
    '{"schemaVersion":"1.0.0","frame":%d,"samples":%d,"patterns":%d,"spriteOccurrences":%d,"backgroundBlocks":%d,"gameplayFrames":%d,"paused":%s,"stopped":%s,"startedAt":%d,"updatedAt":%d,"screenHash":"%s"}\n',
    frame, samples, #patternOrder, spriteCount, #blockOrder, gameplayFrames,
    paused and "true" or "false", stopped and "true" or "false", startedAt, os.time(), screenHash))
  f:close()
end

local function screenSignature()
  local buffer = emu.getScreenBuffer()
  if not buffer then return "" end
  local hash = 5381
  for i = 1, #buffer, 97 do
    hash = (hash * 33 + (buffer[i] & 0xFF)) & 0x7FFFFFFF
  end
  return string.format("%X", hash)
end

local function currentControl()
  local f = io.open(CONTROL_FILE, "r")
  if not f then return nil end
  local last = nil
  for line in f:lines() do
    if line ~= "" then last = line end
  end
  f:close()
  if not last then return nil end
  local id, command = last:match("^(%d+)|(%a+)$")
  if not id then return nil end
  id = tonumber(id)
  if id <= lastControlId then return nil end
  lastControlId = id
  return command
end

local function writePauseFrame(sprites, ppu)
  local screenshotName = string.format("pause-%s-%08d.png", sessionTag, frame)
  local screenshotPath = OUTPUT_DIR .. "/" .. screenshotName
  local ok, shot = pcall(emu.takeScreenshot)
  if ok and shot then
    local f = io.open(screenshotPath, "wb")
    if f then f:write(shot); f:close() end
  end

  local f = io.open(pausePath, "w")
  if not f then return end
  f:write('{"schemaVersion":"1.0.0","frame":' .. frame .. ',"screenshot":"' .. screenshotName .. '",')
  f:write('"scrollX":' .. (ppu.scrollX or 0) .. ',"scrollY":' .. (ppu.scrollY or 0) .. ',')
  f:write('"mirroring":"' .. tostring(ppu.mirroring or "") .. '",')
  f:write('"largeSprites":' .. (ppu.largeSprites and "true" or "false") .. ',')
  f:write('"spritePatternAddr":' .. (ppu.spritePatternAddr or 0) .. ',')
  f:write('"backgroundPatternAddr":' .. (ppu.backgroundPatternAddr or 0) .. ',')
  f:write('"mapperId":' .. (ppu.mapperId or 0) .. ',')

  f:write('"sprites":[')
  for i, s in ipairs(sprites) do
    if i > 1 then f:write(",") end
    f:write(string.format(
      '{"oam":%d,"part":%d,"x":%d,"y":%d,"tile":%d,"palette":"%s","flipH":%s,"flipV":%s,"backgroundPriority":%s,"pattern":"%s","chrType":"%s","chrAddress":%d,"data":"%s"}',
      s.oam, s.part, s.x, s.y, s.tile, s.palette,
      s.flipH and "true" or "false", s.flipV and "true" or "false", s.priority and "true" or "false",
      s.pattern or "", s.chrType or "", s.chrAddr or -1, s.data or ""))
  end
  f:write('],"background":[')
  for ty = 0, 29 do
    for tx = 0, 31 do
      local bt = emu.getBackgroundTile(tx * 8, ty * 8)
      if bt and bt.chrAddress and bt.chrAddress >= 0 then
        local info = emu.getChrTileInfo(bt.ppuAddress)
        local key = ""
        if info and info.hasPattern then
          key = (info.type == "NesChrRam") and info.data or string.format("%X", math.floor(info.address / 16))
        end
        if key ~= "" then
          if tx > 0 or ty > 0 then f:write(",") end
          f:write(string.format(
            '{"x":%d,"y":%d,"px":%d,"py":%d,"tile":%d,"tileX":%d,"tileY":%d,"paletteIndex":%d,"palette":"%s","pattern":"%s","chrType":"%s","chrAddress":%d,"data":"%s"}',
            tx * 8, ty * 8, tx * 8, ty * 8, bt.tile, tx, ty, bt.paletteIndex,
            paletteKey(bt.paletteIndex), key, info.type, info.address, info.data or ""))
        end
      end
    end
  end
  f:write(']}\n')
  f:close()
  log("paused at frame " .. frame .. " (" .. #sprites .. " sprite parts)")
end

local function sampleSprites(ppu)
  local sprites = {}
  if not ppu then
    return sprites
  end
  --Do not require the sprite-enable bit here: many games turn rendering off
  --during vblank for OAM DMA, and the END-FRAME callback happens in that window.
  --The primary OAM still holds the sprites used by the frame that was displayed.
  local large = ppu.largeSprites
  local base = ppu.spritePatternAddr or 0
  local oam = emu.getOamInfo()
  if not oam then return sprites end

  for i = 0, 63 do
    local offset = i * 4 + 1
    local y = oam:byte(offset)
    if y < 0xEF then
      local tile = oam:byte(offset + 1)
      local attr = oam:byte(offset + 2)
      local x = oam:byte(offset + 3)
      local palette = spritePalette(attr)
      local flipH = (attr & 0x40) ~= 0
      local flipV = (attr & 0x80) ~= 0
      local priority = (attr & 0x20) ~= 0
      local parts = large and 2 or 1
      for part = 0, parts - 1 do
        local ppuAddr
        if large then
          local sourcePart = flipV and (1 - part) or part
          ppuAddr = ((tile & 1) * 0x1000) + ((tile & 0xFE) * 16) + sourcePart * 16
        else
          ppuAddr = base + tile * 16
        end
        local key, info = patternInfo(ppuAddr)
        if key and info then
          local s = {
            oam = i, part = part, x = x, y = y + 1 + part * 8, tile = tile,
            palette = palette, flipH = flipH, flipV = flipV, priority = priority,
            pattern = key, chrType = info.type, chrAddr = info.address,
            data = info.data or ""
          }
          sprites[#sprites + 1] = s
        end
      end
    end
  end
  return sprites
end

local function sampleBackground(ppu)
  if not ppu or not ppu.backgroundEnabled then return end
  local visible = {}
  local tileInfoCache = {}
  local function infoFor(ppuAddress)
    local cached = tileInfoCache[ppuAddress]
    if cached ~= nil then return cached end
    local info = emu.getChrTileInfo(ppuAddress)
    tileInfoCache[ppuAddress] = info or false
    return info or false
  end
  for ty = 0, 29 do
    visible[ty] = {}
    for tx = 0, 31 do
      local bt = emu.getBackgroundTile(tx * 8, ty * 8)
      visible[ty][tx] = bt
    end
  end

  for by = 0, 28 do
    for bx = 0, 30 do
      local info0 = visible[by][bx]
      local info1 = visible[by][bx + 1]
      local info2 = visible[by + 1][bx]
      local info3 = visible[by + 1][bx + 1]
      if info0 and info1 and info2 and info3 and info0.paletteIndex == info1.paletteIndex
        and info0.paletteIndex == info2.paletteIndex and info0.paletteIndex == info3.paletteIndex then
        local keys, data = {}, {}
        local complete = true
        local nonBlank = false
        for i, bt in ipairs({ info0, info1, info2, info3 }) do
          local info = infoFor(bt.ppuAddress)
          if not info or not info.hasPattern then complete = false; break end
          keys[i] = (info.type == "NesChrRam") and info.data or string.format("%X", math.floor(info.address / 16))
          data[i] = info.data or ""
          if data[i] ~= string.rep("0", 32) then nonBlank = true end
        end
        if complete and nonBlank then
          local palette = paletteKey(info0.paletteIndex)
          local sig = palette .. "|" .. keys[1] .. "|" .. keys[2] .. "|" .. keys[3] .. "|" .. keys[4]
          local block = blocks[sig]
          if not block then
            block = { palette = palette, uses = 0, keys = keys, data = data, firstFrame = frame }
            blocks[sig] = block
            blockOrder[#blockOrder + 1] = sig
          end
          block.uses = block.uses + 1
          block.lastFrame = frame
        end
      end
    end
  end
end

local function appendSprites(sprites)
  if #sprites == 0 then return end
  local f = io.open(spritesPath, "a")
  if not f then return end
  for _, s in ipairs(sprites) do
    f:write(string.format("%d\t%d\t%d\t%d\t%d\t%d\t%s\t%d\t%d\t%d\t%s\t%s\t%d\n",
      frame, s.oam, s.part, s.x, s.y, s.tile, s.palette,
      s.flipH and 1 or 0, s.flipV and 1 or 0, s.priority and 1 or 0,
      s.pattern, s.chrType, s.chrAddr))
    spriteCount = spriteCount + 1
  end
  f:close()
end

local function onEndFrame()
  if stopped then return end
  frame = frame + 1

  if frame % 10 == 0 then
    local command = currentControl()
    if command == "pause" and not paused then
      paused = true
      local ppu = emu.getPpuInfo()
      if ppu then
        writePauseFrame(sampleSprites(ppu), ppu)
      end
      writePatterns()
      writeBlocks()
      writeStatus()
    elseif command == "resume" then
      paused = false
    elseif command == "stop" then
      stopped = true
      writePatterns()
      writeBlocks()
      writeStatus()
      log("stopped at frame " .. frame)
      return
    end
  end

  if paused then
    if frame % 30 == 0 then writeStatus() end
    return
  end

  if frame % SAMPLE_EVERY ~= 0 then return end
  local ppu = emu.getPpuInfo()
  if not ppu then return end

  samples = samples + 1
  screenHash = screenSignature()
  local sprites = sampleSprites(ppu)
  appendSprites(sprites)

  --A frame is "active gameplay" when several sprites are on screen and the frame is
  --changing. Menus/title screens are still captured, but counted separately.
  local active = #sprites >= 3
  if active then gameplayFrames = gameplayFrames + 1 end

  if frame % BG_SAMPLE_EVERY == 0 then
    sampleBackground(ppu)
  end

  local f = io.open(framesPath, "a")
  if f then
    f:write(string.format("%d\t%d\t%d\t%s\t%d\t%d\t%d\t%s\t%d\n",
      frame, ppu.scrollX or 0, ppu.scrollY or 0, tostring(ppu.mirroring or ""),
      ppu.largeSprites and 1 or 0, #sprites, #blockOrder, screenHash, active and 1 or 0))
    f:close()
  end

  if samples % 20 == 0 then
    writePatterns()
  end
  if samples % 60 == 0 then
    writeBlocks()
    writeStatus()
  end
end

--Prepare output: append to previous live sessions, never discard approved work.
local function initialize()
  local probe = io.open(OUTPUT_DIR .. "/.probe", "w")
  if not probe then
    log("output folder is not writable: " .. OUTPUT_DIR)
    return false
  end
  probe:close()
  os.remove(OUTPUT_DIR .. "/.probe")

  ensureHeader(spritesPath, "#frame\toam\tpart\tx\ty\ttile\tpalette\tflipH\tflipV\tpriority\tpattern\tchrType\tchrAddr")
  ensureHeader(framesPath, "#frame\tscrollX\tscrollY\tmirroring\tlargeSprites\tsprites\tblocks\tscreenHash\tactive")
  loadExistingPatterns()
  loadExistingBlocks()
  if not fileExists(patternsPath) then writePatterns() end
  if not fileExists(blocksPath) then writeBlocks() end
  writeStatus()
  log("live capture started (sample every " .. SAMPLE_EVERY .. " frames, background every " .. BG_SAMPLE_EVERY .. ")")
  return true
end

if initialize() then
  emu.addEventCallback(onEndFrame, emu.eventType.endFrame)
  emu.addEventCallback(function()
    writePatterns()
    writeBlocks()
    writeStatus()
  end, emu.eventType.scriptEnded)
  emu.log("HD Studio live capture active")
else
  emu.log("HD Studio live capture could not start")
end
