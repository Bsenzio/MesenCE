using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Shared artwork adaptation pipeline.
	///
	/// The whole composed object is processed once (background removal, crop, fit, anchor,
	/// zoom, flips), then segmented into its original 8x8 tile grid. Individual NES tiles are
	/// never resized independently. This code is used by the command line importer, by the
	/// native Studio preview and by the project compiler, so all three produce identical art.
	/// </summary>
	internal static class HdStudioArtwork
	{
		internal sealed class ArtworkImage
		{
			public int Width;
			public int Height;
			public SKColor[] Pixels = Array.Empty<SKColor>();
		}

		internal sealed class TileSpec
		{
			public string Pattern = "";
			public string? TileData;
			public string ChrType = "";
			public int X;
			public int Y;
			public bool FlipH;
			public bool FlipV;
			public bool BackgroundPriority;
			public string Palette = "";
		}

		internal sealed class Cell
		{
			public string Key = "";
			public string Palette = "";
			public string Condition = "";
			public int X;
			public uint[] Pixels = Array.Empty<uint>();
		}

		public static ArtworkImage LoadImage(string path)
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
			return new ArtworkImage() { Width = rgba.Width, Height = rgba.Height, Pixels = rgba.Pixels };
		}

		/// <summary>Removes the connected border background, preserving enclosed same-color details.</summary>
		public static int RemoveBackground(ArtworkImage image, int tolerance)
		{
			int width = image.Width;
			int height = image.Height;
			SKColor[] pixels = image.Pixels;
			if(width <= 0 || height <= 0) {
				return 0;
			}

			SKColor[] corners = {
				pixels[0], pixels[width - 1], pixels[(height - 1) * width], pixels[height * width - 1]
			};
			if(corners.All(c => c.Alpha <= 8)) {
				//The image already has a transparent background. Running the color-key
				//removal here would eat dark outlines that happen to be close to black;
				//transparency is authoritative.
				return 0;
			}
			SKColor key = corners.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
			if(corners.Any(c => ColorDistance(c, key) > tolerance)) {
				//Different corner colors mean the image is already a composed subject.
				return 0;
			}

			int removed = 0;
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
				if(pixels[index].Alpha > 8) {
					removed++;
				}
				pixels[index] = SKColors.Transparent;
				int x = index % width;
				int y = index / width;
				if(x > 0) queue.Enqueue(index - 1);
				if(x < width - 1) queue.Enqueue(index + 1);
				if(y > 0) queue.Enqueue(index - width);
				if(y < height - 1) queue.Enqueue(index + width);
			}
			return removed;
		}

		/// <summary>Keeps only the largest 4-connected opaque component.</summary>
		public static int KeepLargestComponent(ArtworkImage image)
		{
			int width = image.Width;
			int height = image.Height;
			SKColor[] pixels = image.Pixels;
			if(width <= 0 || height <= 0) {
				return 0;
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
				return 0;
			}
			HashSet<int> keep = new(largest);
			int removed = 0;
			for(int i = 0; i < pixels.Length; i++) {
				if(pixels[i].Alpha > 8 && !keep.Contains(i)) {
					pixels[i] = SKColors.Transparent;
					removed++;
				}
			}
			return removed;

			void Visit(int index)
			{
				if(index >= 0 && index < pixels.Length && !visited[index] && pixels[index].Alpha > 8) {
					visited[index] = true;
					queue.Enqueue(index);
				}
			}
		}

		public static SKRectI AlphaBounds(ArtworkImage image)
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

		/// <summary>Composes the prepared source image into the exact target pixel grid.</summary>
		public static SKColor[] Compose(ArtworkImage source, SKRectI bounds, int targetWidth, int targetHeight, HdStudioAdaptationSettings settings)
		{
			//Crop handles in the artwork canvas store their insets in source pixels. Keep the
			//rectangle inside the prepared image so an aggressive crop can never produce an
			//empty or negative source region.
			int cropLeft = Math.Clamp(settings.CropLeft, 0, Math.Max(0, bounds.Width - 1));
			int cropTop = Math.Clamp(settings.CropTop, 0, Math.Max(0, bounds.Height - 1));
			int cropRight = Math.Clamp(settings.CropRight, 0, Math.Max(0, bounds.Width - 1 - cropLeft));
			int cropBottom = Math.Clamp(settings.CropBottom, 0, Math.Max(0, bounds.Height - 1 - cropTop));
			bounds = new SKRectI(bounds.Left + cropLeft, bounds.Top + cropTop,
				bounds.Right - cropRight, bounds.Bottom - cropBottom);
			int padding = Math.Max(0, settings.Padding);
			int availableWidth = Math.Max(1, targetWidth - padding * 2);
			int availableHeight = Math.Max(1, targetHeight - padding * 2);
			double zoom = Math.Clamp(settings.ZoomPercent, 10, 400) / 100.0;
			int scaledWidth;
			int scaledHeight;
			if(string.Equals(settings.Fit, "stretch", StringComparison.OrdinalIgnoreCase)) {
				scaledWidth = Math.Max(1, (int)Math.Round(availableWidth * zoom));
				scaledHeight = Math.Max(1, (int)Math.Round(availableHeight * zoom));
			} else {
				double scale = Math.Min(availableWidth / (double)bounds.Width, availableHeight / (double)bounds.Height) * zoom;
				scaledWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
				scaledHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));
			}
			SKColor[] scaled = ResizeBilinear(source, bounds, scaledWidth, scaledHeight);

			int offsetX = padding + settings.OffsetX;
			int offsetY = padding + settings.OffsetY;
			switch((settings.Anchor ?? "center").ToLowerInvariant()) {
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

			if(settings.FlipH || settings.FlipV) {
				SKColor[] flipped = new SKColor[target.Length];
				for(int y = 0; y < targetHeight; y++) {
					int sy = settings.FlipV ? targetHeight - 1 - y : y;
					for(int x = 0; x < targetWidth; x++) {
						int sx = settings.FlipH ? targetWidth - 1 - x : x;
						flipped[y * targetWidth + x] = target[sy * targetWidth + sx];
					}
				}
				return flipped;
			}
			return target;
		}

		public static SKColor[] ResizeBilinear(ArtworkImage source, SKRectI bounds, int width, int height)
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

		/// <summary>
		/// Segments the composed image into replacement cells. Mirrored occurrences are stored
		/// unflipped because Mesen flips the replacement at render time; conditional entries are
		/// ordered before unconditional ones for the same hardware tile.
		/// </summary>
		public static List<Cell> BuildCells(IReadOnlyList<TileSpec> tiles, SKColor[] composed, int targetWidth, int targetHeight, int packScale, bool mirrorConditions, Action<string> log, IReadOnlyList<string>? paletteVariants = null, bool matchBackgroundPriority = false, string extraCondition = "")
		{
			int tileSize = 8 * packScale;
			List<Cell> cells = new();
			Dictionary<string, string> conflicts = new();
			foreach(TileSpec tile in tiles) {
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
				string condition = GetCondition(tile, mirrorConditions, matchBackgroundPriority, extraCondition);

				//The same original object can be drawn with several sprite/background palettes.
				//A user replacement is visual content, not a palette, so it applies to every
				//observed variant by default. Keys remain exact (pattern + palette), so Mesen
				//still matches only the intended hardware combination.
				List<string> palettes = new();
				void AddPalette(string? palette)
				{
					string normalized = (palette ?? "").ToUpperInvariant();
					if(normalized.Length > 0 && !palettes.Contains(normalized)) {
						palettes.Add(normalized);
					}
				}
				AddPalette(tile.Palette);
				if(paletteVariants != null) {
					foreach(string palette in paletteVariants) {
						AddPalette(palette);
					}
				}
				if(palettes.Count == 0) {
					palettes.Add(tile.Palette.ToUpperInvariant());
				}

				foreach(string palette in palettes) {
					string full = condition + "|" + key + "|" + palette;
					string hash = HashPixels(cell);
					if(conflicts.TryGetValue(full, out string? previous) && previous != hash) {
						log("Import: warning: tile " + key + " appears in multiple positions with different content; the first one is used.");
						continue;
					}
					conflicts[full] = hash;
					cells.Add(new Cell() {
						Key = key,
						Palette = palette,
						Condition = condition,
						Pixels = cell
					});
				}
			}

			//Conditional entries must be checked before the unconditional entry for the same key,
			//otherwise a mirrored occurrence would always match the unflipped replacement first.
			cells = cells.OrderBy(c => string.IsNullOrEmpty(c.Condition) ? 1 : 0).ToList();
			for(int i = 0; i < cells.Count; i++) {
				cells[i].X = i * tileSize;
			}
			return cells;
		}

		public static uint[] CropCell(SKColor[] composed, int width, int height, int x, int y, int size)
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

		public static uint[] FlipHorizontal(uint[] pixels, int size)
		{
			uint[] result = new uint[pixels.Length];
			for(int y = 0; y < size; y++) {
				for(int x = 0; x < size; x++) {
					result[y * size + x] = pixels[y * size + (size - 1 - x)];
				}
			}
			return result;
		}

		public static uint[] FlipVertical(uint[] pixels, int size)
		{
			uint[] result = new uint[pixels.Length];
			for(int y = 0; y < size; y++) {
				for(int x = 0; x < size; x++) {
					result[y * size + x] = pixels[(size - 1 - y) * size + x];
				}
			}
			return result;
		}

		public static string GetTileKey(TileSpec tile)
		{
			//CHR-ROM packs key tiles by the physical pattern index; only CHR-RAM packs use
			//the 32-byte pattern content as the key.
			if(tile.ChrType.Contains("ChrRam", StringComparison.OrdinalIgnoreCase) &&
				!string.IsNullOrEmpty(tile.TileData) && tile.TileData.Length == 32) {
				return tile.TileData.ToUpperInvariant();
			}
			return tile.Pattern.ToUpperInvariant();
		}

		public static string GetCondition(TileSpec tile, bool mirrorConditions, bool matchBackgroundPriority = false, string extraCondition = "")
		{
			List<string> conditions = new();
			if(mirrorConditions) {
				if(tile.FlipH && tile.FlipV) {
					//Mesen combines conditions with '&'. The previous compiler treated both-axis
					//flips as inexpressible and silently emitted an unconditional entry.
					conditions.Add("hmirror");
					conditions.Add("vmirror");
				} else if(tile.FlipH) {
					conditions.Add("hmirror");
				} else if(tile.FlipV) {
					conditions.Add("vmirror");
				}
			}
			if(matchBackgroundPriority && tile.BackgroundPriority) {
				conditions.Add("bgpriority");
			}
			if(!string.IsNullOrWhiteSpace(extraCondition)) {
				foreach(string token in extraCondition.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
					if(!conditions.Contains(token, StringComparer.OrdinalIgnoreCase)) {
						conditions.Add(token);
					}
				}
			}
			return conditions.Count == 0 ? "" : "[" + string.Join("&", conditions) + "]";
		}

		/// <summary>Renders the original NES appearance of an asset from captured bytes.</summary>
		public static uint[] RenderOriginal(IReadOnlyList<TileSpec> tiles, NesRomData? rom, uint[] nesPalette, int width, int height)
		{
			uint[] original = new uint[width * height];
			foreach(TileSpec tile in tiles) {
				byte[]? data = null;
				if(!string.IsNullOrEmpty(tile.TileData)) {
					try {
						data = Convert.FromHexString(tile.TileData);
					} catch {
						data = null;
					}
				} else if(tile.ChrType.Contains("ChrRom", StringComparison.OrdinalIgnoreCase) && rom != null &&
					int.TryParse(tile.Pattern, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pattern) &&
					pattern >= 0 && pattern < rom.ChrTileCount) {
					data = rom.GetChrTile(pattern);
				}
				if(data == null || data.Length < 16) {
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
						if(px >= 0 && px < width && py >= 0 && py < height) {
							original[py * width + px] = PaletteToArgb(nesPalette, tile.Palette, color);
						}
					}
				}
			}
			return original;
		}

		public static void WritePreviews(string folder, string slug, uint[] original, int origWidth, int origHeight, uint[] replacement, int width, int height, Action<string> log)
		{
			origWidth = Math.Max(8, origWidth);
			origHeight = Math.Max(8, origHeight);
			uint[] orig4 = HdStudioPng.Upscale(original, origWidth, origHeight, 4);
			HdStudioPng.Write(Path.Combine(folder, slug + "_original.png"), orig4, origWidth * 4, origHeight * 4);
			HdStudioPng.Write(Path.Combine(folder, slug + "_replacement.png"), replacement, width, height);

			int displayW = Math.Max(origWidth * 4, width);
			int displayH = Math.Max(origHeight * 4, height);
			uint[] side = new uint[displayW * 2 * displayH];
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

		public static void Blit(uint[] target, int targetWidth, int cellSize, int x, int y, uint[] source, int size)
		{
			for(int yy = 0; yy < size; yy++) {
				for(int xx = 0; xx < size; xx++) {
					target[(y + yy) * targetWidth + x + xx] = source[yy * size + xx];
				}
			}
		}

		public static SKColor Lerp(SKColor a, SKColor b, double t)
		{
			return new SKColor(
				(byte)Math.Round(a.Red + (b.Red - a.Red) * t),
				(byte)Math.Round(a.Green + (b.Green - a.Green) * t),
				(byte)Math.Round(a.Blue + (b.Blue - a.Blue) * t),
				(byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * t));
		}

		public static double ColorDistance(SKColor a, SKColor b)
		{
			int dr = a.Red - b.Red;
			int dg = a.Green - b.Green;
			int db = a.Blue - b.Blue;
			return Math.Sqrt(dr * dr + dg * dg + db * db);
		}

		public static uint ToArgb(SKColor color)
		{
			return ((uint)color.Alpha << 24) | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;
		}

		private static int GetTilePixel(byte[] data, int x, int y)
		{
			int low = (data[y] >> (7 - x)) & 1;
			int high = (data[y + 8] >> (7 - x)) & 1;
			return low | (high << 1);
		}

		public static uint PaletteToArgb(uint[] nesPalette, string palette, int colorIndex)
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

		public static string HashPixels(uint[] pixels)
		{
			uint hash = 2166136261;
			foreach(uint p in pixels) {
				hash = (hash ^ p) * 16777619;
			}
			return hash.ToString("X8", CultureInfo.InvariantCulture);
		}

		public static string SafeSlug(string value)
		{
			StringBuilder sb = new();
			foreach(char c in value) {
				sb.Append(char.IsLetterOrDigit(c) ? c : '_');
			}
			return sb.ToString().Trim('_');
		}
	}
}
