using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.HdStudio
{
	internal sealed class HdStudioArchaeologyBatchResult
	{
		public int Attempted;
		public int Completed;
		public int Unsupported;
		public int Errors;
		public int Skipped;
		public int Duplicates;
		public int Cancelled;
		public int Budget;
		public string SummaryPath = "";
		public string CsvPath = "";
		public string ResumeCommand = "";
		public readonly List<HdStudioArchaeologyBatchRow> Rows = new();
	}

	internal sealed class HdStudioArchaeologyBatchRow
	{
		public string Path = "";
		public string FileName = "";
		public string Sha256 = "";
		public string Status = "";
		public string StopReason = "";
		public int Mapper;
		public string ChrMode = "";
		public int UniquePatterns;
		public int ObservedPatterns;
		public int Assets;
		public int Gaps;
		public int GapsResolved;
		public int GapsPartial;
		public int GapsUnresolved;
		public int Compositions;
		public int AnimationSequences;
		public double Seconds;
		public string OutputFolder = "";
		public string Error = "";
		public string EngineFingerprint = "";
		public string RunFingerprint = "";
	}

	/// <summary>
	/// Resumable, parallel static-first batch. Each ROM gets an isolated output folder
	/// and a small batch-result.json so an interrupted run can resume without repeating
	/// compatible work. The full evidence graph and catalog stay in the per-ROM folder.
	/// </summary>
	internal static class HdStudioArchaeologyBatch
	{
		public static HdStudioArchaeologyBatchResult Run(string rootFolder, string outRoot, HdStudioArchaeologyOptions options,
			int parallel, Action<string>? log, CancellationToken cancellation)
		{
			HdStudioArchaeologyBatchResult result = new();
			rootFolder = Path.GetFullPath(rootFolder);
			outRoot = Path.GetFullPath(outRoot);
			Directory.CreateDirectory(outRoot);
			string romsRoot = Path.Combine(outRoot, "roms");
			Directory.CreateDirectory(romsRoot);

			List<string> files = Directory.EnumerateFiles(rootFolder, "*.nes", SearchOption.AllDirectories)
				.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
			log?.Invoke("Archaeology batch: " + files.Count + " .nes file(s) below " + rootFolder + "; output " + outRoot);
			if(files.Count == 0) {
				WriteSummary(outRoot, result, options, parallel, rootFolder);
				return result;
			}

			ConcurrentBag<HdStudioArchaeologyBatchRow> rows = new();
			ConcurrentDictionary<string, string> hashOwner = new(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> evidenceMap = LoadEvidenceMap(options.EvidenceMapPath);
			ParallelOptions parallelOptions = new() {
				MaxDegreeOfParallelism = Math.Max(1, parallel),
				CancellationToken = cancellation
			};
			try {
				Parallel.ForEach(files, parallelOptions, file => {
					if(cancellation.IsCancellationRequested) {
						return;
					}
					HdStudioArchaeologyBatchRow row = new() {
						Path = file,
						FileName = Path.GetFileName(file)
					};
					string safe = SafeName(Path.GetFileNameWithoutExtension(file));
					string folder = Path.Combine(romsRoot, HdStudioEvidenceGraph.StableHash(file).Substring(0, 12) + "_" + safe);
					row.OutputFolder = folder;
					Directory.CreateDirectory(folder);
					string logPath = Path.Combine(folder, "archaeology.log");
					string resultPath = Path.Combine(folder, "batch-result.json");
					using StreamWriter logWriter = new(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
					void RowLog(string message)
					{
						lock(logWriter) {
							logWriter.WriteLine(message);
						}
					}

					try {
						HdStudioRomImage rom = HdStudioRomImage.Load(file);
						row.Sha256 = rom.Sha256;
						row.Mapper = rom.Mapper;
						row.ChrMode = rom.ChrMode;
						if(!rom.Valid) {
							row.Status = "unsupported";
							row.StopReason = "unsupported";
							row.Error = rom.Error;
							WriteRowResult(resultPath, row, options);
							rows.Add(row);
							return;
						}
						HdStudioArchaeologyOptions rowOptions = options.Clone();
						rowOptions.EvidencePackFolder = ResolveEvidenceFolder(evidenceMap, file, rom);
						bool fresh = !File.Exists(resultPath);
						if(!fresh && IsCachedResult(resultPath, rom.Sha256, rowOptions, out _)) {
							row.Status = "skipped";
							row.StopReason = "cached";
							row.Sha256 = rom.Sha256;
							row.Mapper = rom.Mapper;
							row.ChrMode = rom.ChrMode;
							rows.Add(row);
							return;
						}
						if(!hashOwner.TryAdd(rom.Sha256, file)) {
							row.Status = "skipped";
							row.StopReason = "duplicate";
							row.Error = "duplicate of " + hashOwner[rom.Sha256];
							WriteRowResult(resultPath, row, rowOptions);
							rows.Add(row);
							return;
						}

						HdStudioArchaeologyResult archaeology = HdStudioArchaeology.Run(file, folder, rowOptions, DefaultPalette(), RowLog, null, cancellation);
						row.Status = archaeology.Success ? "complete" : archaeology.StopReason;
						row.StopReason = archaeology.StopReason;
						row.UniquePatterns = archaeology.UniquePatterns;
						row.ObservedPatterns = archaeology.RuntimeObservedPatterns;
						row.Assets = archaeology.AssetsExposed;
						row.Gaps = archaeology.Gaps;
						row.GapsResolved = archaeology.GapsResolved;
						row.GapsPartial = archaeology.GapsPartiallyResolved;
						row.GapsUnresolved = archaeology.GapsUnresolved;
						row.Compositions = archaeology.Compositions;
						row.AnimationSequences = archaeology.AnimationSequences;
						row.Seconds = archaeology.Seconds;
						row.EngineFingerprint = archaeology.EngineFingerprint;
						row.RunFingerprint = archaeology.RunFingerprint;
						row.Error = string.Join("; ", archaeology.Errors);
						WriteRowResult(resultPath, row, rowOptions);
						rows.Add(row);
					} catch(Exception ex) {
						row.Status = "error";
						row.StopReason = "error";
						row.Error = ex.Message;
						try {
							WriteRowResult(resultPath, row, options);
						} catch {
						}
						rows.Add(row);
					}
				});
			} catch(OperationCanceledException) {
				result.Cancelled++;
			}

			result.Rows.AddRange(rows.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase));
			result.Attempted = result.Rows.Count(r => r.Status != "skipped");
			result.Completed = result.Rows.Count(r => r.Status == "complete");
			result.Unsupported = result.Rows.Count(r => r.Status == "unsupported");
			result.Errors = result.Rows.Count(r => r.Status == "error");
			result.Budget = result.Rows.Count(r => r.Status == "budget");
			result.Cancelled += result.Rows.Count(r => r.Status == "cancelled");
			result.Skipped = result.Rows.Count(r => r.Status == "skipped");
			result.Duplicates = result.Rows.Count(r => r.StopReason == "duplicate");
			WriteSummary(outRoot, result, options, parallel, rootFolder);
			log?.Invoke("Archaeology batch summary: " + result.SummaryPath);
			return result;
		}

		private static void WriteSummary(string outRoot, HdStudioArchaeologyBatchResult result, HdStudioArchaeologyOptions options, int parallel, string root)
		{
			result.SummaryPath = Path.Combine(outRoot, "archaeology-batch.json");
			result.CsvPath = Path.Combine(outRoot, "archaeology-batch.csv");
			string temp = result.SummaryPath + ".tmp";
			using(FileStream stream = File.Create(temp)) {
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
				json.WriteStartObject();
				json.WriteString("schemaVersion", "1.0.0");
				json.WriteString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
				json.WriteString("engineVersion", HdStudioEvidenceGraph.EngineVersion);
				json.WriteString("engineFingerprint", HdStudioEvidenceGraph.ComputeEngineFingerprint());
				json.WriteString("mode", options.Mode);
				json.WriteString("root", root);
				json.WriteNumber("parallel", parallel);
				json.WriteNumber("attempted", result.Attempted);
				json.WriteNumber("completed", result.Completed);
				json.WriteNumber("unsupported", result.Unsupported);
				json.WriteNumber("errors", result.Errors);
				json.WriteNumber("budget", result.Budget);
				json.WriteNumber("cancelled", result.Cancelled);
				json.WriteNumber("skipped", result.Skipped);
				json.WriteNumber("duplicates", result.Duplicates);
				json.WriteNumber("gapsResolved", result.Rows.Sum(r => r.GapsResolved));
				json.WriteNumber("gapsPartial", result.Rows.Sum(r => r.GapsPartial));
				json.WriteNumber("gapsUnresolved", result.Rows.Sum(r => r.GapsUnresolved));
				json.WriteNumber("compositions", result.Rows.Sum(r => r.Compositions));
				json.WriteNumber("animationSequences", result.Rows.Sum(r => r.AnimationSequences));
				json.WriteStartArray("rows");
				foreach(HdStudioArchaeologyBatchRow row in result.Rows) {
					json.WriteStartObject();
					json.WriteString("path", row.Path);
					json.WriteString("fileName", row.FileName);
					json.WriteString("sha256", row.Sha256);
					json.WriteString("status", row.Status);
					json.WriteString("stopReason", row.StopReason);
					json.WriteNumber("mapper", row.Mapper);
					json.WriteString("chrMode", row.ChrMode);
					json.WriteNumber("uniquePatterns", row.UniquePatterns);
					json.WriteNumber("observedPatterns", row.ObservedPatterns);
					json.WriteNumber("assets", row.Assets);
					json.WriteNumber("gaps", row.Gaps);
					json.WriteNumber("gapsResolved", row.GapsResolved);
					json.WriteNumber("gapsPartial", row.GapsPartial);
					json.WriteNumber("gapsUnresolved", row.GapsUnresolved);
					json.WriteNumber("compositions", row.Compositions);
					json.WriteNumber("animationSequences", row.AnimationSequences);
					json.WriteNumber("seconds", row.Seconds);
					json.WriteString("outputFolder", row.OutputFolder);
					json.WriteString("engineFingerprint", row.EngineFingerprint);
					json.WriteString("runFingerprint", row.RunFingerprint);
					json.WriteString("error", row.Error);
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			if(File.Exists(result.SummaryPath)) {
				File.Replace(temp, result.SummaryPath, result.SummaryPath + ".prev", true);
			} else {
				File.Move(temp, result.SummaryPath);
			}
			StringBuilder csv = new();
			csv.AppendLine("path,fileName,sha256,status,stopReason,mapper,chrMode,uniquePatterns,observedPatterns,assets,gaps,gapsResolved,gapsPartial,gapsUnresolved,compositions,animationSequences,seconds,outputFolder,error");
			foreach(HdStudioArchaeologyBatchRow row in result.Rows) {
				csv.AppendLine(string.Join(",", new[] {
					Csv(row.Path), Csv(row.FileName), Csv(row.Sha256), Csv(row.Status), Csv(row.StopReason),
					row.Mapper.ToString(CultureInfo.InvariantCulture), Csv(row.ChrMode),
					row.UniquePatterns.ToString(CultureInfo.InvariantCulture), row.ObservedPatterns.ToString(CultureInfo.InvariantCulture),
					row.Assets.ToString(CultureInfo.InvariantCulture), row.Gaps.ToString(CultureInfo.InvariantCulture),
					row.GapsResolved.ToString(CultureInfo.InvariantCulture), row.GapsPartial.ToString(CultureInfo.InvariantCulture),
					row.GapsUnresolved.ToString(CultureInfo.InvariantCulture), row.Compositions.ToString(CultureInfo.InvariantCulture),
					row.AnimationSequences.ToString(CultureInfo.InvariantCulture),
					row.Seconds.ToString("0.0", CultureInfo.InvariantCulture), Csv(row.OutputFolder), Csv(row.Error)
				}));
			}
			File.WriteAllText(result.CsvPath, csv.ToString(), new UTF8Encoding(false));
			result.ResumeCommand = "Mesen.exe --hdstudio --arch-batch=\"" + root + "\" --arch-out-root=\"" + outRoot + "\" --arch-mode=" + options.Mode;
			File.WriteAllText(Path.Combine(outRoot, "resume.txt"), result.ResumeCommand + Environment.NewLine, new UTF8Encoding(false));
		}

		private static bool IsCachedResult(string path, string sha256, HdStudioArchaeologyOptions options, out HdStudioArchaeologyBatchRow row)
		{
			row = new HdStudioArchaeologyBatchRow();
			if(options.Force) {
				return false;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				JsonElement root = doc.RootElement;
				string cachedSha = HdStudioEvidenceGraph.GetString(root, "sha256");
				string status = HdStudioEvidenceGraph.GetString(root, "status");
				string fingerprint = HdStudioEvidenceGraph.GetString(root, "runFingerprint");
				string mode = HdStudioEvidenceGraph.GetString(root, "mode");
				if(!string.Equals(cachedSha, sha256, StringComparison.OrdinalIgnoreCase) ||
					status != "complete" ||
					!string.Equals(mode, options.Mode, StringComparison.OrdinalIgnoreCase) ||
					(!options.Force && !string.Equals(fingerprint, HdStudioEvidenceGraph.ComputeFingerprint(
						options.Mode + "|struct=" + options.IncludeStructural + "|prg=" + options.IncludePrgCandidates +
						"|assets=" + options.MaxAssets + "|streams=" + options.MaxCandidateStreams +
						"|resolve=" + options.ResolveGaps + "|gapsex=" + options.MaxGapResolutionSeconds +
						"|evidence=" + options.EvidencePackFolder), StringComparison.OrdinalIgnoreCase))) {
					return false;
				}
				row.Sha256 = cachedSha;
				row.Status = "skipped";
				return true;
			} catch {
				return false;
			}
		}

		private static void WriteRowResult(string path, HdStudioArchaeologyBatchRow row, HdStudioArchaeologyOptions options)
		{
			string temp = path + ".tmp";
			using(FileStream stream = File.Create(temp)) {
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
				json.WriteStartObject();
				json.WriteString("schemaVersion", "1.0.0");
				json.WriteString("sha256", row.Sha256);
				json.WriteString("fileName", row.FileName);
				json.WriteString("path", row.Path);
				json.WriteString("status", row.Status);
				json.WriteString("stopReason", row.StopReason);
				json.WriteString("mode", options.Mode);
				json.WriteString("engineFingerprint", row.EngineFingerprint.Length > 0 ? row.EngineFingerprint : HdStudioEvidenceGraph.ComputeEngineFingerprint());
				json.WriteString("runFingerprint", row.RunFingerprint);
				json.WriteNumber("mapper", row.Mapper);
				json.WriteString("chrMode", row.ChrMode);
				json.WriteNumber("uniquePatterns", row.UniquePatterns);
				json.WriteNumber("observedPatterns", row.ObservedPatterns);
				json.WriteNumber("assets", row.Assets);
				json.WriteNumber("gaps", row.Gaps);
				json.WriteNumber("gapsResolved", row.GapsResolved);
				json.WriteNumber("gapsPartial", row.GapsPartial);
				json.WriteNumber("gapsUnresolved", row.GapsUnresolved);
				json.WriteNumber("compositions", row.Compositions);
				json.WriteNumber("animationSequences", row.AnimationSequences);
				json.WriteNumber("seconds", row.Seconds);
				json.WriteString("outputFolder", row.OutputFolder);
				json.WriteString("error", row.Error);
				json.WriteEndObject();
			}
			File.Move(temp, path, true);
		}

		private static Dictionary<string, string> LoadEvidenceMap(string path)
		{
			Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
			if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
				return map;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				if(doc.RootElement.ValueKind != JsonValueKind.Object) {
					return map;
				}
				foreach(JsonProperty property in doc.RootElement.EnumerateObject()) {
					if(property.Value.ValueKind == JsonValueKind.String) {
						string folder = property.Value.GetString() ?? "";
						if(folder.Length > 0 && Directory.Exists(folder)) {
							map[property.Name] = folder;
						}
					}
				}
			} catch {
			}
			return map;
		}

		private static string ResolveEvidenceFolder(Dictionary<string, string> map, string romPath, HdStudioRomImage rom)
		{
			if(map.Count == 0) {
				return "";
			}
			string sha256Prefix = rom.Sha256.Length >= 12 ? rom.Sha256.Substring(0, 12).ToLowerInvariant() : "";
			if(sha256Prefix.Length == 12 && map.TryGetValue(sha256Prefix, out string? bySha256)) {
				return bySha256;
			}
			string title = Path.GetFileNameWithoutExtension(romPath);
			if(map.TryGetValue(title, out string? exact)) {
				return exact;
			}
			string normalized = NormalizeTitle(title);
			foreach(KeyValuePair<string, string> pair in map) {
				if(NormalizeTitle(pair.Key) == normalized) {
					return pair.Value;
				}
			}
			string sha1Prefix = rom.Sha1.Length >= 12 ? rom.Sha1.Substring(0, 12).ToLowerInvariant() : "";
			if(sha1Prefix.Length == 12 && map.TryGetValue(sha1Prefix, out string? byHash)) {
				return byHash;
			}
			return "";
		}

		private static string NormalizeTitle(string title)
		{
			StringBuilder builder = new(title.Length);
			foreach(char c in title.ToLowerInvariant()) {
				if(char.IsLetterOrDigit(c)) {
					builder.Append(c);
				}
			}
			return builder.ToString();
		}

		private static string SafeName(string name)
		{
			string safe = new(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
			if(safe.Length == 0) safe = "rom";
			if(safe.Length > 48) safe = safe.Substring(0, 48);
			return safe;
		}

		private static string Csv(string value)
		{
			if(value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) {
				return "\"" + value.Replace("\"", "\"\"") + "\"";
			}
			return value;
		}

		private static uint[] DefaultPalette()
		{
			uint[] palette = new uint[64];
			for(int i = 0; i < palette.Length; i++) {
				int r = (i * 37) & 0xFF;
				int g = (i * 73) & 0xFF;
				int b = (i * 109) & 0xFF;
				palette[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
			}
			return palette;
		}
	}
}
