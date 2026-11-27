using System;
using System.IO;
using System.Text.Json;
using Mesen.Config;
using Mesen.Interop;
using Mesen.Utilities;

namespace Mesen.HdStudio
{
	internal static class HdStudioSamplingModes
	{
		public const string Fast = "fast";
		public const string Minimal = "minimal";
		public const string Efficient = "efficient";
		public const string Balanced = "balanced";
		public const string Exhaustive = "exhaustive";
		public const string SingleFrame = "single-frame";

		public static string DisplayName(string mode)
		{
			return mode switch {
				Fast => "Fast (every 12 frames, backgrounds every 240)",
				Minimal => "Minimal sampling (every 30 frames, backgrounds every 600)",
				Efficient => "Efficient (every 4 frames, backgrounds every 60)",
				Exhaustive => "Exhaustive (every displayed frame)",
				SingleFrame => "Single-frame inspection",
				_ => "Balanced (every 2 frames, backgrounds every 30)"
			};
		}

		public static (int Sprites, int Background, bool Single) Values(string mode)
		{
			return mode switch {
				Fast => (12, 240, false),
				Minimal => (30, 600, false),
				Efficient => (4, 60, false),
				Exhaustive => (1, 1, false),
				SingleFrame => (1, 1, true),
				_ => (2, 30, false)
			};
		}

		public static string Normalize(string? mode)
		{
			string value = (mode ?? "").Trim().ToLowerInvariant();
			return value switch {
				Fast or Minimal or Efficient or Exhaustive or SingleFrame => value,
				_ => Balanced
			};
		}
	}

	/// <summary>
	/// Controls the live capture script inside the running Mesen emulator.
	///
	/// Unlike headless discovery, no second process and no movie/Lua input is involved:
	/// the user plays normally and the script samples the real displayed frames. Pause
	/// stops sampling (the game keeps running) and writes a still frame for hardware-aware
	/// picking; Stop flushes the observations for the normal asset exporter.
	/// </summary>
	internal sealed class HdStudioLiveSession
	{
		public int ScriptId = -1;
		public bool Paused;
		public bool Stopped;
		public int LastCommandId;
		public string Folder = "";
		public string ControlPath = "";
		public string StatusPath = "";
		public string PausePath = "";
		public string SamplingMode = HdStudioSamplingModes.Balanced;
		public DateTime StartedAtUtc = DateTime.UtcNow;
	}

	internal static class HdStudioLiveCapture
	{
		public static string GetFolder(string packFolder)
		{
			return Path.Combine(packFolder, HdStudioReport.StudioFolderName, "live");
		}

		public static HdStudioLiveSession Start(HdStudioProject project, string samplingMode, Action<string> log)
		{
			string mode = HdStudioSamplingModes.Normalize(samplingMode);
			(int sampleEvery, int backgroundEvery, bool singleFrame) = HdStudioSamplingModes.Values(mode);
			if(!EmuApi.IsRunning()) {
				//Start the project's ROM in the integrated emulator. This is the same ROM the
				//project was created from and the original file is opened read-only.
				if(!EmuApi.LoadRom(project.RomPath)) {
					throw new InvalidOperationException("The ROM could not be opened in the emulator.");
				}
			}

			RomInfo info = EmuApi.GetRomInfo();
			if(info.ConsoleType != ConsoleType.Nes) {
				throw new InvalidOperationException("Live capture currently supports NES games only.");
			}
			string running = ((ResourcePath)info.RomPath).Path;
			if(!string.IsNullOrWhiteSpace(running) && File.Exists(project.RomPath) &&
				!string.Equals(Path.GetFullPath(running), Path.GetFullPath(project.RomPath), StringComparison.OrdinalIgnoreCase)) {
				log("Live capture: the running ROM differs from the project ROM. The capture is stored in this project; reopen the project ROM for a matching pack.");
			}

			string folder = GetFolder(project.PackFolder);
			Directory.CreateDirectory(folder);
			string control = Path.Combine(folder, "control.txt");
			File.WriteAllText(control, "0|resume\n");

			string script = HdStudioCapture.GetBuiltInExplorer("live_capture.lua")
				.Replace("__OUTPUT__", folder.Replace("\\", "/"))
				.Replace("__CONTROL__", control.Replace("\\", "/"))
				.Replace("__SAMPLE_EVERY__", Math.Clamp(sampleEvery, 1, 30).ToString())
				.Replace("__BG_SAMPLE_EVERY__", Math.Clamp(backgroundEvery, 1, 600).ToString())
				.Replace("__CAPTURE_MODE__", mode)
				.Replace("__SINGLE_FRAME__", singleFrame ? "true" : "false")
				.Replace("__MAX_PATTERNS__", ReadPatternBudget().ToString());

			string scriptPath = Path.Combine(folder, "live_capture.lua");
			File.WriteAllText(scriptPath, script);
			if(!ConfigManager.Config.Debug.ScriptWindow.AllowIoOsAccess) {
				ConfigManager.Config.Debug.ScriptWindow.AllowIoOsAccess = true;
				ConfigManager.Config.ApplyConfig();
			}
			int id = DebugApi.LoadScript("live_capture.lua", scriptPath, script);
			if(id < 0) {
				throw new InvalidOperationException("The live capture script could not be loaded. Open the game first, then press Record.");
			}

			HdStudioLiveSession session = new() {
				ScriptId = id,
				Folder = folder,
				ControlPath = control,
				StatusPath = Path.Combine(folder, "live-status.json"),
				PausePath = Path.Combine(folder, "pause-frame.json"),
				SamplingMode = mode
			};
			log($"Live capture started ({HdStudioSamplingModes.DisplayName(mode)}, script {id}); output {folder}.");
			return session;
		}

