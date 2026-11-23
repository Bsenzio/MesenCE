using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Mesen.Config;
using Mesen.Debugger.Utilities;
using Mesen.Interop;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Records a short real-Mesen GIF with the HD pack active. The Lua driver loads SMB3 1-1,
	/// walks Mario to a scenic spot and forces a Goomba next to him, so the recorded GIF shows
	/// the custom HD enemy tile inside the actual game.
	/// </summary>
	internal static class HdStudioDemo
	{
		[DllImport(EmuApi.DllName)][return: MarshalAs(UnmanagedType.I1)] private static extern bool HdStudioSaveScreenshot([MarshalAs(UnmanagedType.LPUTF8Str)] string filename);
		[DllImport(EmuApi.DllName)] private static extern void HdStudioRunSingleFrame();

		public static int Run(string romPath, string packFolder, int frames, string? outputPath, Action<string> log)
		{
			string expectedFolder = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(romPath));
			if(!string.Equals(Path.GetFullPath(expectedFolder).TrimEnd('\\', '/'), Path.GetFullPath(packFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) {
				log($"Error: Mesen only loads HD packs from {expectedFolder}.");
				return -1;
			}
			if(!File.Exists(Path.Combine(packFolder, "hires.txt"))) {
				log("Error: no HD pack found in " + packFolder);
				return -1;
			}

			string folder = Path.Combine(packFolder, HdStudioReport.StudioFolderName, "demo");
			Directory.CreateDirectory(folder);
			string gif = outputPath ?? Path.Combine(folder, "hd_demo.gif");

			EmuApi.InitDll();
			ConfigManager.Config.ApplyConfig();
			EmuApi.InitializeEmu(ConfigManager.HomeFolder, IntPtr.Zero, IntPtr.Zero, true, true, true, true);
			ConfigApi.SetEmulationFlag(EmulationFlags.ConsoleMode, true);

			bool oldHd = ConfigManager.Config.Nes.EnableHdPacks;
			try {
				ConfigManager.Config.Nes.EnableHdPacks = true;
				ConfigManager.Config.Nes.ApplyConfig();

				EmuApi.Pause();
				if(!EmuApi.LoadRom(romPath, string.Empty)) {
					log("Error: the ROM could not be loaded.");
					return -1;
				}
				EmuApi.Pause();

				DebugWorkspaceManager.Load();
				int scriptId = DebugApi.LoadScript("demo_1-1.lua", ConfigManager.HomeFolder + Path.DirectorySeparatorChar, BuildLua());

				ConfigApi.SetEmulationFlag(EmulationFlags.MaximumSpeed, true);
				RecordApi.AviRecord(gif, new RecordAviOptions() {
					Codec = VideoCodec.GIF,
					CompressionLevel = 6,
					RecordSystemHud = false,
					RecordInputHud = false
				});

				EmuApi.Resume();
				Stopwatch sw = Stopwatch.StartNew();
				while(GetFrameCount() < frames && sw.Elapsed.TotalSeconds < 180) {
					Thread.Sleep(10);
					if(!EmuApi.IsRunning()) {
						break;
					}
				}
				EmuApi.Pause();
				RecordApi.AviStop();
				DebugApi.RemoveScript(scriptId);
				EmuApi.Stop();
				log($"Demo GIF: {gif} ({frames} frames)");
				return File.Exists(gif) ? 0 : -2;
			} catch(Exception ex) {
				log("Demo: " + ex.Message);
				return -1;
			} finally {
				ConfigManager.Config.Nes.EnableHdPacks = oldHd;
				ConfigManager.Config.Nes.ApplyConfig();
				EmuApi.Release();
			}
		}

		private static uint GetFrameCount()
		{
			return EmuApi.GetTimingInfo(CpuType.Nes).FrameCount;
		}

		private static string BuildLua()
		{
			return @"
local frame = 0
local armed = false
local entered = false
local enteredFrame = -1
local input = { a = false, b = false, select = false, start = false, up = false, down = false, left = false, right = false }

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

local function forceGoomba()
  local px, py = r(0x0090), r(0x00A2)
  local pxh, pyh = r(0x0075), r(0x0087)
  w(0x0661, 1)
  w(0x0671, 114)
  w(0x0091, (px + 48) % 256); w(0x00A3, py); w(0x0076, pxh); w(0x0088, pyh)
  w(0x0669, 0); w(0x0679, 0)
  w(0x00BE, 0) -- stop the Goomba's horizontal velocity so it stays next to Mario
end

emu.addMemoryCallback(function(addr, value)
  if not armed then return end
  armed = false
  w(0x2000, 0); w(0x00FF, 0); w(0xE000, 0); w(0x03DE, 0)
  w(0x070A, 1); w(0x0061, 0x82); w(0x0062, 0xBB)
  emu.setCpuState({ pc = 0x897B })
end, emu.callbackType.exec, 0xF486, 0xF486)

emu.addMemoryCallback(function(addr, value)
  local st = emu.getCpuState()
  w(0x2000, 0); emu.read(0x2002, emu.memType.nesMemory)
  emu.setCpuState({ pc = 0x89AB, sp = 0xFF, ps = st.ps & ~0x04 })
  entered = true
  enteredFrame = frame
end, emu.callbackType.exec, 0x89A0, 0x89A0)

emu.addEventCallback(function()
  frame = frame + 1
  if frame == 180 then armed = true end
  if not entered then return end
  local rel = frame - enteredFrame

  if rel == 40 then
    clearObjects()
  end
  if rel >= 60 and rel < 170 then
    -- Walk right for a moment so the camera is past the starting screen.
    w(0x0017, 0x01)
  elseif rel >= 170 then
    w(0x0017, 0x00)
  end
  if rel >= 170 and rel % 2 == 0 then
    forceGoomba()
  end
  if rel >= 170 and rel % 4 == 0 then
    w(0x0017, 0x00)
  end
end, emu.eventType.startFrame)

emu.addEventCallback(function()
  input.a = false; input.b = false; input.up = false; input.down = false
  input.left = false; input.right = false
  input.start = false; input.select = false
  if entered and frame >= enteredFrame + 60 and frame < enteredFrame + 170 then
    input.right = true
  end
  if entered and frame >= enteredFrame + 90 and frame < enteredFrame + 170 and (frame % 50) < 4 then
    input.a = true
  end
  emu.setInput(input, 0)
end, emu.eventType.inputPolled)
";
		}
	}
}
