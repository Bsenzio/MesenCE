using Mesen.Config;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Checks that an HD pack works: plays the game twice (HD packs enabled / disabled),
	/// saves screenshots of the same frames and writes a side-by-side comparison page.
	/// </summary>
	internal class HdStudioVerify
	{
		[DllImport(EmuApi.DllName)][return: MarshalAs(UnmanagedType.I1)] private static extern bool HdStudioSaveScreenshot([MarshalAs(UnmanagedType.LPUTF8Str)] string filename);
		[DllImport(EmuApi.DllName)] private static extern void HdStudioRunSingleFrame();

		private readonly string _romPath;
		private readonly string? _moviePath;
		private readonly List<string> _luaScripts;
		private readonly bool _useBuiltInExplorer;
		private readonly string _packFolder;
		private readonly string _verifyFolder;
		private readonly List<uint> _checkpoints = new();
		private readonly Action<string> _log;
		private readonly List<string> _hdStateHashes = new();
		private readonly List<string> _originalStateHashes = new();

		public HdStudioVerify(string romPath, string? moviePath, List<string> luaScripts, bool useBuiltInExplorer, string packFolder, int frames, int shots, Action<string> log)
		{
			_romPath = romPath;
			_moviePath = moviePath;
			_luaScripts = luaScripts;
			_useBuiltInExplorer = useBuiltInExplorer && luaScripts.Count == 0 && moviePath == null;
			_packFolder = packFolder;
			_verifyFolder = Path.Combine(packFolder, HdStudioReport.StudioFolderName, "verify");
			_log = log;

			shots = Math.Clamp(shots, 1, 20);
			for(int i = 1; i <= shots; i++) {
				_checkpoints.Add((uint)Math.Max(10, (long)frames * i / (shots + 1)));
			}
		}

		/// <returns>0 when the HD pack was loaded and the screenshots were saved</returns>
		public int Run()
		{
			string expectedFolder = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(_romPath));
			if(!string.Equals(Path.GetFullPath(expectedFolder).TrimEnd('\\', '/'), Path.GetFullPath(_packFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) {
				_log($"Error: Mesen only loads HD packs from {expectedFolder} (the --out folder is ignored for verification).");
				return -1;
			}
			if(!File.Exists(Path.Combine(_packFolder, "hires.txt"))) {
				_log($"Error: no HD pack found in {_packFolder}. Run the capture first.");
				return -1;
			}

			Directory.CreateDirectory(_verifyFolder);
			foreach(string oldFile in Directory.GetFiles(_verifyFolder, "frame_*.png")) {
				File.Delete(oldFile);
			}

			EmuApi.InitDll();
			ConfigManager.Config.ApplyConfig();
			EmuApi.InitializeEmu(ConfigManager.HomeFolder, IntPtr.Zero, IntPtr.Zero, true, true, true, true);
			ConfigApi.SetEmulationFlag(EmulationFlags.ConsoleMode, true);

			bool originalSetting = ConfigManager.Config.Nes.EnableHdPacks;
			FrameInfo hdSize;
			FrameInfo originalSize;
			bool ok;
			try {
				ok = RunPass(true, "hd", out hdSize) & RunPass(false, "original", out originalSize);
				CompareStateHashes();
			} finally {
				ConfigManager.Config.Nes.EnableHdPacks = originalSetting;
				ConfigManager.Config.Nes.ApplyConfig();
				EmuApi.Release();
			}

			bool hdLoaded = hdSize.Width > originalSize.Width;
			_log(hdLoaded
				? $"HD pack ACTIVE: the game renders at {hdSize.Width}x{hdSize.Height} instead of {originalSize.Width}x{originalSize.Height}."
				: "HD pack NOT active: the game rendered at the original resolution. Check the pack for errors (Mesen's log window shows [HDPack] messages).");

			string page = WriteHtml(hdLoaded, hdSize, originalSize);
			_log("Comparison: " + page);
			return ok && hdLoaded ? 0 : -3;
		}

		private bool RunPass(bool enableHdPacks, string suffix, out FrameInfo screenSize)
		{
			screenSize = new FrameInfo();
			ConfigManager.Config.Nes.EnableHdPacks = enableHdPacks;
			ConfigManager.Config.Nes.ApplyConfig();

			EmuApi.Pause();
			if(!EmuApi.LoadRom(_romPath, string.Empty)) {
				_log("Error: the ROM could not be loaded.");
				return false;
			}
			EmuApi.Pause();
			if(_moviePath != null) {
				RecordApi.MoviePlay(_moviePath);
			}

			//Scripts are loaded again for each pass so their state (frame counters, random seed) restarts with the game
			List<int> scriptIds = new();
			if(_luaScripts.Count > 0) {
				DebugWorkspaceManager.Load();
				foreach(string luaScript in _luaScripts) {
					string path = (Path.GetDirectoryName(luaScript) ?? Program.OriginalFolder) + Path.DirectorySeparatorChar;
					scriptIds.Add(DebugApi.LoadScript(luaScript, path, File.ReadAllText(luaScript)));
				}
			} else if(_useBuiltInExplorer) {
				DebugWorkspaceManager.Load();
				scriptIds.Add(DebugApi.LoadScript("explore.lua", ConfigManager.HomeFolder + Path.DirectorySeparatorChar, HdStudioCapture.GetBuiltInExplorer()));
			}

			ConfigApi.SetEmulationFlag(EmulationFlags.MaximumSpeed, true);
			_log($"Pass '{suffix}' (HD packs {(enableHdPacks ? "enabled" : "disabled")})");

			List<string> stateHashes = enableHdPacks ? _hdStateHashes : _originalStateHashes;
			stateHashes.Clear();
			bool ok = true;
			foreach(uint frame in _checkpoints) {
				if(!RunToFrame(frame)) {
					_log($"  could not reach frame {frame}");
					ok = false;
					break;
				}
				string stateHash = ComputeStateHash();
				stateHashes.Add(stateHash);
				_log($"  gameplay state at frame {frame}: {stateHash}");
				string file = Path.Combine(_verifyFolder, $"frame_{frame:00000}_{suffix}.png");
				if(!HdStudioSaveScreenshot(file)) {
					_log($"  screenshot failed at frame {frame}");
					ok = false;
				}
			}

			screenSize = ReadPngSize(Path.Combine(_verifyFolder, $"frame_{_checkpoints[0]:00000}_{suffix}.png"));
			foreach(int scriptId in scriptIds) {
				DebugApi.RemoveScript(scriptId);
			}
			if(RecordApi.MoviePlaying()) {
				RecordApi.MovieStop();
			}
			EmuApi.Stop();
			return ok;
		}

		/// <summary>Runs at maximum speed until close to the target, then frame by frame (both passes stop on the exact same frame)</summary>
		private bool RunToFrame(uint target)
		{
			const int TimeoutSeconds = 120;
			Stopwatch sw = Stopwatch.StartNew();
			string State() => $"frame={GetFrame()} paused={EmuApi.IsPaused()} running={EmuApi.IsRunning()}";

			if(GetFrame() + 30 < target) {
				EmuApi.Resume();
				while(GetFrame() + 30 < target) {
					if(!EmuApi.IsRunning() || sw.Elapsed.TotalSeconds > TimeoutSeconds) {
						_log($"  timeout while running to frame {target} ({State()})");
						return false;
					}
					Thread.Sleep(1);
				}
				EmuApi.Pause();
			}

			//IsPaused() becomes true as soon as a pause is requested, while the emulation thread may still be
			//finishing the current frame: wait until the frame counter is really stable before stepping
			if(!WaitUntilStable(sw, TimeoutSeconds)) {
				_log($"  timeout while pausing ({State()})");
				return false;
			}

			while(GetFrame() < target) {
				uint current = GetFrame();
				HdStudioRunSingleFrame();
				Stopwatch stepTimer = Stopwatch.StartNew();
				while(GetFrame() == current || !EmuApi.IsPaused()) {
					if(!EmuApi.IsRunning() || sw.Elapsed.TotalSeconds > TimeoutSeconds) {
						_log($"  timeout while stepping to frame {target} ({State()})");
						return false;
					}
					if(GetFrame() == current && EmuApi.IsPaused() && stepTimer.ElapsedMilliseconds > 300) {
						//The request was consumed by a pause that was still pending: ask again
						HdStudioRunSingleFrame();
						stepTimer.Restart();
					}
					Thread.Sleep(1);
				}
				if(!WaitUntilStable(sw, TimeoutSeconds)) {
					return false;
				}
			}
			return GetFrame() == target;
		}

		private static bool WaitUntilStable(Stopwatch sw, int timeoutSeconds)
		{
			uint frame = GetFrame();
			Stopwatch stable = Stopwatch.StartNew();
			while(stable.ElapsedMilliseconds < 60) {
				if(sw.Elapsed.TotalSeconds > timeoutSeconds) {
					return false;
				}
				Thread.Sleep(5);
				uint now = GetFrame();
				if(now != frame || !EmuApi.IsPaused()) {
					frame = now;
					stable.Restart();
				}
			}
			return true;
		}

		/// <summary>
		/// Hashes the emulated NES state that HD rendering must never alter: CPU RAM,
		/// nametables, OAM, palette RAM and PPU memory. Two runs with identical input are
		/// expected to produce the same hash at the same frame.
		/// </summary>
		private static string ComputeStateHash()
		{
			try {
				uint hash = 2166136261;
				void Mix(MemoryType type)
				{
					byte[] data = DebugApi.GetMemoryState(type);
					foreach(byte value in data) {
						hash = (hash ^ value) * 16777619;
					}
				}
				Mix(MemoryType.NesInternalRam);
				Mix(MemoryType.NesNametableRam);
				Mix(MemoryType.NesSpriteRam);
				Mix(MemoryType.NesPaletteRam);
				Mix(MemoryType.NesPpuMemory);
				return hash.ToString("X8");
			} catch(Exception ex) {
				return "unavailable(" + ex.GetType().Name + ")";
			}
		}

		private bool CompareStateHashes()
		{
			if(_hdStateHashes.Count == 0 || _hdStateHashes.Count != _originalStateHashes.Count) {
				_log("Gameplay state comparison: unavailable (no checkpoints).");
				return false;
			}
			for(int i = 0; i < _hdStateHashes.Count; i++) {
				if(_hdStateHashes[i] != _originalStateHashes[i]) {
					_log($"Gameplay state comparison: DIFFERENT at checkpoint {i + 1} (HD {_hdStateHashes[i]}, original {_originalStateHashes[i]}).");
					return false;
				}
			}
			_log("Gameplay state comparison: IDENTICAL at every checkpoint.");
			return true;
		}

		/// <summary>The screenshot is the real output: with an active HD pack it is N times larger than 256x240</summary>
		private static FrameInfo ReadPngSize(string path)
		{
			if(!File.Exists(path)) {
				return new FrameInfo();
			}
			byte[] header = new byte[24];
			using(FileStream file = File.OpenRead(path)) {
				if(file.Read(header, 0, 24) != 24) {
					return new FrameInfo();
				}
			}
			return new FrameInfo() {
				Width = (uint)(header[16] << 24 | header[17] << 16 | header[18] << 8 | header[19]),
				Height = (uint)(header[20] << 24 | header[21] << 16 | header[22] << 8 | header[23])
			};
		}

		private static uint GetFrame()
		{
			return EmuApi.GetTimingInfo(CpuType.Nes).FrameCount;
		}

		private string WriteHtml(bool hdLoaded, FrameInfo hdSize, FrameInfo originalSize)
		{
			string E(string s) => WebUtility.HtmlEncode(s);
			StringBuilder sb = new();
			sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
			sb.AppendLine($"<title>HD Studio — verification</title>");
			sb.AppendLine("<style>:root{--bg:#f6f7f5;--card:#fff;--fg:#1d211c;--muted:#5d665a;--line:#dfe3dc;--ok:#2f8f2a;--bad:#b23b2e}");
			sb.AppendLine("@media (prefers-color-scheme:dark){:root{--bg:#141713;--card:#1d211c;--fg:#e7ebe4;--muted:#9aa595;--line:#2f352d;--ok:#62c45a;--bad:#ef7a6c}}");
			sb.AppendLine("body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,Segoe UI,sans-serif}main{max-width:1200px;margin:0 auto;padding:24px 16px}");
			sb.AppendLine(".pair{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-bottom:24px}figure{margin:0;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:8px}");
			sb.AppendLine("figure img{width:100%;height:auto;display:block;border-radius:4px}figcaption{color:var(--muted);font-size:13px;margin-top:4px}.ok{color:var(--ok)}.bad{color:var(--bad)}");
			sb.AppendLine("@media (max-width:640px){.pair{grid-template-columns:1fr}}</style></head><body><main>");
			sb.AppendLine($"<h1>HD pack verification — {E(Path.GetFileName(_romPath))}</h1>");
			sb.AppendLine(hdLoaded
				? $"<p class=\"ok\"><b>✔ The HD pack is active.</b> The game is rendered at {hdSize.Width}×{hdSize.Height} instead of {originalSize.Width}×{originalSize.Height}.</p>"
				: "<p class=\"bad\"><b>✘ The HD pack did not load.</b> The game was rendered at its original resolution.</p>");
			sb.AppendLine("<p>The same game frames, without the pack (left) and with the pack (right). Click an image to see it at full size.</p>");

			foreach(uint frame in _checkpoints) {
				string original = $"frame_{frame:00000}_original.png";
				string hd = $"frame_{frame:00000}_hd.png";
				sb.AppendLine("<div class=\"pair\">");
				sb.AppendLine($"<figure><a href=\"{original}\"><img src=\"{original}\" style=\"image-rendering:pixelated\" alt=\"Original, frame {frame}\"></a><figcaption>Original · frame {frame}</figcaption></figure>");
				sb.AppendLine($"<figure><a href=\"{hd}\"><img src=\"{hd}\" alt=\"HD, frame {frame}\"></a><figcaption>With HD pack · frame {frame}</figcaption></figure>");
				sb.AppendLine("</div>");
			}
			sb.AppendLine("</main></body></html>");

			string path = Path.Combine(_verifyFolder, "verify.html");
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
			return path;
		}
	}
}
