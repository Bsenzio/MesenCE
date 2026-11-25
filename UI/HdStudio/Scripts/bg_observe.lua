-- HD Studio runtime background observer.
--
-- Samples nametable/attribute/CHR context through the emulator's real mapper
-- resolution. This is the CHR-RAM path: a background block is identified by the
-- runtime pattern bytes of its four 8x8 tiles, not by a static cartridge index.
-- The output is a TSV consumed by HdStudioAssets; it is evidence of observed
-- nametable content, not a claim that every background was reached.
local OUTPUT = "__OUTPUT__"
local SAMPLE_EVERY = 30
local MAX_BLOCKS = 6000

local ntType = emu.memType.nesNametableRam
local palType = emu.memType.nesPaletteRam
local blocks = {}
local order = {}
local frame = 0
local samples = 0

local function readNt(offset)
  return emu.read(offset, ntType)
end

local function paletteKey(index)
  local base = index * 4
  return string.format("%02X%02X%02X%02X",
    emu.read(base, palType), emu.read(base + 1, palType),
    emu.read(base + 2, palType), emu.read(base + 3, palType))
end

local function attributePalette(ntBase, x, y)
  local offset = ntBase + 0x3C0 + math.floor(y / 4) * 8 + math.floor(x / 4)
  local value = readNt(offset)
  local shift = math.floor((y % 4) / 2) * 4 + math.floor((x % 4) / 2) * 2
  return (value >> shift) & 3
end

local function tileInfo(ppuAddress)
  local info = emu.getChrTileInfo(ppuAddress)
  if not info or not info.hasPattern then
    return nil
  end
  return info
end

local function writeFile()
  local file = io.open(OUTPUT, "w")
  if not file then
    return
  end
  file:write("# HD Studio runtime background blocks\n")
  file:write("# uses\tpalette\tkey0\tkey1\tkey2\tkey3\tdata0\tdata1\tdata2\tdata3\n")
  for _, signature in ipairs(order) do
    local block = blocks[signature]
    file:write(string.format("%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n",
      block.uses, block.palette,
      block.keys[1], block.keys[2], block.keys[3], block.keys[4],
      block.data[1], block.data[2], block.data[3], block.data[4]))
  end
  file:close()
end

local function sample()
  local ppu = emu.getPpuInfo()
  if not ppu or not ppu.backgroundEnabled then
    return
  end
  local patternBase = ppu.backgroundPatternAddr or 0
  for nt = 0, 3 do
    local ntBase = nt * 0x400
    for by = 0, 28 do
      local y = by * 2
      for bx = 0, 30 do
        local x = bx * 2
        local paletteIndex = attributePalette(ntBase, x, y)
        local keys, data = {}, {}
        local complete = true
        local nonBlank = false
        for i = 0, 3 do
          local dx = i % 2
          local dy = math.floor(i / 2)
          local tileIndex = readNt(ntBase + (y + dy) * 32 + (x + dx))
          local info = tileInfo(patternBase + tileIndex * 16)
          if not info then
            complete = false
            break
          end
          if info.type == "NesChrRam" then
            keys[i + 1] = info.data
          else
            keys[i + 1] = string.format("%X", math.floor(info.address / 16))
          end
          data[i + 1] = info.data or ""
          if data[i + 1] ~= string.rep("0", 32) then
            nonBlank = true
          end
        end
        if complete and nonBlank then
          local palette = paletteKey(paletteIndex)
          local signature = palette .. "|" .. table.concat(keys, "|")
          local block = blocks[signature]
          if not block then
            if #order >= MAX_BLOCKS then
              return
            end
            block = { uses = 0, palette = palette, keys = keys, data = data }
            blocks[signature] = block
            order[#order + 1] = signature
          end
          block.uses = block.uses + 1
        end
      end
    end
  end
  samples = samples + 1
  if samples % 10 == 0 then
    writeFile()
  end
end

local function onFrame()
  frame = frame + 1
  if frame % SAMPLE_EVERY ~= 0 then
    return
  end
  sample()
end

local function onEnd()
  writeFile()
end

emu.addEventCallback(onFrame, emu.eventType.endFrame)
emu.addEventCallback(onEnd, emu.eventType.scriptEnded)
emu.log("HD Studio background observer ready")
