using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Mesen.Config;
using Mesen.Debugger.Utilities;
using Mesen.Interop;

namespace Mesen.HdStudio
{
	/// <summary>
	/// HD Studio: player-form atlas. The player is not an object ID: SMB3 draws it from
	/// Player_Suit, Player_Statue and Player_Frame. This pass queues each suit through the game's
	/// own power-up code, forces the requested animation frame immediately before Player_Draw and
	/// captures the six OAM sprites with the exact sprite palette.
	///
	/// SMB3 (USA) only: the level-loader and Player_Draw addresses are from Southbird's disassembly.
	/// </summary>
	internal static class HdStudioPlayerAtlas
	{
		private const string FolderName = "playerforms";
		private const string Smb3UsaSha1 = "A03E7E526E79DF222E048AE22214BCA2BC49C449";

		private static readonly (int Id, string Name, int Suit)[] _forms = {
			(0, "Small", 0),
			(1, "Super", 1),
			(2, "Fire", 2),
			(3, "Raccoon", 3),
			(4, "Frog", 4),
			(5, "Tanuki", 5),
			(6, "Hammer", 6),
			(7, "Tanuki Statue", 5),
		};

		//Player_Frame values from Southbird's SMB3 disassembly (smb3.asm, PF_* constants).
		private static readonly (int Code, string Name)[] _smallPoses = {
			(0x3E, "walk 1"), (0x3F, "walk 2"),
			(0x4C, "run 1"), (0x4D, "run 2"),
			(0x40, "jump"), (0x41, "skid"), (0x42, "kick"),
			(0x46, "swim"), (0x4A, "climb"),
		};

		private static readonly (int Code, string Name)[] _bigPoses = {
			(0x0C, "walk 1"), (0x0D, "walk 2"), (0x0E, "walk 3"),
			(0x39, "run 1"), (0x3A, "run 2"), (0x3B, "run 3"),
			(0x4F, "jump"), (0x06, "fall"), (0x30, "skid"), (0x2D, "kick"),
			(0x0F, "duck"), (0x3C, "swim"), (0x2C, "climb"),
		};

		private static readonly (int Code, string Name)[] _firePoses = {
			(0x0C, "walk 1"), (0x0D, "walk 2"), (0x0E, "walk 3"),
			(0x39, "run 1"), (0x3A, "run 2"), (0x3B, "run 3"),
			(0x4F, "jump"), (0x06, "fall"), (0x30, "skid"), (0x2D, "kick"),
			(0x0F, "duck"), (0x3C, "swim"), (0x2C, "climb"),
			(0x10, "throw 2"), (0x11, "throw 3"),
		};

		private static readonly (int Code, string Name)[] _raccoonPoses = {
			(0x00, "walk 1"), (0x01, "walk 2"), (0x02, "walk 3"),
			(0x03, "tail 1"), (0x04, "tail 2"), (0x05, "tail 3"),
			(0x07, "duck"), (0x08, "flutter 1"), (0x09, "flutter 2"), (0x0A, "flutter 3"),
			(0x36, "fly 1"), (0x37, "fly 2"), (0x38, "fly 3"), (0x50, "jump"),
		};

		private static readonly (int Code, string Name)[] _tanukiPoses = {
			(0x00, "walk 1"), (0x01, "walk 2"), (0x02, "walk 3"),
			(0x03, "tail 1"), (0x04, "tail 2"), (0x05, "tail 3"),
			(0x07, "duck"), (0x08, "flutter 1"), (0x09, "flutter 2"), (0x0A, "flutter 3"),
			(0x36, "fly 1"), (0x37, "fly 2"), (0x38, "fly 3"), (0x50, "jump"),
		};

		private static readonly (int Code, string Name)[] _frogPoses = {
			(0x15, "hop 1"), (0x16, "hop 2"), (0x17, "hop 3"),
			(0x12, "swim up 1"), (0x13, "swim up 2"), (0x14, "swim up 3"),
			(0x1F, "swim idle 1"), (0x20, "swim idle 2"),
			(0x21, "swim side 1"), (0x22, "swim side 2"), (0x23, "swim side 3"),
			(0x24, "swim down 1"), (0x25, "swim down 2"), (0x26, "swim down 3"),
			(0x28, "climb"),
		};

