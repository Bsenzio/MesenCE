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
	/// <summary>
	/// Hardware-aware object picking from a paused live-capture frame.
	///
	/// The picked asset is not a PNG crop: it keeps the mapper-resolved pattern bytes,
	/// palette, flips, priority and the original NES cell positions, so the compiler can
	/// emit a standard Mesen HD Pack entry for it. The screenshot is only a preview.
	/// </summary>
	internal static class HdStudioPicking
	{
		public const string PickedFileName = "picked-assets.json";
		public const string PauseFileName = "pause-frame.json";

		internal sealed class PauseSprite
		{
			public int Oam;
			public int Part;
			public int X;
			public int Y;
			public int Tile;
			public string Palette = "";
			public bool FlipH;
			public bool FlipV;
			public bool BackgroundPriority;
			public string Pattern = "";
			public string ChrType = "";
			public int ChrAddress = -1;
			public string Data = "";
		}

		internal sealed class PauseBackgroundTile
		{
			public int X;
			public int Y;
			public int TileX;
			public int TileY;
			public int Tile;
			public int PaletteIndex;
			public string Palette = "";
			public string Pattern = "";
			public string ChrType = "";
			public int ChrAddress = -1;
			public string Data = "";
		}

		internal sealed class PauseFrame
		{
			public int Frame;
			public string Screenshot = "";
			public string ScreenshotPath = "";
			public int ScrollX;
			public int ScrollY;
			public string Mirroring = "";
			public bool LargeSprites;
			public int SpritePatternAddr;
			public int BackgroundPatternAddr;
			public int MapperId;
			public readonly List<PauseSprite> Sprites = new();
			public readonly List<PauseBackgroundTile> Background = new();

			public static PauseFrame? Load(string packFolder)
			{
				string path = Path.Combine(packFolder, HdStudioReport.StudioFolderName, "live", PauseFileName);
				if(!File.Exists(path)) {
					return null;
				}
				try {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
					JsonElement root = doc.RootElement;
					PauseFrame frame = new() {
						Frame = GetInt(root, "frame"),
						Screenshot = GetString(root, "screenshot"),
						ScrollX = GetInt(root, "scrollX"),
						ScrollY = GetInt(root, "scrollY"),
						Mirroring = GetString(root, "mirroring"),
						LargeSprites = GetBool(root, "largeSprites"),
						SpritePatternAddr = GetInt(root, "spritePatternAddr"),
						BackgroundPatternAddr = GetInt(root, "backgroundPatternAddr"),
						MapperId = GetInt(root, "mapperId")
					};
					frame.ScreenshotPath = string.IsNullOrEmpty(frame.Screenshot) ? "" :
						Path.Combine(Path.GetDirectoryName(path) ?? "", frame.Screenshot);
					if(root.TryGetProperty("sprites", out JsonElement sprites) && sprites.ValueKind == JsonValueKind.Array) {
						foreach(JsonElement item in sprites.EnumerateArray()) {
							frame.Sprites.Add(new PauseSprite() {
								Oam = item.TryGetProperty("renderSlot", out JsonElement slot) ? GetInt(item, "renderSlot") : GetInt(item, "oam"),
								Part = GetInt(item, "part"),
								X = GetInt(item, "x"),
								Y = GetInt(item, "y"),
								Tile = GetInt(item, "tile"),
								Palette = GetString(item, "palette"),
								FlipH = GetBool(item, "flipH"),
								FlipV = GetBool(item, "flipV"),
								BackgroundPriority = GetBool(item, "backgroundPriority"),
								Pattern = GetString(item, "pattern"),
								ChrType = GetString(item, "chrType"),
								ChrAddress = GetInt(item, "chrAddress", -1),
								Data = GetString(item, "data")
							});
						}
					}
					if(root.TryGetProperty("background", out JsonElement background) && background.ValueKind == JsonValueKind.Array) {
						foreach(JsonElement item in background.EnumerateArray()) {
							frame.Background.Add(new PauseBackgroundTile() {
								X = GetInt(item, "x"),
								Y = GetInt(item, "y"),
								TileX = GetInt(item, "tileX"),
								TileY = GetInt(item, "tileY"),
								Tile = GetInt(item, "tile"),
								PaletteIndex = GetInt(item, "paletteIndex"),
								Palette = GetString(item, "palette"),
								Pattern = GetString(item, "pattern"),
								ChrType = GetString(item, "chrType"),
								ChrAddress = GetInt(item, "chrAddress", -1),
								Data = GetString(item, "data")
							});
						}
					}
					return frame;
				} catch {
					return null;
				}
			}
		}

		internal sealed class PickCandidate
		{
			public string Kind = "";
			public string Name = "";
			public string Detail = "";
			public int X;
			public int Y;
			public int Width;
			public int Height;
			public double Confidence;
			public readonly List<PauseSprite> Sprites = new();
			public readonly List<PauseBackgroundTile> Background = new();
			public HdStudioCatalogAsset? Asset;
		}

		public static List<PickCandidate> GetCandidates(PauseFrame frame, int x, int y, uint[] nesPalette)
		{
			List<PickCandidate> result = new();
			List<PauseSprite> hits = frame.Sprites
				.Where(s => x >= s.X && x < s.X + 8 && y >= s.Y && y < s.Y + 8)
				.OrderBy(s => s.Oam).ThenBy(s => s.Part)
				.ToList();

			foreach(PauseSprite hit in hits.Take(6)) {
				PickCandidate candidate = new() {
					Kind = "hardwareSprite",
					Name = $"Render slot {hit.Oam}" + (frame.LargeSprites ? $" part {hit.Part}" : ""),
					Detail = $"8x8 at {hit.X},{hit.Y} - palette {hit.Palette}",
					X = hit.X,
					Y = hit.Y,
					Width = 8,
					Height = 8,
					Confidence = 0.95
				};
				candidate.Sprites.Add(hit);
				Finish(candidate, frame, nesPalette);
				result.Add(candidate);
			}

			if(hits.Count > 0) {
				List<PauseSprite> composition = BuildComposition(frame, hits[0]);
				if(composition.Count > 1) {
					PickCandidate candidate = new() {
						Kind = "metasprite",
						Name = $"Reconstructed composition ({composition.Count} parts)",
						Detail = $"OAM {string.Join("+", composition.Select(s => s.Oam).Distinct().Take(8))}",
						Confidence = 0.7
					};
					candidate.Sprites.AddRange(composition);
					Finish(candidate, frame, nesPalette);
					result.Add(candidate);
				}
			}

			PauseBackgroundTile? tile = frame.Background
				.Where(t => x >= t.X && x < t.X + 8 && y >= t.Y && y < t.Y + 8)
				.FirstOrDefault();
			if(tile != null) {
				PickCandidate single = new() {
					Kind = "backgroundTile",
					Name = $"Background tile {tile.Tile:X2}",
					Detail = $"nametable {tile.TileX},{tile.TileY} - palette {tile.Palette}",
					Confidence = 0.9
				};
				single.Background.Add(tile);
				Finish(single, frame, nesPalette);
				result.Add(single);

				int blockX = (tile.TileX / 2) * 2;
				int blockY = (tile.TileY / 2) * 2;
				List<PauseBackgroundTile> block = frame.Background
					.Where(t => t.TileX >= blockX && t.TileX < blockX + 2 && t.TileY >= blockY && t.TileY < blockY + 2)
					.ToList();
				if(block.Count == 4 && block.Select(t => t.Palette).Distinct().Count() == 1) {
					PickCandidate grouped = new() {
						Kind = "backgroundBlock",
						Name = "Background 2x2 composition",
						Detail = $"nametable block {blockX},{blockY} - palette {block[0].Palette}",
						Confidence = 0.8
					};
					grouped.Background.AddRange(block);
					Finish(grouped, frame, nesPalette);
					result.Add(grouped);
				}
			}

			return result;
		}

		private static List<PauseSprite> BuildComposition(PauseFrame frame, PauseSprite seed)
		{
			HashSet<PauseSprite> members = new() { seed };
			Queue<PauseSprite> queue = new();
			queue.Enqueue(seed);
			while(queue.Count > 0 && members.Count < 64) {
				PauseSprite current = queue.Dequeue();
				foreach(PauseSprite other in frame.Sprites) {
					if(members.Contains(other)) {
						continue;
					}
					bool overlap = current.X < other.X + 8 && other.X < current.X + 8 &&
						current.Y < other.Y + 8 && other.Y < current.Y + 8;
					bool verticalPair = current.X == other.X && Math.Abs(current.Y - other.Y) == 8 &&
						current.Palette == other.Palette && Math.Abs(current.Tile - other.Tile) == 1;
					int tileDelta = Math.Abs(current.Tile - other.Tile);
					bool horizontalPair = current.Y == other.Y && Math.Abs(current.X - other.X) == 8 &&
						current.Palette == other.Palette && tileDelta > 0 && tileDelta <= 2;
					//Touching alone is not composition evidence: a row of enemies would
					//otherwise become one object. The user can still group manually.
					if(overlap || verticalPair || horizontalPair) {
						members.Add(other);
						queue.Enqueue(other);
					}
				}
			}
			return members.OrderBy(s => s.Oam).ThenBy(s => s.Part).ToList();
		}

		private static void Finish(PickCandidate candidate, PauseFrame frame, uint[] nesPalette)
		{
			int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
			if(candidate.Sprites.Count > 0) {
				minX = candidate.Sprites.Min(s => s.X);
				minY = candidate.Sprites.Min(s => s.Y);
				maxX = candidate.Sprites.Max(s => s.X + 8);
				maxY = candidate.Sprites.Max(s => s.Y + 8);
			} else {
				minX = candidate.Background.Min(t => t.X);
				minY = candidate.Background.Min(t => t.Y);
				maxX = candidate.Background.Max(t => t.X + 8);
				maxY = candidate.Background.Max(t => t.Y + 8);
			}
			candidate.X = minX;
			candidate.Y = minY;
			candidate.Width = Math.Max(8, maxX - minX);
			candidate.Height = Math.Max(8, maxY - minY);
			candidate.Asset = BuildAsset(candidate, frame, nesPalette);
		}

		private static HdStudioCatalogAsset BuildAsset(PickCandidate candidate, PauseFrame frame, uint[] nesPalette)
		{
			string kind = candidate.Kind is "backgroundTile" or "backgroundBlock" ? "pickedBackground" : "pickedObject";
			string signature = string.Join("|", candidate.Sprites.Select(s => $"{s.Oam}:{s.Part}:{s.X}:{s.Y}:{s.Pattern}"))
				+ string.Join("|", candidate.Background.Select(t => $"{t.TileX}:{t.TileY}:{t.Pattern}"));
			string id = "picked:" + ShortHash(kind + "|" + candidate.Kind + "|" + signature);
			HdStudioCatalogAsset asset = new() {
				Id = id,
				Kind = kind,
				Name = candidate.Name,
				Label = "",
				Source = "liveCapture",
				Provenance = $"live frame {frame.Frame}, user selection ({candidate.Kind}); {candidate.Detail}",
				Confidence = candidate.Confidence,
				ValidationStatus = "observed-live",
				Width = candidate.Width,
				Height = candidate.Height,
				OriginX = candidate.X,
				OriginY = candidate.Y,
				OriginalImage = ""
			};

			foreach(PauseSprite sprite in candidate.Sprites) {
				asset.Tiles.Add(new HdStudioCatalogTile() {
					Pattern = sprite.Pattern,
					TileData = string.IsNullOrWhiteSpace(sprite.Data) ? null : sprite.Data,
					ChrType = sprite.ChrType,
					ChrAddress = sprite.ChrAddress,
					X = sprite.X - candidate.X,
					Y = sprite.Y - candidate.Y,
					FlipH = sprite.FlipH,
					FlipV = sprite.FlipV,
					BackgroundPriority = sprite.BackgroundPriority,
					Palette = sprite.Palette
				});
				if(!asset.PaletteVariants.Contains(sprite.Palette)) {
					asset.PaletteVariants.Add(sprite.Palette);
				}
			}
			foreach(PauseBackgroundTile tile in candidate.Background) {
				asset.Tiles.Add(new HdStudioCatalogTile() {
					Pattern = tile.Pattern,
					TileData = string.IsNullOrWhiteSpace(tile.Data) ? null : tile.Data,
					ChrType = tile.ChrType,
					ChrAddress = tile.ChrAddress,
					X = tile.X - candidate.X,
					Y = tile.Y - candidate.Y,
					FlipH = false,
					FlipV = false,
					BackgroundPriority = false,
					Palette = tile.Palette
				});
				if(!asset.PaletteVariants.Contains(tile.Palette)) {
					asset.PaletteVariants.Add(tile.Palette);
				}
			}
			return asset;
		}

		public static HdStudioCatalogAsset BuildManualComposition(PickCandidate merged, PauseFrame frame, uint[] nesPalette)
		{
			PickCandidate clean = new() {
				Kind = "manualComposition",
				Name = merged.Name,
				Detail = merged.Detail,
				Confidence = 1.0
			};
			HashSet<string> seenSprites = new();
			foreach(PauseSprite sprite in merged.Sprites) {
				if(seenSprites.Add(sprite.Oam + ":" + sprite.Part)) {
					clean.Sprites.Add(sprite);
				}
			}
			HashSet<string> seenBackground = new();
			foreach(PauseBackgroundTile tile in merged.Background) {
				if(seenBackground.Add(tile.TileX + ":" + tile.TileY)) {
					clean.Background.Add(tile);
				}
			}
			if(clean.Sprites.Count == 0 && clean.Background.Count == 0) {
				throw new InvalidOperationException("The manual composition has no graphical cells.");
			}
			Finish(clean, frame, nesPalette);
			return clean.Asset!;
		}

		public static string SavePickedAsset(string packFolder, HdStudioCatalogAsset asset, Action<string>? log = null)
		{
			string studio = Path.Combine(packFolder, HdStudioReport.StudioFolderName);
			string folder = Path.Combine(studio, "live");
			Directory.CreateDirectory(folder);
			string path = Path.Combine(folder, PickedFileName);

			List<JsonElement> existing = new();
			if(File.Exists(path)) {
				try {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
					if(doc.RootElement.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array) {
						foreach(JsonElement item in assets.EnumerateArray()) {
							existing.Add(item.Clone());
						}
					}
				} catch { }
			}

			existing.RemoveAll(e => string.Equals(GetString(e, "id"), asset.Id, StringComparison.OrdinalIgnoreCase));
			string temp = path + ".tmp";
			using(FileStream stream = File.Create(temp))
			using(Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true })) {
				json.WriteStartObject();
				json.WriteString("schemaVersion", "1.0.0");
				json.WriteString("generatedBy", "RetroArcDo HD Studio");
				json.WriteString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
				json.WriteStartArray("assets");
				foreach(JsonElement element in existing) {
					element.WriteTo(json);
				}
				WriteAsset(json, asset);
				json.WriteEndArray();
				json.WriteEndObject();
			}
			File.Move(temp, path, true);
			log?.Invoke($"Live pick saved: {asset.Name} ({asset.Id}).");
			return path;
		}

		/// <summary>Merged into assets.json by the exporter so a subsequent --assets run
		/// never loses a user selection made during live play.</summary>
		public static List<JsonElement> ReadPickedAssetElements(string packFolder)
		{
			List<JsonElement> result = new();
			string path = Path.Combine(packFolder, HdStudioReport.StudioFolderName, "live", PickedFileName);
			if(!File.Exists(path)) {
				return result;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				if(doc.RootElement.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement item in assets.EnumerateArray()) {
						result.Add(item.Clone());
					}
				}
			} catch { }
			return result;
		}

		public static void WriteAsset(Utf8JsonWriter json, HdStudioCatalogAsset asset)
		{
			json.WriteStartObject();
			json.WriteString("id", asset.Id);
			json.WriteString("kind", asset.Kind);
			json.WriteString("name", asset.Name);
			json.WriteString("label", asset.Label);
			json.WriteString("source", asset.Source);
			json.WriteString("provenance", asset.Provenance);
			json.WriteNumber("confidence", Math.Round(asset.Confidence, 2));
			json.WriteStartObject("validation");
			json.WriteString("status", asset.ValidationStatus);
			json.WriteStartArray("notes");
			json.WriteStringValue("Selected from the actual displayed live frame; the pattern bytes are mapper-resolved.");
			json.WriteStringValue("The original game code, tile placement and animation timing are unchanged.");
			json.WriteEndArray();
			json.WriteEndObject();
			json.WriteStartObject("dimensions");
			json.WriteNumber("width", asset.Width);
			json.WriteNumber("height", asset.Height);
			json.WriteNumber("originX", asset.OriginX);
			json.WriteNumber("originY", asset.OriginY);
			json.WriteEndObject();
			if(asset.Tiles.Count > 0) {
				int minX = asset.Tiles.Min(t => t.X);
				int minY = asset.Tiles.Min(t => t.Y);
				int maxX = asset.Tiles.Max(t => t.X + 8);
				int maxY = asset.Tiles.Max(t => t.Y + 8);
				json.WriteStartObject("boundingBox");
				json.WriteNumber("x", minX);
				json.WriteNumber("y", minY);
				json.WriteNumber("width", maxX - minX);
				json.WriteNumber("height", maxY - minY);
				json.WriteEndObject();
				json.WriteStartObject("anchor");
				json.WriteNumber("x", 0);
				json.WriteNumber("y", 0);
				json.WriteEndObject();
			}
			json.WriteStartObject("transparency");
			json.WriteBoolean("hasTransparency", true);
			json.WriteNumber("transparentColorIndex", 0);
			json.WriteEndObject();
			json.WriteStartArray("paletteVariants");
			foreach(string palette in asset.PaletteVariants.Distinct()) {
				json.WriteStringValue(palette);
			}
			json.WriteEndArray();
			json.WriteStartArray("tiles");
			foreach(HdStudioCatalogTile tile in asset.Tiles) {
				json.WriteStartObject();
				json.WriteString("pattern", tile.Pattern);
				if(tile.TileData != null) {
					json.WriteString("tileData", tile.TileData);
				}
				json.WriteNumber("chrAddress", tile.ChrAddress);
				json.WriteString("chrType", tile.ChrType);
				json.WriteNumber("x", tile.X);
				json.WriteNumber("y", tile.Y);
				json.WriteBoolean("flipH", tile.FlipH);
				json.WriteBoolean("flipV", tile.FlipV);
				json.WriteBoolean("backgroundPriority", tile.BackgroundPriority);
				json.WriteString("palette", tile.Palette);
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteString("originalImage", asset.OriginalImage);
			json.WriteEndObject();
		}

		public static string ShortHash(string value)
		{
			byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
			return Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant();
		}

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		private static int GetInt(JsonElement element, string name, int fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
		}

		private static bool GetBool(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}
	}
}
