using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace Mesen.HdStudio
{
	/// <summary>
	/// HD Studio: generalized user-art importer.
	///
	/// The user selects an asset from HdStudio/assets/assets.json and supplies any supported
	/// image. The whole composed object is processed once (background removal, crop, aspect
	/// ratio, anchor alignment), then segmented into the object's original 8x8 tile grid.
	/// Individual NES tiles are never resized independently.
	///
	/// The output is a standard Mesen HD pack: a new sheet plus exact tile entries. Mirrored
	/// occurrences are stored unflipped because Mesen flips the replacement at render time.
	/// </summary>
	internal static class HdStudioImporter
	{
		internal class Options
		{
			public string AssetId = "";
			public string ImagePath = "";
			public string ImportName = "";
			public bool RemoveBackground;
			public int BackgroundTolerance = 40;
			public bool KeepLargestComponent;
			public bool Crop = true;
			public string Fit = "contain";
			public string Anchor = "center";
			public int Padding;
			public int PackScale;
			public bool MirrorConditions;
			public bool Minimal;
			public bool DryRun;
		}

		private class AssetTile
		{
			public string Pattern = "";
			public string? TileData;
			public string ChrType = "";
			public int X;
			public int Y;
			public bool FlipH;
			public bool FlipV;
			public string Palette = "";
		}

		private class AssetInfo
		{
			public string Id = "";
			public string Kind = "";
			public string Name = "";
			public int Width;
			public int Height;
			public List<AssetTile> Tiles = new();
			public List<string> Palettes = new();
		}

		private class LoadedImage
		{
			public int Width;
			public int Height;
			public SKColor[] Pixels = Array.Empty<SKColor>();
		}

		private class ReplacementCell
		{
			public string Key = "";
			public string Palette = "";
			public string Condition = "";
			public int X;
			public int Y;
			public uint[] Pixels = Array.Empty<uint>();
		}

		public static int Run(string romPath, string outFolder, Options options, uint[] nesPalette, Action<string> log)
		{
			if(string.IsNullOrWhiteSpace(options.AssetId) || string.IsNullOrWhiteSpace(options.ImagePath)) {
				log("Import: --asset=ID and --image=FILE are required.");
				return 1;
			}

			string studio = Path.Combine(outFolder, HdStudioReport.StudioFolderName);
			string assetsPath = Path.Combine(studio, "assets", "assets.json");
			if(!File.Exists(assetsPath)) {
				log("Import: no asset schema at " + assetsPath + ". Run --assets after capturing the game.");
				return 1;
			}
			if(!File.Exists(options.ImagePath)) {
				log("Import: image not found: " + options.ImagePath);
				return 1;
			}
			if(!File.Exists(Path.Combine(outFolder, "hires.txt"))) {
				options.Minimal = true;
			}

			AssetInfo? asset = LoadAsset(assetsPath, options.AssetId);
			if(asset == null) {
				log("Import: no asset matches '" + options.AssetId + "'.");
				return 1;
			}
			if(asset.Tiles.Count == 0) {
				log("Import: the selected asset has no tile occurrences; capture it with the mapper-aware atlas first.");
				return 1;
			}

			NesRomData rom = NesRomData.Load(romPath);
			int packScale = ReadPackScale(outFolder, options.Minimal);
			if(options.PackScale > 0) {
				if(!options.Minimal && options.PackScale != packScale) {
					log($"Import: the existing pack is {packScale}x; a different scale requires --minimal. Refusing to write {options.PackScale}x tiles into it.");
					return 1;
				}
				packScale = options.PackScale;
			}
			int tileSize = 8 * packScale;
			int width = asset.Width > 0 ? asset.Width : asset.Tiles.Max(t => t.X + 8);
			int height = asset.Height > 0 ? asset.Height : asset.Tiles.Max(t => t.Y + 8);
			int targetWidth = width * packScale;
			int targetHeight = height * packScale;

			LoadedImage source = LoadImage(options.ImagePath);
			if(options.RemoveBackground) {
				RemoveBackground(source, options.BackgroundTolerance);
			}
			if(options.KeepLargestComponent) {
				KeepLargestComponent(source);
			}
			SKRectI bounds = options.Crop ? AlphaBounds(source) : new SKRectI(0, 0, source.Width, source.Height);
			if(bounds.Width <= 0 || bounds.Height <= 0) {
				log("Import: the image is empty or fully transparent.");
				return 1;
			}

			SKColor[] composed = Compose(source, bounds, targetWidth, targetHeight, options);
			string slug = SafeSlug(string.IsNullOrWhiteSpace(options.ImportName) ? asset.Name : options.ImportName) + "_" + asset.Id.Split(':').Last();
			string importsFolder = Path.Combine(studio, "imports");
			Directory.CreateDirectory(importsFolder);

			WritePreviews(importsFolder, slug, asset, rom, nesPalette, composed, targetWidth, targetHeight, log);

			//Segment the one composed image into replacement tiles.
			List<ReplacementCell> cells = new();
			Dictionary<string, string> conflicts = new();
			foreach(AssetTile tile in asset.Tiles) {
				int cellX = tile.X * packScale;
				int cellY = tile.Y * packScale;
				uint[] cell = CropCell(composed, targetWidth, targetHeight, cellX, cellY, tileSize);
				if(tile.FlipH) {
					cell = FlipHorizontal(cell, tileSize);
				}
				if(tile.FlipV) {
					cell = FlipVertical(cell, tileSize);
				}
				string key = GetTileKey(tile);
				if(string.IsNullOrEmpty(key)) {
					continue;
				}
				string condition = GetCondition(tile, options.MirrorConditions);
				string full = condition + "|" + key + "|" + tile.Palette;
				if(conflicts.TryGetValue(full, out string? previous) && previous != HashPixels(cell)) {
					log($"Import: warning: tile {key} appears in multiple positions with different content; the first one is used.");
					continue;
				}
				conflicts[full] = HashPixels(cell);
				cells.Add(new ReplacementCell() {
					Key = key,
					Palette = tile.Palette.ToUpperInvariant(),
					Condition = condition,
					X = cells.Count * tileSize,
					Y = 0,
					Pixels = cell
				});
			}

			if(cells.Count == 0) {
				log("Import: no replacement cells were produced.");
				return 1;
			}

			//Conditional entries must be checked before the unconditional entry for the same key,
			//otherwise a mirrored occurrence would always match the unflipped replacement first.
			//This is what allows an asymmetric replacement of a mirrored/shared tile.
			cells = cells.OrderBy(c => string.IsNullOrEmpty(c.Condition) ? 1 : 0).ToList();
			for(int i = 0; i < cells.Count; i++) {
				cells[i].X = i * tileSize;
			}

			string sheetName = $"HdStudioImport_{slug}.png";
			string sheetPath = Path.Combine(outFolder, sheetName);
			uint[] sheet = new uint[cells.Count * tileSize * tileSize];
			for(int i = 0; i < cells.Count; i++) {
				Blit(sheet, cells.Count * tileSize, tileSize, cells[i].X, 0, cells[i].Pixels, tileSize);
			}
			HdStudioPng.Write(sheetPath, sheet, cells.Count * tileSize, tileSize);

			string hiresPath = Path.Combine(outFolder, "hires.txt");
			if(!options.DryRun) {
				WriteHires(hiresPath, sheetName, cells, packScale, rom, options.Minimal, log);
			}

			WriteManifest(importsFolder, slug, options, asset, cells, sheetName, width, height, targetWidth, targetHeight, packScale);
			log($"Import: {asset.Name} ({asset.Kind}) -> {cells.Count} replacement tiles, sheet {sheetName}, pack scale {packScale}x{(options.DryRun ? " (dry run)" : "")}.");
			return 0;
		}

		private static AssetInfo? LoadAsset(string path, string selector)
		{
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			if(!doc.RootElement.TryGetProperty("assets", out JsonElement list)) {
				return null;
			}
			foreach(JsonElement element in list.EnumerateArray()) {
				string id = element.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? "" : "";
				string name = element.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
				if(!string.Equals(id, selector, StringComparison.OrdinalIgnoreCase) &&
					!string.Equals(name, selector, StringComparison.OrdinalIgnoreCase) &&
					!id.EndsWith(":" + selector, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				AssetInfo asset = new() {
					Id = id,
					Kind = element.TryGetProperty("kind", out JsonElement k) ? k.GetString() ?? "" : "",
					Name = name,
					Width = element.TryGetProperty("dimensions", out JsonElement d) && d.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0,
					Height = element.TryGetProperty("dimensions", out JsonElement dh) && dh.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0
				};
				if(element.TryGetProperty("paletteVariants", out JsonElement palettes)) {
					foreach(JsonElement palette in palettes.EnumerateArray()) {
						asset.Palettes.Add(palette.GetString() ?? "");
					}
				}
				if(element.TryGetProperty("tiles", out JsonElement tiles)) {
					foreach(JsonElement tile in tiles.EnumerateArray()) {
						asset.Tiles.Add(new AssetTile() {
							Pattern = tile.TryGetProperty("pattern", out JsonElement p) ? p.GetString() ?? "" : "",
							TileData = tile.TryGetProperty("tileData", out JsonElement td) ? td.GetString() : null,
							ChrType = tile.TryGetProperty("chrType", out JsonElement ct) ? ct.GetString() ?? "" : "",
							X = tile.TryGetProperty("x", out JsonElement tx) ? tx.GetInt32() : 0,
							Y = tile.TryGetProperty("y", out JsonElement ty) ? ty.GetInt32() : 0,
							FlipH = tile.TryGetProperty("flipH", out JsonElement fh) && fh.GetBoolean(),
							FlipV = tile.TryGetProperty("flipV", out JsonElement fv) && fv.GetBoolean(),
							Palette = tile.TryGetProperty("palette", out JsonElement pal) ? pal.GetString() ?? "" : ""
						});
					}
				}
				return asset;
			}
			return null;
		}

		private static int ReadPackScale(string outFolder, bool minimal)
		{
			string hires = Path.Combine(outFolder, "hires.txt");
			if(!minimal && File.Exists(hires)) {
				foreach(string line in File.ReadLines(hires)) {
					if(line.StartsWith("<scale>", StringComparison.Ordinal) && int.TryParse(line.Substring(7).Trim(), out int scale) && scale >= 1 && scale <= 10) {
						return scale;
					}
				}
			}
			return 4;
		}

		private static LoadedImage LoadImage(string path)
		{
			using SKBitmap? bitmap = SKBitmap.Decode(path);
			if(bitmap == null) {
				throw new InvalidDataException("the image could not be decoded: " + path);
			}
			using SKBitmap rgba = new(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
			using(SKCanvas canvas = new(rgba)) {
				canvas.Clear(SKColors.Transparent);
				canvas.DrawBitmap(bitmap, 0, 0);
			}
			return new LoadedImage() { Width = rgba.Width, Height = rgba.Height, Pixels = rgba.Pixels };
		}

		private static void RemoveBackground(LoadedImage image, int tolerance)
		{
			int width = image.Width;
			int height = image.Height;
			SKColor[] pixels = image.Pixels;
			if(width <= 0 || height <= 0) {
				return;
			}

			//The background key is the most common opaque corner color. A border flood fill
			//removes only the connected background, preserving white eyes/details inside the object.
			SKColor[] corners = {
				pixels[0], pixels[width - 1], pixels[(height - 1) * width], pixels[height * width - 1]
			};
			//Only treat the image as having a removable background when the corners agree.
			//A user image with different corner colors is already a composed subject, and
			//removing one corner's color would erase a legitimate half of the art.
			SKColor key = corners.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
			if(corners.Any(c => ColorDistance(c, key) > tolerance)) {
				return;
			}
			bool[] visited = new bool[pixels.Length];
			Queue<int> queue = new();
			for(int x = 0; x < width; x++) {
				queue.Enqueue(x);
				queue.Enqueue((height - 1) * width + x);
			}
			for(int y = 0; y < height; y++) {
				queue.Enqueue(y * width);
				queue.Enqueue(y * width + width - 1);
			}

			while(queue.Count > 0) {
				int index = queue.Dequeue();
				if(visited[index]) {
					continue;
				}
				visited[index] = true;
				SKColor color = pixels[index];
				if(color.Alpha > 8 && ColorDistance(color, key) > tolerance) {
					continue;
				}
				pixels[index] = SKColors.Transparent;
				int x = index % width;
				int y = index / width;
				if(x > 0) queue.Enqueue(index - 1);
				if(x < width - 1) queue.Enqueue(index + 1);
				if(y > 0) queue.Enqueue(index - width);
				if(y < height - 1) queue.Enqueue(index + width);
			}
		}

		private static void KeepLargestComponent(LoadedImage image)
		{
			int width = image.Width;
			int height = image.Height;
			SKColor[] pixels = image.Pixels;
			if(width <= 0 || height <= 0) {
				return;
			}
			bool[] visited = new bool[pixels.Length];
			List<int> largest = new();
			Queue<int> queue = new();
			for(int i = 0; i < pixels.Length; i++) {
				if(visited[i] || pixels[i].Alpha <= 8) {
					continue;
				}
				List<int> component = new();
				queue.Clear();
				queue.Enqueue(i);
				visited[i] = true;
				while(queue.Count > 0) {
					int index = queue.Dequeue();
					component.Add(index);
					int x = index % width;
					int y = index / width;
					Visit(x > 0 ? index - 1 : -1);
					Visit(x < width - 1 ? index + 1 : -1);
					Visit(y > 0 ? index - width : -1);
					Visit(y < height - 1 ? index + width : -1);
				}
				if(component.Count > largest.Count) {
					largest = component;
				}
			}
			if(largest.Count == 0) {
				return;
			}
			HashSet<int> keep = new(largest);
			for(int i = 0; i < pixels.Length; i++) {
				if(pixels[i].Alpha > 8 && !keep.Contains(i)) {
					pixels[i] = SKColors.Transparent;
				}
			}

			void Visit(int index)
			{
				if(index >= 0 && index < pixels.Length && !visited[index] && pixels[index].Alpha > 8) {
					visited[index] = true;
					queue.Enqueue(index);
				}
			}
		}

		private static SKRectI AlphaBounds(LoadedImage image)
		{
			int width = image.Width;
			int height = image.Height;
			int minX = width, minY = height, maxX = -1, maxY = -1;
			for(int y = 0; y < height; y++) {
				for(int x = 0; x < width; x++) {
					if(image.Pixels[y * width + x].Alpha > 8) {
						minX = Math.Min(minX, x);
						minY = Math.Min(minY, y);
						maxX = Math.Max(maxX, x);
						maxY = Math.Max(maxY, y);
					}
				}
			}
			return maxX < 0 ? new SKRectI() : new SKRectI(minX, minY, maxX + 1, maxY + 1);
		}

		private static SKColor[] Compose(LoadedImage source, SKRectI bounds, int targetWidth, int targetHeight, Options options)
		{
			int padding = Math.Max(0, options.Padding);
			int availableWidth = Math.Max(1, targetWidth - padding * 2);
			int availableHeight = Math.Max(1, targetHeight - padding * 2);
			int scaledWidth;
			int scaledHeight;
			if(options.Fit == "stretch") {
				scaledWidth = availableWidth;
				scaledHeight = availableHeight;
			} else {
				double scale = Math.Min(availableWidth / (double)bounds.Width, availableHeight / (double)bounds.Height);
				scaledWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
				scaledHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));
			}
			SKColor[] scaled = ResizeBilinear(source, bounds, scaledWidth, scaledHeight);

			int offsetX = padding;
			int offsetY = padding;
			switch(options.Anchor.ToLowerInvariant()) {
				case "bottom": offsetX += (availableWidth - scaledWidth) / 2; offsetY += availableHeight - scaledHeight; break;
				case "top": offsetX += (availableWidth - scaledWidth) / 2; break;
				case "left": offsetY += (availableHeight - scaledHeight) / 2; break;
				case "right": offsetX += availableWidth - scaledWidth; offsetY += (availableHeight - scaledHeight) / 2; break;
				default: offsetX += (availableWidth - scaledWidth) / 2; offsetY += (availableHeight - scaledHeight) / 2; break;
			}

			SKColor[] target = new SKColor[targetWidth * targetHeight];
			Array.Fill(target, SKColors.Transparent);
			for(int y = 0; y < scaledHeight; y++) {
				int ty = offsetY + y;
				if(ty < 0 || ty >= targetHeight) continue;
				for(int x = 0; x < scaledWidth; x++) {
					int tx = offsetX + x;
					if(tx < 0 || tx >= targetWidth) continue;
					target[ty * targetWidth + tx] = scaled[y * scaledWidth + x];
				}
			}
			return target;
		}

		private static SKColor[] ResizeBilinear(LoadedImage source, SKRectI bounds, int width, int height)
		{
			SKColor[] result = new SKColor[width * height];
			for(int y = 0; y < height; y++) {
				double sy = bounds.Top + (y + 0.5) * bounds.Height / height - 0.5;
				int y0 = Math.Clamp((int)Math.Floor(sy), 0, source.Height - 1);
				int y1 = Math.Min(y0 + 1, source.Height - 1);
				double fy = Math.Clamp(sy - y0, 0, 1);
				for(int x = 0; x < width; x++) {
					double sx = bounds.Left + (x + 0.5) * bounds.Width / width - 0.5;
					int x0 = Math.Clamp((int)Math.Floor(sx), 0, source.Width - 1);
					int x1 = Math.Min(x0 + 1, source.Width - 1);
					double fx = Math.Clamp(sx - x0, 0, 1);
					SKColor c00 = source.Pixels[y0 * source.Width + x0];
					SKColor c10 = source.Pixels[y0 * source.Width + x1];
					SKColor c01 = source.Pixels[y1 * source.Width + x0];
					SKColor c11 = source.Pixels[y1 * source.Width + x1];
					result[y * width + x] = Lerp(Lerp(c00, c10, fx), Lerp(c01, c11, fx), fy);
				}
			}
			return result;
		}

		private static uint[] CropCell(SKColor[] composed, int width, int height, int x, int y, int size)
		{
			uint[] result = new uint[size * size];
			for(int yy = 0; yy < size; yy++) {
				for(int xx = 0; xx < size; xx++) {
					int sx = x + xx;
					int sy = y + yy;
					if(sx >= 0 && sx < width && sy >= 0 && sy < height) {
						result[yy * size + xx] = ToArgb(composed[sy * width + sx]);
					}
				}
			}
			return result;
		}

		private static uint[] FlipHorizontal(uint[] pixels, int size)
		{
			uint[] result = new uint[pixels.Length];
			for(int y = 0; y < size; y++) {
				for(int x = 0; x < size; x++) {
					result[y * size + x] = pixels[y * size + (size - 1 - x)];
				}
			}
			return result;
		}

		private static uint[] FlipVertical(uint[] pixels, int size)
		{
			uint[] result = new uint[pixels.Length];
			for(int y = 0; y < size; y++) {
				for(int x = 0; x < size; x++) {
					result[y * size + x] = pixels[(size - 1 - y) * size + x];
				}
			}
			return result;
		}

		private static string GetTileKey(AssetTile tile)
		{
			//CHR-ROM packs key tiles by the physical pattern index; only CHR-RAM packs use
			//the 32-byte pattern content as the key.
			if(tile.ChrType.Contains("ChrRam", StringComparison.OrdinalIgnoreCase) &&
				!string.IsNullOrEmpty(tile.TileData) && tile.TileData.Length == 32) {
				return tile.TileData.ToUpperInvariant();
			}
			return tile.Pattern.ToUpperInvariant();
		}

		private static string GetCondition(AssetTile tile, bool mirrorConditions)
		{
			if(!mirrorConditions) {
				return "";
			}
			bool h = tile.FlipH;
			bool v = tile.FlipV;
			if(h && !v) return "[hmirror]";
			if(v && !h) return "[vmirror]";
			//Both axes cannot be expressed by a single Mesen condition; leave it unconditional.
			return "";
		}

		private static void WriteHires(string path, string sheetName, List<ReplacementCell> cells, int scale, NesRomData rom, bool minimal, Action<string> log)
		{
			List<string> lines = new();
			if(!minimal && File.Exists(path)) {
				lines.AddRange(File.ReadAllLines(path));
			} else {
				lines.Add("<ver>109");
				lines.Add($"<scale>{scale}");
				lines.Add("<supportedRom>" + rom.Sha1);
			}

			int imageIndex = lines.Count(l => l.StartsWith("<img>", StringComparison.Ordinal));
			int lastImg = -1;
			int firstTile = -1;
			for(int i = 0; i < lines.Count; i++) {
				if(lines[i].StartsWith("<img>", StringComparison.Ordinal)) lastImg = i;
				if(firstTile < 0 && lines[i].StartsWith("<tile>", StringComparison.Ordinal)) firstTile = i;
			}
			if(lastImg < 0) {
				lastImg = firstTile >= 0 ? firstTile - 1 : lines.Count - 1;
			}

			StringBuilder tileBlock = new();
			foreach(ReplacementCell cell in cells) {
				tileBlock.Append(cell.Condition);
				tileBlock.Append("<tile>");
				tileBlock.Append(imageIndex).Append(',');
				tileBlock.Append(cell.Key).Append(',');
				tileBlock.Append(string.IsNullOrEmpty(cell.Palette) ? "FF000000" : cell.Palette).Append(',');
				tileBlock.Append(cell.X).Append(",0,1,N").Append('\n');
			}
			lines.Insert(lastImg + 1, "<img>" + sheetName);
			lines.Insert(lastImg + 2, tileBlock.ToString().TrimEnd('\n'));
			File.WriteAllLines(path, lines, new UTF8Encoding(false));
			log($"Import: {cells.Count} exact tile entries written to {path}.");
		}

		private static void WritePreviews(string folder, string slug, AssetInfo asset, NesRomData rom, uint[] nesPalette, SKColor[] composed, int width, int height, Action<string> log)
		{
			int origWidth = Math.Max(8, asset.Width);
			int origHeight = Math.Max(8, asset.Height);
			uint[] original = new uint[origWidth * origHeight];
			foreach(AssetTile tile in asset.Tiles) {
				byte[]? data = null;
				if(!string.IsNullOrEmpty(tile.TileData) && tile.TileData.Length == 32) {
					data = Convert.FromHexString(tile.TileData);
				} else if(tile.ChrType.Contains("ChrRom", StringComparison.OrdinalIgnoreCase) && int.TryParse(tile.Pattern, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pattern) && pattern >= 0 && pattern < rom.ChrTileCount) {
					data = rom.GetChrTile(pattern);
				}
				if(data == null) {
					continue;
				}
				for(int sy = 0; sy < 8; sy++) {
					for(int sx = 0; sx < 8; sx++) {
						int srcX = tile.FlipH ? 7 - sx : sx;
						int srcY = tile.FlipV ? 7 - sy : sy;
						int color = GetTilePixel(data, srcX, srcY);
						if(color == 0) continue;
						int px = tile.X + sx;
						int py = tile.Y + sy;
						if(px >= 0 && px < origWidth && py >= 0 && py < origHeight) {
							original[py * origWidth + px] = PaletteToArgb(nesPalette, tile.Palette, color);
						}
					}
				}
			}
			uint[] replacement = composed.Select(ToArgb).ToArray();
			HdStudioPng.Write(Path.Combine(folder, slug + "_original.png"), HdStudioPng.Upscale(original, origWidth, origHeight, 4), origWidth * 4, origHeight * 4);
			HdStudioPng.Write(Path.Combine(folder, slug + "_replacement.png"), replacement, width, height);

			int displayW = Math.Max(origWidth * 4, width);
			int displayH = Math.Max(origHeight * 4, height);
			uint[] side = new uint[displayW * 2 * displayH];
			uint[] orig4 = HdStudioPng.Upscale(original, origWidth, origHeight, 4);
			for(int y = 0; y < displayH; y++) {
				for(int x = 0; x < displayW; x++) {
					if(x < origWidth * 4 && y < origHeight * 4) {
						side[y * displayW * 2 + x] = orig4[y * origWidth * 4 + x];
					}
					if(x < width && y < height) {
						side[y * displayW * 2 + displayW + x] = replacement[y * width + x];
					}
				}
			}
			HdStudioPng.Write(Path.Combine(folder, slug + "_preview.png"), side, displayW * 2, displayH);
			log("Import: previews and original/replacement images written to " + folder);
		}

		private static void WriteManifest(string folder, string slug, Options options, AssetInfo asset, List<ReplacementCell> cells, string sheetName, int width, int height, int targetWidth, int targetHeight, int scale)
		{
			using FileStream stream = File.Create(Path.Combine(folder, slug + ".json"));
			using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
			json.WriteStartObject();
			json.WriteString("assetId", asset.Id);
			json.WriteString("assetName", asset.Name);
			json.WriteString("sourceImage", options.ImagePath);
			json.WriteString("sheet", sheetName);
			json.WriteNumber("packScale", scale);
			json.WriteNumber("originalWidth", width);
			json.WriteNumber("originalHeight", height);
			json.WriteNumber("targetWidth", targetWidth);
			json.WriteNumber("targetHeight", targetHeight);
			json.WriteBoolean("removeBackground", options.RemoveBackground);
			json.WriteBoolean("keepLargestComponent", options.KeepLargestComponent);
			json.WriteString("fit", options.Fit);
			json.WriteString("anchor", options.Anchor);
			json.WriteStartArray("tiles");
			foreach(ReplacementCell cell in cells) {
				json.WriteStartObject();
				json.WriteString("key", cell.Key);
				json.WriteString("palette", cell.Palette);
				json.WriteString("condition", cell.Condition);
				json.WriteNumber("sheetX", cell.X);
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}

		private static void Blit(uint[] target, int targetWidth, int cellSize, int x, int y, uint[] source, int size)
		{
			for(int yy = 0; yy < size; yy++) {
				for(int xx = 0; xx < size; xx++) {
					target[(y + yy) * targetWidth + x + xx] = source[yy * size + xx];
				}
			}
		}

		private static SKColor Lerp(SKColor a, SKColor b, double t)
		{
			return new SKColor(
				(byte)Math.Round(a.Red + (b.Red - a.Red) * t),
				(byte)Math.Round(a.Green + (b.Green - a.Green) * t),
				(byte)Math.Round(a.Blue + (b.Blue - a.Blue) * t),
				(byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * t));
		}

		private static double ColorDistance(SKColor a, SKColor b)
		{
			int dr = a.Red - b.Red;
			int dg = a.Green - b.Green;
			int db = a.Blue - b.Blue;
			return Math.Sqrt(dr * dr + dg * dg + db * db);
		}

		private static uint ToArgb(SKColor color)
		{
			return ((uint)color.Alpha << 24) | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;
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
			return nesPalette[nesColor] | 0xFF000000;
		}

		private static string HashPixels(uint[] pixels)
		{
			uint hash = 2166136261;
			foreach(uint p in pixels) {
				hash = (hash ^ p) * 16777619;
			}
			return hash.ToString("X8", CultureInfo.InvariantCulture);
		}

		private static string SafeSlug(string value)
		{
			StringBuilder sb = new();
			foreach(char c in value) {
				sb.Append(char.IsLetterOrDigit(c) ? c : '_');
			}
			return sb.ToString().Trim('_');
		}
	}
}