		private static readonly (int Code, string Name)[] _hammerPoses = {
			(0x0C, "walk 1"), (0x0D, "walk 2"), (0x0E, "walk 3"),
			(0x39, "run 1"), (0x3A, "run 2"), (0x3B, "run 3"),
			(0x4F, "jump"), (0x06, "fall"), (0x30, "skid"), (0x2D, "kick"),
			(0x0F, "duck"), (0x3C, "swim"), (0x2C, "climb"),
			(0x10, "throw 2"), (0x11, "throw 3"),
		};

		private static readonly (int Code, string Name)[] _statuePoses = {
			(0x1E, "statue"),
		};

		public static int RunAll(string romPath, string outFolder, uint[] nesPalette, Action<string> log)
		{
			NesRomData rom = NesRomData.Load(romPath);
			if(rom.Sha1 != Smb3UsaSha1) {
				log("Player forms: this pass is calibrated for Super Mario Bros. 3 (USA), SHA-1 " + Smb3UsaSha1 + ". The ROM is " + rom.Sha1 + ".");
				return 1;
			}

			string folder = Path.Combine(outFolder, HdStudioReport.StudioFolderName, FolderName);
			Directory.CreateDirectory(folder);
			string csv = Path.Combine(folder, "playerforms.csv");
			string done = csv + ".done";
			foreach(string path in new[] { csv, done }) {
				if(File.Exists(path)) {
					File.Delete(path);
				}
			}

			bool oldIo = ConfigManager.Config.Debug.ScriptWindow.AllowIoOsAccess;
			uint oldTimeout = ConfigManager.Config.Debug.ScriptWindow.ScriptTimeout;
			try {
				ConfigManager.Config.Debug.ScriptWindow.AllowIoOsAccess = true;
				ConfigManager.Config.Debug.ScriptWindow.ScriptTimeout = 30;

				EmuApi.InitDll();
				ConfigManager.Config.ApplyConfig();
				EmuApi.InitializeEmu(ConfigManager.HomeFolder, IntPtr.Zero, IntPtr.Zero, true, true, true, true);
				EmuApi.Pause();
				ConfigApi.SetEmulationFlag(EmulationFlags.ConsoleMode, true);

				if(!EmuApi.LoadRom(romPath, string.Empty)) {
					log("Player forms: the ROM could not be loaded.");
					return 1;
				}
				if(EmuApi.GetRomInfo().ConsoleType != ConsoleType.Nes) {
					log("Player forms: only NES games are supported.");
					return 1;
				}

				string script = BuildLua(csv.Replace("\\", "/"), done.Replace("\\", "/"));
				string luaPath = Path.Combine(folder, "driver.lua");
				File.WriteAllText(luaPath, script, new UTF8Encoding(false));

				DebugWorkspaceManager.Load();
				DebugApi.LoadScript("player_forms.lua", folder + Path.DirectorySeparatorChar, script);

				ConfigApi.SetEmulationFlag(EmulationFlags.MaximumSpeed, true);
				EmuApi.Resume();

				Stopwatch sw = Stopwatch.StartNew();
				while(sw.Elapsed.TotalSeconds < 120) {
					Thread.Sleep(20);
					if(!EmuApi.IsRunning()) {
						break;
					}
					if(File.Exists(done)) {
						break;
					}
				}

				if(EmuApi.IsRunning()) {
					EmuApi.Pause();
				}

				if(!File.Exists(done)) {
					log("Player forms: the capture did not finish (timeout).");
					return 2;
				}

				//Render while the emulator is paused; EmuApi.Release() can fault after a long
				//Lua session on this build, and the images/JSON only need the dumped CSV.
				RenderAll(rom, folder, nesPalette, log);
				return 0;
			} catch(Exception ex) {
				log("Player forms: " + ex.Message);
				return 1;
			} finally {
				ConfigManager.Config.Debug.ScriptWindow.AllowIoOsAccess = oldIo;
				ConfigManager.Config.Debug.ScriptWindow.ScriptTimeout = oldTimeout;
			}
		}

