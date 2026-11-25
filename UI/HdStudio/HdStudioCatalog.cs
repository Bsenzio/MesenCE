using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Typed, read-only view over HdStudio/assets/assets.json.
	///
	/// This is the shared asset catalog used by the native Studio window, the importer and the
	/// compiler. Asset ids are stable (they include the ROM hash and the discovery provenance)
	/// and are never derived from the user-visible name.
	/// </summary>
	internal sealed class HdStudioCatalog
	{
		public string SchemaVersion = "";
		public string RomSha1 = "";
		public int Mapper;
		public bool HasChrRom;
		public readonly List<HdStudioCatalogAsset> Assets = new();

		public string PackFolder = "";
		public string AssetsPath = "";
		public string RomPath = "";

		public static HdStudioCatalog Load(string packFolder)
		{
			HdStudioCatalog catalog = new() { PackFolder = Path.GetFullPath(packFolder) };
			catalog.AssetsPath = Path.Combine(catalog.PackFolder, HdStudioReport.StudioFolderName, "assets", "assets.json");
			if(!File.Exists(catalog.AssetsPath)) {
				return catalog;
			}

			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(catalog.AssetsPath));
			JsonElement root = doc.RootElement;
			catalog.SchemaVersion = GetString(root, "schemaVersion");
			if(root.TryGetProperty("game", out JsonElement game)) {
				catalog.RomSha1 = GetString(game, "romSha1");
				catalog.Mapper = GetInt(game, "mapper");
				catalog.HasChrRom = game.TryGetProperty("hasChrRom", out JsonElement h) && h.ValueKind == JsonValueKind.True;
			}
			if(root.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement element in list.EnumerateArray()) {
					HdStudioCatalogAsset? asset = HdStudioCatalogAsset.FromJson(element, catalog);
					if(asset != null) {
						catalog.Assets.Add(asset);
					}
				}
			}
			return catalog;
		}

		public HdStudioCatalogAsset? Find(string idOrName)
		{
			return Assets.FirstOrDefault(a => string.Equals(a.Id, idOrName, StringComparison.OrdinalIgnoreCase)) ??
				Assets.FirstOrDefault(a => string.Equals(a.Name, idOrName, StringComparison.OrdinalIgnoreCase)) ??
				Assets.FirstOrDefault(a => a.Id.EndsWith(":" + idOrName, StringComparison.OrdinalIgnoreCase));
		}

		public IEnumerable<HdStudioCatalogAsset> WithTiles => Assets.Where(a => a.Tiles.Count > 0);

		/// <summary>
		/// Finds other captured assets that use the same (pattern, palette) hardware keys.
		/// Standard HD packs can only select by those keys (plus supported conditions), so a
		/// replacement cannot be private to one semantic object without a distinguishing
		/// condition. The caller reports this instead of silently replacing unrelated visuals.
		/// </summary>
		public static List<HdStudioCatalogAsset> FindAssetsSharingKeys(HdStudioCatalog catalog, string assetId, IEnumerable<(string Key, string Palette)> cells)
		{
			HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
			foreach((string key, string palette) in cells) {
				if(!string.IsNullOrWhiteSpace(key)) {
					keys.Add(key.ToUpperInvariant() + "|" + (palette ?? "").ToUpperInvariant());
				}
			}
			if(keys.Count == 0) {
				return new List<HdStudioCatalogAsset>();
			}

			List<HdStudioCatalogAsset> shared = new();
			foreach(HdStudioCatalogAsset asset in catalog.Assets) {
				if(string.Equals(asset.Id, assetId, StringComparison.OrdinalIgnoreCase) || asset.Tiles.Count == 0) {
					continue;
				}
				foreach(HdStudioCatalogTile tile in asset.Tiles) {
					string key = GetTileKey(tile);
					if(key.Length == 0) {
						continue;
					}
					if(keys.Contains(key.ToUpperInvariant() + "|" + (tile.Palette ?? "").ToUpperInvariant())) {
						shared.Add(asset);
						break;
					}
				}
			}
			return shared;
		}

		internal static string GetTileKey(HdStudioCatalogTile tile)
		{
			if(tile.ChrType.Contains("ChrRam", StringComparison.OrdinalIgnoreCase) &&
				!string.IsNullOrEmpty(tile.TileData) && tile.TileData.Length == 32) {
				return tile.TileData;
			}
			return tile.Pattern;
		}

		public static string ResolveImage(HdStudioCatalog catalog, HdStudioCatalogAsset asset)
		{
			if(string.IsNullOrWhiteSpace(asset.OriginalImage)) {
				return "";
			}
			string studio = Path.Combine(catalog.PackFolder, HdStudioReport.StudioFolderName);
			string direct = Path.Combine(studio, asset.OriginalImage);
			if(File.Exists(direct)) {
				return direct;
			}
			foreach(string folder in new[] { "atlas", "playerforms", "backgrounds", "artwork", "imports" }) {
				string candidate = Path.Combine(studio, folder, asset.OriginalImage);
				if(File.Exists(candidate)) {
					return candidate;
				}
			}
			try {
				return Directory.EnumerateFiles(studio, asset.OriginalImage, SearchOption.AllDirectories).FirstOrDefault() ?? "";
			} catch {
				return "";
			}
		}

		/// <summary>
		/// Returns a usable original image. When the asset has no stored screenshot it is
		/// rendered from the recorded pattern bytes/cartridge, never from a generic placeholder.
		/// </summary>
		public static string EnsureOriginalPreview(HdStudioCatalog catalog, HdStudioCatalogAsset asset, uint[] nesPalette)
		{
			string image = ResolveImage(catalog, asset);
			if(!string.IsNullOrEmpty(image)) {
				return image;
			}
			if(asset.Tiles.Count == 0) {
				return "";
			}

			string folder = Path.Combine(catalog.PackFolder, HdStudioReport.StudioFolderName, "cache");
			Directory.CreateDirectory(folder);
			string safe = new string((asset.Id + "_" + asset.Name).Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
			if(safe.Length > 90) safe = safe.Substring(0, 90);
			string path = Path.Combine(folder, safe + "_original.png");
			if(File.Exists(path)) {
				return path;
			}

			NesRomData? rom = null;
			string candidatePath = catalog.RomPath;
			if(string.IsNullOrWhiteSpace(candidatePath)) {
				string romPath = Path.Combine(catalog.PackFolder, HdStudioReport.StudioFolderName, "rom-path.txt");
				if(File.Exists(romPath)) {
					candidatePath = File.ReadAllText(romPath).Trim();
				}
			}
			if(!string.IsNullOrWhiteSpace(candidatePath) && File.Exists(candidatePath)) {
				rom = NesRomData.Load(candidatePath);
			}

			int width = Math.Max(8, asset.Width > 0 ? asset.Width : asset.Tiles.Max(t => t.X + 8));
			int height = Math.Max(8, asset.Height > 0 ? asset.Height : asset.Tiles.Max(t => t.Y + 8));
			uint[] pixels = new uint[width * height];
			foreach(HdStudioCatalogTile tile in asset.Tiles) {
				byte[]? data = null;
				if(!string.IsNullOrEmpty(tile.TileData)) {
					try {
						data = Convert.FromHexString(tile.TileData);
					} catch {
						data = null;
					}
				}
				if((data == null || data.Length < 16) && rom != null &&
					int.TryParse(tile.Pattern, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int tileIndex) &&
					tileIndex >= 0 && tileIndex < rom.ChrTileCount) {
					data = rom.GetChrTile(tileIndex);
				}
				if(data == null || data.Length < 16) {
					continue;
				}
				for(int sy = 0; sy < 8; sy++) {
					for(int sx = 0; sx < 8; sx++) {
						int srcX = tile.FlipH ? 7 - sx : sx;
						int srcY = tile.FlipV ? 7 - sy : sy;
						int color = GetTilePixel(data, srcX, srcY);
						if(color == 0) {
							continue;
						}
						int px = tile.X + sx;
						int py = tile.Y + sy;
						if(px >= 0 && px < width && py >= 0 && py < height) {
							pixels[py * width + px] = PaletteToArgb(nesPalette, tile.Palette, color);
						}
					}
				}
			}
			uint[] upscaled = HdStudioPng.Upscale(pixels, width, height, 4);
			HdStudioPng.Write(path, upscaled, width * 4, height * 4);
			return path;
		}

		private static int GetTilePixel(byte[] data, int x, int y)
		{
			int low = (data[y] >> (7 - x)) & 1;
			int high = (data[y + 8] >> (7 - x)) & 1;
			return low | (high << 1);
		}

		private static uint PaletteToArgb(uint[] nesPalette, string palette, int colorIndex)
		{
			if(!uint.TryParse(palette, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) {
				return 0;
			}
			int shift = (3 - colorIndex) * 8;
			int nesColor = (int)((value >> shift) & 0x3F);
			if(nesColor < 0 || nesColor >= nesPalette.Length) {
				return 0;
			}
			return nesPalette[nesColor] | 0xFF000000;
		}

		internal static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		internal static int GetInt(JsonElement element, string name, int fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
		}

		internal static bool GetBool(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}
	}

	internal sealed class HdStudioCatalogTile
	{
		public string Pattern = "";
		public string? TileData;
		public string ChrType = "";
		public int ChrAddress = -1;
		public int X;
		public int Y;
		public bool FlipH;
		public bool FlipV;
		public bool BackgroundPriority;
		public string Palette = "";
	}

	internal sealed class HdStudioCatalogAsset
	{
		public string Id = "";
		public string Kind = "";
		public string Name = "";
		public string Label = "";
		public string Source = "";
		public string Provenance = "";
		public double Confidence;
		public string ValidationStatus = "";
		public int Width;
		public int Height;
		public int OriginX;
		public int OriginY;
		public string OriginalImage = "";
		public string ParentId = "";
		public int FrameIndex = -1;
		public string AnimationId = "";
		public readonly List<string> PaletteVariants = new();
		public readonly List<HdStudioCatalogTile> Tiles = new();

		public string Category => Kind switch {
			"semanticObject" => "Objects",
			"metasprite" => "Rendered metasprites",
			"animationFrame" => "Animation frames",
			"animationSequence" => "Animation sequences",
			"backgroundBlock" => "Backgrounds",
			"backgroundTile" => "Background tiles",
			_ => "Other"
		};

		public static HdStudioCatalogAsset? FromJson(JsonElement element, HdStudioCatalog catalog)
		{
			string id = HdStudioCatalog.GetString(element, "id");
			if(string.IsNullOrWhiteSpace(id)) {
				return null;
			}
			HdStudioCatalogAsset asset = new() {
				Id = id,
				Kind = HdStudioCatalog.GetString(element, "kind"),
				Name = HdStudioCatalog.GetString(element, "name"),
				Label = HdStudioCatalog.GetString(element, "label"),
				Source = HdStudioCatalog.GetString(element, "source"),
				Provenance = HdStudioCatalog.GetString(element, "provenance"),
				OriginalImage = HdStudioCatalog.GetString(element, "originalImage"),
				ParentId = HdStudioCatalog.GetString(element, "parentId"),
				AnimationId = HdStudioCatalog.GetString(element, "animationId"),
				FrameIndex = HdStudioCatalog.GetInt(element, "frameIndex", -1)
			};
			if(element.TryGetProperty("confidence", out JsonElement confidence) && confidence.TryGetDouble(out double conf)) {
				asset.Confidence = conf;
			}
			if(element.TryGetProperty("validation", out JsonElement validation)) {
				asset.ValidationStatus = HdStudioCatalog.GetString(validation, "status");
			}
			if(element.TryGetProperty("dimensions", out JsonElement dims)) {
				asset.Width = HdStudioCatalog.GetInt(dims, "width");
				asset.Height = HdStudioCatalog.GetInt(dims, "height");
				asset.OriginX = HdStudioCatalog.GetInt(dims, "originX");
				asset.OriginY = HdStudioCatalog.GetInt(dims, "originY");
			}
			if(element.TryGetProperty("paletteVariants", out JsonElement palettes) && palettes.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement p in palettes.EnumerateArray()) {
					if(p.ValueKind == JsonValueKind.String) asset.PaletteVariants.Add(p.GetString() ?? "");
				}
			}
			if(element.TryGetProperty("tiles", out JsonElement tiles) && tiles.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement tile in tiles.EnumerateArray()) {
					asset.Tiles.Add(new HdStudioCatalogTile() {
						Pattern = HdStudioCatalog.GetString(tile, "pattern"),
						TileData = tile.TryGetProperty("tileData", out JsonElement td) && td.ValueKind == JsonValueKind.String ? td.GetString() : null,
						ChrType = HdStudioCatalog.GetString(tile, "chrType"),
						ChrAddress = HdStudioCatalog.GetInt(tile, "chrAddress", -1),
						X = HdStudioCatalog.GetInt(tile, "x"),
						Y = HdStudioCatalog.GetInt(tile, "y"),
						FlipH = HdStudioCatalog.GetBool(tile, "flipH"),
						FlipV = HdStudioCatalog.GetBool(tile, "flipV"),
						BackgroundPriority = HdStudioCatalog.GetBool(tile, "backgroundPriority"),
						Palette = HdStudioCatalog.GetString(tile, "palette")
					});
				}
			}
			if(asset.Width <= 0 && asset.Tiles.Count > 0) {
				asset.Width = asset.Tiles.Max(t => t.X + 8);
			}
			if(asset.Height <= 0 && asset.Tiles.Count > 0) {
				asset.Height = asset.Tiles.Max(t => t.Y + 8);
			}
			if(string.IsNullOrWhiteSpace(asset.Name)) {
				asset.Name = ((asset.Kind.Length > 0 ? asset.Kind : "Asset") + " " + asset.Id.Split(':').Last());
			}
			return asset;
		}
	}
}
