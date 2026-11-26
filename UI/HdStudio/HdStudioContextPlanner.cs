using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Automatic standard-Mesen context rule proposal.
	///
	/// The input is the real, render-time evidence already produced by the live capture:
	/// per-frame sprite cells (live-sprites.tsv), per-frame rendered background cells
	/// (live-background-frames.tsv) and the paused picking frame. The planner finds every
	/// occurrence of the selected composition, then measures candidate rules on the actual
	/// captured occurrences. Only parser-supported standard conditions are emitted
	/// (spriteNearby, tileNearby, hmirror, vmirror, bgpriority, positionCheckX/Y).
	///
	/// A rule is never reported as globally independent from one screenshot. The result
	/// carries separate intended/unrelated counts, the number of scenes and a conservative
	/// classification. When no standard condition can isolate the object, the plan says so
	/// instead of silently claiming independence.
	/// </summary>
	internal static class HdStudioContextPlanner
	{
		private const int MaxScenesToEvaluate = 900;

		internal sealed class Cell
		{
			public int X;
			public int Y;
			public int RelX;
			public int RelY;
			public string Key = "";
			public string Palette = "";
			public string ChrType = "";
			public int ChrAddress = -1;
			public string Data = "";
			public bool FlipH;
			public bool FlipV;
			public bool BackgroundPriority;
			public bool IsBackground;
			public FrameEvidence? Scene;
		}

		internal sealed class FrameEvidence
		{
			public int Frame;
			public bool Pause;
			public readonly List<Cell> Sprites = new();
			public readonly List<Cell> Background = new();
		}

		private sealed class Occurrence
		{
			public FrameEvidence Scene = null!;
			public List<Cell> Cells = new();
			public int X;
			public int Y;
			public bool FromPausedFrame;
		}

		private sealed class Token
		{
			public string Kind = "";
			public int X;
			public int Y;
			public string Key = "";
			public string Palette = "";
			public string Op = "==";
			public int Operand;
			public bool Inverted;
			public string Text => Kind switch {
				"hmirror" or "vmirror" or "bgpriority" => (Inverted ? "!" : "") + Kind,
				"spriteNearby" or "tileNearby" => $"{Kind}:{X},{Y},{Key},{Palette},false",
				"positionCheckX" or "positionCheckY" => $"{Kind}:{Op},{Operand}",
				_ => ""
			};
		}

		private sealed class Candidate
		{
			public readonly List<Token> Tokens = new();
			public string Text = "";
			public int TargetMatched;
			public int TargetTotal;
			public int UnrelatedMatched;
			public int UnrelatedTotal;
			public int Scenes;
			public bool UsesPosition;
			public double Score;
		}

		private sealed class Definition
		{
			public Cell Prototype = null!;
			public readonly List<Cell> Instances = new();
			public Candidate? Best = null!;
		}

		private sealed class CellPlan
		{
			public string Key = "";
			public int X;
			public int Y;
			public string Expression = "";
			public int TargetMatched;
			public int TargetTotal;
			public int UnrelatedMatched;
			public int Scenes;
		}

		public static int WritePlan(string packFolder, string assetId, string outputPath, Action<string> log)
		{
			HdStudioCatalog catalog = HdStudioCatalog.Load(packFolder);
			HdStudioCatalogAsset? asset = catalog.Find(assetId);
			if(asset == null) {
				log("Context: no asset matches '" + assetId + "'.");
				return 1;
			}
			if(asset.Tiles.Count == 0) {
				log("Context: the selected asset has no observed tile occurrences.");
				return 1;
			}
			HdStudioPicking.PauseFrame? pause = HdStudioPicking.PauseFrame.Load(packFolder);
			if(pause == null) {
				log("Context: no paused picking frame is available.");
				return 1;
			}

			bool isBackground = asset.Kind.Contains("Background", StringComparison.OrdinalIgnoreCase);
			List<Definition> definitions = asset.Tiles
				.GroupBy(t => (Key: HdStudioCatalog.GetTileKey(t).ToUpperInvariant(), t.X, t.Y, Palette: (t.Palette ?? "").ToUpperInvariant()))
				.Select(g => {
					HdStudioCatalogTile t = g.First();
					return new Definition() {
						Prototype = new Cell() {
							X = t.X, Y = t.Y, RelX = t.X, RelY = t.Y,
							Key = HdStudioCatalog.GetTileKey(t).ToUpperInvariant(),
							Palette = (t.Palette ?? "").ToUpperInvariant(),
							ChrType = t.ChrType, ChrAddress = t.ChrAddress,
							Data = t.TileData ?? "",
							FlipH = t.FlipH, FlipV = t.FlipV,
							BackgroundPriority = t.BackgroundPriority,
							IsBackground = isBackground
						}
					};
				}).OrderBy(d => d.Prototype.RelY).ThenBy(d => d.Prototype.RelX).ToList();

			List<FrameEvidence> frames = LoadFrames(packFolder, pause, isBackground);
			if(frames.Count == 0) {
				log("Context: no captured frames were available.");
				return 1;
			}

			List<Occurrence> targetOccurrences = FindTargetOccurrences(frames, definitions, isBackground, pause);
			if(targetOccurrences.Count == 0) {
				log("Context: the selected composition was not found in the captured evidence.");
				return 1;
			}

			//A replacement entry is keyed by pattern AND palette. Cells with the same
			//pattern but a different palette are not changed unless the import explicitly
			//adds that palette variant, so they are not "unrelated occurrences" here.
			HashSet<string> targetKeys = new(definitions.Select(d => d.Prototype.Key + "|" + d.Prototype.Palette), StringComparer.OrdinalIgnoreCase);
			HashSet<string> targetCellIdentities = new(StringComparer.OrdinalIgnoreCase);
			foreach(Occurrence occurrence in targetOccurrences) {
				foreach(Cell cell in occurrence.Cells) {
					targetCellIdentities.Add(Identity(occurrence.Scene, cell));
				}
			}
			foreach(Occurrence occurrence in targetOccurrences) {
				foreach(Cell cell in occurrence.Cells) {
					Definition? match = definitions.FirstOrDefault(d =>
						d.Prototype.Key == cell.Key && d.Prototype.RelX == cell.RelX && d.Prototype.RelY == cell.RelY);
					if(match != null && !match.Instances.Contains(cell)) {
						match.Instances.Add(cell);
					}
				}
			}

			List<Cell> unrelated = new();
			foreach(FrameEvidence frame in frames) {
				foreach(Cell cell in Cells(frame, isBackground)) {
					if(targetKeys.Contains(cell.Key + "|" + cell.Palette) && !targetCellIdentities.Contains(Identity(frame, cell))) {
						unrelated.Add(cell);
					}
				}
			}

			int sceneCount = targetOccurrences.Select(o => o.Scene.Frame).Distinct().Count();
			int pauseOriginX = isBackground ? pause.Background.Min(t => t.X) : pause.Sprites.Min(s => s.X);
			int pauseOriginY = isBackground ? pause.Background.Min(t => t.Y) : pause.Sprites.Min(s => s.Y);
			int duplicateInstances = targetOccurrences
				.GroupBy(o => o.Scene.Frame)
				.Sum(g => Math.Max(0, g.Count() - 1));

			Dictionary<string, string> cellConditions = new(StringComparer.OrdinalIgnoreCase);
			List<CellPlan> perCell = new();
			int intendedTotal = 0;
			int intendedMatched = 0;
			int unrelatedMatched = 0;
			bool usesPosition = false;
			bool allCovered = true;

			foreach(Definition definition in definitions) {
				List<Token> internalTokens = BuildInternalTokens(definition, definitions);
				List<Token> externalTokens = BuildExternalTokens(definition, frames, pause, isBackground);
				List<Candidate> candidates = BuildCandidates(definition, internalTokens, externalTokens);
				Candidate? best = null;
				foreach(Candidate candidate in candidates) {
					Evaluate(candidate, definition, unrelated, frames);
					if(best == null || candidate.Score > best.Score) {
						best = candidate;
					}
				}
				definition.Best = best ?? new Candidate() { Text = "", TargetMatched = 0, TargetTotal = Math.Max(1, definition.Instances.Count) };
				intendedTotal += definition.Best.TargetTotal;
				intendedMatched += definition.Best.TargetMatched;
				unrelatedMatched += definition.Best.UnrelatedMatched;
				usesPosition |= definition.Best.UsesPosition;
				if(definition.Best.TargetMatched < definition.Best.TargetTotal) {
					allCovered = false;
				}
				if(!string.IsNullOrEmpty(definition.Best.Text)) {
					cellConditions[definition.Prototype.Key + "@" + definition.Prototype.RelX.ToString(CultureInfo.InvariantCulture) + "," + definition.Prototype.RelY.ToString(CultureInfo.InvariantCulture)] = definition.Best.Text;
				}
				perCell.Add(new CellPlan() {
					Key = definition.Prototype.Key,
					X = definition.Prototype.RelX,
					Y = definition.Prototype.RelY,
					Expression = definition.Best.Text,
					TargetMatched = definition.Best.TargetMatched,
					TargetTotal = definition.Best.TargetTotal,
					UnrelatedMatched = definition.Best.UnrelatedMatched,
					Scenes = definition.Best.Scenes
				});
			}

			string status;
			bool requiresApproval;
			if(!allCovered) {
				status = "unresolved-context";
				requiresApproval = true;
			} else if(unrelatedMatched > 0) {
				status = "shared-context";
				requiresApproval = true;
			} else if(usesPosition || sceneCount <= 1) {
				status = "scene-isolated";
				requiresApproval = false;
			} else if(cellConditions.Count == 0 && HdStudioCatalog.FindAssetsSharingKeys(catalog, asset.Id,
				definitions.Select(d => (d.Prototype.Key, d.Prototype.Palette))).Count == 0) {
				status = "strict-independent";
				requiresApproval = false;
			} else {
				status = "contextually-isolated-multi-scene";
				requiresApproval = false;
			}

			//A rule that is only true at one screen position must never be sold as a
			//generic replacement. It remains useful as an explicit scene-isolated test.
			string contextRule = cellConditions.Count == 0
				? "unconditional (no shared key observed in the captured evidence)"
				: string.Join(" ; ", cellConditions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
					.Select(p => p.Key + " = " + p.Value));

			string folder = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? packFolder;
			Directory.CreateDirectory(folder);
			string temp = outputPath + ".tmp";
			using(FileStream stream = File.Create(temp)) {
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
				json.WriteStartObject();
				json.WriteString("schemaVersion", "1.0.0");
				json.WriteString("generatedBy", "RetroArcDo HD Studio context planner");
				json.WriteString("assetId", asset.Id);
				json.WriteString("assetName", asset.Name);
				json.WriteString("assetKind", asset.Kind);
				json.WriteString("targetMode", isBackground ? "background" : "sprite");
				json.WriteString("status", status);
				json.WriteString("contextRule", contextRule);
				json.WriteBoolean("requiresApproval", requiresApproval);
				json.WriteNumber("scenes", sceneCount);
				json.WriteNumber("targetOccurrences", targetOccurrences.Count);
				json.WriteNumber("targetDefinitions", definitions.Count);
				json.WriteNumber("intendedCells", intendedTotal);
				json.WriteNumber("intendedMatched", intendedMatched);
				json.WriteNumber("intendedMissed", Math.Max(0, intendedTotal - intendedMatched));
				json.WriteNumber("unrelatedCandidates", unrelated.Count);
				json.WriteNumber("unrelatedMatched", unrelatedMatched);
				json.WriteNumber("duplicateInstances", duplicateInstances);
				json.WriteNumber("pauseFrame", pause.Frame);
				json.WriteNumber("pauseOriginX", pauseOriginX);
				json.WriteNumber("pauseOriginY", pauseOriginY);
				json.WriteNumber("sharedAssetCount", HdStudioCatalog.FindAssetsSharingKeys(catalog, asset.Id,
					definitions.Select(d => (d.Prototype.Key, d.Prototype.Palette))).Count);

				int targetWidth = definitions.Max(d => d.Prototype.RelX) + 8;
				int targetHeight = definitions.Max(d => d.Prototype.RelY) + 8;
				json.WriteStartArray("targetFrames");
				foreach(IGrouping<int, Occurrence> frameGroup in targetOccurrences
					.GroupBy(o => o.Scene.Frame)
					.OrderBy(g => g.Key)) {
					Occurrence primary = frameGroup.OrderByDescending(o => o.FromPausedFrame).First();
					json.WriteStartObject();
					json.WriteNumber("frame", primary.Scene.Frame);
					json.WriteNumber("x", primary.X);
					json.WriteNumber("y", primary.Y);
					json.WriteNumber("width", targetWidth);
					json.WriteNumber("height", targetHeight);
					json.WriteBoolean("pausedFrame", frameGroup.Any(o => o.FromPausedFrame));
					json.WriteStartArray("bboxes");
					foreach((int X, int Y) box in frameGroup.Select(o => (o.X, o.Y)).Distinct().OrderBy(b => b.Y).ThenBy(b => b.X)) {
						json.WriteStartArray();
						json.WriteNumberValue(box.X);
						json.WriteNumberValue(box.Y);
						json.WriteNumberValue(targetWidth);
						json.WriteNumberValue(targetHeight);
						json.WriteEndArray();
					}
					json.WriteEndArray();
					json.WriteEndObject();
				}
				json.WriteEndArray();

				json.WriteStartObject("cellConditions");
				foreach(KeyValuePair<string, string> pair in cellConditions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) {
					json.WriteString(pair.Key, pair.Value);
				}
				json.WriteEndObject();

				json.WriteStartArray("expressions");
				foreach(string expression in cellConditions.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) {
					json.WriteStringValue(expression);
				}
				json.WriteEndArray();

				json.WriteStartArray("perCell");
				foreach(CellPlan entry in perCell) {
					json.WriteStartObject();
					json.WriteString("key", entry.Key);
					json.WriteNumber("x", entry.X);
					json.WriteNumber("y", entry.Y);
					json.WriteString("expression", entry.Expression);
					json.WriteNumber("targetMatched", entry.TargetMatched);
					json.WriteNumber("targetTotal", entry.TargetTotal);
					json.WriteNumber("unrelatedMatched", entry.UnrelatedMatched);
					json.WriteNumber("scenes", entry.Scenes);
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteNumber("algorithmVersion", 1);
				json.WriteEndObject();
			}
			File.Move(temp, outputPath, true);
			log($"Context: {status}; {targetOccurrences.Count} target occurrence(s), {sceneCount} scene(s), " +
				$"{intendedMatched}/{intendedTotal} intended cell(s), {unrelatedMatched}/{unrelated.Count} unrelated cell(s), " +
				$"{cellConditions.Count} rule(s) -> {outputPath}");
			return status == "unresolved-context" ? 2 : 0;
		}

		private static IEnumerable<Cell> Cells(FrameEvidence frame, bool background)
		{
			return background ? frame.Background : frame.Sprites;
		}

		private static string Identity(FrameEvidence frame, Cell cell)
		{
			return frame.Frame.ToString(CultureInfo.InvariantCulture) + "|" + cell.X + "|" + cell.Y + "|" + cell.Key + "|" + cell.Palette;
		}

		private static List<FrameEvidence> LoadFrames(string packFolder, HdStudioPicking.PauseFrame pause, bool background)
		{
			Dictionary<int, FrameEvidence> byFrame = new();
			FrameEvidence PauseFrame()
			{
				if(!byFrame.TryGetValue(pause.Frame, out FrameEvidence? frame)) {
					frame = new FrameEvidence() { Frame = pause.Frame };
					byFrame[pause.Frame] = frame;
				}
				frame.Pause = true;
				return frame;
			}

			FrameEvidence paused = PauseFrame();
			foreach(HdStudioPicking.PauseSprite sprite in pause.Sprites) {
				paused.Sprites.Add(new Cell() {
					X = sprite.X, Y = sprite.Y,
					Key = (sprite.Pattern ?? "").ToUpperInvariant(),
					Palette = (sprite.Palette ?? "").ToUpperInvariant(),
					ChrType = sprite.ChrType, ChrAddress = sprite.ChrAddress,
					Data = sprite.Data ?? "", FlipH = sprite.FlipH, FlipV = sprite.FlipV,
					BackgroundPriority = sprite.BackgroundPriority, IsBackground = false, Scene = paused
				});
			}
			foreach(HdStudioPicking.PauseBackgroundTile tile in pause.Background) {
				paused.Background.Add(new Cell() {
					X = tile.X, Y = tile.Y,
					Key = (tile.Pattern ?? "").ToUpperInvariant(),
					Palette = (tile.Palette ?? "").ToUpperInvariant(),
					ChrType = tile.ChrType, ChrAddress = tile.ChrAddress,
					Data = tile.Data ?? "", IsBackground = true, Scene = paused
				});
			}

			string live = Path.Combine(packFolder, HdStudioReport.StudioFolderName, "live");
			if(!background) {
				string path = Path.Combine(live, "live-sprites.tsv");
				if(File.Exists(path)) {
					foreach(string line in File.ReadLines(path)) {
						if(line.Length == 0 || line[0] == '#') {
							continue;
						}
						string[] p = line.Split('\t');
						if(p.Length < 13 || !int.TryParse(p[0], out int frameNumber) ||
							!int.TryParse(p[3], out int x) || !int.TryParse(p[4], out int y)) {
							continue;
						}
						if(!byFrame.TryGetValue(frameNumber, out FrameEvidence? frame)) {
							frame = new FrameEvidence() { Frame = frameNumber };
							byFrame[frameNumber] = frame;
						}
						frame.Sprites.Add(new Cell() {
							X = x, Y = y, Key = p[10].ToUpperInvariant(), Palette = p[6].ToUpperInvariant(),
							ChrType = p[11], ChrAddress = int.TryParse(p[12], out int addr) ? addr : -1,
							FlipH = p[7] == "1", FlipV = p[8] == "1", BackgroundPriority = p[9] == "1",
							IsBackground = false, Scene = frame
						});
					}
				}
			} else {
				string path = Path.Combine(live, "live-background-frames.tsv");
				if(File.Exists(path)) {
					foreach(string line in File.ReadLines(path)) {
						if(line.Length == 0 || line[0] == '#') {
							continue;
						}
						string[] p = line.Split('\t');
						if(p.Length < 13 || !int.TryParse(p[0], out int frameNumber) ||
							!int.TryParse(p[1], out int x) || !int.TryParse(p[2], out int y)) {
							continue;
						}
						if(!byFrame.TryGetValue(frameNumber, out FrameEvidence? frame)) {
							frame = new FrameEvidence() { Frame = frameNumber };
							byFrame[frameNumber] = frame;
						}
						frame.Background.Add(new Cell() {
							X = x, Y = y, Key = p[9].ToUpperInvariant(), Palette = p[8].ToUpperInvariant(),
							ChrType = p[10], ChrAddress = int.TryParse(p[11], out int addr) ? addr : -1,
							Data = p[12], IsBackground = true, Scene = frame
						});
					}
				}
			}
			return byFrame.Values.OrderBy(f => f.Frame).Take(MaxScenesToEvaluate).ToList();
		}

		private static List<Occurrence> FindTargetOccurrences(List<FrameEvidence> frames, List<Definition> definitions, bool background, HdStudioPicking.PauseFrame pause)
		{
			List<Occurrence> result = new();
			string targetSignature = Signature(definitions.Select(d => d.Prototype));
			foreach(FrameEvidence frame in frames) {
				if(background) {
					Dictionary<(int X, int Y), Cell> map = new();
					foreach(Cell cell in frame.Background) {
						map[(cell.X / 8, cell.Y / 8)] = cell;
					}
					for(int y = 0; y <= 30 - definitions.Max(d => d.Prototype.Y / 8 + 1); y++) {
						for(int x = 0; x <= 32 - definitions.Max(d => d.Prototype.X / 8 + 1); x++) {
							List<Cell> matched = new();
							bool ok = true;
							foreach(Definition def in definitions) {
								int tx = x + def.Prototype.X / 8;
								int ty = y + def.Prototype.Y / 8;
								if(!map.TryGetValue((tx, ty), out Cell? cell) ||
									!string.Equals(cell.Key, def.Prototype.Key, StringComparison.OrdinalIgnoreCase) ||
									!string.Equals(cell.Palette, def.Prototype.Palette, StringComparison.OrdinalIgnoreCase)) {
									ok = false;
									break;
								}
								Cell copy = Clone(cell);
								copy.RelX = def.Prototype.RelX;
								copy.RelY = def.Prototype.RelY;
								copy.Scene = frame;
								matched.Add(copy);
							}
							if(ok && matched.Count == definitions.Count && Signature(matched) == targetSignature) {
								result.Add(new Occurrence() {
									Scene = frame, X = x * 8, Y = y * 8, Cells = matched,
									FromPausedFrame = frame.Pause
								});
							}
						}
					}
				} else {
					foreach(List<Cell> composition in Compositions(frame.Sprites)) {
						List<Cell> distinct = DistinctCells(composition);
						if(Signature(distinct) == targetSignature) {
							int ox = distinct.Min(c => c.X);
							int oy = distinct.Min(c => c.Y);
							List<Cell> cells = distinct.Select(Clone).ToList();
							foreach(Cell cell in cells) {
								cell.RelX = cell.X - ox;
								cell.RelY = cell.Y - oy;
								cell.Scene = frame;
							}
							result.Add(new Occurrence() {
								Scene = frame, X = ox, Y = oy, Cells = cells, FromPausedFrame = frame.Pause
							});
						}
					}
				}
			}

			result = result
				.GroupBy(o => o.Scene.Frame.ToString(CultureInfo.InvariantCulture) + "|" + o.X + "|" + o.Y + "|" + Signature(o.Cells))
				.Select(g => g.First())
				.ToList();

			//The paused pick is the authoritative first occurrence. If the sampled TSV
			//does not contain the same composition (sampling cadence or a single-frame
			//selection), add it explicitly so its cells are evaluated too.
			if(!result.Any(o => o.FromPausedFrame)) {
				int originX = background ? pause.Background.Min(t => t.X) : pause.Sprites.Min(s => s.X);
				int originY = background ? pause.Background.Min(t => t.Y) : pause.Sprites.Min(s => s.Y);
				FrameEvidence? pauseEvidence = frames.FirstOrDefault(f => f.Pause);
				if(pauseEvidence != null) {
					List<Cell> cells = new();
					foreach(Definition def in definitions) {
						Cell prototype = def.Prototype;
						cells.Add(new Cell() {
							X = originX + prototype.X, Y = originY + prototype.Y,
							RelX = prototype.RelX, RelY = prototype.RelY,
							Key = prototype.Key, Palette = prototype.Palette,
							ChrType = prototype.ChrType, ChrAddress = prototype.ChrAddress,
							Data = prototype.Data, FlipH = prototype.FlipH, FlipV = prototype.FlipV,
							BackgroundPriority = prototype.BackgroundPriority, IsBackground = background,
							Scene = pauseEvidence
						});
					}
					result.Add(new Occurrence() {
						Scene = pauseEvidence, X = originX, Y = originY, Cells = cells, FromPausedFrame = true
					});
				}
			}
			return result;
		}

		private static List<List<Cell>> Compositions(List<Cell> sprites)
		{
			int n = sprites.Count;
			int[] parent = Enumerable.Range(0, n).ToArray();
			int Find(int a)
			{
				while(parent[a] != a) {
					parent[a] = parent[parent[a]];
					a = parent[a];
				}
				return a;
			}
			void Union(int a, int b)
			{
				int ra = Find(a), rb = Find(b);
				if(ra != rb) {
					parent[rb] = ra;
				}
			}
			for(int i = 0; i < n; i++) {
				for(int j = i + 1; j < n; j++) {
					Cell a = sprites[i], b = sprites[j];
					bool overlap = a.X < b.X + 8 && b.X < a.X + 8 && a.Y < b.Y + 8 && b.Y < a.Y + 8;
					bool vertical = a.X == b.X && Math.Abs(a.Y - b.Y) == 8 &&
						a.Palette == b.Palette && Math.Abs(PatternIndex(a.Key) - PatternIndex(b.Key)) == 1;
					int delta = Math.Abs(PatternIndex(a.Key) - PatternIndex(b.Key));
					bool horizontal = a.Y == b.Y && Math.Abs(a.X - b.X) == 8 &&
						a.Palette == b.Palette && delta > 0 && delta <= 2;
					if(overlap || vertical || horizontal) {
						Union(i, j);
					}
				}
			}
			Dictionary<int, List<Cell>> groups = new();
			for(int i = 0; i < n; i++) {
				int root = Find(i);
				if(!groups.TryGetValue(root, out List<Cell>? list)) {
					groups[root] = list = new List<Cell>();
				}
				list.Add(sprites[i]);
			}
			return groups.Values.Where(g => g.Count <= 64).ToList();
		}

		private static int PatternIndex(string key)
		{
			return int.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value : 0;
		}

		private static List<Cell> DistinctCells(IEnumerable<Cell> cells)
		{
			return cells
				.GroupBy(c => c.X + "|" + c.Y + "|" + c.Key + "|" + c.Palette + "|" + c.FlipH + "|" + c.FlipV)
				.Select(g => g.First())
				.ToList();
		}

		private static string Signature(IEnumerable<Cell> cells)
		{
			List<Cell> list = cells.ToList();
			if(list.Count == 0) {
				return "";
			}
			int minX = list.Min(c => c.X);
			int minY = list.Min(c => c.Y);
			return string.Join(";", list
				.OrderBy(c => c.Y - minY).ThenBy(c => c.X - minX).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
				.Select(c => $"{c.X - minX},{c.Y - minY},{c.Key},{c.Palette},{c.FlipH},{c.FlipV}"));
		}

		private static Cell Clone(Cell cell)
		{
			return new Cell() {
				X = cell.X, Y = cell.Y, RelX = cell.RelX, RelY = cell.RelY, Key = cell.Key, Palette = cell.Palette,
				ChrType = cell.ChrType, ChrAddress = cell.ChrAddress, Data = cell.Data,
				FlipH = cell.FlipH, FlipV = cell.FlipV, BackgroundPriority = cell.BackgroundPriority,
				IsBackground = cell.IsBackground, Scene = cell.Scene
			};
		}

		private static List<Token> BuildInternalTokens(Definition current, List<Definition> definitions)
		{
			List<Token> tokens = new();
			foreach(Definition other in definitions) {
				if(ReferenceEquals(other, current)) {
					continue;
				}
				int dx = other.Prototype.RelX - current.Prototype.RelX;
				int dy = other.Prototype.RelY - current.Prototype.RelY;
				if(dx == 0 && dy == 0) {
					continue;
				}
				//Mesen's HD renderer fills ScreenTiles as it scans the frame. A
				//condition that references a pixel below or to the right of the
				//current pixel is evaluated before that pixel exists, so it cannot
				//match at runtime even though the complete captured frame contains
				//the neighbour. Only above and left-of-current evidence is valid.
				if(dy > 0 || (dy == 0 && dx > 0)) {
					continue;
				}
				//Mesen mirrors a spriteNearby offset according to the current tile's
				//flip. Convert the actual screen offset into the condition's logical
				//offset so a flipped occurrence still checks the correct neighbour.
				int condX = current.Prototype.IsBackground ? dx : dx * (current.Prototype.FlipH ? -1 : 1);
				int condY = current.Prototype.IsBackground ? dy : dy * (current.Prototype.FlipV ? -1 : 1);
				tokens.Add(new Token() {
					Kind = current.Prototype.IsBackground ? "tileNearby" : "spriteNearby",
					X = condX, Y = condY,
					Key = other.Prototype.Key,
					Palette = other.Prototype.Palette
				});
			}
			return tokens.DistinctBy(t => t.Text).Take(24).ToList();
		}

		private static List<Token> BuildExternalTokens(Definition current, List<FrameEvidence> frames, HdStudioPicking.PauseFrame pause, bool background)
		{
			FrameEvidence? paused = frames.FirstOrDefault(f => f.Pause);
			if(paused == null) {
				return new List<Token>();
			}
			List<Token> tokens = new();
			int pauseOriginX = background ? pause.Background.Min(t => t.X) : pause.Sprites.Min(s => s.X);
			int pauseOriginY = background ? pause.Background.Min(t => t.Y) : pause.Sprites.Min(s => s.Y);
			int currentX = pauseOriginX + current.Prototype.RelX;
			int currentY = pauseOriginY + current.Prototype.RelY;
			foreach(Cell cell in paused.Sprites.Concat(paused.Background)) {
				if(string.Equals(cell.Key, current.Prototype.Key, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				int dx = cell.X - currentX;
				int dy = cell.Y - currentY;
				if(dx == 0 && dy == 0) {
					continue;
				}
				if(Math.Abs(dx) > 48 || Math.Abs(dy) > 48 || dx * dx + dy * dy < 64) {
					continue;
				}
				//Same render-order restriction as internal neighbours.
				if(dy > 0 || (dy == 0 && dx > 0)) {
					continue;
				}
				int condX = cell.IsBackground ? dx : (background ? dx : dx * (current.Prototype.FlipH ? -1 : 1));
				int condY = cell.IsBackground ? dy : (background ? dy : dy * (current.Prototype.FlipV ? -1 : 1));
				tokens.Add(new Token() {
					Kind = cell.IsBackground ? "tileNearby" : "spriteNearby",
					X = condX, Y = condY,
					Key = cell.Key, Palette = cell.Palette
				});
			}
			//Nearest external evidence is more likely to survive another scene.
			return tokens
				.GroupBy(t => t.Text)
				.Select(g => g.First())
				.OrderBy(t => Math.Abs(t.X) + Math.Abs(t.Y))
				.Take(12)
				.ToList();
		}

		private static List<Candidate> BuildCandidates(Definition definition, List<Token> internalTokens, List<Token> externalTokens)
		{
			List<Candidate> candidates = new();
			List<Token> globals = new();
			if(!definition.Prototype.IsBackground) {
				globals.Add(new Token() { Kind = "hmirror", Inverted = !definition.Prototype.FlipH });
				globals.Add(new Token() { Kind = "vmirror", Inverted = !definition.Prototype.FlipV });
				globals.Add(new Token() { Kind = "bgpriority", Inverted = !definition.Prototype.BackgroundPriority });
			}
			void AddMany(IEnumerable<Token> tokens)
			{
				List<Token> list = tokens.Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
				string text = string.Join("&", list.Select(t => t.Text));
				if(candidates.Any(c => string.Equals(c.Text, text, StringComparison.OrdinalIgnoreCase))) {
					return;
				}
				Candidate candidate = new() { Text = text };
				candidate.Tokens.AddRange(list);
				candidate.UsesPosition = list.Any(t => t.Kind.StartsWith("position", StringComparison.Ordinal));
				candidates.Add(candidate);
			}

			AddMany(Array.Empty<Token>());
			if(globals.Count > 0) {
				AddMany(globals);
				AddMany(globals.Take(2));
				AddMany(globals.Take(1));
			}
			foreach(Token token in internalTokens) {
				AddMany(new[] { token });
				if(globals.Count > 0) {
					AddMany(new[] { token }.Concat(globals.Take(1)));
					AddMany(new[] { token }.Concat(globals.Take(2)));
					AddMany(new[] { token }.Concat(globals));
				}
			}
			for(int i = 0; i < internalTokens.Count; i++) {
				for(int j = i + 1; j < internalTokens.Count; j++) {
					AddMany(new[] { internalTokens[i], internalTokens[j] });
					if(globals.Count > 0) {
						AddMany(new[] { internalTokens[i], internalTokens[j] }.Concat(globals.Take(2)));
					}
				}
			}
			foreach(Token token in externalTokens) {
				AddMany(new[] { token });
				if(globals.Count > 0) {
					AddMany(new[] { token }.Concat(globals.Take(1)));
				}
			}
			foreach(Token internalToken in internalTokens.Take(6)) {
				foreach(Token externalToken in externalTokens.Take(6)) {
					AddMany(new[] { internalToken, externalToken });
				}
			}

			//Scene-isolated fallback. These are intentionally last and are only selected
			//when no translation-independent rule covers the captured occurrences. They
			//are valid standard Mesen conditions for both sprites and backgrounds, but
			//the plan reports them as scene-isolated rather than independent.
			{
				int posX = definition.Instances.FirstOrDefault()?.X ?? definition.Prototype.RelX;
				int posY = definition.Instances.FirstOrDefault()?.Y ?? definition.Prototype.RelY;
				AddMany(new[] {
					new Token() { Kind = "positionCheckX", Op = ">=", Operand = posX },
					new Token() { Kind = "positionCheckX", Op = "<=", Operand = posX + 7 }
				});
				AddMany(new[] {
					new Token() { Kind = "positionCheckY", Op = ">=", Operand = posY },
					new Token() { Kind = "positionCheckY", Op = "<=", Operand = posY + 7 }
				});
				AddMany(new[] {
					new Token() { Kind = "positionCheckX", Op = ">=", Operand = posX },
					new Token() { Kind = "positionCheckX", Op = "<=", Operand = posX + 7 },
					new Token() { Kind = "positionCheckY", Op = ">=", Operand = posY },
					new Token() { Kind = "positionCheckY", Op = "<=", Operand = posY + 7 }
				});
			}
			return candidates;
		}

		private static void Evaluate(Candidate candidate, Definition definition, List<Cell> unrelated, List<FrameEvidence> frames)
		{
			candidate.TargetMatched = 0;
			candidate.TargetTotal = Math.Max(1, definition.Instances.Count);
			candidate.UnrelatedMatched = 0;
			candidate.UnrelatedTotal = unrelated.Count;
			foreach(Cell cell in definition.Instances) {
				FrameEvidence frame = cell.Scene ?? frames.First();
				bool matched = candidate.Tokens.All(t => Matches(t, cell, frame));
				if(matched) {
					candidate.TargetMatched++;
				}
			}
			foreach(Cell cell in unrelated) {
				if(!string.Equals(cell.Key, definition.Prototype.Key, StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(cell.Palette, definition.Prototype.Palette, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				FrameEvidence frame = cell.Scene ?? frames.First();
				if(candidate.Tokens.All(t => Matches(t, cell, frame))) {
					candidate.UnrelatedMatched++;
				}
			}
			candidate.Scenes = definition.Instances.Select(c => c.Scene?.Frame ?? -1).Distinct().Count();

			bool covers = candidate.TargetMatched == candidate.TargetTotal;
			int sceneBonus = Math.Min(candidate.Scenes, 20) * 4;
			candidate.Score = (covers ? 1_000_000 : candidate.TargetMatched * 1000)
				- candidate.UnrelatedMatched * 20_000
				- (candidate.UsesPosition ? 25_000 : 0)
				- candidate.Tokens.Count * 40
				+ sceneBonus;
		}

		private static bool Matches(Token token, Cell current, FrameEvidence frame)
		{
			switch(token.Kind) {
				case "hmirror": return current.FlipH != token.Inverted;
				case "vmirror": return current.FlipV != token.Inverted;
				case "bgpriority": return current.BackgroundPriority != token.Inverted;
				case "positionCheckX": return Compare(current.X, token.Op, token.Operand);
				case "positionCheckY": return Compare(current.Y, token.Op, token.Operand);
				case "spriteNearby": {
					int dx = token.X * (current.FlipH ? -1 : 1);
					int dy = token.Y * (current.FlipV ? -1 : 1);
					int px = current.X + dx;
					int py = current.Y + dy;
					return frame.Sprites.Any(s => px >= s.X && px < s.X + 8 && py >= s.Y && py < s.Y + 8 &&
						(string.Equals(s.Key, token.Key, StringComparison.OrdinalIgnoreCase) &&
						 string.Equals(s.Palette, token.Palette, StringComparison.OrdinalIgnoreCase)));
				}
				case "tileNearby": {
					int px = current.X + token.X;
					int py = current.Y + token.Y;
					return frame.Background.Any(s => px >= s.X && px < s.X + 8 && py >= s.Y && py < s.Y + 8 &&
						(string.Equals(s.Key, token.Key, StringComparison.OrdinalIgnoreCase) &&
						 string.Equals(s.Palette, token.Palette, StringComparison.OrdinalIgnoreCase)));
				}
				default:
					return true;
			}
		}

		private static bool Compare(int value, string op, int operand)
		{
			return op switch {
				"==" => value == operand,
				"!=" => value != operand,
				">" => value > operand,
				"<" => value < operand,
				">=" => value >= operand,
				"<=" => value <= operand,
				_ => false
			};
		}
	}
}