		private static IEnumerable<(int Code, string Name)> PosesFor(int formId)
		{
			return formId switch {
				0 => _smallPoses,
				1 => _bigPoses,
				2 => _firePoses,
				3 => _raccoonPoses,
				4 => _frogPoses,
				5 => _tanukiPoses,
				6 => _hammerPoses,
				7 => _statuePoses,
				_ => Array.Empty<(int, string)>()
			};
		}

		private static string BuildLua(string csvPath, string donePath)
		{
			StringBuilder forms = new();
			foreach((int id, string name, int suit) in _forms) {
				forms.AppendLine($"  {{ id={id}, name='{name}', suit={suit}, statue={(id == 7 ? "true" : "false")}, poses={{");
				foreach((int code, string poseName) in PosesFor(id)) {
					forms.AppendLine($"    {{ code=0x{code:X2}, name='{poseName}' }},");
				}
				forms.AppendLine("  }},");
			}

			return $@"
local OUT = '{csvPath}'
local DONE = '{donePath}'
local FORMS = {{
{forms}}}
local frame = 0
local armed = false
local entered = false
local enteredFrame = -1
local formIndex = 1
local poseIndex = 1
local phase = 'setup'
local waitUntil = 0
local forcedPose = -1
local forceStatueDown = false
local b = nil

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

local function finish()
  if b then b:close(); b = nil end
  local d = io.open(DONE, 'w')
  if d then d:write('done'); d:close() end
  emu.log('player forms: done')
  emu.stop()
end

local function emit(form, pose)
  local spr = r(0x058E)
  local pal = {{}}
  for i = 0, 31 do pal[i] = emu.read(i, emu.memType.nesPaletteRam) end
  local banks = {{}}
  for i = 0, 3 do banks[i] = r(0x071B + i) end
  for j = 0, 5 do
    local p = 0x0200 + spr + j * 4
    local y = r(p); local tile = r(p + 1); local attr = r(p + 2); local x = r(p + 3)
    if y < 0xEF and tile ~= 0xF1 then
      local pattern = (banks[tile // 64] or 0) * 64 + (tile % 64)
      local pi = 16 + (attr % 4) * 4
      local palette = 0xFF000000 | (pal[pi + 1] << 16) | (pal[pi + 2] << 8) | pal[pi + 3]
      b:write(string.format('%d,%s,%d,%d,%s,%d,%08X,%d,%d,%02X\n',
        form.id, form.name, form.suit, pose.code, pose.name, pattern, palette, x, y, attr))
    end
  end
end

b = io.open(OUT, 'w')
if not b then
  emu.log('player forms: cannot open ' .. OUT)
  emu.stop()
  return
end
b:write('formId,formName,suit,poseCode,poseName,pattern,palette,x,y,attr\n')

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
  enteredFrame = frame
end, emu.callbackType.exec, 0x89A0, 0x89A0)

-- Player_Draw entry (bank 29, SMB3 USA). Force the requested frame just before the
-- game reads Player_Frame, so the pose is exact and the next frame cannot overwrite it.
emu.addMemoryCallback(function(addr, value)
  if forcedPose >= 0 then
    w(0x00EE, forcedPose)
    w(0x00EF, 0x40)
  end
end, emu.callbackType.exec, 0xCE9F, 0xCE9F)

-- Player_TanookiStatue checks Pad_Holding at entry. The attract input is empty, which
-- would clear the statue immediately; force DOWN only while the statue form is active.
emu.addMemoryCallback(function(addr, value)
  if forceStatueDown then
    w(0x0017, 0x04)
  end
end, emu.callbackType.exec, 0xAFAE, 0xAFAE)

emu.addEventCallback(function()
  frame = frame + 1
  if frame == 180 then armed = true end
  if not entered then
    if frame > 2200 then finish() end
    return
  end

  local rel = frame - enteredFrame
  if rel == 60 then
    clearObjects()
    w(0x00F1, 0) -- Player_IsDying = 0
    w(0x058E, 0x28) -- use the normal gameplay OAM area for the player
  end
  if rel < 80 then return end

  w(0x0017, 0); w(0x0018, 0) -- stop the attract/demo input from moving the player

  if formIndex > #FORMS then
    finish()
    return
  end

  local form = FORMS[formIndex]
  if phase == 'setup' then
    forcedPose = -1
    forceStatueDown = false
    w(0x057A, 0) -- clear a previous Tanuki statue
    if form.statue then
      w(0x00ED, 5)
      w(0x0578, 0x0F)
      forceStatueDown = true
    else
      w(0x0578, form.suit + 1)
    end
    emu.log('player form: ' .. form.name)
    phase = 'suitwait'
    waitUntil = rel + 12
  elseif phase == 'suitwait' and rel >= waitUntil then
    poseIndex = 1
    phase = 'pose'
    waitUntil = 0
  elseif phase == 'pose' then
    if poseIndex > #form.poses then
      forcedPose = -1
      formIndex = formIndex + 1
      phase = 'setup'
      waitUntil = 0
    elseif waitUntil == 0 then
      forcedPose = form.poses[poseIndex].code
      waitUntil = rel + 4
    elseif rel >= waitUntil then
      emit(form, form.poses[poseIndex])
      poseIndex = poseIndex + 1
      waitUntil = 0
    end
  end
end, emu.eventType.startFrame)
";
		}

		private class PlayerSprite
		{
			public int FormId;
			public string FormName = "";
			public int Suit;
			public int PoseCode;
			public string PoseName = "";
			public int Pattern;
			public uint Palette;
			public int X;
			public int Y;
			public int Attr;
		}

		private class PoseEntry
		{
			public int Code;
			public string Name = "";
			public string Png = "";
			public int Width;
			public int Height;
			public int Sprites;
		}

		private class FormEntry
		{
			public int Id;
			public string Name = "";
			public int Suit;
			public List<PoseEntry> Poses = new();
		}

		private static void RenderAll(NesRomData rom, string folder, uint[] nesPalette, Action<string> log)
		{
			string csv = Path.Combine(folder, "playerforms.csv");
			if(!File.Exists(csv)) {
				log("Player forms: no CSV was written.");
				return;
			}

			List<PlayerSprite> sprites = new();
			foreach(string line in File.ReadLines(csv).Skip(1)) {
				string[] p = line.Split(',');
				if(p.Length < 10) {
					continue;
				}
				if(!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int formId) ||
					!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int suit) ||
					!int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int poseCode) ||
					!int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pattern) ||
					!uint.TryParse(p[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint palette) ||
					!int.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
					!int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
					!int.TryParse(p[9], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int attr)) {
					continue;
				}
				sprites.Add(new PlayerSprite() {
					FormId = formId, FormName = p[1], Suit = suit,
					PoseCode = poseCode, PoseName = p[4],
					Pattern = pattern, Palette = palette, X = x, Y = y, Attr = attr
				});
			}

