using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Mesen.HdStudio
{
	internal sealed class HdStudioCompileResult
	{
		public bool Success;
		public int CompiledReplacements;
		public int SkippedReplacements;
		public int TileEntries;
		public int Images;
		public string HiresPath = "";
		public readonly List<string> Errors = new();
		public readonly List<string> Warnings = new();
		public readonly List<string> Log = new();
	}

	/// <summary>
	/// Compiles an HD Studio project into a standard Mesen HD Pack.
	///
	/// The generated hires.txt is always rebuilt from an immutable base snapshot plus the
	/// project's managed replacements, so repeated builds are deterministic. The base snapshot
	/// keeps the capture-generated content (and any pre-existing legacy imports) untouched.
	/// Only standard hires.txt syntax is emitted.
	/// </summary>
	internal static class HdStudioCompiler
	{
		public const string ManagedPrefix = "HdStudioManaged_";
		private const string BaseFileName = "base-hires.txt";

		public static HdStudioCompileResult Compile(HdStudioProject project, Action<string>? log = null, bool minimal = false, string? outputFolder = null, bool dryRun = false)
		{
			HdStudioCompileResult result = new();
			void Report(string message)
			{
				result.Log.Add(message);
				log?.Invoke(message);
			}

			string packFolder = Path.GetFullPath(outputFolder ?? project.PackFolder);
			string hiresPath = Path.Combine(packFolder, "hires.txt");
			result.HiresPath = hiresPath;

			//ROM identity is the root of every asset id in this project.
			if(!string.IsNullOrWhiteSpace(project.RomPath) && File.Exists(project.RomPath)) {
				string sha1 = HdStudioProject.NormalizeSha1(project.RomPath);
				if(!string.IsNullOrWhiteSpace(project.RomSha1) && !string.Equals(sha1, project.RomSha1, StringComparison.OrdinalIgnoreCase)) {
					result.Errors.Add("The ROM at " + project.RomPath + " no longer matches the project (SHA1 changed).");
				}
			} else if(!string.IsNullOrWhiteSpace(project.RomPath)) {
				result.Errors.Add("The project ROM is missing: " + project.RomPath);
			}

			HdStudioCatalog catalog = HdStudioCatalog.Load(project.PackFolder);
			if(catalog.Assets.Count == 0) {
				result.Warnings.Add("No asset catalog was found; replacements are compiled from their stored cells only.");
			}

			int scale = Math.Clamp(project.OutputScale, 1, 10);
			List<HdStudioReplacement> managed = project.Replacements
				.Where(r => r.Approved)
				.OrderBy(r => r.AssetId, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
				.ToList();
			result.SkippedReplacements = project.Replacements.Count(r => !r.Approved);

			List<(HdStudioReplacement Replacement, List<HdStudioArtwork.Cell> Cells, string SheetName)> outputs = new();
			Dictionary<string, (string ReplacementId, string Hash)> contexts = new();

			foreach(HdStudioReplacement replacement in managed) {
				try {
					int replacementScale = replacement.PackScale > 0 ? Math.Clamp(replacement.PackScale, 1, 10) : scale;
					if(replacementScale != scale) {
						result.Errors.Add($"{replacement.AssetName}: replacement scale {replacementScale}x does not match project scale {scale}x. Set the whole project to one scale.");
						continue;
					}
					HdStudioCatalogAsset? asset = catalog.Find(replacement.AssetId);
					if(asset == null) {
						result.Warnings.Add($"{replacement.AssetName}: the asset is no longer in the catalog; using the stored target size.");
					}
					int width = asset != null && asset.Width > 0 ? asset.Width : Math.Max(0, replacement.TargetWidth / scale);
					int height = asset != null && asset.Height > 0 ? asset.Height : Math.Max(0, replacement.TargetHeight / scale);
					if(width <= 0 || height <= 0) {
						result.Errors.Add($"{replacement.AssetName}: the asset has no usable dimensions.");
						continue;
					}
					string sourcePath = project.ResolvePath(replacement.SourceImage);
					if(string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) {
						result.Errors.Add($"{replacement.AssetName}: source artwork is missing ({replacement.SourceImage}).");
						continue;
					}
					List<HdStudioArtwork.TileSpec> specs = asset != null ? asset.Tiles.Select(t => new HdStudioArtwork.TileSpec() {
						Pattern = t.Pattern,
						TileData = t.TileData,
						ChrType = t.ChrType,
						X = t.X,
						Y = t.Y,
						FlipH = t.FlipH,
						FlipV = t.FlipV,
						Palette = t.Palette
					}).ToList() : replacement.Cells.Select(c => new HdStudioArtwork.TileSpec() {
						Pattern = c.Key,
						Palette = c.Palette,
						TileData = c.Key.Length == 32 ? c.Key : null,
						ChrType = c.Key.Length == 32 ? "NesChrRam" : "NesChrRom"
					}).ToList();
					if(specs.Count == 0) {
						result.Errors.Add($"{replacement.AssetName}: no tile occurrences are known for this asset.");
						continue;
					}

					HdStudioArtwork.ArtworkImage image = HdStudioArtwork.LoadImage(sourcePath);
					if(replacement.Settings.RemoveBackground) {
						HdStudioArtwork.RemoveBackground(image, replacement.Settings.BackgroundTolerance);
					}
					if(replacement.Settings.KeepLargestComponent) {
						HdStudioArtwork.KeepLargestComponent(image);
					}
					SKRectI bounds = replacement.Settings.Crop ? HdStudioArtwork.AlphaBounds(image) : new SKRectI(0, 0, image.Width, image.Height);
					if(bounds.Width <= 0 || bounds.Height <= 0) {
						result.Errors.Add($"{replacement.AssetName}: the source artwork is empty or fully transparent.");
						continue;
					}
					int targetWidth = width * scale;
					int targetHeight = height * scale;
					if(targetWidth <= 0 || targetHeight <= 0 || targetWidth > 16384 || targetHeight > 16384) {
						result.Errors.Add($"{replacement.AssetName}: invalid target size {targetWidth}x{targetHeight}.");
						continue;
					}

					SKColor[] composed = HdStudioArtwork.Compose(image, bounds, targetWidth, targetHeight, replacement.Settings);
					List<HdStudioArtwork.Cell> cells = HdStudioArtwork.BuildCells(specs, composed, targetWidth, targetHeight, scale, replacement.Settings.MirrorConditions, m => result.Warnings.Add(m), replacement.Settings.AllPaletteVariants && asset != null ? asset.PaletteVariants : null);
					if(cells.Count == 0) {
						result.Errors.Add($"{replacement.AssetName}: the artwork produced no replacement tiles.");
						continue;
					}

					if(asset != null) {
						List<HdStudioCatalogAsset> sharedAssets = HdStudioCatalog.FindAssetsSharingKeys(catalog, asset.Id,
							cells.Select(c => (c.Key, c.Palette)));
						if(sharedAssets.Count > 0) {
							string names = string.Join(", ", sharedAssets.Take(4).Select(a => a.Name));
							if(sharedAssets.Count > 4) {
								names += ", ...";
							}
							result.Warnings.Add($"{replacement.AssetName}: its tile+palette keys are shared with {sharedAssets.Count} other asset(s) ({names}); those occurrences are replaced too unless a supported condition distinguishes them.");
						}
					}

					string sheetName = ManagedPrefix + HdStudioProject.StableId(replacement.Id) + ".png";
					foreach(HdStudioArtwork.Cell cell in cells) {
						//Unconditional entries are contexts too: two assets claiming the same
						//tile key and palette would otherwise silently overwrite each other.
						//Identical pixels are shared safely; different pixels are a real conflict.
						string context = cell.Condition + "|" + cell.Key + "|" + cell.Palette;
						string hash = HdStudioArtwork.HashPixels(cell.Pixels);
						if(contexts.TryGetValue(context, out var existing)) {
							if(existing.Hash != hash) {
								result.Errors.Add($"Context conflict: {cell.Condition}{cell.Key} palette {cell.Palette} is claimed by both {existing.ReplacementId} and {replacement.Id} with different artwork.");
							}
						} else {
							contexts[context] = (replacement.Id, hash);
						}
					}

					if(!minimal) {
						replacement.Sheet = sheetName;
						replacement.PackScale = scale;
						replacement.OriginalWidth = width;
						replacement.OriginalHeight = height;
						replacement.TargetWidth = targetWidth;
						replacement.TargetHeight = targetHeight;
						replacement.Cells.Clear();
						foreach(HdStudioArtwork.Cell cell in cells) {
							replacement.Cells.Add(new HdStudioReplacementCell() {
								Key = cell.Key,
								Palette = cell.Palette,
								Condition = cell.Condition,
								SheetX = cell.X
							});
						}
					}
					outputs.Add((replacement, cells, sheetName));

					if(!dryRun) {
						int tileSize = 8 * scale;
						uint[] sheet = new uint[cells.Count * tileSize * tileSize];
						for(int i = 0; i < cells.Count; i++) {
							HdStudioArtwork.Blit(sheet, cells.Count * tileSize, tileSize, cells[i].X, 0, cells[i].Pixels, tileSize);
						}
						HdStudioPng.Write(Path.Combine(packFolder, sheetName), sheet, cells.Count * tileSize, tileSize);
					}
					Report($"Compiled {replacement.AssetName}: {cells.Count} tiles, sheet {sheetName}.");
				} catch(Exception ex) {
					result.Errors.Add($"{replacement.AssetName}: {ex.Message}");
				}
			}

			result.CompiledReplacements = outputs.Count;
			result.TileEntries = outputs.Sum(o => o.Cells.Count);

			List<string> baseLines;
			if(minimal) {
				baseLines = new List<string>() {
					"<ver>109",
					$"<scale>{scale}",
					"<supportedRom>" + project.RomSha1
				};
			} else {
				baseLines = LoadBase(project, hiresPath, result, Report);
			}

			int baseImageCount = baseLines.Count(l => l.StartsWith("<img>", StringComparison.Ordinal));
			List<string> output = new(baseLines);
			int insertAt = LastImageIndex(output);
			if(insertAt < 0) {
				int firstTile = output.FindIndex(l => l.StartsWith("<tile>", StringComparison.Ordinal));
				insertAt = firstTile >= 0 ? firstTile - 1 : output.Count - 1;
			}
			for(int i = 0; i < outputs.Count; i++) {
				output.Insert(insertAt + 1 + i, "<img>" + outputs[i].SheetName);
			}

			StringBuilder tiles = new();
			for(int i = 0; i < outputs.Count; i++) {
				int imageIndex = baseImageCount + i;
				foreach(HdStudioArtwork.Cell cell in outputs[i].Cells) {
					tiles.Append(cell.Condition);
					tiles.Append("<tile>");
					tiles.Append(imageIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
					tiles.Append(cell.Key).Append(',');
					tiles.Append(string.IsNullOrEmpty(cell.Palette) ? "FF000000" : cell.Palette).Append(',');
					tiles.Append(cell.X.ToString(CultureInfo.InvariantCulture)).Append(",0,1,N\n");
				}
			}

			string om = tiles.ToString().TrimEnd('\n');
			if(om.Length > 0) {
				//Mesen checks candidate entries for a tile key in file order and uses the first
				//one whose condition matches. Capture-generated base entries therefore MUST NOT
				//precede authored replacements: an exact capture entry for the same pattern and
				//palette would otherwise shadow the user's art and nothing would change on
				//screen. Authored entries are inserted before the first base tile entry.
				int firstBaseTile = output.FindIndex(l => l.StartsWith("<tile>", StringComparison.Ordinal) || l.StartsWith("[", StringComparison.Ordinal));
				if(firstBaseTile >= 0) {
					output.Insert(firstBaseTile, om);
				} else {
					output.Add(om);
				}
			}
			result.Images = baseImageCount + outputs.Count;

			result.Success = result.Errors.Count == 0;
			if(!dryRun && result.Success) {
				Directory.CreateDirectory(packFolder);
				File.WriteAllLines(hiresPath, output, new UTF8Encoding(false));
				if(!minimal) {
					UpdateAssetDocument(project, catalog, Report);
					project.ValidationNotes.Clear();
					project.ValidationNotes.AddRange(result.Errors.Select(e => "error: " + e));
					project.ValidationNotes.AddRange(result.Warnings.Select(w => "warning: " + w));
					project.Save(HdStudioProject.GetDefaultPath(project.PackFolder));
				}
			} else if(!dryRun && !result.Success) {
				Report("Build: the pack was not written because the project has errors.");
			}

			foreach(string error in result.Errors) {
				Report("Error: " + error);
			}
			foreach(string warning in result.Warnings) {
				Report("Warning: " + warning);
			}
			Report($"Build {(result.Success ? "succeeded" : "failed")}: {result.CompiledReplacements} replacements, {result.TileEntries} tile entries, {result.Images} images, {result.Errors.Count} error(s), {result.Warnings.Count} warning(s).");
			return result;
		}

		/// <summary>
		/// Produces an isolated preview of a single replacement without changing the project.
		/// Returns the composed image path, or an empty string on failure.
		/// </summary>
		public static string PreviewReplacement(HdStudioProject project, HdStudioReplacement replacement, out List<string> messages)
		{
			messages = new List<string>();
			HdStudioCatalog catalog = HdStudioCatalog.Load(project.PackFolder);
			HdStudioCatalogAsset? asset = catalog.Find(replacement.AssetId);
			int scale = replacement.PackScale > 0 ? Math.Clamp(replacement.PackScale, 1, 10) : Math.Clamp(project.OutputScale, 1, 10);
			int width = asset != null && asset.Width > 0 ? asset.Width : Math.Max(0, replacement.TargetWidth / Math.Max(1, scale));
			int height = asset != null && asset.Height > 0 ? asset.Height : Math.Max(0, replacement.TargetHeight / Math.Max(1, scale));
			if(width <= 0 || height <= 0) {
				messages.Add("The asset has no usable dimensions.");
				return "";
			}
			string sourcePath = project.ResolvePath(replacement.SourceImage);
			if(string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) {
				messages.Add("Source artwork is missing: " + replacement.SourceImage);
				return "";
			}
			try {
				HdStudioArtwork.ArtworkImage image = HdStudioArtwork.LoadImage(sourcePath);
				if(replacement.Settings.RemoveBackground) {
					HdStudioArtwork.RemoveBackground(image, replacement.Settings.BackgroundTolerance);
				}
				if(replacement.Settings.KeepLargestComponent) {
					HdStudioArtwork.KeepLargestComponent(image);
				}
				SKRectI bounds = replacement.Settings.Crop ? HdStudioArtwork.AlphaBounds(image) : new SKRectI(0, 0, image.Width, image.Height);
				if(bounds.Width <= 0 || bounds.Height <= 0) {
					messages.Add("The source artwork is empty or fully transparent.");
					return "";
				}
				int targetWidth = width * scale;
				int targetHeight = height * scale;
				SKColor[] composed = HdStudioArtwork.Compose(image, bounds, targetWidth, targetHeight, replacement.Settings);
				string folder = Path.Combine(project.ProjectFolder, "preview");
				Directory.CreateDirectory(folder);
				string path = Path.Combine(folder, "preview_" + HdStudioProject.StableId(replacement.Id) + ".png");
				HdStudioPng.Write(path, composed.Select(HdStudioArtwork.ToArgb).ToArray(), targetWidth, targetHeight);
				messages.Add($"Preview {targetWidth}x{targetHeight} at {scale}x.");
				return path;
			} catch(Exception ex) {
				messages.Add(ex.Message);
				return "";
			}
		}

		private static List<string> LoadBase(HdStudioProject project, string hiresPath, HdStudioCompileResult result, Action<string> report)
		{
			string basePath = Path.Combine(project.ProjectFolder, BaseFileName);
			if(File.Exists(basePath)) {
				//Legacy CLI imports may be baked into an older snapshot. They are project
				//replacements now, so strip them from the base and let the compiler re-emit them.
				return SanitizeManaged(File.ReadAllLines(basePath).ToList());
			}
			if(File.Exists(hiresPath)) {
				List<string> sanitized = SanitizeManaged(File.ReadAllLines(hiresPath).ToList());
				Directory.CreateDirectory(project.ProjectFolder);
				File.WriteAllLines(basePath, sanitized, new UTF8Encoding(false));
				result.Warnings.Add("Created the immutable base hires snapshot from the current pack.");
				report("Build: base snapshot written to " + basePath);
				return sanitized;
			}
			return new List<string>() {
				"<ver>109",
				$"<scale>{Math.Clamp(project.OutputScale, 1, 10)}",
				"<supportedRom>" + project.RomSha1
			};
		}

		/// <summary>Removes generated images from a base snapshot and remaps tile image indices.</summary>
		private static List<string> SanitizeManaged(List<string> lines)
		{
			List<string> images = new();
			List<string> kept = new();
			foreach(string line in lines) {
				if(line.StartsWith("<img>", StringComparison.Ordinal)) {
					images.Add(line.Substring(5));
				} else {
					kept.Add(line);
				}
			}
			List<string> filteredImages = images.Where(n => !IsGeneratedImage(n)).ToList();
			Dictionary<int, int> map = new();
			for(int i = 0; i < images.Count; i++) {
				int newIndex = filteredImages.IndexOf(images[i]);
				if(newIndex >= 0) {
					map[i] = newIndex;
				}
			}
			if(images.Count == filteredImages.Count) {
				return lines;
			}

			List<string> result = new();
			foreach(string line in kept) {
				if(!TryRemapTileLine(line, map, out string mapped)) {
					continue;
				}
				result.Add(mapped);
			}
			int insertAt = LastImageIndex(result);
			if(insertAt < 0) {
				int firstTile = result.FindIndex(l => l.StartsWith("<tile>", StringComparison.Ordinal));
				insertAt = firstTile >= 0 ? firstTile - 1 : result.Count - 1;
			}
			for(int i = 0; i < filteredImages.Count; i++) {
				result.Insert(insertAt + 1 + i, "<img>" + filteredImages[i]);
			}
			return result;
		}

		private static bool IsGeneratedImage(string name)
		{
			return name.StartsWith(ManagedPrefix, StringComparison.OrdinalIgnoreCase) ||
				name.StartsWith("HdStudioImport_", StringComparison.OrdinalIgnoreCase);
		}

		private static bool TryRemapTileLine(string line, Dictionary<int, int> map, out string mapped)
		{
			mapped = line;
			int index = line.IndexOf("<tile>", StringComparison.Ordinal);
			if(index < 0) {
				return true;
			}
			string prefix = line.Substring(0, index + 6);
			string rest = line.Substring(index + 6);
			string[] parts = rest.Split(',');
			if(parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int oldIndex)) {
				return true;
			}
			if(!map.TryGetValue(oldIndex, out int newIndex)) {
				return false;
			}
			parts[0] = newIndex.ToString(CultureInfo.InvariantCulture);
			mapped = prefix + string.Join(",", parts);
			return true;
		}

		private static int LastImageIndex(List<string> lines)
		{
			for(int i = lines.Count - 1; i >= 0; i--) {
				if(lines[i].StartsWith("<img>", StringComparison.Ordinal)) {
					return i;
				}
			}
			return -1;
		}

		private static void UpdateAssetDocument(HdStudioProject project, HdStudioCatalog catalog, Action<string> report)
		{
			string path = Path.Combine(project.ProjectFolder, "assets", "assets.json");
			if(!File.Exists(path)) {
				return;
			}
			try {
				JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
				if(root is not JsonObject document) {
					return;
				}
				JsonArray replacementAssets = new();
				JsonArray replacementConditions = new();
				HashSet<string> conditionIds = new();
				foreach(HdStudioReplacement replacement in project.Replacements.Where(r => r.Cells.Count > 0).OrderBy(r => r.AssetId).ThenBy(r => r.Id)) {
					string source = replacement.SourceImage ?? "";
					if(Path.IsPathFullyQualified(source) && source.StartsWith(project.PackFolder, StringComparison.OrdinalIgnoreCase)) {
						source = Path.GetRelativePath(project.PackFolder, source).Replace('\\', '/');
					}
					JsonArray tiles = new();
					foreach(HdStudioReplacementCell cell in replacement.Cells) {
						tiles.Add((JsonNode)new JsonObject() {
							["pattern"] = cell.Key,
							["palette"] = cell.Palette,
							["x"] = cell.SheetX
						});
						if(!string.IsNullOrEmpty(cell.Condition)) {
							string type = cell.Condition.Contains("hmirror", StringComparison.OrdinalIgnoreCase) ? "standard-hmirror" : "standard-vmirror";
							string id = "condition:" + HdStudioProject.StableId(type + "|" + cell.Key + "|" + cell.Palette);
							if(conditionIds.Add(id)) {
								replacementConditions.Add((JsonNode)new JsonObject() {
									["id"] = id,
									["type"] = type,
									["tileKey"] = cell.Key,
									["palette"] = cell.Palette,
									["verified"] = true
								});
							}
						}
					}
					replacementAssets.Add((JsonNode)new JsonObject() {
						["id"] = replacement.Id,
						["assetId"] = replacement.AssetId,
						["sourceImage"] = source,
						["sheet"] = replacement.Sheet,
						["packScale"] = replacement.PackScale,
						["targetWidth"] = replacement.TargetWidth,
						["targetHeight"] = replacement.TargetHeight,
						["approved"] = replacement.Approved,
						["generatedBy"] = "RetroArcDo HD Studio",
						["tiles"] = tiles
					});
				}
				document["replacementAssets"] = replacementAssets;
				document["replacementConditions"] = replacementConditions;

				if(document["assets"] is JsonArray assetArray) {
					foreach(JsonNode? node in assetArray) {
						if(node is not JsonObject asset) {
							continue;
						}
						string? assetId = asset["id"]?.GetValue<string>();
						if(assetId == null) {
							continue;
						}
						JsonArray ids = new();
						foreach(HdStudioReplacement replacement in project.Replacements.Where(r => r.Cells.Count > 0 && string.Equals(r.AssetId, assetId, StringComparison.OrdinalIgnoreCase))) {
							ids.Add((JsonNode?)JsonValue.Create(replacement.Id));
						}
						if(ids.Count > 0) {
							asset["replacementIds"] = ids;
						} else {
							asset.Remove("replacementIds");
						}
					}
				}

				string temp = path + ".tmp";
				using(MemoryStream stream = new()) {
					using(Utf8JsonWriter writer = new(stream, new JsonWriterOptions() { Indented = true })) {
						document.WriteTo(writer);
					}
					File.WriteAllBytes(temp, stream.ToArray());
				}
				File.Move(temp, path, true);
				report("Build: replacement assignments written to " + path);
			} catch(Exception ex) {
				report("Build: warning: assets.json could not be updated: " + ex.Message);
			}
		}
	}
}
