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
	///   --reportonly      Only regenerate the report from an existing capture
	///   --generate        Fully automatic: plays the game with the built-in explorer (unless a movie/script is given)
	///                     and adds every CHR-ROM tile of the cartridge to the pack, even the ones never displayed
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
			string outFolder = opt.OutFolder ?? Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(opt.RomPath));
			Directory.CreateDirectory(outFolder);

			if(opt.Verify) {
				int frames = opt.FramesSpecified ? opt.Frames : 1200;
				return new HdStudioVerify(opt.RomPath, opt.MoviePath, opt.LuaScripts, opt.Generate, outFolder, frames, opt.Shots, Log).Run();
			}

			if(!opt.ReportOnly) {
				int result = Capture(opt, outFolder);
				if(result != 0) {
					return result;
				}
			}

			try {
				uint[] palette = ConfigManager.Config.Nes.UserPalette;
				HdStudioReport report = new(outFolder, palette, Log);
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

		private static void StartRecording(string outFolder, Options opt)
		{
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
			Log("Usage: Mesen --hdstudio game.nes [movie.mmo|movie.bk2|movie.fm2] [script.lua] [--scale=4] [--filter=xbrz|hqx|scale2x|prescale] [--frames=18000] [--out=folder] [--timeout=1800] [--generate] [--reportonly] [--verify [--shots=4]]");
		}

		private static void Log(string message)
		{
			Console.WriteLine(message);
		}

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