			if(sprites.Count == 0) {
				log("Player forms: the CSV is empty.");
				return;
			}

			HashSet<(int Pattern, uint Palette)> exact = new();
			foreach(PlayerSprite sprite in sprites) {
				int first = sprite.Pattern & ~1;
				exact.Add((first, sprite.Palette));
				exact.Add((first + 1, sprite.Palette));
			}
			using(StreamWriter writer = new(Path.Combine(folder, "playerforms_exact.txt"), false, new UTF8Encoding(false))) {
				foreach((int pattern, uint palette) in exact.OrderBy(e => e.Pattern).ThenBy(e => e.Palette)) {
					writer.WriteLine($"{pattern:X} {palette:X8}");
				}
			}

			int totalPoses = 0;
			List<FormEntry> forms = new();
			foreach((int id, string name, int suit) in _forms) {
				List<PlayerSprite> formSprites = sprites.Where(s => s.FormId == id).ToList();
				if(formSprites.Count == 0) {
					continue;
				}

				FormEntry entry = new() { Id = id, Name = name, Suit = suit };
				foreach(var group in formSprites.GroupBy(s => (s.PoseCode, s.PoseName)).OrderBy(g => g.Key.PoseCode)) {
					List<PlayerSprite> poseSprites = group.ToList();
					int minX = poseSprites.Min(s => s.X);
					int minY = poseSprites.Min(s => s.Y);
					int maxX = poseSprites.Max(s => s.X + 8);
					int maxY = poseSprites.Max(s => s.Y + 16);
					int width = Math.Clamp(maxX - minX, 8, 64);
					int height = Math.Clamp(maxY - minY, 8, 64);
					uint[] pixels = new uint[width * height];
					foreach(PlayerSprite sprite in poseSprites) {
						//SMB3 runs the PPU in 8x16 sprite mode: each OAM entry is a pair of CHR tiles.
						int firstPattern = sprite.Pattern & ~1;
						for(int half = 0; half < 2; half++) {
							int pattern = firstPattern + (((sprite.Attr & 0x80) != 0) ? 1 - half : half);
							if(pattern < 0 || pattern >= rom.ChrTileCount) {
								continue;
							}
							byte[] data = rom.GetChrTile(pattern);
							for(int sy = 0; sy < 8; sy++) {
								for(int sx = 0; sx < 8; sx++) {
									int srcX = (sprite.Attr & 0x40) != 0 ? 7 - sx : sx;
									int color = GetTilePixel(data, srcX, sy);
									if(color == 0) {
										continue;
									}
									int px = sprite.X - minX + sx;
									int py = sprite.Y - minY + half * 8 + sy;
									if(px >= 0 && px < width && py >= 0 && py < height) {
										pixels[py * width + px] = PaletteToArgb(nesPalette, sprite.Palette, color);
									}
								}
							}
						}
					}

					string png = $"{id:00}_{SafeName(name)}_{SafeName(group.Key.PoseName)}.png";
					HdStudioPng.Write(Path.Combine(folder, png), HdStudioPng.Upscale(pixels, width, height, 3), width * 3, height * 3);
					entry.Poses.Add(new PoseEntry() {
						Code = group.Key.PoseCode, Name = group.Key.PoseName, Png = png,
						Width = width, Height = height, Sprites = poseSprites.Count
					});
				}
				forms.Add(entry);
				totalPoses += entry.Poses.Count;
			}