		private static int ReadPatternBudget()
		{
			//A safety budget, not a semantic/presentation limit. It is configurable and the
			//script reports when it is reached, so no truncation is silent.
			string? value = Environment.GetEnvironmentVariable("HDSTUDIO_MAX_LIVE_PATTERNS");
			return int.TryParse(value, out int configured) && configured >= 40000 ? configured : 250000;
		}

		public static void SendCommand(HdStudioLiveSession session, string command)
		{
			session.LastCommandId++;
			File.WriteAllText(session.ControlPath, session.LastCommandId + "|" + command + "\n");
			if(command == "pause") {
				session.Paused = true;
			} else if(command == "resume") {
				session.Paused = false;
			} else if(command == "stop") {
				session.Stopped = true;
			}
		}

		public static void Remove(HdStudioLiveSession session)
		{
			try {
				if(session.ScriptId >= 0) {
					DebugApi.RemoveScript(session.ScriptId);
				}
			} catch {
			}
			session.ScriptId = -1;
		}

		internal sealed class LiveStatus
		{
			public int Frame;
			public int Samples;
			public int Patterns;
			public int SpriteOccurrences;
			public int BackgroundBlocks;
			public int GameplayFrames;
			public bool Paused;
			public bool Stopped;
			public string ScreenHash = "";
			public string CaptureMode = "";
			public int DisplaySpriteSamples;
			public int OamFallbackSamples;
			public long SampleMicros;
			public bool PatternLimitReached;
			public bool SingleFrame;
		}

		public static LiveStatus? ReadStatus(HdStudioLiveSession session)
		{
			if(!File.Exists(session.StatusPath)) {
				return null;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(session.StatusPath));
				JsonElement root = doc.RootElement;
				return new LiveStatus() {
					Frame = GetInt(root, "frame"),
					Samples = GetInt(root, "samples"),
					Patterns = GetInt(root, "patterns"),
					SpriteOccurrences = GetInt(root, "spriteOccurrences"),
					BackgroundBlocks = GetInt(root, "backgroundBlocks"),
					GameplayFrames = GetInt(root, "gameplayFrames"),
					Paused = GetBool(root, "paused"),
					Stopped = GetBool(root, "stopped"),
					ScreenHash = GetString(root, "screenHash"),
					CaptureMode = GetString(root, "captureMode"),
					DisplaySpriteSamples = GetInt(root, "displaySpriteSamples"),
					OamFallbackSamples = GetInt(root, "oamFallbackSamples"),
					SampleMicros = GetLong(root, "sampleMicros"),
					PatternLimitReached = GetBool(root, "patternLimitReached"),
					SingleFrame = GetBool(root, "singleFrame")
				};
			} catch {
				return null;
			}
		}

		private static int GetInt(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;
		}

		private static bool GetBool(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}

		private static long GetLong(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : 0;
		}

		private static string GetString(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
		}
	}
}
