using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mesen.HdStudio
{
	internal sealed class HdStudioArchaeologyCatalogAsset
	{
		public string Id = "";
		public string Kind = "";
		public string Name = "";
		public string Source = "archaeology";
		public string Provenance = "";
		public double Confidence;
		public string ValidationStatus = "";
		public string GraphNode = "";
		public int Width = 8;
		public int Height = 8;
		public int OriginX = 0;
		public int OriginY = 0;
		public string OriginalImage = "";
		public string Pattern = "";
		public string? TileData;
		public string ChrType = "NesChrRom";
		public int ChrAddress = -1;
		public string Palette = "";
		public bool Observed;
		public bool Replaceable = true;
		public readonly List<string> PaletteVariants = new();
		public readonly List<string> Notes = new();

		public void WriteJson(Utf8JsonWriter json)
		{
			json.WriteStartObject();
			json.WriteString("id", Id);
			json.WriteString("kind", Kind);
			json.WriteString("name", Name);
			json.WriteString("label", "");
			json.WriteString("source", Source);
			json.WriteString("provenance", Provenance);
			json.WriteNumber("confidence", Confidence);
			json.WriteString("graphNode", GraphNode);
			json.WriteBoolean("observed", Observed);
			json.WriteBoolean("replaceable", Replaceable);
			json.WriteStartObject("validation");
			json.WriteString("status", ValidationStatus);
			json.WriteStartArray("notes");
			foreach(string note in Notes) {
				json.WriteStringValue(note);
			}
			json.WriteEndArray();
			json.WriteEndObject();
			json.WriteStartObject("dimensions");
			json.WriteNumber("width", Width);
			json.WriteNumber("height", Height);
			json.WriteNumber("originX", OriginX);
			json.WriteNumber("originY", OriginY);
			json.WriteEndObject();
			json.WriteStartObject("boundingBox");
			json.WriteNumber("x", 0);
			json.WriteNumber("y", 0);
			json.WriteNumber("width", Width);
			json.WriteNumber("height", Height);
			json.WriteEndObject();
			json.WriteStartObject("anchor");
			json.WriteNumber("x", 0);
			json.WriteNumber("y", 0);
			json.WriteEndObject();
			json.WriteStartObject("transparency");
			json.WriteBoolean("hasTransparency", true);
			json.WriteNumber("transparentColorIndex", 0);
			json.WriteEndObject();
			json.WriteStartArray("paletteVariants");
			foreach(string palette in PaletteVariants) {
				json.WriteStringValue(palette);
			}
			json.WriteEndArray();
			json.WriteStartArray("tiles");
			json.WriteStartObject();
			json.WriteString("pattern", Pattern);
			if(!string.IsNullOrEmpty(TileData)) {
				json.WriteString("tileData", TileData);
			}
			json.WriteNumber("chrAddress", ChrAddress);
			json.WriteString("chrType", ChrType);
			json.WriteNumber("x", 0);
			json.WriteNumber("y", 0);
			json.WriteBoolean("flipH", false);
			json.WriteBoolean("flipV", false);
			json.WriteBoolean("backgroundPriority", false);
			json.WriteString("palette", Palette);
			json.WriteEndObject();
			json.WriteEndArray();
			json.WriteString("originalImage", OriginalImage);
			json.WriteEndObject();
		}
	}

	internal sealed class HdStudioArchaeologyCatalogResult
	{
		public string CatalogPath = "";
		public string AssetsPath = "";
		public int AssetsExposed;
		public int AssetsObserved;
		public int AssetsStatic;
		public int AssetsMerged;
		public int Candidates;
		public int PaletteCandidates;
		public int Gaps;
		public bool TwentyObservedAvailable;
		public readonly List<string> Errors = new();
	}

	/// <summary>
	/// Projects the evidence graph into the artist-facing catalog and merges the
	/// additive static records into the existing assets.json. Existing records and
	/// approved artwork are preserved by identity; the projection never rewrites
	/// an artist's approved replacement.
	/// </summary>
	internal static class HdStudioArchaeologyCatalog
	{
		public const string SchemaVersion = "1.0.0";
		public const string NeutralPalette = "0F001020";

		public static HdStudioArchaeologyCatalogResult Build(string packFolder, HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			HdStudioArchaeologyOptions options, uint[] palette, Action<string>? log)
		{
			HdStudioArchaeologyCatalogResult result = new();
			string studio = Path.Combine(packFolder, HdStudioReport.StudioFolderName);
			string archaeology = HdStudioEvidenceGraph.GraphFolder(packFolder);
			string thumbs = Path.Combine(archaeology, "thumbs");
			Directory.CreateDirectory(thumbs);
			string romKey = rom.Sha256.Length >= 8 ? rom.Sha256.Substring(0, 8) : "unknown";
			Dictionary<string, string> existingAssetByKey = LoadExistingAssetIds(packFolder);

			List<HdStudioArchaeologyCatalogAsset> assets = new();
			List<ulong> selectedSilhouetteBits = new();

			List<HdStudioEvidenceNode> observedPatterns = graph.Nodes.Values
				.Where(n => n.Type == "pattern" && n.Status == "runtime-observed" && n.Properties.GetValueOrDefault("isBlank") != "true")
				.OrderByDescending(n => ParseInt(n.Properties.GetValueOrDefault("observedPixels", "0")))
				.ThenBy(n => n.Id, StringComparer.Ordinal)
				.ToList();
			List<HdStudioEvidenceNode> staticPatterns = graph.Nodes.Values
				.Where(n => n.Type == "pattern" && n.Status != "runtime-observed" && n.Properties.GetValueOrDefault("isBlank") != "true")
				.OrderBy(n => n.Id, StringComparer.Ordinal)
				.ToList();

			void AddPatternAsset(HdStudioEvidenceNode node, bool observed)
			{
				if(assets.Count >= options.MaxAssets) {
					return;
				}
				if(!int.TryParse(node.Properties.GetValueOrDefault("firstTileIndex", "-1"), out int tileIndex) || tileIndex < 0) {
					return;
				}
				byte[] tile = rom.GetChrTile(tileIndex);
				if(tile.All(b => b == 0)) {
					return;
				}
				ulong silhouette = Silhouette(tile);
				foreach(ulong selected in selectedSilhouetteBits) {
					if(BitCount(silhouette ^ selected) <= 2) {
						return;
					}
				}
				string? observedPalette = node.Properties.GetValueOrDefault("observedPalettes", "")
					.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
				string paletteValue = observed && observedPalette is { Length: 8 } ? observedPalette : NeutralPalette;
				string hash = node.Properties.GetValueOrDefault("sha256", node.Id).Substring(0, Math.Min(16, node.Properties.GetValueOrDefault("sha256", node.Id).Length));
				string patternKey = tileIndex.ToString("X", CultureInfo.InvariantCulture);
				string canonicalKey = patternKey.ToUpperInvariant() + "|" + paletteValue.ToUpperInvariant();
				string assetId = existingAssetByKey.TryGetValue(canonicalKey, out string? existingId)
					? existingId
					: (observed ? "rawPattern:" : "staticPattern:") + romKey + ":" + hash.Substring(0, Math.Min(12, hash.Length));
				if(assets.Any(a => string.Equals(a.Id, assetId, StringComparison.OrdinalIgnoreCase))) {
					return;
				}
				HdStudioArchaeologyCatalogAsset asset = new() {
					Id = assetId,
					Kind = observed ? "rawPattern" : "staticPattern",
					Name = "CHR pattern " + tileIndex.ToString("X3", CultureInfo.InvariantCulture) + (observed ? " (observed)" : " (static)"),
					Provenance = "CHR-ROM physical tile index 0x" + tileIndex.ToString("X", CultureInfo.InvariantCulture) +
						", bank " + node.Properties.GetValueOrDefault("firstBank", "?") +
						", offset 0x" + node.Properties.GetValueOrDefault("firstPhysicalOffset", "?") +
						"; content " + node.Properties.GetValueOrDefault("sha256", "") +
						(observed ? "; runtime observed " + node.Properties.GetValueOrDefault("observedPixels", "0") + " pixels" : "; static only, palette unresolved"),
					Confidence = observed ? 0.7 : 0.35,
					ValidationStatus = observed ? "runtime-observed" : "static-extracted",
					GraphNode = node.Id,
					Pattern = patternKey,
					ChrType = "NesChrRom",
					ChrAddress = tileIndex * 16,
					Palette = paletteValue,
					Observed = observed,
					Replaceable = true
				};
				asset.Notes.Add(observed
					? "Static physical pattern identity plus observed pixels. The catalog does not claim that this is the whole semantic object."
					: "Static physical CHR pattern. No runtime observation or semantic label is implied; replacement applicability is unverified.");
				foreach(string p in node.Properties.GetValueOrDefault("observedPalettes", "").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
					if(p.Length == 8 && !asset.PaletteVariants.Contains(p)) {
						asset.PaletteVariants.Add(p);
					}
				}
				if(asset.PaletteVariants.Count == 0) {
					asset.PaletteVariants.Add(NeutralPalette);
				}
				string thumb = RenderTileThumbnail(thumbs, assetId, tile, asset.Palette, palette, options.ThumbnailScale);
				if(thumb.Length > 0) {
					asset.OriginalImage = thumb;
				}
				assets.Add(asset);
				selectedSilhouetteBits.Add(silhouette);
				if(observed) {
					result.AssetsObserved++;
				} else {
					result.AssetsStatic++;
				}
			}

			foreach(HdStudioEvidenceNode node in observedPatterns) {
				AddPatternAsset(node, true);
			}
			foreach(HdStudioEvidenceNode node in staticPatterns) {
				AddPatternAsset(node, false);
			}
			foreach(HdStudioEvidenceNode node in graph.Nodes.Values.Where(n => n.Type == "chrRamObservation")) {
				if(assets.Count >= options.MaxAssets) {
					break;
				}
				string tileData = node.Properties.GetValueOrDefault("tileData", "");
				string paletteValue = node.Properties.GetValueOrDefault("palette", NeutralPalette);
				if(tileData.Length != 32 || paletteValue.Length != 8) {
					continue;
				}
				string ramKey = tileData.ToUpperInvariant() + "|" + paletteValue.ToUpperInvariant();
				string assetId = existingAssetByKey.TryGetValue(ramKey, out string? existingRamId)
					? existingRamId
					: "rawPattern:" + romKey + ":rt:" + HdStudioEvidenceGraph.StableHash(tileData + "|" + paletteValue).Substring(0, 12);
				if(assets.Any(a => string.Equals(a.Id, assetId, StringComparison.OrdinalIgnoreCase))) {
					continue;
				}
				HdStudioArchaeologyCatalogAsset asset = new() {
					Id = assetId,
					Kind = "rawPattern",
					Name = "CHR-RAM pattern " + tileData.Substring(0, 8) + " (observed)",
					Provenance = "Runtime CHR-RAM content observed in tiles.txt; static upload path unresolved",
					Confidence = 0.6,
					ValidationStatus = "runtime-observed-chr-ram",
					GraphNode = node.Id,
					Pattern = tileData,
					TileData = tileData,
					ChrType = "NesChrRam",
					Palette = paletteValue,
					Observed = true,
					Replaceable = true
				};
				asset.Notes.Add("CHR-RAM identity is a runtime content key, not a physical cartridge tile. PositionCheck may be needed for a scene-isolated replacement.");
				asset.PaletteVariants.Add(paletteValue);
				byte[] tile = Convert.FromHexString(tileData);
				string thumb = RenderTileThumbnail(thumbs, assetId, tile, asset.Palette, palette, options.ThumbnailScale);
				if(thumb.Length > 0) {
					asset.OriginalImage = thumb;
				}
				assets.Add(asset);
				result.AssetsObserved++;
			}

			result.AssetsExposed = assets.Count;
			result.TwentyObservedAvailable = result.AssetsObserved >= 20;

			List<HdStudioEvidenceNode> candidates = graph.Nodes.Values
				.Where(n => n.Type == "candidatePatternStream")
				.OrderByDescending(n => ParseDouble(n.Properties.GetValueOrDefault("meanScore", "0")))
				.ThenBy(n => n.Id, StringComparer.Ordinal)
				.Take(options.MaxCandidateStreams)
				.ToList();
			List<HdStudioEvidenceNode> paletteCandidates = graph.Nodes.Values
				.Where(n => n.Type == "candidatePaletteTable")
				.OrderByDescending(n => ParseDouble(n.Properties.GetValueOrDefault("score", "0")))
				.ThenBy(n => n.Id, StringComparer.Ordinal)
				.Take(options.MaxPaletteCandidates)
				.ToList();
			List<HdStudioGap> gaps = graph.Gaps.Values
				.OrderByDescending(g => g.Priority)
				.ThenBy(g => g.Id, StringComparer.Ordinal)
				.Take(80)
				.ToList();

			result.CatalogPath = Path.Combine(archaeology, "catalog.json");
			string temp = result.CatalogPath + ".tmp";
			using(FileStream stream = File.Create(temp)) {
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
				json.WriteStartObject();
				json.WriteString("schemaVersion", SchemaVersion);
				json.WriteString("generatedBy", "RetroArcDo HD Studio RetroArchaeology");
				json.WriteString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
				json.WriteString("engineVersion", HdStudioEvidenceGraph.EngineVersion);
				json.WriteString("engineFingerprint", graph.EngineFingerprint);
				json.WriteString("runFingerprint", graph.RunFingerprint);
				json.WriteStartObject("rom");
				json.WriteString("sha256", rom.Sha256);
				json.WriteString("sha1", rom.Sha1);
				json.WriteString("fileName", rom.FileName);
				json.WriteNumber("mapper", rom.Mapper);
				json.WriteNumber("submapper", rom.Submapper);
				json.WriteString("chrMode", rom.ChrMode);
				json.WriteNumber("prgBytes", rom.Prg.Length);
				json.WriteNumber("chrBytes", rom.Chr.Length);
				json.WriteBoolean("nes20", rom.Nes20);
				json.WriteBoolean("hasTrainer", rom.HasTrainer);
				json.WriteEndObject();
				json.WriteStartObject("summary");
				json.WriteNumber("chrBanks", graph.CountNodes("chrBank"));
				json.WriteNumber("chrPages", graph.CountNodes("chrPage"));
				json.WriteNumber("uniquePatterns", graph.CountNodes("pattern") - (graph.Nodes.Values.Any(n => n.Properties.GetValueOrDefault("isBlank") == "true") ? 1 : 0));
				json.WriteNumber("blankPatternOccurrences", graph.Nodes.Values.Where(n => n.Properties.GetValueOrDefault("isBlank") == "true").Sum(n => ParseInt(n.Properties.GetValueOrDefault("occurrenceCount", "0"))));
				json.WriteNumber("observedChrRomPatterns", observedPatterns.Count);
				json.WriteNumber("observedChrRamPatterns", graph.CountNodes("chrRamObservation"));
				json.WriteNumber("assetsExposed", result.AssetsExposed);
				json.WriteNumber("assetsObserved", result.AssetsObserved);
				json.WriteNumber("assetsStatic", result.AssetsStatic);
				json.WriteBoolean("twentyObservedAvailable", result.TwentyObservedAvailable);
				json.WriteNumber("codeBlocks", graph.CountNodes("codeBlock"));
				json.WriteNumber("ppuWrites", graph.CountNodes("ppuReference"));
				json.WriteNumber("dataReferences", graph.CountNodes("dataReference"));
				json.WriteNumber("pointerTables", graph.CountNodes("candidateTable"));
				json.WriteNumber("candidateStreams", candidates.Count);
				json.WriteNumber("paletteCandidates", paletteCandidates.Count);
				json.WriteNumber("openGaps", graph.Gaps.Values.Count(g => g.Status == "open"));
				json.WriteEndObject();
				json.WriteStartArray("assets");
				foreach(HdStudioArchaeologyCatalogAsset asset in assets) {
					asset.WriteJson(json);
				}
				json.WriteEndArray();
				json.WriteStartArray("candidates");
				foreach(HdStudioEvidenceNode candidate in candidates) {
					WriteCandidate(json, candidate);
				}
				json.WriteEndArray();
				json.WriteStartArray("paletteCandidates");
				foreach(HdStudioEvidenceNode candidate in paletteCandidates) {
					WriteCandidate(json, candidate);
				}
				json.WriteEndArray();
				json.WriteStartArray("gaps");
				foreach(HdStudioGap gap in gaps) {
					json.WriteStartObject();
					json.WriteString("id", gap.Id);
					json.WriteString("kind", gap.Kind);
					json.WriteString("description", gap.Description);
					json.WriteString("action", gap.Action);
					json.WriteNumber("priority", gap.Priority);
					json.WriteNumber("expectedGain", gap.ExpectedGain);
					json.WriteNumber("cost", gap.Cost);
					json.WriteString("status", gap.Status);
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteStartArray("notes");
				foreach(string note in graph.Notes) {
					json.WriteStringValue(note);
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			if(File.Exists(result.CatalogPath)) {
				File.Replace(temp, result.CatalogPath, result.CatalogPath + ".prev", true);
			} else {
				File.Move(temp, result.CatalogPath);
			}
			result.Candidates = candidates.Count;
			result.PaletteCandidates = paletteCandidates.Count;
			result.Gaps = gaps.Count;
			log?.Invoke("Archaeology catalog: " + result.AssetsExposed + " assets (" + result.AssetsObserved + " observed, " +
				result.AssetsStatic + " static), " + candidates.Count + " PRG candidate streams, " + gaps.Count + " prioritized gaps -> " + result.CatalogPath);

			if(options.MergeIntoAssets) {
				result.AssetsMerged = MergeIntoAssets(packFolder, log);
				result.AssetsPath = Path.Combine(studio, "assets", "assets.json");
			}
			return result;
		}

		/// <summary>
		/// Adds static/archaeology records to HdStudio/assets/assets.json. Existing
		/// records win by id and by (pattern, palette) identity, so a live/approved
		/// asset is never replaced by a static projection.
		/// </summary>
		public static int MergeIntoAssets(string packFolder, Action<string>? log)
		{
			string studio = Path.Combine(packFolder, HdStudioReport.StudioFolderName);
			string catalogPath = Path.Combine(HdStudioEvidenceGraph.GraphFolder(packFolder), "catalog.json");
			string assetsPath = Path.Combine(studio, "assets", "assets.json");
			if(!File.Exists(catalogPath)) {
				return 0;
			}
			try {
				using JsonDocument catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
				JsonElement catalogRoot = catalog.RootElement;
				if(!catalogRoot.TryGetProperty("assets", out JsonElement catalogAssets) || catalogAssets.ValueKind != JsonValueKind.Array) {
					return 0;
				}
				JsonElement game = catalogRoot.TryGetProperty("rom", out JsonElement romElement) ? romElement : default;
				string romSha1 = GetString(game, "sha1");
				string romSha256 = GetString(game, "sha256");
				int mapper = GetInt(game, "mapper");
				long chrBytes = GetLong(game, "chrBytes");
				string chrMode = GetString(game, "chrMode");

				List<JsonElement> existingAssets = new();
				JsonElement existingRoot = default;
				if(File.Exists(assetsPath)) {
					using JsonDocument existing = JsonDocument.Parse(File.ReadAllText(assetsPath));
					existingRoot = existing.RootElement.Clone();
					if(existingRoot.TryGetProperty("assets", out JsonElement existingList) && existingList.ValueKind == JsonValueKind.Array) {
						foreach(JsonElement item in existingList.EnumerateArray()) {
							existingAssets.Add(item.Clone());
						}
					}
				}

				HashSet<string> existingIds = new(StringComparer.OrdinalIgnoreCase);
				HashSet<string> existingPatternPalettes = new(StringComparer.OrdinalIgnoreCase);
				foreach(JsonElement item in existingAssets) {
					string id = GetString(item, "id");
					string kind = GetString(item, "kind");
					if(id.Length > 0) {
						existingIds.Add(id);
					}
					if(kind == "rawPattern" || kind == "staticPattern") {
						if(item.TryGetProperty("tiles", out JsonElement tiles) && tiles.ValueKind == JsonValueKind.Array) {
							foreach(JsonElement tile in tiles.EnumerateArray()) {
								string pattern = GetString(tile, "pattern");
								string palette = GetString(tile, "palette");
								if(pattern.Length > 0) {
									existingPatternPalettes.Add(pattern + "|" + palette);
								}
							}
						}
					}
				}

				List<HdStudioArchaeologyCatalogAsset> additions = new();
				foreach(JsonElement item in catalogAssets.EnumerateArray()) {
					string id = GetString(item, "id");
					if(id.Length == 0 || existingIds.Contains(id)) {
						continue;
					}
					string pattern = "";
					string palette = "";
					if(item.TryGetProperty("tiles", out JsonElement tiles) && tiles.ValueKind == JsonValueKind.Array && tiles.GetArrayLength() > 0) {
						pattern = GetString(tiles[0], "pattern");
						palette = GetString(tiles[0], "palette");
					}
					if(pattern.Length > 0 && existingPatternPalettes.Contains(pattern + "|" + palette)) {
						continue;
					}
					string validationStatus = "";
					if(item.TryGetProperty("validation", out JsonElement validation)) {
						validationStatus = GetString(validation, "status");
					}
					HdStudioArchaeologyCatalogAsset asset = new() {
						Id = id,
						Kind = GetString(item, "kind"),
						Name = GetString(item, "name"),
						Provenance = GetString(item, "provenance"),
						Confidence = GetDouble(item, "confidence"),
						ValidationStatus = validationStatus,
						GraphNode = GetString(item, "graphNode"),
						Pattern = pattern,
						ChrType = "NesChrRom",
						Palette = palette,
						Observed = item.TryGetProperty("observed", out JsonElement observed) && observed.ValueKind == JsonValueKind.True,
						Replaceable = !item.TryGetProperty("replaceable", out JsonElement replaceable) || replaceable.ValueKind == JsonValueKind.True
					};
					if(item.TryGetProperty("dimensions", out JsonElement dims)) {
						asset.Width = GetInt(dims, "width");
						asset.Height = GetInt(dims, "height");
					}
					asset.OriginalImage = GetString(item, "originalImage");
					if(item.TryGetProperty("tiles", out JsonElement tileList) && tileList.ValueKind == JsonValueKind.Array && tileList.GetArrayLength() > 0) {
						asset.TileData = tileList[0].TryGetProperty("tileData", out JsonElement td) && td.ValueKind == JsonValueKind.String ? td.GetString() : null;
						asset.ChrType = GetString(tileList[0], "chrType", "NesChrRom");
						asset.ChrAddress = GetInt(tileList[0], "chrAddress", -1);
					}
					foreach(string p in GetStringArray(item, "paletteVariants")) {
						asset.PaletteVariants.Add(p);
					}
					foreach(string note in GetStringArray(item, "validation", "notes")) {
						asset.Notes.Add(note);
					}
					if(asset.Width <= 0) asset.Width = 8;
					if(asset.Height <= 0) asset.Height = 8;
					additions.Add(asset);
					existingIds.Add(id);
				}
				if(additions.Count == 0) {
					return 0;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(assetsPath) ?? ".");
				string temp = assetsPath + ".tmp";
				using(FileStream stream = File.Create(temp)) {
					using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
					json.WriteStartObject();
					json.WriteString("schemaVersion", "1.0.0");
					json.WriteString("generatedBy", existingRoot.ValueKind == JsonValueKind.Object ? GetString(existingRoot, "generatedBy", "RetroArcDo HD Studio") : "RetroArcDo HD Studio");
					json.WriteString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
					if(existingRoot.ValueKind == JsonValueKind.Object && existingRoot.TryGetProperty("game", out JsonElement existingGame)) {
						json.WritePropertyName("game");
						existingGame.WriteTo(json);
					} else {
						json.WriteStartObject("game");
						json.WriteString("romSha1", romSha1);
						json.WriteString("romSha256", romSha256);
						json.WriteNumber("mapper", mapper);
						json.WriteNumber("chrRomBytes", chrBytes);
						json.WriteBoolean("hasChrRom", string.Equals(chrMode, "CHR-ROM", StringComparison.OrdinalIgnoreCase));
						json.WriteEndObject();
					}
					json.WriteStartArray("assets");
					foreach(JsonElement item in existingAssets) {
						item.WriteTo(json);
					}
					foreach(HdStudioArchaeologyCatalogAsset asset in additions) {
						asset.WriteJson(json);
					}
					json.WriteEndArray();
					json.WriteStartArray("replacementAssets");
					if(existingRoot.ValueKind == JsonValueKind.Object && existingRoot.TryGetProperty("replacementAssets", out JsonElement replacementAssets) && replacementAssets.ValueKind == JsonValueKind.Array) {
						foreach(JsonElement item in replacementAssets.EnumerateArray()) {
							item.WriteTo(json);
						}
					}
					json.WriteEndArray();
					json.WriteStartArray("replacementConditions");
					if(existingRoot.ValueKind == JsonValueKind.Object && existingRoot.TryGetProperty("replacementConditions", out JsonElement replacementConditions) && replacementConditions.ValueKind == JsonValueKind.Array) {
						foreach(JsonElement item in replacementConditions.EnumerateArray()) {
							item.WriteTo(json);
						}
					}
					json.WriteEndArray();
					json.WriteEndObject();
				}
				if(File.Exists(assetsPath)) {
					File.Replace(temp, assetsPath, assetsPath + ".prev", true);
				} else {
					File.Move(temp, assetsPath);
				}
				log?.Invoke("Archaeology assets: " + additions.Count + " additive static records merged into " + assetsPath);
				return additions.Count;
			} catch(Exception ex) {
				log?.Invoke("Archaeology assets merge failed: " + ex.Message);
				return 0;
			}
		}

		private static Dictionary<string, string> LoadExistingAssetIds(string packFolder)
		{
			Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
			string assetsPath = Path.Combine(packFolder, HdStudioReport.StudioFolderName, "assets", "assets.json");
			if(!File.Exists(assetsPath)) {
				return map;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
				if(!doc.RootElement.TryGetProperty("assets", out JsonElement list) || list.ValueKind != JsonValueKind.Array) {
					return map;
				}
				foreach(JsonElement asset in list.EnumerateArray()) {
					string id = GetString(asset, "id");
					string kind = GetString(asset, "kind");
					if(id.Length == 0 || (kind != "rawPattern" && kind != "staticPattern") ||
						!asset.TryGetProperty("tiles", out JsonElement tiles) || tiles.ValueKind != JsonValueKind.Array) {
						continue;
					}
					foreach(JsonElement tile in tiles.EnumerateArray()) {
						string pattern = GetString(tile, "pattern");
						string tileData = tile.TryGetProperty("tileData", out JsonElement td) && td.ValueKind == JsonValueKind.String ? td.GetString() ?? "" : "";
						string palette = GetString(tile, "palette");
						string patternKey;
						if(tileData.Length == 32) {
							patternKey = tileData.ToUpperInvariant();
						} else if(int.TryParse(pattern, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int tileIndex)) {
							//Existing rawPattern records use the capture's zero-padded key
							//("0101"); normalize both forms to the numeric tile identity.
							patternKey = tileIndex.ToString("X", CultureInfo.InvariantCulture);
						} else {
							patternKey = pattern.ToUpperInvariant();
						}
						string key = patternKey + "|" + palette.ToUpperInvariant();
						if(key.Length > 1 && !map.ContainsKey(key)) {
							map[key] = id;
						}
					}
				}
			} catch {
			}
			return map;
		}

		private static void WriteCandidate(Utf8JsonWriter json, HdStudioEvidenceNode node)
		{
			json.WriteStartObject();
			json.WriteString("id", node.Id);
			json.WriteString("type", node.Type);
			json.WriteString("name", node.Name);
			json.WriteString("status", node.Status);
			json.WriteNumber("confidence", node.Confidence);
			json.WriteString("provenance", node.Provenance);
			json.WriteStartObject("properties");
			foreach(KeyValuePair<string, string> property in node.Properties) {
				json.WriteString(property.Key, property.Value);
			}
			json.WriteEndObject();
			json.WriteEndObject();
		}

		private static string RenderTileThumbnail(string folder, string assetId, byte[] tile, string palette, uint[] nesPalette, int scale)
		{
			try {
				Directory.CreateDirectory(folder);
				scale = Math.Clamp(scale, 2, 8);
				int width = 8;
				int height = 8;
				uint[] pixels = new uint[width * height];
				for(int y = 0; y < 8; y++) {
					for(int x = 0; x < 8; x++) {
						int color = ((tile[y] >> (7 - x)) & 1) | (((tile[y + 8] >> (7 - x)) & 1) << 1);
						pixels[y * width + x] = PaletteToArgb(nesPalette, palette, color);
					}
				}
				uint[] scaled = HdStudioPng.Upscale(pixels, width, height, scale);
				string safe = new string(assetId.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
				if(safe.Length > 80) safe = safe.Substring(0, 80);
				string path = Path.Combine(folder, safe + ".png");
				HdStudioPng.Write(path, scaled, width * scale, height * scale);
				return Path.Combine("archaeology", "thumbs", safe + ".png").Replace('\\', '/');
			} catch {
				return "";
			}
		}

		private static uint PaletteToArgb(uint[] nesPalette, string palette, int colorIndex)
		{
			if(colorIndex == 0) {
				return 0;
			}
			if(!uint.TryParse(palette, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) {
				return 0xFF808080;
			}
			int shift = (3 - colorIndex) * 8;
			int nesColor = (int)((value >> shift) & 0x3F);
			if(nesColor < 0 || nesColor >= nesPalette.Length) {
				return 0xFF808080;
			}
			return nesPalette[nesColor] | 0xFF000000;
		}

		private static ulong Silhouette(byte[] tile)
		{
			ulong hash = 0;
			for(int y = 0; y < 8; y++) {
				for(int x = 0; x < 8; x++) {
					int pixel = ((tile[y] >> (7 - x)) & 1) | (((tile[y + 8] >> (7 - x)) & 1) << 1);
					hash = (hash << 1) | (pixel != 0 ? 1UL : 0UL);
				}
			}
			return hash;
		}

		private static int BitCount(ulong value)
		{
			int count = 0;
			while(value != 0) {
				value &= value - 1;
				count++;
			}
			return count;
		}

		private static int ParseInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
		private static double ParseDouble(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0;

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		private static int GetInt(JsonElement element, string name, int fallback = 0)
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
		}

		private static long GetLong(JsonElement element, string name, long fallback = 0)
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : fallback;
		}

		private static double GetDouble(JsonElement element, string name, double fallback = 0)
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : fallback;
		}

		private static IEnumerable<string> GetStringArray(JsonElement element, params string[] path)
		{
			JsonElement current = element;
			foreach(string part in path) {
				if(current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) {
					yield break;
				}
			}
			if(current.ValueKind != JsonValueKind.Array) {
				yield break;
			}
			foreach(JsonElement item in current.EnumerateArray()) {
				if(item.ValueKind == JsonValueKind.String) {
					yield return item.GetString() ?? "";
				}
			}
		}

		private static string ShortHash(string value)
		{
			byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
			return Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant();
		}
	}
}