			using(FileStream stream = File.Create(Path.Combine(folder, "playerforms.json"))) {
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
				json.WriteStartObject();
				json.WriteString("generatedBy", "Mesen HD Studio");
				json.WriteString("game", "Super Mario Bros. 3");
				json.WriteStartArray("forms");
				foreach(FormEntry form in forms) {
					json.WriteStartObject();
					json.WriteNumber("id", form.Id);
					json.WriteString("name", form.Name);
					json.WriteNumber("suit", form.Suit);
					json.WriteStartArray("poses");
					foreach(PoseEntry pose in form.Poses) {
						json.WriteStartObject();
						json.WriteNumber("code", pose.Code);
						json.WriteString("name", pose.Name);
						json.WriteString("png", pose.Png);
						json.WriteNumber("width", pose.Width);
						json.WriteNumber("height", pose.Height);
						json.WriteNumber("sprites", pose.Sprites);
						json.WriteEndObject();
					}
					json.WriteEndArray();
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}

			log($"Player forms: {forms.Count} forms, {totalPoses} poses, {sprites.Count} sprites -> {folder}");
		}

		private static string SafeName(string value)
		{
			StringBuilder sb = new();
			foreach(char c in value) {
				sb.Append(char.IsLetterOrDigit(c) ? c : '_');
			}
			return sb.ToString().Trim('_');
		}

		private static int GetTilePixel(byte[] tileData, int x, int y)
		{
			int low = (tileData[y] >> (7 - x)) & 1;
			int high = (tileData[y + 8] >> (7 - x)) & 1;
			return low | (high << 1);
		}

		private static uint PaletteToArgb(uint[] nesPalette, uint palette, int colorIndex)
		{
			int shift = (3 - colorIndex) * 8;
			int nesColor = (int)((palette >> shift) & 0x3F);
			return nesPalette[nesColor] | 0xFF000000;
		}
	}
}
