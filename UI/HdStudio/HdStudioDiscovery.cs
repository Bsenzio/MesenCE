using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.HdStudio
{
	internal static class HdStudioDiscoveryModes
	{
		public const string Quick = "quick";
		public const string Deep = "deep";
		public const string Replay = "replay";
		public const string Assisted = "assisted";
		public const string Live = "live";

		public static string DisplayName(string mode)
		{
			return mode switch {
				Quick => "Quick scan",
				Deep => "Deep scan",
				Replay => "Replay capture",
				Assisted => "Assisted exploration",
				Live => "Live capture",
				_ => mode
			};
		}
	}

	internal sealed class HdStudioDiscoveryOptions
	{
		public string Mode = HdStudioDiscoveryModes.Deep;
		/// <summary>Frames to add in this run. A resumed session adds this to its existing budget.</summary>
		public int Frames = 18000;
		public int ChunkFrames = 1800;
		public int Seed = 20261124;
		public int SaturationChunks = 3;
		public int TimeoutSeconds = 900;
		public int Scale = 4;
		public string MoviePath = "";
		public string LuaScriptPath = "";
		public bool ObserveBackgrounds = true;
		public bool Restart;
	}

	internal sealed class HdStudioDiscoverySnapshot
	{
		public int Chunk;
		public int Seed;
		public int Frames;
		public long FramesCaptured;
		public int Assets;
		public int Metasprites;
		public int BackgroundBlocks;
		public int SemanticObjects;
		public int AnimationFrames;
		public int UniqueTilePaletteCombos;
		public int ChrTilesSeen;
		public int ChrTilesInPack;
		public int NewAssets;
		public int NewPatterns;
		public int NewSeenCombos;
		public double Seconds;
		public string CapturedAtUtc = "";
	}

	internal sealed class HdStudioDiscoverySession
	{
		public const string FileName = "discovery-session.json";
		public const string SchemaVersion = "1.0.0";
		public string ProjectName = "";
		public string RomSha1 = "";
		public string Mode = HdStudioDiscoveryModes.Deep;
		public int RequestedFrames;
		public int ChunkFrames;
		public int BaseSeed;
		public long FramesCaptured;
		public int Chunks;
		public string StartedAtUtc = "";
		public string UpdatedAtUtc = "";
		public string StopReason = "";
		public bool Complete;
		public int LastAssets;
		public int LastPatterns;
		public int LastSeenCombos;
		public readonly List<HdStudioDiscoverySnapshot> Snapshots = new();

		public static string GetPath(string packFolder)
		{
			return Path.Combine(packFolder, HdStudioReport.StudioFolderName, FileName);
		}

		public static HdStudioDiscoverySession? Load(string packFolder)
		{
			string path = GetPath(packFolder);
			if(!File.Exists(path)) {
				return null;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				JsonElement root = doc.RootElement;
				HdStudioDiscoverySession session = new() {
					ProjectName = GetString(root, "projectName"),
					RomSha1 = GetString(root, "romSha1"),
					Mode = GetString(root, "mode", HdStudioDiscoveryModes.Deep),
					RequestedFrames = GetInt(root, "requestedFrames"),
					ChunkFrames = GetInt(root, "chunkFrames"),
					BaseSeed = GetInt(root, "baseSeed"),
					FramesCaptured = GetLong(root, "framesCaptured"),
					Chunks = GetInt(root, "chunks"),
					StartedAtUtc = GetString(root, "startedAtUtc"),
					UpdatedAtUtc = GetString(root, "updatedAtUtc"),
					StopReason = GetString(root, "stopReason"),
					Complete = root.TryGetProperty("complete", out JsonElement complete) && complete.ValueKind == JsonValueKind.True,
					LastAssets = GetInt(root, "lastAssets"),
					LastPatterns = GetInt(root, "lastPatterns"),
					LastSeenCombos = GetInt(root, "lastSeenCombos")
				};
				if(root.TryGetProperty("snapshots", out JsonElement snapshots) && snapshots.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement item in snapshots.EnumerateArray()) {
						session.Snapshots.Add(new HdStudioDiscoverySnapshot() {
							Chunk = GetInt(item, "chunk"),
							Seed = GetInt(item, "seed"),
							Frames = GetInt(item, "frames"),
							FramesCaptured = GetLong(item, "framesCaptured"),
							Assets = GetInt(item, "assets"),
							Metasprites = GetInt(item, "metasprites"),
							BackgroundBlocks = GetInt(item, "backgroundBlocks"),
							SemanticObjects = GetInt(item, "semanticObjects"),
							AnimationFrames = GetInt(item, "animationFrames"),
							UniqueTilePaletteCombos = GetInt(item, "uniqueTilePaletteCombos"),
							ChrTilesSeen = GetInt(item, "chrTilesSeen"),
							ChrTilesInPack = GetInt(item, "chrTilesInPack"),
							NewAssets = GetInt(item, "newAssets"),
							NewPatterns = GetInt(item, "newPatterns"),
							NewSeenCombos = GetInt(item, "newSeenCombos"),
							Seconds = GetDouble(item, "seconds"),
							CapturedAtUtc = GetString(item, "capturedAtUtc")
						});
					}
				}
				return session;
			} catch {
				return null;
			}
		}

		public void Save(string packFolder)
		{
			UpdatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
			string path = GetPath(packFolder);
			string folder = Path.GetDirectoryName(path) ?? ".";
			Directory.CreateDirectory(folder);
			string temp = path + ".tmp";
			using(FileStream stream = File.Create(temp))
			using(Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true })) {
				json.WriteStartObject();
				json.WriteString("schemaVersion", SchemaVersion);
				json.WriteString("generatedBy", "RetroArcDo HD Studio");
				json.WriteString("projectName", ProjectName);
				json.WriteString("romSha1", RomSha1);
				json.WriteString("mode", Mode);
				json.WriteNumber("requestedFrames", RequestedFrames);
				json.WriteNumber("chunkFrames", ChunkFrames);
				json.WriteNumber("baseSeed", BaseSeed);
				json.WriteNumber("framesCaptured", FramesCaptured);
				json.WriteNumber("chunks", Chunks);
				json.WriteString("startedAtUtc", StartedAtUtc);
				json.WriteString("updatedAtUtc", UpdatedAtUtc);
				json.WriteString("stopReason", StopReason);
				json.WriteBoolean("complete", Complete);
				json.WriteNumber("lastAssets", LastAssets);
				json.WriteNumber("lastPatterns", LastPatterns);
				json.WriteNumber("lastSeenCombos", LastSeenCombos);
				json.WriteStartArray("snapshots");
				foreach(HdStudioDiscoverySnapshot snapshot in Snapshots) {
					json.WriteStartObject();
					json.WriteNumber("chunk", snapshot.Chunk);
					json.WriteNumber("seed", snapshot.Seed);
					json.WriteNumber("frames", snapshot.Frames);
					json.WriteNumber("framesCaptured", snapshot.FramesCaptured);
					json.WriteNumber("assets", snapshot.Assets);
					json.WriteNumber("metasprites", snapshot.Metasprites);
					json.WriteNumber("backgroundBlocks", snapshot.BackgroundBlocks);
					json.WriteNumber("semanticObjects", snapshot.SemanticObjects);
					json.WriteNumber("animationFrames", snapshot.AnimationFrames);
					json.WriteNumber("uniqueTilePaletteCombos", snapshot.UniqueTilePaletteCombos);
					json.WriteNumber("chrTilesSeen", snapshot.ChrTilesSeen);
					json.WriteNumber("chrTilesInPack", snapshot.ChrTilesInPack);
					json.WriteNumber("newAssets", snapshot.NewAssets);
					json.WriteNumber("newPatterns", snapshot.NewPatterns);
					json.WriteNumber("newSeenCombos", snapshot.NewSeenCombos);
					json.WriteNumber("seconds", snapshot.Seconds);
					json.WriteString("capturedAtUtc", snapshot.CapturedAtUtc);
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			File.Move(temp, path, true);
		}

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		private static int GetInt(JsonElement element, string name, int fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
		}

		private static long GetLong(JsonElement element, string name, long fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : fallback;
		}

		private static double GetDouble(JsonElement element, string name, double fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : fallback;
		}
	}

	internal sealed class HdStudioDiscoveryProgress
	{
		public int Chunk;
		public long FramesCaptured;
		public int RequestedFrames;
		public int Assets;
		public int Patterns;
		public int SeenCombos;
		public int NewAssets;
		public int NewPatterns;
		public string Message = "";
		public string StopReason = "";
	}

	internal sealed class HdStudioDiscoveryResult
	{
		public bool Success;
		public string StopReason = "";
		public string SessionPath = "";
		public HdStudioDiscoverySession Session = new();
		public readonly List<string> Errors = new();
	}

	/// <summary>
	/// Progressive, resumable discovery.
	///
	/// Each chunk is a complete headless capture in its own process, so the pack is merged
	/// safely and approved artwork is never lost. The session file tracks frames, seeds, asset
	/// growth and the stopping reason. Saturation means "no measured growth for N chunks", not
	/// proof of completeness. Live capture is intentionally not claimed: it requires an
	/// interactive Mesen session and is outside this headless scheduler.
	/// </summary>
	internal static class HdStudioDiscovery
	{
		public static async Task<HdStudioDiscoveryResult> RunAsync(HdStudioProject project, HdStudioDiscoveryOptions options, Action<string>? log, IProgress<HdStudioDiscoveryProgress>? progress, CancellationToken cancellation)
		{
			HdStudioDiscoveryResult result = new();
			string mode = options.Mode;
			if(mode == HdStudioDiscoveryModes.Live) {
				result.Errors.Add("Live capture needs an interactive Mesen window and is not part of the headless discovery scheduler.");
				return result;
			}
			if(mode == HdStudioDiscoveryModes.Replay && string.IsNullOrWhiteSpace(options.MoviePath)) {
				result.Errors.Add("Replay capture needs a movie file (.fm2, .mmo or .bk2).");
				return result;
			}
			if(mode == HdStudioDiscoveryModes.Assisted && string.IsNullOrWhiteSpace(options.LuaScriptPath) && string.IsNullOrWhiteSpace(options.MoviePath)) {
				result.Errors.Add("Assisted exploration needs a Lua script or a movie file.");
				return result;
			}
			if(options.Frames <= 0) {
				result.Errors.Add("The discovery budget must be greater than zero frames.");
				return result;
			}

			string sessionPath = HdStudioDiscoverySession.GetPath(project.PackFolder);
			HdStudioDiscoverySession? session = options.Restart ? null : HdStudioDiscoverySession.Load(project.PackFolder);
			if(session == null || !string.Equals(session.RomSha1, project.RomSha1, StringComparison.OrdinalIgnoreCase)) {
				session = new HdStudioDiscoverySession() {
					ProjectName = project.ProjectName,
					RomSha1 = project.RomSha1,
					Mode = mode,
					RequestedFrames = options.Frames,
					ChunkFrames = Math.Max(300, options.ChunkFrames),
					BaseSeed = options.Seed,
					StartedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
					StopReason = ""
				};
			} else {
				session.Mode = mode;
				session.RequestedFrames = Math.Max(session.RequestedFrames, (int)Math.Min(int.MaxValue, session.FramesCaptured) + options.Frames);
				session.ChunkFrames = Math.Max(300, options.ChunkFrames);
				session.StopReason = "";
				session.Complete = false;
				log?.Invoke($"Discovery: resuming session at {session.FramesCaptured:N0} frames; budget {session.RequestedFrames:N0} frames.");
			}
			session.Save(project.PackFolder);
			result.SessionPath = sessionPath;

			while(session.FramesCaptured < session.RequestedFrames) {
				if(cancellation.IsCancellationRequested) {
					session.StopReason = "cancelled";
					break;
				}

				int chunkIndex = session.Chunks + 1;
				int remaining = (int)Math.Min(int.MaxValue, session.RequestedFrames - session.FramesCaptured);
				int chunkFrames = Math.Min(Math.Max(300, session.ChunkFrames), remaining);
				if(mode == HdStudioDiscoveryModes.Quick) {
					chunkFrames = remaining;
				}
				int seed = session.BaseSeed + (chunkIndex - 1) * 7919;
				string[] args = BuildCaptureArgs(project, options, mode, chunkFrames, seed);

				log?.Invoke($"Discovery chunk {chunkIndex}: {HdStudioDiscoveryModes.DisplayName(mode)}, {chunkFrames:N0} frames, seed {seed}.");
				Stopwatch watch = Stopwatch.StartNew();
				HdStudioProcessResult capture = await RunHeadlessAsync(args, log, cancellation);
				watch.Stop();
				if(capture.Cancelled) {
					session.StopReason = "cancelled";
					break;
				}
				if(capture.ExitCode != 0) {
					result.Errors.Add("Discovery capture failed with exit code " + capture.ExitCode + ". See the operation log.");
					session.StopReason = "error";
					break;
				}

				HdStudioProcessResult assets = await RunHeadlessAsync(new[] {
					"--hdstudio", project.RomPath, "--assets", "--out=" + project.PackFolder
				}, log, cancellation);
				if(assets.ExitCode != 0) {
					result.Errors.Add("Asset export failed with exit code " + assets.ExitCode + ".");
					session.StopReason = "error";
					break;
				}

				HdStudioDiscoverySnapshot snapshot = ReadSnapshot(project.PackFolder, chunkIndex, seed, chunkFrames, watch.Elapsed.TotalSeconds);
				snapshot.FramesCaptured = session.FramesCaptured + chunkFrames;
				snapshot.NewAssets = Math.Max(0, snapshot.Assets - session.LastAssets);
				snapshot.NewPatterns = Math.Max(0, snapshot.ChrTilesSeen - session.LastPatterns);
				snapshot.NewSeenCombos = Math.Max(0, snapshot.UniqueTilePaletteCombos - session.LastSeenCombos);
				session.Snapshots.Add(snapshot);
				session.Chunks = chunkIndex;
				session.FramesCaptured = snapshot.FramesCaptured;
				session.LastAssets = snapshot.Assets;
				session.LastPatterns = snapshot.ChrTilesSeen;
				session.LastSeenCombos = snapshot.UniqueTilePaletteCombos;
				session.Save(project.PackFolder);

				HdStudioDiscoveryProgress report = new() {
					Chunk = chunkIndex,
					FramesCaptured = session.FramesCaptured,
					RequestedFrames = session.RequestedFrames,
					Assets = snapshot.Assets,
					Patterns = snapshot.ChrTilesSeen,
					SeenCombos = snapshot.UniqueTilePaletteCombos,
					NewAssets = snapshot.NewAssets,
					NewPatterns = snapshot.NewPatterns,
					Message = $"Chunk {chunkIndex}: {snapshot.Assets:N0} assets, {snapshot.ChrTilesSeen:N0} observed patterns, {snapshot.UniqueTilePaletteCombos:N0} tile+palette combinations.",
					StopReason = ""
				};
				progress?.Report(report);
				log?.Invoke(report.Message);

				if(mode == HdStudioDiscoveryModes.Replay || mode == HdStudioDiscoveryModes.Assisted) {
					if(chunkFrames >= remaining || capture.FramesReported < chunkFrames / 2) {
						session.StopReason = capture.FramesReported < chunkFrames / 2 ? "input-ended" : "budget";
						break;
					}
				}

				if(mode == HdStudioDiscoveryModes.Deep || mode == HdStudioDiscoveryModes.Quick) {
					int noGrowth = 0;
					foreach(HdStudioDiscoverySnapshot item in session.Snapshots.AsEnumerable().Reverse().Take(Math.Max(1, options.SaturationChunks))) {
						if(item.NewAssets == 0 && item.NewPatterns == 0 && item.NewSeenCombos == 0) {
							noGrowth++;
						}
					}
					if(noGrowth >= Math.Max(1, options.SaturationChunks) && session.FramesCaptured >= Math.Min(session.RequestedFrames, 2 * session.ChunkFrames)) {
						session.StopReason = "saturation";
						break;
					}
				}
			}

			if(string.IsNullOrWhiteSpace(session.StopReason)) {
				session.StopReason = session.FramesCaptured >= session.RequestedFrames ? "budget" : "stopped";
			}
			session.Complete = session.StopReason != "error" && session.StopReason != "cancelled";
			session.Save(project.PackFolder);
			result.Session = session;
			result.StopReason = session.StopReason;
			result.Success = session.StopReason != "error" && session.StopReason != "cancelled";
			log?.Invoke($"Discovery stopped: {session.StopReason} after {session.FramesCaptured:N0} frames in {session.Chunks} chunk(s). Saturation is not proof of completeness.");
			progress?.Report(new HdStudioDiscoveryProgress() {
				Chunk = session.Chunks,
				FramesCaptured = session.FramesCaptured,
				RequestedFrames = session.RequestedFrames,
				Assets = session.LastAssets,
				Patterns = session.LastPatterns,
				SeenCombos = session.LastSeenCombos,
				Message = "Discovery stopped: " + session.StopReason,
				StopReason = session.StopReason
			});
			return result;
		}

		private static string[] BuildCaptureArgs(HdStudioProject project, HdStudioDiscoveryOptions options, string mode, int frames, int seed)
		{
			List<string> args = new() {
				"--hdstudio", project.RomPath, "--generate",
				"--frames=" + frames.ToString(CultureInfo.InvariantCulture),
				"--seed=" + seed.ToString(CultureInfo.InvariantCulture),
				"--scale=" + Math.Clamp(options.Scale > 0 ? options.Scale : project.OutputScale, 1, 10).ToString(CultureInfo.InvariantCulture),
				"--filter=prescale",
				"--timeout=" + Math.Max(30, options.TimeoutSeconds).ToString(CultureInfo.InvariantCulture),
				"--out=" + project.PackFolder
			};
			if(options.ObserveBackgrounds) {
				args.Add("--bgscan");
			}
			if(mode == HdStudioDiscoveryModes.Replay || mode == HdStudioDiscoveryModes.Assisted) {
				if(!string.IsNullOrWhiteSpace(options.MoviePath)) {
					args.Add(options.MoviePath);
				} else if(!string.IsNullOrWhiteSpace(options.LuaScriptPath)) {
					args.Add(options.LuaScriptPath);
				}
			}
			return args.ToArray();
		}

		private static HdStudioDiscoverySnapshot ReadSnapshot(string packFolder, int chunk, int seed, int frames, double seconds)
		{
			HdStudioDiscoverySnapshot snapshot = new() {
				Chunk = chunk,
				Seed = seed,
				Frames = frames,
				Seconds = Math.Round(seconds, 2),
				CapturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
			};
			try {
				string reportPath = Path.Combine(packFolder, HdStudioReport.StudioFolderName, "report.json");
				if(File.Exists(reportPath)) {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(reportPath));
					if(doc.RootElement.TryGetProperty("capture", out JsonElement capture)) {
						snapshot.UniqueTilePaletteCombos = GetInt(capture, "uniqueTilePaletteCombos");
						snapshot.ChrTilesSeen = GetInt(capture, "chrTilesSeen");
						snapshot.ChrTilesInPack = GetInt(capture, "chrTilesInPack");
						if(GetInt(capture, "frames") > 0) {
							//report.json contains the cumulative capture frame count; the chunk budget is
							//authoritative for the session, so keep the scheduler's value.
						}
					}
				}
			} catch {
			}
			try {
				HdStudioCatalog catalog = HdStudioCatalog.Load(packFolder);
				snapshot.Assets = catalog.Assets.Count;
				snapshot.Metasprites = catalog.Assets.Count(a => a.Kind == "metasprite");
				snapshot.BackgroundBlocks = catalog.Assets.Count(a => a.Kind == "backgroundBlock" || a.Kind == "backgroundTile");
				snapshot.SemanticObjects = catalog.Assets.Count(a => a.Kind == "semanticObject");
				snapshot.AnimationFrames = catalog.Assets.Count(a => a.Kind == "animationFrame");
			} catch {
			}
			return snapshot;
		}

		private static int GetInt(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;
		}

		internal sealed class HdStudioProcessResult
		{
			public int ExitCode;
			public bool Cancelled;
			public int FramesReported;
			public readonly List<string> Output = new();
		}

		internal static async Task<HdStudioProcessResult> RunHeadlessAsync(string[] args, Action<string>? log, CancellationToken cancellation)
		{
			HdStudioProcessResult result = new();
			ProcessStartInfo info = new(Program.ExePath) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = Program.OriginalFolder
			};
			foreach(string arg in args) {
				info.ArgumentList.Add(arg);
			}
			using Process process = new() { StartInfo = info, EnableRaisingEvents = true };
			process.OutputDataReceived += (s, e) => {
				if(string.IsNullOrWhiteSpace(e.Data)) {
					return;
				}
				lock(result.Output) {
					result.Output.Add(e.Data);
				}
				int frameMarker = e.Data.IndexOf("frames captured", StringComparison.OrdinalIgnoreCase);
				if(frameMarker > 0) {
					string prefix = e.Data.Substring(0, frameMarker).Replace(",", "").Trim();
					if(int.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames)) {
						result.FramesReported = Math.Max(result.FramesReported, frames);
					}
				}
				log?.Invoke(e.Data);
			};
			process.ErrorDataReceived += (s, e) => {
				if(!string.IsNullOrWhiteSpace(e.Data)) {
					log?.Invoke(e.Data);
				}
			};
			try {
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				while(!process.WaitForExit(200)) {
					if(cancellation.IsCancellationRequested) {
						try {
							process.Kill(true);
						} catch { }
						result.Cancelled = true;
						break;
					}
				}
				if(result.Cancelled) {
					return result;
				}
				process.WaitForExit();
				result.ExitCode = process.ExitCode;
			} catch(Exception ex) {
				log?.Invoke("Discovery process error: " + ex.Message);
				result.ExitCode = -1;
			}
			if(result.FramesReported == 0) {
				//Capture messages are also written to the log; estimate from the chunk argument.
				foreach(string arg in args) {
					if(arg.StartsWith("--frames=", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg.Substring(9), out int frames)) {
						result.FramesReported = frames;
					}
				}
			}
			return result;
		}
	}
}
