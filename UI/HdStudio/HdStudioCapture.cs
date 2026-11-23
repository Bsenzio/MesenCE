using Mesen.Config;
using Mesen.Config.Shortcuts;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mesen.HdStudio
{
	/// <summary>
	/// HD Studio, stage 1: headless HD pack capture + analysis report.
	///
	/// Usage:
	///   Mesen --hdstudio game.nes [movie.mmo|movie.bk2|movie.fm2] [script.lua] [options]
	///
	/// Options:
	///   --scale=N         HD scale (default 4)
	///   --filter=NAME     Upscaling filter for the generated tiles: xbrz (default), hqx, scale2x, prescale
	///   --frames=N        Frames to run when no movie is given (default 18000 = 5 minutes)
	///   --out=FOLDER      Output folder (default: Mesen's HdPacks folder / ROM name, so the pack is used automatically)
	///   --timeout=SEC     Maximum real time in seconds (default 1800)
	///   --log=FILE        Also write the console messages to this file (Mesen is a windowed program)
	///   --reportonly      Only regenerate the report from an existing capture
	///   --generate        Fully automatic: plays the game with the built-in explorer (unless a movie/script is given)
	///                     and adds every CHR-ROM tile of the cartridge to the pack, even the ones never displayed
	///   --levels          Visit every level of the game using the level select cheats of the built-in cheat database
	///                     (one run per level, frames per level = --frames, default 900)
	///   --sweep           With --levels: try to reveal each whole level by moving the player position in RAM
	///                     (experimental: it works on some games, the level runner is used when it fails)
	///   --maps            Build full level maps with the game's own loader (needs --disasm; SMB3)
	///   --playerforms     Capture every SMB3 player form (Small, Super, Fire, Raccoon, Frog, Tanuki,
	///                     Hammer and Tanuki Statue) with named poses and exact sprite palettes
	///   --demo            Record a short real-Mesen GIF with the HD pack active (SMB3; for the
	///                     repository demo, not a full capture)
	///   --verify          Check the HD pack instead of capturing: plays the game with and without the pack and
	///                     saves screenshots of the same frames side by side (HdStudio/verify/verify.html)
	///   --shots=N         Number of screenshots for --verify (default 4, spread over --frames, default 1200 here)
	/// </summary>
	internal static class HdStudioCapture
	{
		private class Options
		{
			public string? RomPath;
			public string? MoviePath;
			public List<string> LuaScripts = new();
			public uint Scale = 4;
			public ScaleFilterType Filter = ScaleFilterType.xBRZ;
			public int Frames = 18000;
			public string? OutFolder;
			public int TimeoutSeconds = 1800;
			public bool ReportOnly;
			public bool Verify;
			public bool Generate;
			public bool Levels;
			public bool Sweep;
			public bool Maps;
			public bool PlayerForms;
			public bool Demo;
			public int SpriteId = -1;
			public string? DisasmPath;
			public bool FramesSpecified;
			public int Shots = 4;
			public List<string> Errors = new();
		}

		public static bool IsHdStudio(string[] args)
		{
			return args.Any(arg => NormalizeSwitch(arg) == "hdstudio");
		}

		public static int Run(string[] args)
		{
			AttachToParentConsole();

			Options opt = ParseArgs(args);
			if(opt.Errors.Count > 0 || opt.RomPath == null) {
				foreach(string error in opt.Errors) {
					Log("Error: " + error);
				}
				if(opt.RomPath == null) {
					Log("Error: no ROM file was specified.");
				}
				PrintUsage();
				return -1;
			}

			ConfigManager.DisableSaveSettings = true;
			string packName = Path.GetFileNameWithoutExtension(opt.RomPath).Trim();
			if(packName.Length == 0) {
				packName = "HD Studio game";
			}
			string outFolder = opt.OutFolder ?? Path.Combine(ConfigManager.HdPackFolder, packName);
			Directory.CreateDirectory(outFolder);

			if(opt.SpriteId >= 0) {
				//Internal mode for the sprite atlas: one object per run, no HD recording/report.
				return HdStudioSpriteAtlas.RunOne(opt.RomPath!, opt.SpriteId, outFolder, Log);
			}

			if(opt.PlayerForms) {
				//SMB3-specific: force every player suit and animation frame through the game's drawing code.
				return HdStudioPlayerAtlas.RunAll(opt.RomPath!, outFolder, ConfigManager.Config.Nes.UserPalette, Log);
			}

			if(opt.Demo) {
				int frames = opt.FramesSpecified ? opt.Frames : 600;
				return HdStudioDemo.Run(opt.RomPath!, outFolder, frames, null, Log);
			}

			if(opt.Verify) {
				int frames = opt.FramesSpecified ? opt.Frames : 1200;
				return new HdStudioVerify(opt.RomPath, opt.MoviePath, opt.LuaScripts, opt.Generate, outFolder, frames, opt.Shots, Log).Run();
			}

			if(opt.Maps) {
				//Standalone mode: run the game's own level loader for each level, paint the maps and
				//write the exact tile usage. It does not capture an HD pack, so it can be repeated
				//without touching the existing pack.
				if(string.IsNullOrWhiteSpace(opt.DisasmPath)) {
					Log("Error: --maps needs the game's disassembly, e.g. --disasm=D:\\MesenHighWork\\disasm\\smb3-master");
					return -1;
				}
				uint[] mapPalette = ConfigManager.Config.Nes.UserPalette;
				int mapResult = HdStudioLevelMaps.Run(opt.RomPath!, opt.DisasmPath!, outFolder, mapPalette, Log);
				if(mapResult != 0) {
					return mapResult;
				}
				try {
					HdStudioReport mapsReport = new(outFolder, mapPalette, Log, opt.DisasmPath);
					string reportPath = mapsReport.Generate(opt.RomPath!);
					Log("Report: " + reportPath);
				} catch(Exception ex) {
					Log("Error while generating the report: " + ex.Message);
					return -2;
				}
				return 0;
			}

			if(opt.Levels) {
				//A level sweep always includes every CHR-ROM tile: the levels that could not be visited
				//still end up in the pack (with the palettes found in the ROM)
				opt.Generate = true;
			}

			if(!opt.ReportOnly) {
				int result = opt.Levels ? CaptureLevels(opt, outFolder) : Capture(opt, outFolder);
				if(result != 0) {
					return result;
				}
			}

			try {
				uint[] palette = ConfigManager.Config.Nes.UserPalette;
				HdStudioReport report = new(outFolder, palette, Log, opt.DisasmPath);
				string reportPath = report.Generate(opt.RomPath);
				Log("Report: " + reportPath);
			} catch(Exception ex) {
				Log("Error while generating the report: " + ex.Message);
				return -2;
			}

			return 0;
		}

		private static int Capture(Options opt, string outFolder)
		{
			EmuApi.InitDll();
			ConfigManager.Config.ApplyConfig();

			EmuApi.InitializeEmu(ConfigManager.HomeFolder, IntPtr.Zero, IntPtr.Zero, true, true, true, true);
			EmuApi.Pause();
			ConfigApi.SetEmulationFlag(EmulationFlags.ConsoleMode, true);

			Log($"Loading {opt.RomPath}");
			if(!EmuApi.LoadRom(opt.RomPath!, string.Empty)) {
				Log("Error: the ROM could not be loaded.");
				EmuApi.Release();
				return -1;
			}

			if(EmuApi.GetRomInfo().ConsoleType != ConsoleType.Nes) {
				Log("Error: HD packs are only supported for NES games.");
				EmuApi.Stop();
				EmuApi.Release();
				return -1;
			}

			if(opt.LuaScripts.Count > 0) {
				DebugWorkspaceManager.Load();
				foreach(string luaScript in opt.LuaScripts) {
					string script = File.ReadAllText(luaScript);
					string path = (Path.GetDirectoryName(luaScript) ?? Program.OriginalFolder) + Path.DirectorySeparatorChar;
					_scriptIds.Add(DebugApi.LoadScript(luaScript, path, script));
					Log($"Lua script loaded: {luaScript}");
				}
			} else if(opt.Generate && opt.MoviePath == null) {
				//Generate mode without a movie/script: the built-in deep explorer plays the whole game with savestates
				DebugWorkspaceManager.Load();
				_scriptIds.Add(DebugApi.LoadScript("explore_deep.lua", ConfigManager.HomeFolder + Path.DirectorySeparatorChar, GetBuiltInExplorer("explore_deep.lua")));
				Log("Built-in deep explorer loaded (explores the game automatically with savestates)");
			}

			ConfigApi.SetEmulationFlag(EmulationFlags.MaximumSpeed, true);

			if(opt.MoviePath != null) {
				Log($"Playing movie {opt.MoviePath}");
				Log(CheckMovieRom(opt.RomPath!, opt.MoviePath));
				RecordApi.MoviePlay(opt.MoviePath);
			}

			EmuApi.Resume();

			//Start recording once the movie (if any) has reset/started the game,
			//otherwise the power cycle done by the movie player would discard the recorder.
			Stopwatch sw = Stopwatch.StartNew();
			while(GetFrameCount() < 2 && sw.ElapsedMilliseconds < 10000) {
				Thread.Sleep(5);
			}

			//Start the recorder while paused (at a frame boundary): starting it in the middle of
			//a frame leaves stale sprite data in the builder and produces tiles with wrong palettes
			EmuApi.Pause();
			while(!EmuApi.IsPaused() && sw.ElapsedMilliseconds < 15000) {
				Thread.Sleep(1);
			}
			StartRecording(outFolder, opt);
			uint startFrame = GetFrameCount();
			EmuApi.Resume();
			Log($"Recording HD pack at {opt.Scale}x ({opt.Filter}) into {outFolder}");

			bool movieStarted = opt.MoviePath != null && RecordApi.MoviePlaying();
			if(opt.MoviePath != null && !movieStarted) {
				Log("Warning: the movie is not playing (incompatible file?), running for the frame limit instead.");
			}

			//Count the emulated frames from the differences between two polls: the frame counter goes back
			//when a savestate is loaded (the deep explorer does it every few seconds)
			long frames = 0;
			uint lastFrameCount = startFrame;
			int lastProgress = -1;
			while(sw.Elapsed.TotalSeconds < opt.TimeoutSeconds) {
				Thread.Sleep(50);
				if(!EmuApi.IsRunning()) {
					break;
				}

				uint frameCount = GetFrameCount();
				frames += frameCount >= lastFrameCount ? frameCount - lastFrameCount : 1;
				lastFrameCount = frameCount;
				if(movieStarted) {
					if(!RecordApi.MoviePlaying()) {
						break;
					}
				} else if(frames >= opt.Frames) {
					break;
				}

				int progress = (int)(sw.Elapsed.TotalSeconds / 30);
				if(progress != lastProgress) {
					lastProgress = progress;
					Log($"  {frames:N0} frames captured...");
					PrintScriptLogs();
				}
			}

			if(sw.Elapsed.TotalSeconds >= opt.TimeoutSeconds) {
				Log("Warning: timeout reached, saving what was captured so far.");
			}

			EmuApi.Pause();
			PrintScriptLogs();
			Log($"Capture finished: {frames:N0} frames. Saving HD pack...");
			EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.StopRecordHdPack });

			if(RecordApi.MoviePlaying()) {
				RecordApi.MovieStop();
			}
			EmuApi.Stop();
			EmuApi.Release();
			return 0;
		}

		/// <summary>
		/// Level sweep: the cheat database says how to start the game on each level (e.g. Batman's
		/// "Start on stage 2-1" = 0400:06). The game is reset with that cheat active, played for a while,
		/// and everything is merged into the same HD pack. Without cheats nothing can be done here, so
		/// the normal exploration runs instead.
		/// </summary>
		private static int CaptureLevels(Options opt, string outFolder)
		{
			EmuApi.InitDll();
			ConfigManager.Config.ApplyConfig();

			EmuApi.InitializeEmu(ConfigManager.HomeFolder, IntPtr.Zero, IntPtr.Zero, true, true, true, true);
			EmuApi.Pause();
			ConfigApi.SetEmulationFlag(EmulationFlags.ConsoleMode, true);

			Log($"Loading {opt.RomPath}");
			if(!EmuApi.LoadRom(opt.RomPath!, string.Empty)) {
				Log("Error: the ROM could not be loaded.");
				EmuApi.Release();
				return -1;
			}

			if(EmuApi.GetRomInfo().ConsoleType != ConsoleType.Nes) {
				Log("Error: HD packs are only supported for NES games.");
				EmuApi.Stop();
				EmuApi.Release();
				return -1;
			}

			List<HdStudioCheats.Level> levels = HdStudioCheats.FindLevelCheats(opt.RomPath!, Log);
			if(levels.Count == 0) {
				//No level select: explore the game with the deep explorer, in this same emulator instance
				//(releasing and initializing again a second time crashes)
				Log("Level sweep: no level select cheats, exploring the game instead.");
				DebugWorkspaceManager.Load();
				_scriptIds.Add(DebugApi.LoadScript("explore_deep.lua", ConfigManager.HomeFolder + Path.DirectorySeparatorChar, GetBuiltInExplorer("explore_deep.lua")));
				ConfigApi.SetEmulationFlag(EmulationFlags.MaximumSpeed, true);

				Stopwatch fsw = Stopwatch.StartNew();
				EmuApi.Resume();
				while(GetFrameCount() < 2 && fsw.ElapsedMilliseconds < 10000) {
					Thread.Sleep(5);
				}
				PauseAndWait(fsw);
				StartRecording(outFolder, opt);
				EmuApi.Resume();
				Log($"Recording HD pack at {opt.Scale}x ({opt.Filter}) into {outFolder}");

				long explored = RunForFrames(opt.Frames, fsw, opt.TimeoutSeconds);
				PauseAndWait(fsw);
				PrintScriptLogs();
				Log($"Capture finished: {explored:N0} frames. Saving HD pack...");
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.StopRecordHdPack });
				EmuApi.Stop();
				EmuApi.Release();
				return 0;
			}

			//The first level always exists, even if the database only lists the ones after it
			levels.Insert(0, new HdStudioCheats.Level() { Description = "Start of the game (no cheat)" });

			int framesPerLevel = opt.FramesSpecified ? opt.Frames : 900;
			if(opt.LuaScripts.Count == 0 && opt.MoviePath == null) {
				DebugWorkspaceManager.Load();
				if(opt.Sweep) {
					//Experimental: find the player position in RAM and walk it through the whole level
					//so the game scrolls and draws every column (see explore_sweep.lua)
					_scriptIds.Add(DebugApi.LoadScript("explore_sweep.lua", ConfigManager.HomeFolder + Path.DirectorySeparatorChar, GetBuiltInExplorer("explore_sweep.lua")));
					Log("Camera sweep loaded (experimental: moves the player in RAM to reveal each level)");
				} else {
					//Level runner: runs right, attacks and jumps, so each level is seen as far as possible.
					//No savestates: they would be invalid after every reset.
					_scriptIds.Add(DebugApi.LoadScript("explore_level.lua", ConfigManager.HomeFolder + Path.DirectorySeparatorChar, GetBuiltInExplorer("explore_level.lua")));
					Log("Level runner loaded (runs through each level)");
				}
			} else if(opt.MoviePath != null) {
				Log("Note: movies are ignored with --levels (each level restarts the game).");
			}

			ConfigApi.SetEmulationFlag(EmulationFlags.MaximumSpeed, true);

			//Start the game once and the recorder at a frame boundary (as in a normal capture)
			EmuApi.Resume();
			Stopwatch sw = Stopwatch.StartNew();
			while(GetFrameCount() < 2 && sw.ElapsedMilliseconds < 10000) {
				Thread.Sleep(5);
			}
			EmuApi.Pause();
			while(!EmuApi.IsPaused() && sw.ElapsedMilliseconds < 15000) {
				Thread.Sleep(1);
			}
			StartRecording(outFolder, opt);
			Log($"Recording HD pack at {opt.Scale}x ({opt.Filter}) into {outFolder}");
			Log($"Level sweep: {levels.Count} levels x {framesPerLevel} frames (use --frames=N to change it)");

			string levelsFolder = Path.Combine(outFolder, HdStudioReport.StudioFolderName, "levels");
			Directory.CreateDirectory(levelsFolder);

			int captured = 0;
			long totalFrames = 0;
			for(int i = 0; i < levels.Count; i++) {
				if(sw.Elapsed.TotalSeconds >= opt.TimeoutSeconds) {
					Log("Warning: timeout reached, the remaining levels are skipped (increase --timeout).");
					break;
				}

				HdStudioCheats.Level level = levels[i];
				PauseAndWait(sw);

				//Apply the cheat, then reset: the game reads its level number while starting
				EmuApi.ClearCheats();
				if(level.HasCheat) {
					InteropCheatCode[] codes = HdStudioCheats.ToInteropCodes(level);
					EmuApi.SetCheats(codes, (uint)codes.Length);
				}
				Log($"[{i + 1}/{levels.Count}] {level.Description}" + (level.HasCheat ? $"  ({level.CodesText})" : ""));
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.Reset });
				EmuApi.Resume();

				//One screenshot early (the game on that level) and one at the end (how far the runner got)
				int earlyFrames = Math.Max(120, framesPerLevel / 4);
				totalFrames += RunForFrames(earlyFrames, sw, opt.TimeoutSeconds);
				PauseAndWait(sw);
				PrintScriptLogs();
				SaveLevelScreenshot(levelsFolder, i, level, "start");
				EmuApi.Resume();

				totalFrames += RunForFrames(framesPerLevel - earlyFrames, sw, opt.TimeoutSeconds);
				captured++;

				PauseAndWait(sw);
				PrintScriptLogs();
				SaveLevelScreenshot(levelsFolder, i, level, "end");
			}

			EmuApi.ClearCheats();
			EmuApi.Pause();
			PrintScriptLogs();
			Log($"Level sweep finished: {captured} levels, {totalFrames:N0} frames. Saving HD pack...");
			EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.StopRecordHdPack });

			EmuApi.Stop();
			EmuApi.Release();
			return 0;
		}

		/// <summary>Runs the emulator for a given number of frames, counting differences (a reset goes back to 0)</summary>
		private static long RunForFrames(int frames, Stopwatch sw, int timeoutSeconds)
		{
			long counted = 0;
			uint last = GetFrameCount();
			while(counted < frames && sw.Elapsed.TotalSeconds < timeoutSeconds) {
				Thread.Sleep(20);
				if(!EmuApi.IsRunning()) {
					break;
				}

				uint current = GetFrameCount();
				counted += current >= last ? current - last : 1;
				last = current;
			}
			return counted;
		}

		private static void PauseAndWait(Stopwatch sw)
		{
			EmuApi.Pause();
			while(!EmuApi.IsPaused() && sw.ElapsedMilliseconds < 15000) {
				Thread.Sleep(1);
			}
		}

		private static void SaveLevelScreenshot(string folder, int index, HdStudioCheats.Level level, string suffix)
		{
			try {
				string name = new string(level.Description.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
				string file = Path.Combine(folder, $"{index:00}_{name}_{suffix}.png");
				if(HdStudioSaveScreenshot(file)) {
					Log("  screenshot: " + file);
				}
			} catch {
				//A screenshot is only evidence for the user, never a reason to fail
			}
		}

		[DllImport(EmuApi.DllName)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool HdStudioSaveScreenshot([MarshalAs(UnmanagedType.LPUTF8Str)] string filename);

		private static void StartRecording(string outFolder, Options opt)		{
			bool hasChrRam = EmuApi.GetGameMemorySize(MemoryType.NesChrRam) > 0;
			HdPackBuilderOptions options = new() {
				SaveFolder = outFolder,
				FilterType = opt.Filter,
				Scale = opt.Scale,
				ChrRamBankSize = hasChrRam ? ConfigManager.Config.HdPackBuilder.ChrRamBankSize : 0x1000,
				UseLargeSprites = false,
				SortByUsageFrequency = true,
				GroupBlankTiles = true,
				IgnoreOverscan = false,
				IncludeAllChrTiles = opt.Generate
			};

			IntPtr optionsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(options));
			try {
				Marshal.StructureToPtr(options, optionsPtr, false);
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams() {
					Shortcut = EmulatorShortcut.StartRecordHdPack,
					ParamPtr = optionsPtr
				});
			} finally {
				Marshal.DestroyStructure<HdPackBuilderOptions>(optionsPtr);
				Marshal.FreeHGlobal(optionsPtr);
			}
		}

		/// <summary>
		/// Compares the ROM with the one the movie was recorded with. BizHawk stores the SHA-1 of the whole file
		/// (header included) and FCEUX the MD5 of the ROM without its header; both forms are accepted so a good
		/// dump is never reported as a mismatch. A different ROM version usually makes the movie desync.
		/// </summary>
		public static string CheckMovieRom(string romPath, string moviePath)
		{
			try {
				byte[] rom = File.ReadAllBytes(romPath);
				int headerSize = rom.Length >= 16 && rom[0] == 'N' && rom[1] == 'E' && rom[2] == 'S' && rom[3] == 0x1A ? 16 + ((rom[6] & 0x04) != 0 ? 512 : 0) : 0;
				ReadOnlySpan<byte> romData = rom.AsSpan(headerSize);

				string? expected = null;
				string actual, alternate;
				string ext = Path.GetExtension(moviePath).ToLowerInvariant();
				if(ext == ".bk2") {
					using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(moviePath);
					var entry = zip.GetEntry("Header.txt");
					if(entry != null) {
						using StreamReader reader = new(entry.Open());
						string? line;
						while((line = reader.ReadLine()) != null) {
							if(line.StartsWith("SHA1 ")) {
								expected = line.Substring(5).Trim().ToUpperInvariant();
							}
						}
					}
					//BizHawk hashes the whole file; older tools used the headerless ROM
					actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(rom));
					alternate = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(romData));
				} else if(ext == ".fm2") {
					foreach(string line in File.ReadLines(moviePath)) {
						if(line.StartsWith("|")) {
							break;
						}
						if(line.StartsWith("romChecksum base64:")) {
							expected = Convert.ToHexString(Convert.FromBase64String(line.Substring(19).Trim()));
						}
					}
					actual = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(romData));
					alternate = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(rom));
				} else {
					return "ROM check: not available for this movie format";
				}

				if(expected == null || expected.Trim('0').Length == 0) {
					return "ROM check: the movie does not say which ROM it was recorded with";
				}
				return expected == actual || expected == alternate
					? "ROM check: OK, this is the ROM the movie was recorded with"
					: $"WARNING: this ROM is not the one the movie was recorded with (expected {expected}, got {actual}). The movie will probably desync: find the matching ROM version.";
			} catch(Exception ex) {
				return "ROM check failed: " + ex.Message;
			}
		}

		private static readonly List<int> _scriptIds = new();
		private static readonly Dictionary<int, string> _printedScriptLogs = new();

		/// <summary>Shows the new lines of the Lua scripts' logs (explorer statistics, script errors) in the console</summary>
		private static void PrintScriptLogs()
		{
			foreach(int scriptId in _scriptIds) {
				string log = DebugApi.GetScriptLog(scriptId);
				string previous = _printedScriptLogs.GetValueOrDefault(scriptId, "");
				string newText = log.StartsWith(previous) ? log.Substring(previous.Length) : log;
				foreach(string line in newText.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
					Log("  [lua] " + line.TrimEnd('\r'));
				}
				_printedScriptLogs[scriptId] = log;
			}
		}

		private static uint GetFrameCount()
		{
			return EmuApi.GetTimingInfo(CpuType.Nes).FrameCount;
		}

		/// <summary>Scripts from UI/HdStudio/Scripts, embedded in Mesen.exe (explore.lua or explore_deep.lua)</summary>
		public static string GetBuiltInExplorer(string name = "explore.lua")
		{
			using Stream? stream = typeof(HdStudioCapture).Assembly.GetManifestResourceStream("Mesen.HdStudio." + name);
			if(stream == null) {
				throw new Exception("Built-in explorer script not found");
			}
			using StreamReader reader = new(stream);
			return reader.ReadToEnd();
		}

		private static Options ParseArgs(string[] args)
		{
			Options opt = new();
			foreach(string arg in args) {
				if(arg.StartsWith("-") || (arg.StartsWith("/") && !File.Exists(arg))) {
					string sw = NormalizeSwitch(arg);
					string[] kv = sw.Split('=', 2);
					string name = kv[0].ToLowerInvariant();
					string value = kv.Length > 1 ? kv[1].Trim('"') : "";

					switch(name) {
						case "hdstudio": break;
						case "reportonly": opt.ReportOnly = true; break;
						case "verify": opt.Verify = true; break;
						case "generate": opt.Generate = true; break;
						case "levels": opt.Levels = true; break;
						case "sweep": opt.Sweep = true; break;
						case "maps": opt.Maps = true; break;
						case "playerforms": opt.PlayerForms = true; break;
						case "demo": opt.Demo = true; break;
						case "spriteid":
							if(!int.TryParse(value, out opt.SpriteId) || opt.SpriteId < 0 || opt.SpriteId > 255) {
								opt.Errors.Add("invalid sprite id (0-255): " + value);
								opt.SpriteId = -1;
							}
							break;
						case "disasm":
							opt.DisasmPath = Path.GetFullPath(value, Program.OriginalFolder);
							break;
						case "shots":
							if(!int.TryParse(value, out opt.Shots) || opt.Shots < 1 || opt.Shots > 20) {
								opt.Errors.Add("invalid screenshot count (1-20): " + value);
							}
							break;
						case "scale":
							if(uint.TryParse(value, out uint scale) && scale >= 1 && scale <= 10) {
								opt.Scale = scale;
							} else {
								opt.Errors.Add("invalid scale (1-10): " + value);
							}
							break;
						case "filter":
							switch(value.ToLowerInvariant()) {
								case "xbrz": opt.Filter = ScaleFilterType.xBRZ; break;
								case "hqx": opt.Filter = ScaleFilterType.HQX; break;
								case "scale2x": opt.Filter = ScaleFilterType.Scale2x; break;
								case "prescale": case "none": opt.Filter = ScaleFilterType.Prescale; break;
								default: opt.Errors.Add("unknown filter: " + value); break;
							}
							break;
						case "frames":
							if(!int.TryParse(value, out opt.Frames) || opt.Frames <= 0) {
								opt.Errors.Add("invalid frame count: " + value);
							}
							opt.FramesSpecified = true;
							break;
						case "timeout":
							if(!int.TryParse(value, out opt.TimeoutSeconds) || opt.TimeoutSeconds <= 0) {
								opt.Errors.Add("invalid timeout: " + value);
							}
							break;
						case "out":
							opt.OutFolder = Path.GetFullPath(value, Program.OriginalFolder);
							break;
						case "log":
							//Mesen is a windowed program: when the output is redirected (e.g. to a file or a pipe)
							//the console messages are lost, so they are also written to this file
							_logFile = Path.GetFullPath(value, Program.OriginalFolder);
							Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
							File.WriteAllText(_logFile, "");
							break;
						default:
							opt.Errors.Add("unknown option: " + arg);
							break;
					}
					continue;
				}

				string path = Path.GetFullPath(arg, Program.OriginalFolder);
				if(!File.Exists(path)) {
					opt.Errors.Add("file not found: " + arg);
					continue;
				}

				switch(Path.GetExtension(path).ToLowerInvariant()) {
					case ".lua": opt.LuaScripts.Add(path); break;
					case ".mmo": case ".bk2": case ".fm2": opt.MoviePath = path; break;
					default: opt.RomPath = path; break;
				}
			}

			if(!IsValidScale(opt.Filter, opt.Scale)) {
				opt.Errors.Add($"scale {opt.Scale}x is not available for the {opt.Filter} filter (xbrz: 2-6, hqx/scale2x: 2-4, prescale: 1-10)");
			}
			return opt;
		}

		private static bool IsValidScale(ScaleFilterType filter, uint scale)
		{
			return filter switch {
				ScaleFilterType.xBRZ => scale >= 2 && scale <= 6,
				ScaleFilterType.HQX or ScaleFilterType.Scale2x => scale >= 2 && scale <= 4,
				_ => scale >= 1 && scale <= 10
			};
		}

		private static string NormalizeSwitch(string arg)
		{
			arg = arg.Trim();
			if(arg.StartsWith("--")) {
				return arg.Substring(2);
			} else if(arg.StartsWith("-") || arg.StartsWith("/")) {
				return arg.Substring(1);
			}
			return arg;
		}

		private static void PrintUsage()
		{
			Log("");
			Log("Usage: Mesen --hdstudio game.nes [movie.mmo|movie.bk2|movie.fm2] [script.lua] [--scale=4] [--filter=xbrz|hqx|scale2x|prescale] [--frames=18000] [--out=folder] [--timeout=1800] [--log=file.txt] [--generate] [--levels [--sweep]] [--disasm=folder] [--maps] [--playerforms] [--demo] [--reportonly] [--verify [--shots=4]]");
			Log("  --maps with --disasm=FOLDER: full level maps as PNG + exact per-level tile usage (uses the game's own level generator).");
			Log("  --playerforms: SMB3 (USA) only: all eight player forms and their named poses, exact sprite palettes.");
			Log("  --demo: records a short real-Mesen GIF with the HD pack active (SMB3 1-1).");
		}

		private static void Log(string message)
		{
			Console.WriteLine(message);
			if(_logFile != null) {
				try {
					File.AppendAllText(_logFile, message + Environment.NewLine);
				} catch {
				}
			}
		}

		private static string? _logFile;

		[DllImport("kernel32.dll")]
		private static extern bool AttachConsole(int processId);

		private static void AttachToParentConsole()
		{
			//Mesen is a GUI application: attach to the console that launched it so the progress is visible
			if(OperatingSystem.IsWindows() && AttachConsole(-1)) {
				StreamWriter writer = new(Console.OpenStandardOutput()) { AutoFlush = true };
				Console.SetOut(writer);
				Console.WriteLine();
			}
		}
	}
}
