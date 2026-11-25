-- Diagnostic: logs visible sprite tiles and 2x2 background block signatures at chosen frames.
-- Used to match on-screen graphics to catalog asset IDs during validation.
local FRAMES = { 450, 900, 1350 }
local wanted = {}
for _, f in ipairs(FRAMES) do wanted[f] = true end
local frame = 0

local function tileInfo(ppuAddress)
  local info = emu.getChrTileInfo(ppuAddress)
  if not info or not info.hasPattern then return nil end
  return info
end

local function tileKey(info)
  if info.type == "NesChrRam" then return info.data end
  return string.format("%X", math.floor(info.address / 16))
end

local function paletteKey(base)
  return string.format("%02X%02X%02X%02X",
    emu.read(base, emu.memType.nesPaletteRam), emu.read(base + 1, emu.memType.nesPaletteRam),
    emu.read(base + 2, emu.memType.nesPaletteRam), emu.read(base + 3, emu.memType.nesPaletteRam))
end

local function dump()
  local ppu = emu.getPpuInfo()
  local objs = {}
  for i = 0, 63 do
    local base = i * 4
    local y = emu.read(0x200 + base, emu.memType.nesMemory)
    local tile = emu.read(0x200 + base + 1, emu.memType.nesMemory)
    local attr = emu.read(0x200 + base + 2, emu.memType.nesMemory)
    local x = emu.read(0x200 + base + 3, emu.memType.nesMemory)
    if y < 0xEF and x < 0xF8 then
      local pattern = tile
      if ppu.largeSprites then pattern = tile & 0xFE end
      local info = tileInfo(ppu.spritePatternAddr + pattern * 16)
      if info then
        local palette = paletteKey(0x10 + (attr & 3) * 4)
        objs[#objs + 1] = string.format("VISIBLE SPRITE i=%d x=%d y=%d key=%s pal=%s type=%s", i, x, y, tileKey(info), palette, info.type)
      end
    end
  end
  emu.log("VISIBLE BEGIN frame=" .. frame .. " sprites=" .. #objs)
  for _, line in ipairs(objs) do emu.log(line) end

  local ntBase = 0
  local patternBase = ppu.backgroundPatternAddr or 0
  for y = 0, 58, 2 do
    for x = 0, 62, 2 do
      local keys = {}
      local complete = true
      for i = 0, 3 do
        local dx = i % 2
        local dy = math.floor(i / 2)
        local tileIndex = emu.read(ntBase + (y + dy) * 32 + (x + dx), emu.memType.nesNametableRam)
        local info = tileInfo(patternBase + tileIndex * 16)
        if not info then complete = false break end
        keys[i + 1] = tileKey(info)
      end
      if complete then
        local attr = emu.read(ntBase + 0x3C0 + math.floor(y / 4) * 8 + math.floor(x / 4), emu.memType.nesNametableRam)
        local shift = math.floor((y % 4) / 2) * 4 + math.floor((x % 4) / 2) * 2
        local pal = (attr >> shift) & 3
        emu.log(string.format("VISIBLE BG x=%d y=%d pal=%s keys=%s", x, y, paletteKey(pal * 4), table.concat(keys, ",")))
      end
    end
  end
  emu.log("VISIBLE END frame=" .. frame)
end

local function onFrame()
  frame = frame + 1
  if wanted[frame] then dump() end
end

emu.addEventCallback(onFrame, emu.eventType.endFrame)
emu.log("Visible diagnostic ready")
