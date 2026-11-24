using Mesen.Config;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Mesen.HdStudio
{
	/// <summary>
	/// HD Studio: object atlas. Loads SMB3 1-1 through the game's own level loader, forces one
	/// object ID into a free object slot and captures the OAM sprites the game draws, with the
	/// exact sprite palette. One object per emulator run, so an object that hangs the game cannot
	/// stop the whole sweep.
	/// </summary>
	internal static class HdStudioSpriteAtlas
	{
		public static int RunOne(string romPath, int objectId, string outFolder, Action<string> log)
		{
			string folder = Path.Combine(outFolder, HdStudioReport.StudioFolderName, "sprites");
			Directory.CreateDirectory(folder);
			string csv = Path.Combine(folder, $"{objectId:000}.csv");
			if(File.Exists(csv)) {
				File.Delete(csv);
			}

			bool oldIo = ConfigManager.Config.Debug.ScriptWindow.AllowIoOsAccess;
			uint oldTimeout = ConfigManager.Config.Debug.ScriptWindow.ScriptTimeout;
			try {
				ConfigManager.Config.Debug.ScriptWindow.AllowIoOsAccess = true;
				ConfigManager.Config.Debug.ScriptWindow.ScriptTimeout = 10;

				EmuApi.InitDll();
				ConfigManager.Config.ApplyConfig();
				EmuApi.InitializeEmu(ConfigManager.HomeFolder, IntPtr.Zero, IntPtr.Zero, true, true, true, true);
				EmuApi.Pause();
				ConfigApi.SetEmulationFlag(EmulationFlags.ConsoleMode, true);

				if(!EmuApi.LoadRom(romPath, string.Empty)) {
					log("Sprite atlas: the ROM could not be loaded.");
					return 1;
				}
				if(EmuApi.GetRomInfo().ConsoleType != ConsoleType.Nes) {
					log("Sprite atlas: only NES games are supported.");
					return 1;
				}

				string script = BuildLua(objectId, csv.Replace("\\", "/"));
				string luaPath = Path.Combine(folder, "driver.lua");
				File.WriteAllText(luaPath, script, new UTF8Encoding(false));

				DebugWorkspaceManager.Load();
				DebugApi.LoadScript("sprite_atlas.lua", folder + Path.DirectorySeparatorChar, script);

				ConfigApi.SetEmulationFlag(EmulationFlags.MaximumSpeed, true);
				EmuApi.Resume();

				Stopwatch sw = Stopwatch.StartNew();
				while(sw.Elapsed.TotalSeconds < 30) {
					Thread.Sleep(20);
					if(!EmuApi.IsRunning()) {
						break;
					}
					if(File.Exists(csv)) {
						break;
					}
				}

				if(EmuApi.IsRunning()) {
					EmuApi.Pause();
				}
				EmuApi.Release();
				return File.Exists(csv) ? 0 : 2;
			} catch(Exception ex) {
				log("Sprite atlas: " + ex.Message);
				return 1;
			} finally {
				ConfigManager.Config.Debug.ScriptWindow.AllowIoOsAccess = oldIo;
				ConfigManager.Config.Debug.ScriptWindow.ScriptTimeout = oldTimeout;
			}
		}

		private static string BuildLua(int objectId, string csvPath)
		{
			return $@"
local OBJECT_ID = {objectId}
local OUT = '{csvPath}'
local frame = 0
local armed = false
local entered = false
local combos = {{}}
local sprites = {{}}
local comboCount = 0
local spriteCount = 0
local forcedFrame = -1
local wrote = false
local captured = false

local function w(a, v) emu.write(a, v, emu.memType.nesMemory) end
local function r(a) return emu.read(a, emu.memType.nesMemory) end

local function clearObjects()
  for i = 0, 0x54 do w(0x0651 + i, 0) end
  for i = 0, 7 do
    w(0x0091 + i, 0); w(0x00A3 + i, 0); w(0x0076 + i, 0); w(0x0088 + i, 0)
    w(0x058F + i, 0); w(0x007F + i, 0); w(0x009A + i, 0); w(0x074E + i, 0); w(0x00BE + i, 0)
  end
  for i = 0, 15 do w(0x7B10 + i, 0x80) end
end

local function forceObject()
  clearObjects()
  --Place the object next to the player, inside the visible camera area. A fixed Y=96
  --can be outside the scroll window and the object then never draws.
  local px, py = r(0x0090), r(0x00A2)
  local pxh, pyh = r(0x0075), r(0x0087)
  w(0x0661, 1)
  w(0x0671, OBJECT_ID)
  w(0x0091, (px + 32) % 256); w(0x00A3, py); w(0x0076, pxh); w(0x0088, pyh)
  w(0x0669, 0); w(0x0679, 0)
end

local ppuInfo = nil
local function info()
  if not ppuInfo then ppuInfo = emu.getPpuInfo() end
  return ppuInfo
end

--Resolve the exact PPU pattern address for one OAM entry. For 8x16 sprites the PPU
--uses (tile & 0xFE) << 4 plus the pattern-table bit; the mapper resolves that PPU
--address to a physical CHR ROM/RAM offset. No game-specific CHR bank table is used.
local function resolvePattern(tile, part)
  local p = info()
  local addr
  if p and p.largeSprites then
    local even = tile - (tile % 2)
    addr = (tile % 2) * 0x1000 + even * 16 + part * 16
  else
    addr = ((p and p.spritePatternAddr) or 0) + tile * 16
  end
  return addr, emu.getChrTileInfo(addr)
end

local function capture()
  --Read the PPU state at capture time: the sprite size/pattern base can change between
  --frames, and the mapper mapping must be the one used to render this frame.
  ppuInfo = nil
  --Object_SprRAM[0] is the OAM byte offset the object's drawing code used.
  local spr = r(0x058F)
  if spr == 0 then
    return
  end
  local pal = {{}}
  for i = 0, 31 do pal[i] = emu.read(i, emu.memType.nesPaletteRam) end
  local p = info()
  local large = p and p.largeSprites or false
  local mapperId = p and p.mapperId or -1
  --The object drawing code writes to Sprite_RAM ($0200) at Object_SprRAM[0],
  --not directly to PPU OAM; OAM is DMA'ed from this buffer later.
  local base = 0x0200 + spr
  for j = 0, 7 do
    local oamBase = base + j * 4
    if oamBase + 3 <= 0x02FF then
      local y = r(oamBase); local tile = r(oamBase + 1); local attr = r(oamBase + 2); local x = r(oamBase + 3)
      if y < 0xEF and tile ~= 0xFF then
        local parts = large and 2 or 1
        for part = 0, parts - 1 do
          local sourcePart = ((attr & 0x80) ~= 0) and (1 - part) or part
          local ppuAddr, chr = resolvePattern(tile, sourcePart)
          if chr and chr.hasPattern then
            local pi = 16 + (attr % 4) * 4
            local palette = 0xFF000000 | (pal[pi + 1] << 16) | (pal[pi + 2] << 8) | pal[pi + 3]
            local pattern = -1
            if chr.address >= 0 then pattern = chr.address // 16 end
            local key = pattern .. ':' .. palette
            if not combos[key] then
              comboCount = comboCount + 1
              combos[key] = {{ pattern = pattern, palette = palette }}
            end
            local screenY = y + 1 + part * 8
            local skey = key .. ':' .. x .. ':' .. screenY .. ':' .. attr
            if not sprites[skey] then
              spriteCount = spriteCount + 1
              sprites[skey] = {{
                oamIndex = spr // 4 + j, part = part,
                ppuAddr = ppuAddr, chrAddr = chr.address, chrType = chr.type,
                tileData = chr.data, pattern = pattern, palette = palette,
                screenX = x, screenY = screenY, oamX = x, oamY = y,
                attr = attr, mapperId = mapperId, largeSprites = large and 1 or 0
              }}
            end
          end
        end
      end
    end
  end
end

local dbg = io.open(OUT .. '.dbg', 'w')
local function dbgLog(s)
  if dbg then dbg:write(s .. '\n'); dbg:flush() end
end

local function writeCsv()
  if wrote then return end
  wrote = true
  local b = io.open(OUT, 'w')
  b:write('objectId,oamIndex,part,ppuAddr,chrAddr,chrType,tileData,pattern,palette,screenX,screenY,oamX,oamY,attr,frame,mapperId,largeSprites\n')
  for _, s in pairs(sprites) do
    b:write(string.format('%d,%d,%d,%X,%X,%s,%s,%X,%08X,%d,%d,%d,%d,%02X,%d,%d,%d\n',
      OBJECT_ID, s.oamIndex, s.part, s.ppuAddr, s.chrAddr, s.chrType, s.tileData,
      s.pattern, s.palette, s.screenX, s.screenY, s.oamX, s.oamY, s.attr, frame,
      s.mapperId, s.largeSprites))
  end
  b:close()
  --Evidence: the real rendered frame next to the reconstructed metasprite.
  local shot = emu.takeScreenshot()
  if shot then
    local f = io.open(OUT .. '.png', 'wb')
    if f then f:write(shot); f:close() end
  end
  emu.log('atlas id=' .. OBJECT_ID .. ' combos=' .. comboCount .. ' sprites=' .. spriteCount)
  emu.stop()
end

emu.addMemoryCallback(function(addr, value)
  if not armed then return end
  armed = false
  w(0x2000, 0); w(0x00FF, 0); w(0xE000, 0); w(0x03DE, 0)
  w(0x070A, 1); w(0x0061, 0x82); w(0x0062, 0xBB)
  emu.setCpuState({{ pc = 0x897B }})
end, emu.callbackType.exec, 0xF486, 0xF486)

emu.addMemoryCallback(function(addr, value)
  local st = emu.getCpuState()
  w(0x2000, 0); emu.read(0x2002, emu.memType.nesMemory)
  emu.setCpuState({{ pc = 0x89AB, sp = 0xFF, ps = st.ps & ~0x04 }})
  entered = true
end, emu.callbackType.exec, 0x89A0, 0x89A0)

emu.addEventCallback(function()
  frame = frame + 1
  if frame == 180 then armed = true end
  if not entered then return end

  if frame == 350 then
    clearObjects()
  end
  if frame == 358 then
    clearObjects()
  end
  if frame == 360 then
    forceObject()
    forcedFrame = frame
  end
  if forcedFrame > 0 and frame == forcedFrame + 6 and not captured then
    local sp = r(0x058F)
    dbgLog(string.format('f%d state=%02X id=%02X spr=%02X buf=%02X,%02X,%02X,%02X c=%d',
      frame, r(0x0661), r(0x0671), sp, r(0x0200 + sp), r(0x0201 + sp), r(0x0202 + sp), r(0x0203 + sp), comboCount))
    --Capture one frame only: accumulating every animation frame made walking objects
    --look doubled/shifted in the atlas.
    capture()
    captured = true
    writeCsv()
  end
end, emu.eventType.startFrame)
";
		}
	}
}
