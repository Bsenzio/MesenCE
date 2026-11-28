using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Mesen.HdStudio
{
	internal sealed class HdStudioGapResolutionOutcome
	{
		public string GapId = "";
		public string Kind = "";
		public string Action = "";
		public string Outcome = "";
		public string Reason = "";
		public readonly List<string> Witnesses = new();
		public double Seconds;
	}

	internal sealed class HdStudioGapResolutionResult
	{
		public int OpenBefore;
		public int Resolved;
		public int PartiallyResolved;
		public int Unresolved;
		public int Unsupported;
		public int BudgetExhausted;
		public int NewNodes;
		public int NewEdges;
		public int Compositions;
		public int AnimationSequences;
		public int Metatiles;
		public int MapPlacements;
		public int PaletteTablesValidated;
		public int PrgStreamsAssociated;
		public int ChrRamRoutines;
		public int CrossBankTargetsResolved;
		public double Seconds;
		public string Path = "";
		public readonly List<HdStudioGapResolutionOutcome> Outcomes = new();
	}

	/// <summary>
	/// Generalized automatic evidence-gap resolution.
	///
	/// This stage does not stop at listing a recommended action. For every open gap
	/// it selects a bounded, deterministic analysis over the physical ROM, the
	/// structural graph and any read-only runtime evidence, executes it, and records
	/// a terminal outcome: resolved, partially resolved, unresolved (with the reason)
	/// or unsupported. Nothing is promoted from a plausible byte pattern to a
	/// semantic asset without a witness.
	/// </summary>
	internal static class HdStudioGapResolver
	{
		private const int MaxCompositionNodes = 256;
		private const int MaxAnimationNodes = 128;
		private const int MaxMetatileNodes = 512;
		private const int MaxMapNodes = 32;

		private sealed class ObservedPart
		{
			public int X;
			public int Y;
			public int Tile;
			public string Palette = "";
			public bool FlipH;
			public bool FlipV;
		}

		private sealed class ObservedComposition
		{
			public int FirstFrame;
			public int LastFrame;
			public int FrameCount;
			public int Width;
			public int Height;
			public string Palette = "";
			public string ChrType = "";
			public string LayoutSignature = "";
			public string TileSignature = "";
			public readonly List<ObservedPart> Parts = new();
		}

		public static HdStudioGapResolutionResult Run(HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			string packFolder, HdStudioArchaeologyOptions options, string evidenceFolder, Action<string>? log, CancellationToken token)
		{
			HdStudioGapResolutionResult result = new();
			Stopwatch watch = Stopwatch.StartNew();
			result.OpenBefore = graph.Gaps.Values.Count(g => g.Status == "open");
			if(!options.ResolveGaps || result.OpenBefore == 0) {
				result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 3);
				return result;
			}

			int nodesBefore = graph.Nodes.Count;
			int edgesBefore = graph.Edges.Count;
			string folder = evidenceFolder.Length > 0 && Directory.Exists(evidenceFolder)
				? evidenceFolder
				: packFolder;
			string studio = Path.Combine(folder, HdStudioReport.StudioFolderName);

			Dictionary<int, List<HdStudioEvidenceNode>> patternsByTile = BuildPatternIndex(graph);
			HashSet<string> observedPalettes = CollectObservedPalettes(graph);

			try {
				ObservedEvidence observed = ParseObservedEvidence(studio, graph, patternsByTile, result, token);
				ValidatePaletteCandidates(rom, graph, observedPalettes, result, token);
				AssociatePrgStreams(rom, graph, patternsByTile, result, token);
				AnalyzeChrRamRoutes(rom, graph, result, token);
				ValidatePointerTables(rom, graph);
				AnalyzeObservedMetatiles(observed, graph, result);
				AnalyzeObservedObjects(observed, graph, patternsByTile, result);
			} catch(OperationCanceledException) {
				throw;
			} catch(Exception ex) {
				graph.Notes.Add("Gap resolution prepass stopped: " + ex.Message);
			}

			List<HdStudioGap> gaps = graph.Gaps.Values
				.OrderByDescending(g => g.Priority)
				.ThenBy(g => g.Id, StringComparer.Ordinal)
				.Where(g => g.Status == "open")
				.ToList();
			int operations = 0;
			bool budget = false;
			foreach(HdStudioGap gap in gaps) {
				if(token.IsCancellationRequested) {
					throw new OperationCanceledException(token);
				}
				if(watch.Elapsed.TotalSeconds > Math.Max(1, options.MaxGapResolutionSeconds)) {
					budget = true;
					break;
				}
				if(operations++ > Math.Max(16, options.MaxGapResolutionOps)) {
					budget = true;
					break;
				}
				Stopwatch gapWatch = Stopwatch.StartNew();
				HdStudioGapResolutionOutcome outcome;
				try {
					outcome = ResolveGap(rom, graph, gap, observedPalettes, result);
				} catch(Exception ex) {
					outcome = new HdStudioGapResolutionOutcome() {
						GapId = gap.Id,
						Kind = gap.Kind,
						Action = gap.Action,
						Outcome = "unresolved",
						Reason = "resolver-error: " + ex.Message
					};
				}
				gap.Status = outcome.Outcome;
				gap.Resolution = outcome.Reason;
				outcome.Seconds = Math.Round(gapWatch.Elapsed.TotalSeconds, 3);
				ApplyOutcomeCounts(result, outcome);
				result.Outcomes.Add(outcome);
			}
			if(budget) {
				foreach(HdStudioGap gap in gaps.Where(g => g.Status == "open")) {
					gap.Status = "budget-exhausted";
					gap.Resolution = "bounded gap-resolution budget reached before this gap was analyzed; the gap remains queued";
					result.BudgetExhausted++;
				}
			}

			result.NewNodes = Math.Max(0, graph.Nodes.Count - nodesBefore);
			result.NewEdges = Math.Max(0, graph.Edges.Count - edgesBefore);
			result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 3);
			graph.Notes.Add("Automatic gap resolution: " + result.Resolved.ToString(CultureInfo.InvariantCulture) +
				" resolved, " + result.PartiallyResolved.ToString(CultureInfo.InvariantCulture) + " partially resolved, " +
				result.Unresolved.ToString(CultureInfo.InvariantCulture) + " unresolved with a terminal reason, " +
				result.Unsupported.ToString(CultureInfo.InvariantCulture) + " unsupported. " +
				result.Compositions.ToString(CultureInfo.InvariantCulture) + " observed compositions, " +
				result.AnimationSequences.ToString(CultureInfo.InvariantCulture) + " observed animation sequences, " +
				result.Metatiles.ToString(CultureInfo.InvariantCulture) + " metatiles, " +
				result.PaletteTablesValidated.ToString(CultureInfo.InvariantCulture) + " validated palette tables.");
			WriteReport(packFolder, graph, result);
			log?.Invoke("[archaeology] Gap resolution: " + result.Resolved + " resolved, " +
				result.PartiallyResolved + " partial, " + result.Unresolved + " unresolved, " +
				result.Unsupported + " unsupported (" + result.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
			return result;
		}

		private static void ApplyOutcomeCounts(HdStudioGapResolutionResult result, HdStudioGapResolutionOutcome outcome)
		{
			switch(outcome.Outcome) {
				case "resolved": result.Resolved++; break;
				case "partially-resolved": result.PartiallyResolved++; break;
				case "unsupported": result.Unsupported++; break;
				case "unresolved": result.Unresolved++; break;
				default: result.Unresolved++; break;
			}
		}

		private static Dictionary<int, List<HdStudioEvidenceNode>> BuildPatternIndex(HdStudioEvidenceGraph graph)
		{
			Dictionary<int, List<HdStudioEvidenceNode>> byTile = new();
			foreach(HdStudioEvidenceNode node in graph.Nodes.Values.Where(n => n.Type == "pattern")) {
				foreach(string token in node.Properties.GetValueOrDefault("tileIndices", "").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
					if(int.TryParse(token, out int index)) {
						if(!byTile.TryGetValue(index, out List<HdStudioEvidenceNode>? list)) {
							byTile[index] = list = new List<HdStudioEvidenceNode>();
						}
						list.Add(node);
					}
				}
				if(int.TryParse(node.Properties.GetValueOrDefault("runtimeTileIndex", "-1"), out int runtime) && runtime >= 0) {
					if(!byTile.TryGetValue(runtime, out List<HdStudioEvidenceNode>? list)) {
						byTile[runtime] = list = new List<HdStudioEvidenceNode>();
					}
					if(!list.Contains(node)) {
						list.Add(node);
					}
				}
			}
			return byTile;
		}

		private static HashSet<string> CollectObservedPalettes(HdStudioEvidenceGraph graph)
		{
			HashSet<string> palettes = new(StringComparer.OrdinalIgnoreCase);
			foreach(HdStudioEvidenceNode node in graph.Nodes.Values) {
				string observed = node.Properties.GetValueOrDefault("observedPalettes", "");
				foreach(string value in observed.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
					if(value.Length == 8) {
						palettes.Add(value.ToUpperInvariant());
					}
				}
				string palette = node.Properties.GetValueOrDefault("palette", "");
				if(palette.Length == 8) {
					palettes.Add(palette.ToUpperInvariant());
				}
			}
			return palettes;
		}

		private sealed class ObservedEvidence
		{
			public readonly List<ObservedComposition> Objects = new();
			public readonly List<MetatileRow> Metatiles = new();
			public readonly List<MapFrameRow> MapFrames = new();
			public int MetatileRows;
			public int MapRows;
			public int MapFrameCount;
			public readonly HashSet<string> Palettes = new(StringComparer.OrdinalIgnoreCase);
		}

		private sealed class MetatileRow
		{
			public string Palette = "";
			public string Keys = "";
			public int Uses;
		}

		private sealed class MapFrameRow
		{
			public int Frame;
			public int Rows;
			public int MinTileX = int.MaxValue;
			public int MaxTileX;
			public int MinTileY = int.MaxValue;
			public int MaxTileY;
		}

		private static ObservedEvidence ParseObservedEvidence(string studio, HdStudioEvidenceGraph graph,
			Dictionary<int, List<HdStudioEvidenceNode>> patternsByTile, HdStudioGapResolutionResult result, CancellationToken token)
		{
			ObservedEvidence evidence = new();
			if(!Directory.Exists(studio)) {
				return evidence;
			}
			try {
				string objectsPath = Path.Combine(studio, "objects.txt");
				if(File.Exists(objectsPath)) {
					ParseObjectsFile(objectsPath, evidence, result);
				}
				string spritesPath = Path.Combine(studio, "live", "live-sprites.tsv");
				if(File.Exists(spritesPath)) {
					ParseLiveSprites(spritesPath, evidence, result, token);
				}
				string liveBackgrounds = Path.Combine(studio, "live", "live-backgrounds.tsv");
				if(File.Exists(liveBackgrounds)) {
					evidence.MetatileRows += ParseMetatileFile(liveBackgrounds, evidence, 0, 1);
				}
				string bgObserved = Path.Combine(studio, "bg_observed.tsv");
				if(File.Exists(bgObserved)) {
					evidence.MetatileRows += ParseMetatileFile(bgObserved, evidence, 0, 1);
				}
				string liveBackgroundFrames = Path.Combine(studio, "live", "live-background-frames.tsv");
				if(File.Exists(liveBackgroundFrames)) {
					evidence.MapRows = ParseBackgroundFrames(liveBackgroundFrames, evidence, graph);
				}
				string palettesPath = Path.Combine(studio, "palettes.txt");
				if(File.Exists(palettesPath)) {
					foreach(string line in File.ReadLines(palettesPath).Take(4096)) {
						if(line.Length == 0 || line[0] == '#') {
							continue;
						}
						string key = line.Split('\t')[0].Trim().ToUpperInvariant();
						if(key.Length == 8) {
							evidence.Palettes.Add(key);
						}
					}
				}
			} catch(Exception ex) {
				graph.Notes.Add("Runtime evidence prepass was incomplete: " + ex.Message);
			}
			return evidence;
		}

		private static void ParseObjectsFile(string path, ObservedEvidence evidence, HdStudioGapResolutionResult result)
		{
			int count = 0;
			foreach(string line in File.ReadLines(path)) {
				if(count > 20000) {
					break;
				}
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] columns = line.Split('\t');
				if(columns.Length < 5) {
					continue;
				}
				if(!int.TryParse(columns[0], out int frameCount) ||
					!int.TryParse(columns[1], out int firstFrame) ||
					!int.TryParse(columns[2], out int width) ||
					!int.TryParse(columns[3], out int height)) {
					continue;
				}
				ObservedComposition composition = new() {
					FirstFrame = firstFrame,
					LastFrame = firstFrame + Math.Max(1, frameCount) - 1,
					FrameCount = Math.Max(1, frameCount),
					Width = width,
					Height = height
				};
				foreach(string partText in columns[4].Split(';', StringSplitOptions.RemoveEmptyEntries)) {
					string[] fields = partText.Split(',');
					if(fields.Length < 4 ||
						!int.TryParse(fields[0], out int dx) ||
						!int.TryParse(fields[1], out int dy) ||
						!int.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int tile)) {
						continue;
					}
					ObservedPart part = new() {
						X = dx,
						Y = dy,
						Tile = tile,
						Palette = fields[3].Trim().ToUpperInvariant(),
						FlipH = fields.Length > 4 && fields[4].Length > 0 && fields[4][0] == '1'
					};
					composition.Parts.Add(part);
					if(part.Palette.Length == 8) {
						composition.Palette = part.Palette;
						evidence.Palettes.Add(part.Palette);
					}
				}
				if(composition.Parts.Count > 0 && composition.Parts.Count <= 64) {
					NormalizeComposition(composition);
					evidence.Objects.Add(composition);
					count++;
				}
			}
			result.Compositions = evidence.Objects.Count;
		}

		private static void ParseLiveSprites(string path, ObservedEvidence evidence, HdStudioGapResolutionResult result, CancellationToken token)
		{
			Dictionary<int, List<ObservedPart>> byFrame = new();
			int count = 0;
			foreach(string line in File.ReadLines(path)) {
				if(token.IsCancellationRequested) {
					throw new OperationCanceledException(token);
				}
				if(count++ > 400000) {
					break;
				}
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] columns = line.Split('\t');
				if(columns.Length < 10 || !int.TryParse(columns[0], out int frame)) {
					continue;
				}
				if(!int.TryParse(columns[3], out int x) || !int.TryParse(columns[4], out int y) ||
					!int.TryParse(columns[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int tile)) {
					continue;
				}
				ObservedPart part = new() {
					X = x,
					Y = y,
					Tile = tile,
					Palette = columns[7].Trim().ToUpperInvariant(),
					FlipH = columns[8].Trim() == "1",
					FlipV = columns[9].Trim() == "1"
				};
				if(part.Palette.Length == 8) {
					evidence.Palettes.Add(part.Palette);
				}
				if(!byFrame.TryGetValue(frame, out List<ObservedPart>? parts)) {
					byFrame[frame] = parts = new List<ObservedPart>();
				}
				parts.Add(part);
			}
			//A frame can contain several independent objects. Cluster by proximity on a
			//bounded grid; a cluster is a composition witness, not a semantic object.
			foreach(KeyValuePair<int, List<ObservedPart>> frame in byFrame.OrderBy(k => k.Key).Take(4096)) {
				List<List<ObservedPart>> clusters = ClusterParts(frame.Value);
				foreach(List<ObservedPart> cluster in clusters) {
					if(cluster.Count == 0 || cluster.Count > 64) {
						continue;
					}
					ObservedComposition composition = new() {
						FirstFrame = frame.Key,
						LastFrame = frame.Key,
						FrameCount = 1
					};
					composition.Parts.AddRange(cluster);
					composition.Palette = cluster.GroupBy(p => p.Palette).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
					NormalizeComposition(composition);
					evidence.Objects.Add(composition);
				}
			}
			result.Compositions = evidence.Objects.Count;
		}

		private static List<List<ObservedPart>> ClusterParts(List<ObservedPart> parts)
		{
			List<List<ObservedPart>> clusters = new();
			bool[] used = new bool[parts.Count];
			for(int i = 0; i < parts.Count; i++) {
				if(used[i]) {
					continue;
				}
				Queue<int> queue = new();
				queue.Enqueue(i);
				used[i] = true;
				List<ObservedPart> cluster = new();
				while(queue.Count > 0) {
					int index = queue.Dequeue();
					ObservedPart part = parts[index];
					cluster.Add(part);
					for(int j = 0; j < parts.Count; j++) {
						if(used[j]) {
							continue;
						}
						ObservedPart other = parts[j];
						int dx = Math.Abs(part.X - other.X);
						int dy = Math.Abs(part.Y - other.Y);
						if(dx <= 24 && dy <= 24) {
							used[j] = true;
							queue.Enqueue(j);
						}
					}
				}
				clusters.Add(cluster);
			}
			return clusters;
		}

		private static void NormalizeComposition(ObservedComposition composition)
		{
			int minX = composition.Parts.Min(p => p.X);
			int minY = composition.Parts.Min(p => p.Y);
			int maxX = composition.Parts.Max(p => p.X + 8);
			int maxY = composition.Parts.Max(p => p.Y + 8);
			foreach(ObservedPart part in composition.Parts) {
				part.X -= minX;
				part.Y -= minY;
			}
			composition.Width = Math.Max(8, maxX - minX);
			composition.Height = Math.Max(8, maxY - minY);
			composition.LayoutSignature = string.Join(",", composition.Parts
				.OrderBy(p => p.Y).ThenBy(p => p.X).ThenBy(p => p.Tile)
				.Select(p => p.X + ":" + p.Y + ":" + (p.FlipH ? 1 : 0) + (p.FlipV ? 1 : 0)));
			composition.TileSignature = string.Join(",", composition.Parts
				.OrderBy(p => p.Y).ThenBy(p => p.X).ThenBy(p => p.Tile)
				.Select(p => p.Tile.ToString("X", CultureInfo.InvariantCulture)));
		}

		private static int ParseMetatileFile(string path, ObservedEvidence evidence, int minimumUses, int paletteIndex)
		{
			int count = 0;
			foreach(string line in File.ReadLines(path)) {
				if(count > 20000) {
					break;
				}
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] columns = line.Split('\t');
				if(columns.Length < 3) {
					continue;
				}
				string palette = columns[paletteIndex].Trim().ToUpperInvariant();
				if(palette.Length == 8) {
					evidence.Palettes.Add(palette);
				}
				if(int.TryParse(columns[0], out int uses) && uses >= minimumUses) {
					count++;
					if(columns.Length >= 7 && evidence.Metatiles.Count < MaxMetatileNodes) {
						evidence.Metatiles.Add(new MetatileRow() {
							Uses = uses,
							Palette = palette,
							Keys = string.Join(",", columns.Skip(2).Take(4).Select(c => c.Trim().ToUpperInvariant()))
						});
					}
				}
			}
			return count;
		}

		private static int ParseBackgroundFrames(string path, ObservedEvidence evidence, HdStudioEvidenceGraph graph)
		{
			int rows = 0;
			Dictionary<int, MapFrameRow> frames = new();
			foreach(string line in File.ReadLines(path)) {
				if(rows > 400000) {
					break;
				}
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] columns = line.Split('\t');
				if(columns.Length < 8 || !int.TryParse(columns[0], out int frame)) {
					continue;
				}
				if(!int.TryParse(columns[3], out int tileX) || !int.TryParse(columns[4], out int tileY)) {
					continue;
				}
				if(!frames.TryGetValue(frame, out MapFrameRow? row)) {
					frames[frame] = row = new MapFrameRow() { Frame = frame };
				}
				row.Rows++;
				row.MinTileX = Math.Min(row.MinTileX, tileX);
				row.MaxTileX = Math.Max(row.MaxTileX, tileX);
				row.MinTileY = Math.Min(row.MinTileY, tileY);
				row.MaxTileY = Math.Max(row.MaxTileY, tileY);
				rows++;
			}
			evidence.MapRows = rows;
			evidence.MapFrameCount = frames.Count;
			evidence.MapFrames.AddRange(frames.Values.OrderBy(f => f.Frame).Take(MaxMapNodes));
			return rows;
		}

		private static void AnalyzeObservedObjects(ObservedEvidence evidence, HdStudioEvidenceGraph graph,
			Dictionary<int, List<HdStudioEvidenceNode>> patternsByTile, HdStudioGapResolutionResult result)
		{
			if(evidence.Objects.Count == 0) {
				return;
			}
			Dictionary<string, HdStudioEvidenceNode> compositionByKey = new(StringComparer.Ordinal);
			foreach(ObservedComposition composition in evidence.Objects.Take(MaxCompositionNodes)) {
				string key = composition.Width + "x" + composition.Height + "|" + composition.Palette + "|" + composition.LayoutSignature + "|" + composition.TileSignature;
				if(compositionByKey.ContainsKey(key)) {
					continue;
				}
				HdStudioEvidenceNode node = graph.AddNode("composition", key,
					"Observed composition " + composition.Width + "x" + composition.Height + " (" + composition.Parts.Count + " parts)",
					"runtime-observed", 0.75, "heuristic",
					"objects.txt/live-sprites.tsv runtime composition; relative OAM placement, not a nametable decoder");
				node.Properties["width"] = composition.Width.ToString(CultureInfo.InvariantCulture);
				node.Properties["height"] = composition.Height.ToString(CultureInfo.InvariantCulture);
				node.Properties["parts"] = composition.Parts.Count.ToString(CultureInfo.InvariantCulture);
				node.Properties["palette"] = composition.Palette;
				node.Properties["firstFrame"] = composition.FirstFrame.ToString(CultureInfo.InvariantCulture);
				node.Properties["frameCount"] = composition.FrameCount.ToString(CultureInfo.InvariantCulture);
				node.Properties["layout"] = composition.LayoutSignature;
				node.Properties["tiles"] = composition.TileSignature;
				node.Properties["reconstructionScope"] = "observed-composition-only";
				foreach(ObservedPart part in composition.Parts) {
					if(patternsByTile.TryGetValue(part.Tile, out List<HdStudioEvidenceNode>? matches)) {
						foreach(HdStudioEvidenceNode pattern in matches.Take(2)) {
							graph.AddEdge(node.Id, pattern.Id, "contains-part", "runtime-observed", 0.7, "observed tile index in composition");
						}
					}
				}
				compositionByKey[key] = node;
			}
			result.Compositions = compositionByKey.Count;
			//Animation identity is only claimed for observed compositions sharing the
			//same footprint/layout/palette but showing different tile sets.
			Dictionary<string, List<ObservedComposition>> groups = new(StringComparer.Ordinal);
			foreach(ObservedComposition composition in evidence.Objects) {
				string groupKey = composition.Width + "x" + composition.Height + "|" + composition.Palette + "|" + composition.LayoutSignature;
				if(!groups.TryGetValue(groupKey, out List<ObservedComposition>? list)) {
					groups[groupKey] = list = new List<ObservedComposition>();
				}
				if(list.All(c => c.TileSignature != composition.TileSignature)) {
					list.Add(composition);
				}
			}
			int sequences = 0;
			foreach(KeyValuePair<string, List<ObservedComposition>> group in groups
				.Where(g => g.Value.Count >= 2)
				.OrderByDescending(g => g.Value.Sum(c => c.FrameCount))
				.Take(MaxAnimationNodes)) {
				string key = "observed:" + HdStudioEvidenceGraph.StableHash(group.Key).Substring(0, 12);
				HdStudioEvidenceNode node = graph.AddNode("animationSequence", key,
					"Observed animation sequence (" + group.Value.Count + " tile states)",
					"runtime-observed", 0.65, "heuristic",
					"observed frames share a footprint/layout/palette but show different tile sets; this is frame identity, not the game's internal timer");
				node.Properties["states"] = group.Value.Count.ToString(CultureInfo.InvariantCulture);
				node.Properties["firstFrames"] = string.Join(",", group.Value.Take(16).Select(c => c.FirstFrame.ToString(CultureInfo.InvariantCulture)));
				node.Properties["tileSets"] = string.Join(";", group.Value.Take(16).Select(c => c.TileSignature));
				node.Properties["periodClaim"] = "none; observed-phase approximation only";
				node.Properties["reconstructionScope"] = "observed-frames-only";
				foreach(ObservedComposition composition in group.Value.Take(16)) {
					string compositionKey = composition.Width + "x" + composition.Height + "|" + composition.Palette + "|" + composition.LayoutSignature + "|" + composition.TileSignature;
					if(compositionByKey.TryGetValue(compositionKey, out HdStudioEvidenceNode? compositionNode)) {
						graph.AddEdge(node.Id, compositionNode.Id, "animation-frame", "runtime-observed", 0.65, "observed layout identity");
					}
				}
				sequences++;
			}
			result.AnimationSequences = sequences;
			graph.Notes.Add("Observed objects: " + compositionByKey.Count.ToString(CultureInfo.InvariantCulture) +
				" compositions and " + sequences.ToString(CultureInfo.InvariantCulture) +
				" animation identities reconstructed from runtime evidence. These are observed frames, not complete semantic reconstruction.");
		}

		private static void AnalyzeObservedMetatiles(ObservedEvidence evidence, HdStudioEvidenceGraph graph, HdStudioGapResolutionResult result)
		{
			if(evidence.MetatileRows > 0) {
				HdStudioEvidenceNode node = graph.AddNode("metatileCoverage", "observed:" + evidence.MetatileRows.ToString(CultureInfo.InvariantCulture),
					"Observed 16x16 metatile blocks", "runtime-observed", 0.7, "heuristic",
					"bg_observed.tsv/live-backgrounds.tsv 2x2 tile blocks with a captured palette");
				node.Properties["blocks"] = evidence.MetatileRows.ToString(CultureInfo.InvariantCulture);
				node.Properties["reconstructionScope"] = "observed-metatile-only";
				result.Metatiles = evidence.MetatileRows;
				int nodeCount = 0;
				foreach(MetatileRow row in evidence.Metatiles.Take(MaxMetatileNodes)) {
					if(row.Keys.Length == 0) {
						continue;
					}
					HdStudioEvidenceNode meta = graph.AddNode("metatile", row.Palette + "|" + row.Keys,
						"Metatile " + row.Keys + " / " + row.Palette,
						"runtime-observed", 0.7, "heuristic",
						"observed 2x2 background block; placement is a separate map witness");
					meta.Properties["palette"] = row.Palette;
					meta.Properties["tiles"] = row.Keys;
					meta.Properties["uses"] = row.Uses.ToString(CultureInfo.InvariantCulture);
					meta.Properties["reconstructionScope"] = "observed-metatile-only";
					nodeCount++;
				}
				if(nodeCount > 0) {
					graph.AddEdge(node.Id, graph.Nodes.Values.First(n => n.Type == "metatile").Id, "contains", "runtime-observed", 0.7, "metatile coverage");
				}
			}
			if(evidence.MapRows > 0) {
				HdStudioEvidenceNode node = graph.AddNode("mapStructure", "observed-placements:" + evidence.MapRows.ToString(CultureInfo.InvariantCulture),
					"Observed nametable placement", "runtime-observed", 0.8, "heuristic",
					"live-background-frames.tsv frame/tile coordinates; this is an observed map, not a decoder for unobserved maps");
				node.Properties["placements"] = evidence.MapRows.ToString(CultureInfo.InvariantCulture);
				node.Properties["frames"] = evidence.MapFrameCount.ToString(CultureInfo.InvariantCulture);
				node.Properties["reconstructionScope"] = "observed-placement-only";
				result.MapPlacements = evidence.MapRows;
				foreach(MapFrameRow frame in evidence.MapFrames.Take(MaxMapNodes)) {
					HdStudioEvidenceNode frameNode = graph.AddNode("mapFrame", "frame:" + frame.Frame,
						"Observed background frame " + frame.Frame,
						"runtime-observed", 0.8, "heuristic",
						"live-background-frames.tsv; placement observed at this frame");
					frameNode.Properties["frame"] = frame.Frame.ToString(CultureInfo.InvariantCulture);
					frameNode.Properties["blocks"] = frame.Rows.ToString(CultureInfo.InvariantCulture);
					frameNode.Properties["bounds"] = frame.MinTileX + "," + frame.MinTileY + "," + frame.MaxTileX + "," + frame.MaxTileY;
					graph.AddEdge(node.Id, frameNode.Id, "contains-placement", "runtime-observed", 0.8, "observed map frame");
				}
			}
		}

		private static void ValidatePaletteCandidates(HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			HashSet<string> observedPalettes, HdStudioGapResolutionResult result, CancellationToken token)
		{
			if(observedPalettes.Count == 0) {
				return;
			}
			Dictionary<string, List<HdStudioEvidenceNode>> patternsByPalette = new(StringComparer.OrdinalIgnoreCase);
			foreach(HdStudioEvidenceNode node in graph.Nodes.Values.Where(n => n.Type == "pattern")) {
				foreach(string palette in node.Properties.GetValueOrDefault("observedPalettes", "").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
					if(!patternsByPalette.TryGetValue(palette, out List<HdStudioEvidenceNode>? list)) {
						patternsByPalette[palette] = list = new List<HdStudioEvidenceNode>();
					}
					list.Add(node);
				}
			}
			int validated = 0;
			foreach(HdStudioEvidenceNode node in graph.Nodes.Values.Where(n => n.Type == "candidatePaletteTable").Take(1024)) {
				if(token.IsCancellationRequested) {
					return;
				}
				List<string> matches = new();
				foreach(string candidate in node.Properties.GetValueOrDefault("palettes", "").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
					if(observedPalettes.Contains(candidate)) {
						matches.Add(candidate.ToUpperInvariant());
					}
				}
				if(matches.Count == 0) {
					int offset = ParseHex(node.Properties.GetValueOrDefault("physicalOffset", "0x0"));
					int length = Math.Min(96, Math.Max(16, ParseInt(node.Properties.GetValueOrDefault("length", "16"))));
					int start = Math.Max(0, offset - 32);
					int end = Math.Min(rom.Prg.Length - 16, offset + length + 32);
					for(int i = start; i <= end; i += 4) {
						string value = HexPalette(rom.Prg, i);
						if(observedPalettes.Contains(value)) {
							matches.Add(value);
							if(matches.Count >= 4) {
								break;
							}
						}
					}
				}
				if(matches.Count == 0) {
					continue;
				}
				node.Status = "corroborated";
				node.Confidence = Math.Max(node.Confidence, 0.85);
				node.Properties["validatedPalettes"] = string.Join(",", matches.Distinct(StringComparer.OrdinalIgnoreCase));
				node.Properties["validation"] = "observed palette relationship";
				foreach(string palette in matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(4)) {
					if(patternsByPalette.TryGetValue(palette, out List<HdStudioEvidenceNode>? patterns)) {
						foreach(HdStudioEvidenceNode pattern in patterns.Take(8)) {
							graph.AddEdge(node.Id, pattern.Id, "explains-palette", "corroborated", 0.8, "candidate table contains the observed palette");
						}
					}
				}
				validated++;
			}
			result.PaletteTablesValidated = validated;
			if(validated > 0) {
				HdStudioEvidenceNode node = graph.AddNode("paletteRelationship", "validated:" + validated.ToString(CultureInfo.InvariantCulture),
					"Validated palette relationships", "corroborated", 0.85, "heuristic",
					"observed palettes matched candidate PRG palette tables");
				node.Properties["tables"] = validated.ToString(CultureInfo.InvariantCulture);
				node.Properties["palettes"] = string.Join(",", observedPalettes.Take(16));
			}
		}

		private static void AssociatePrgStreams(HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			Dictionary<int, List<HdStudioEvidenceNode>> patternsByTile, HdStudioGapResolutionResult result, CancellationToken token)
		{
			Dictionary<string, HdStudioEvidenceNode> patternsByHash = new(StringComparer.OrdinalIgnoreCase);
			foreach(HdStudioEvidenceNode node in graph.Nodes.Values.Where(n => n.Type == "pattern")) {
				string sha = node.Properties.GetValueOrDefault("sha256", "");
				if(sha.Length == 64) {
					patternsByHash[sha] = node;
				}
			}
			int associated = 0;
			foreach(HdStudioEvidenceNode node in graph.Nodes.Values.Where(n => n.Type == "candidatePatternStream").Take(512)) {
				if(token.IsCancellationRequested) {
					return;
				}
				int offset = ParseHex(node.Properties.GetValueOrDefault("physicalOffset", "0x0"));
				int tiles = Math.Min(256, ParseInt(node.Properties.GetValueOrDefault("tiles", "0")));
				if(tiles <= 0 || offset < 0 || offset + 16 > rom.Prg.Length) {
					continue;
				}
				int checkedTiles = Math.Min(tiles, 256);
				int matches = 0;
				int observedMatches = 0;
				for(int i = 0; i < checkedTiles && offset + i * 16 + 16 <= rom.Prg.Length; i++) {
					string hash = Convert.ToHexString(SHA256.HashData(rom.Prg.AsSpan(offset + i * 16, 16))).ToLowerInvariant();
					if(patternsByHash.TryGetValue(hash, out HdStudioEvidenceNode? pattern)) {
						matches++;
						if(pattern.Status == "runtime-observed") {
							observedMatches++;
						}
						graph.AddEdge(node.Id, pattern.Id, "matches-pattern-content", "corroborated", 0.9, "PRG stream tile is byte-identical to a CHR pattern");
					}
				}
				node.Properties["patternMatches"] = matches.ToString(CultureInfo.InvariantCulture);
				node.Properties["observedPatternMatches"] = observedMatches.ToString(CultureInfo.InvariantCulture);
				if(observedMatches >= 2 || (observedMatches >= 1 && matches >= 4)) {
					node.Status = "corroborated";
					node.Properties["class"] = "prg-stream-with-runtime-associated-patterns";
					associated++;
				} else if(matches >= 2) {
					node.Status = "corroborated";
					node.Properties["class"] = "prg-stream-copy-of-chr-patterns";
					associated++;
				}
			}
			result.PrgStreamsAssociated = associated;
		}

		private static void AnalyzeChrRamRoutes(HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			HdStudioGapResolutionResult result, CancellationToken token)
		{
			if(rom.HasChrRom) {
				return;
			}
			List<HdStudioPrgWindow> windows = rom.GetPrgWindows();
			Dictionary<int, byte[]> codeByBank = new();
			foreach(HdStudioPrgWindow window in windows) {
				byte[] code = new byte[window.Length];
				Array.Copy(rom.Prg, window.PhysicalOffset, code, 0, Math.Min(window.Length, rom.Prg.Length - window.PhysicalOffset));
				codeByBank[window.Index] = code;
			}
			int routines = 0;
			foreach(HdStudioEvidenceNode blockNode in graph.Nodes.Values.Where(n => n.Type == "codeBlock").Take(4096)) {
				if(token.IsCancellationRequested) {
					return;
				}
				if(!int.TryParse(blockNode.Properties.GetValueOrDefault("bank", "-1"), out int bankIndex) ||
					!int.TryParse(blockNode.Properties.GetValueOrDefault("startOffset", "0x0").Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int startOffset)) {
					continue;
				}
				HdStudioPrgWindow? window = windows.FirstOrDefault(w => w.Index == bankIndex);
				if(window == null || !codeByBank.TryGetValue(bankIndex, out byte[]? code)) {
					continue;
				}
				int ppu2006 = 0;
				int ppu2007 = 0;
				int dataLoads = 0;
				int offset = startOffset;
				int instructions = 0;
				while(offset >= 0 && offset + 3 <= window.Length && instructions++ < 256) {
					if(!HdStudioCpu6502.TryDecode(code, offset, window.CpuBase + offset, bankIndex, out HdStudioInstruction instruction)) {
						break;
					}
					if(instruction.AbsoluteAddress == 0x2006) {
						ppu2006++;
					} else if(instruction.AbsoluteAddress == 0x2007) {
						ppu2007++;
					} else if(instruction.Mnemonic == "LDA" && instruction.AbsoluteAddress is int address && address >= 0x8000) {
						dataLoads++;
					}
					if(instruction.Flow is HdStudioCpuFlow.Return or HdStudioCpuFlow.Jump or HdStudioCpuFlow.JumpIndirect) {
						break;
					}
					offset += Math.Max(1, instruction.Length);
				}
				if(ppu2006 > 0 && ppu2007 > 0) {
					HdStudioEvidenceNode routine = graph.AddNode("chrRamUploadRoutine", "bank:" + bankIndex + ":addr:" + blockNode.Properties.GetValueOrDefault("startCpu", "0"),
						"CHR-RAM upload routine in bank " + bankIndex,
						"inferred", 0.65, "heuristic",
						"reachable code writes $2006 and $2007; source bytes are not proven to be the complete upload stream");
					routine.Properties["ppuAddressWrites"] = ppu2006.ToString(CultureInfo.InvariantCulture);
					routine.Properties["ppuDataWrites"] = ppu2007.ToString(CultureInfo.InvariantCulture);
					routine.Properties["prgDataLoads"] = dataLoads.ToString(CultureInfo.InvariantCulture);
					routine.Properties["block"] = blockNode.Id;
					graph.AddEdge(blockNode.Id, routine.Id, "contains-upload", "inferred", 0.65, "structural PPU write sequence");
					routines++;
				}
			}
			result.ChrRamRoutines = routines;
		}

		private static HdStudioGapResolutionOutcome ResolveGap(HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			HdStudioGap gap, HashSet<string> observedPalettes, HdStudioGapResolutionResult result)
		{
			HdStudioGapResolutionOutcome outcome = new() {
				GapId = gap.Id,
				Kind = gap.Kind,
				Action = gap.Action
			};
			switch(gap.Kind) {
				case "palette-table-unvalidated":
				case "palette-assignment-unvalidated":
					if(result.PaletteTablesValidated > 0) {
						outcome.Outcome = "resolved";
						outcome.Reason = "validated " + result.PaletteTablesValidated + " candidate palette table(s) against runtime-observed palettes";
						outcome.Witnesses.AddRange(graph.Nodes.Values.Where(n => n.Type == "candidatePaletteTable" && n.Status == "corroborated").Take(8).Select(n => n.Id));
					} else if(observedPalettes.Count == 0) {
						outcome.Outcome = "unresolved";
						outcome.Reason = "no runtime-observed palette exists; a byte table cannot be assigned to a PPU palette without observation";
					} else {
						outcome.Outcome = "unresolved";
						outcome.Reason = "candidate tables did not contain any observed palette; no table was promoted";
					}
					return outcome;
				case "prg-stream-unvalidated":
					if(result.PrgStreamsAssociated > 0) {
						outcome.Outcome = "resolved";
						outcome.Reason = "associated " + result.PrgStreamsAssociated + " PRG 2bpp stream(s) with byte-identical CHR or runtime-observed patterns";
						outcome.Witnesses.AddRange(graph.Nodes.Values.Where(n => n.Type == "candidatePatternStream" && n.Status == "corroborated").Take(8).Select(n => n.Id));
					} else {
						outcome.Outcome = "unresolved";
						outcome.Reason = "streams decode as tile-like bytes but no tile matched a CHR or runtime-observed pattern; no semantic promotion";
					}
					return outcome;
				case "chr-ram-upload-unknown":
					if(result.ChrRamRoutines > 0) {
						int observedRam = graph.CountNodes("chrRamObservation");
						outcome.Outcome = observedRam > 0 ? "resolved" : "partially-resolved";
						outcome.Reason = "found " + result.ChrRamRoutines + " reachable $2006/$2007 upload routine(s); " +
							(observedRam > 0 ? observedRam + " runtime CHR-RAM content row(s) provide the observed side" : "runtime content was not observed in this capture");
						outcome.Witnesses.AddRange(graph.Nodes.Values.Where(n => n.Type == "chrRamUploadRoutine").Take(8).Select(n => n.Id));
					} else {
						outcome.Outcome = "unresolved";
						outcome.Reason = "no reachable $2006/$2007 upload routine was isolated; CHR-RAM content remains runtime-only evidence";
					}
					return outcome;
				case "cross-bank-jump":
				case "cross-bank-call":
				case "indirect-jump":
					return ResolveControlFlowTarget(rom, graph, gap, outcome, result);
				case "structural-cross-bank-flow":
					if(result.CrossBankTargetsResolved > 0) {
						outcome.Outcome = "partially-resolved";
						outcome.Reason = result.CrossBankTargetsResolved + " cross-bank target(s) mapped uniquely; remaining targets stay explicit";
					} else {
						outcome.Outcome = "unresolved";
						outcome.Reason = "no cross-bank target could be mapped to one physical bank";
					}
					return outcome;
				case "structural-indirect-flow":
					{
						int resolvedTargets = graph.Nodes.Values.Count(n => n.Type == "indirectJumpTarget" && n.Status == "corroborated");
						if(resolvedTargets > 0) {
							outcome.Outcome = "partially-resolved";
							outcome.Reason = resolvedTargets + " indirect target(s) resolved from a table; runtime-only indirect flow remains";
						} else {
							outcome.Outcome = "unresolved";
							outcome.Reason = "indirect pointer lives in RAM; no runtime trace witness is present, so the target is not guessed";
						}
						return outcome;
					}
				case "structural-pointer-tables":
					{
						int validated = graph.Nodes.Values.Count(n => n.Type == "candidateTable" && n.Status == "corroborated");
						if(validated > 0) {
							outcome.Outcome = "resolved";
							outcome.Reason = "validated " + validated + " pointer table(s) with monotonic/consistent little-endian entries";
							outcome.Witnesses.AddRange(graph.Nodes.Values.Where(n => n.Type == "candidateTable" && n.Status == "corroborated").Take(8).Select(n => n.Id));
						} else {
							outcome.Outcome = "unresolved";
							outcome.Reason = "no referenced pointer table met the consistency bound";
						}
						return outcome;
					}
				case "map-not-reconstructed":
					if(result.MapPlacements > 0) {
						outcome.Outcome = "resolved";
						outcome.Reason = "observed nametable placement(s) reconstructed from runtime background frames; this does not decode unobserved maps";
						outcome.Witnesses.AddRange(graph.Nodes.Values.Where(n => n.Type == "mapStructure").Take(8).Select(n => n.Id));
					} else if(result.Metatiles > 0) {
						outcome.Outcome = "partially-resolved";
						outcome.Reason = "observed 16x16 metatile blocks exist, but no positional nametable witness was captured; map placement remains unresolved";
					} else {
						outcome.Outcome = "unresolved";
						outcome.Reason = "no nametable placement or metatile observation exists; a plausible byte array is not promoted to a map";
					}
					return outcome;
				case "animation-not-reconstructed":
					if(result.AnimationSequences > 0) {
						outcome.Outcome = "resolved";
						outcome.Reason = "observed " + result.AnimationSequences + " animation sequence(s) with shared layout and changing tile identity; internal timing is not claimed";
						outcome.Witnesses.AddRange(graph.Nodes.Values.Where(n => n.Type == "animationSequence").Take(8).Select(n => n.Id));
					} else {
						outcome.Outcome = "unresolved";
						outcome.Reason = "no repeated composition with a changing tile set was observed; successor frames are not inferred from adjacent CHR bytes";
					}
					return outcome;
				case "composition-not-reconstructed":
					if(result.Compositions > 0) {
						outcome.Outcome = "resolved";
						outcome.Reason = "reconstructed " + result.Compositions + " observed composition(s) from runtime OAM/object evidence; nametable-only objects remain separate";
						outcome.Witnesses.AddRange(graph.Nodes.Values.Where(n => n.Type == "composition").Take(8).Select(n => n.Id));
					} else {
						outcome.Outcome = "unresolved";
						outcome.Reason = "no runtime composition witness exists; 8x8 patterns are not assembled into objects by guesswork";
					}
					return outcome;
				case "oam-table-confirmation":
					{
						int compositions = result.Compositions;
						if(compositions > 0) {
							outcome.Outcome = "resolved";
							outcome.Reason = "OAM/object evidence was grouped into " + compositions + " composition(s); stride and relative origins came from observed parts";
						} else {
							outcome.Outcome = "unresolved";
							outcome.Reason = "OAM DMA was found statically, but no observed OAM table or composition witness exists";
						}
						return outcome;
					}
				case "unobserved-static-patterns":
					{
						int observed = graph.Nodes.Values.Count(n => n.Type == "pattern" && n.Status == "runtime-observed");
						int staticPatterns = graph.CountNodes("pattern");
						if(observed > 0 && result.Compositions > 0) {
							outcome.Outcome = "partially-resolved";
							outcome.Reason = "runtime evidence covers " + observed + " node(s)/" + staticPatterns + " static pattern(s); absence of observation is not evidence of non-use, so the remainder stays open";
						} else {
							outcome.Outcome = "unresolved";
							outcome.Reason = "no local dynamic run was executed for this graph; static absence is not proof that a pattern is unused";
						}
						return outcome;
					}
				case "capture-no-chr-rom-patterns":
				case "runtime-confirmation-missing":
					if(graph.CountNodes("scene") > 0) {
						outcome.Outcome = "partially-resolved";
						outcome.Reason = "a capture scene exists but did not expose the missing pattern class; a targeted dynamic run is still required";
					} else {
						outcome.Outcome = "unresolved";
						outcome.Reason = "no runtime capture exists; gap remains queued for targeted dynamic exploration";
					}
					return outcome;
				default:
					outcome.Outcome = "unsupported";
					outcome.Reason = "no automatic resolver is defined for gap kind '" + gap.Kind + "'; it remains explicit rather than being guessed";
					return outcome;
			}
		}

		private static HdStudioGapResolutionOutcome ResolveControlFlowTarget(HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			HdStudioGap gap, HdStudioGapResolutionOutcome outcome, HdStudioGapResolutionResult result)
		{
			int target = -1;
			int dollar = gap.Description.IndexOf('$');
			if(dollar >= 0 && dollar + 5 <= gap.Description.Length &&
				int.TryParse(gap.Description.Substring(dollar + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed)) {
				target = parsed;
			}
			if(target < 0) {
				outcome.Outcome = "unresolved";
				outcome.Reason = "gap description does not contain a concrete target address";
				return outcome;
			}
			if(gap.Kind == "indirect-jump") {
				//A JMP ($xxxx) pointer is normally a RAM vector. Without a runtime
				//witness there is no honest static answer; keep it explicit.
				HdStudioEvidenceNode indirect = graph.AddNode("indirectJumpTarget", "pointer:" + target.ToString("X4", CultureInfo.InvariantCulture),
					"Indirect jump pointer $" + target.ToString("X4", CultureInfo.InvariantCulture),
					"unresolved", 0.3, "heuristic", "pointer location from a reachable JMP (indirect)");
				indirect.Properties["pointer"] = "0x" + target.ToString("X4", CultureInfo.InvariantCulture);
				indirect.Properties["target"] = "runtime-required";
				outcome.Outcome = "unresolved";
				outcome.Reason = "indirect target requires a runtime memory witness; it is not inferred from unrelated PRG bytes";
				return outcome;
			}
			List<HdStudioPrgWindow> windows = rom.GetPrgWindows()
				.Where(w => target >= w.CpuBase && target < w.CpuEnd)
				.ToList();
			if(windows.Count == 1) {
				HdStudioPrgWindow window = windows[0];
				HdStudioEvidenceNode node = graph.AddNode("crossBankResolution", "target:" + target.ToString("X4", CultureInfo.InvariantCulture),
					"Target $" + target.ToString("X4", CultureInfo.InvariantCulture) + " in PRG bank " + window.Index,
					"corroborated", 0.75, "heuristic",
					"unique CPU-address mapping across physical PRG windows");
				node.Properties["target"] = "0x" + target.ToString("X4", CultureInfo.InvariantCulture);
				node.Properties["bank"] = window.Index.ToString(CultureInfo.InvariantCulture);
				node.Properties["physicalOffset"] = "0x" + (window.PhysicalOffset + target - window.CpuBase).ToString("X", CultureInfo.InvariantCulture);
				node.Properties["mapping"] = window.Description;
				outcome.Outcome = "resolved";
				outcome.Reason = "target mapped uniquely to PRG bank " + window.Index + " (" + window.Description + ")";
				outcome.Witnesses.Add(node.Id);
				result.CrossBankTargetsResolved++;
				return outcome;
			}
			if(windows.Count > 1) {
				HdStudioEvidenceNode node = graph.AddNode("crossBankResolution", "target:" + target.ToString("X4", CultureInfo.InvariantCulture),
					"Target $" + target.ToString("X4", CultureInfo.InvariantCulture) + " is ambiguous",
					"unresolved", 0.35, "heuristic",
					"CPU address exists in " + windows.Count + " physical PRG windows");
				node.Properties["candidates"] = string.Join(",", windows.Select(w => w.Index));
				outcome.Outcome = "unresolved";
				outcome.Reason = "target exists in " + windows.Count + " physical banks; mapper state is required to disambiguate";
				return outcome;
			}
			outcome.Outcome = "unresolved";
			outcome.Reason = target >= 0x6000 && target < 0x8000
				? "target is in PRG-RAM/expansion space, not a physical PRG window"
				: "target is outside every physical PRG window";
			return outcome;
		}

		private static void ValidatePointerTables(HdStudioRomImage rom, HdStudioEvidenceGraph graph)
		{
			foreach(HdStudioEvidenceNode node in graph.Nodes.Values.Where(n => n.Type == "candidateTable")) {
				int offset = ParseHex(node.Properties.GetValueOrDefault("physicalOffset", "0x0"));
				int entries = Math.Min(64, ParseInt(node.Properties.GetValueOrDefault("entries", "0")));
				if(entries < 4 || offset < 0 || offset + entries * 2 > rom.Prg.Length) {
					continue;
				}
				int inRange = 0;
				int previous = -1;
				int monotonic = 0;
				for(int i = 0; i < entries; i++) {
					int value = rom.Prg[offset + i * 2] | (rom.Prg[offset + i * 2 + 1] << 8);
					if(value >= 0x8000) {
						inRange++;
					}
					if(previous >= 0 && value >= previous) {
						monotonic++;
					}
					previous = value;
				}
				if(inRange >= Math.Max(3, entries / 2) || monotonic >= Math.Max(3, entries - 2)) {
					node.Status = "corroborated";
					node.Properties["validation"] = inRange + "/" + entries + " pointers in PRG space; " + monotonic + " monotonic steps";
				}
			}
		}

		private static void WriteReport(string packFolder, HdStudioEvidenceGraph graph, HdStudioGapResolutionResult result)
		{
			try {
				string folder = HdStudioEvidenceGraph.GraphFolder(packFolder);
				Directory.CreateDirectory(folder);
				result.Path = Path.Combine(folder, "gap-resolutions.json");
				using FileStream stream = File.Create(result.Path);
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
				json.WriteStartObject();
				json.WriteString("schemaVersion", "1.0.0");
				json.WriteString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
				json.WriteString("engineFingerprint", graph.EngineFingerprint);
				json.WriteString("runFingerprint", graph.RunFingerprint);
				json.WriteString("romSha256", graph.RomSha256);
				json.WriteStartObject("summary");
				json.WriteNumber("openBefore", result.OpenBefore);
				json.WriteNumber("resolved", result.Resolved);
				json.WriteNumber("partiallyResolved", result.PartiallyResolved);
				json.WriteNumber("unresolved", result.Unresolved);
				json.WriteNumber("unsupported", result.Unsupported);
				json.WriteNumber("budgetExhausted", result.BudgetExhausted);
				json.WriteNumber("newNodes", result.NewNodes);
				json.WriteNumber("newEdges", result.NewEdges);
				json.WriteNumber("compositions", result.Compositions);
				json.WriteNumber("animationSequences", result.AnimationSequences);
				json.WriteNumber("metatiles", result.Metatiles);
				json.WriteNumber("mapPlacements", result.MapPlacements);
				json.WriteNumber("paletteTablesValidated", result.PaletteTablesValidated);
				json.WriteNumber("prgStreamsAssociated", result.PrgStreamsAssociated);
				json.WriteNumber("chrRamRoutines", result.ChrRamRoutines);
				json.WriteNumber("crossBankTargetsResolved", result.CrossBankTargetsResolved);
				json.WriteNumber("seconds", result.Seconds);
				json.WriteEndObject();
				json.WriteStartArray("outcomes");
				foreach(HdStudioGapResolutionOutcome outcome in result.Outcomes) {
					json.WriteStartObject();
					json.WriteString("gapId", outcome.GapId);
					json.WriteString("kind", outcome.Kind);
					json.WriteString("action", outcome.Action);
					json.WriteString("outcome", outcome.Outcome);
					json.WriteString("reason", outcome.Reason);
					json.WriteNumber("seconds", outcome.Seconds);
					json.WriteStartArray("witnesses");
					foreach(string witness in outcome.Witnesses) {
						json.WriteStringValue(witness);
					}
					json.WriteEndArray();
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			} catch {
			}
		}

		private static string HexPalette(byte[] data, int offset)
		{
			if(offset < 0 || offset + 4 > data.Length) {
				return "";
			}
			return data[offset].ToString("X2", CultureInfo.InvariantCulture) +
				data[offset + 1].ToString("X2", CultureInfo.InvariantCulture) +
				data[offset + 2].ToString("X2", CultureInfo.InvariantCulture) +
				data[offset + 3].ToString("X2", CultureInfo.InvariantCulture);
		}

		private static int ParseHex(string value)
		{
			string text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.Substring(2) : value;
			return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int result) ? result : 0;
		}

		private static int ParseInt(string value)
		{
			return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
		}
	}
}
