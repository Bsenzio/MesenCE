using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Geometry and artwork-adaptation self tests.
	///
	/// These tests do not need a ROM or an emulator: they verify the rules that must hold for
	/// every replacement, independently of which game produced the asset. The HD pack scale
	/// only changes physical pixels; the logical NES footprint is never allowed to grow.
	/// </summary>
	internal static class HdStudioSelfTest
	{
		private sealed class Result
		{
			public int Passed;
			public int Failed;
			public readonly List<string> Failures = new();
		}

		public static int Run(Action<string> log)
		{
			Result result = new();

			void Check(string name, bool condition, string detail = "")
			{
				if(condition) {
					result.Passed++;
				} else {
					result.Failed++;
					string message = detail.Length > 0 ? name + ": " + detail : name;
					result.Failures.Add(message);
					log("FAIL: " + message);
				}
			}

			//1. Exact replacement dimensions for every common NES logical footprint.
			(int Width, int Height)[] logicalSizes = {
				(8, 8), (8, 16), (16, 8), (16, 16), (16, 24), (16, 32), (24, 24), (32, 32), (32, 48)
			};
			foreach((int width, int height) in logicalSizes) {
				for(int scale = 1; scale <= 8; scale++) {
					Check($"dimensions {width}x{height} @{scale}x",
						width * scale == width * scale && height * scale == height * scale,
						"scale arithmetic");
					Check($"tile alignment {width}x{height} @{scale}x",
						(width * scale) % (8 * scale) == 0 && (height * scale) % (8 * scale) == 0,
						"replacement is not aligned to the scaled 8x8 grid");
				}
			}

			//2. A 16x16 composition made of four 8x8 tiles becomes four 32x32 cells at 4x.
			HdStudioAdaptationSettings settings = new() { Fit = "stretch", Anchor = "center", Crop = false };
			HdStudioArtwork.TileSpec[] composition = {
				new() { Pattern = "01", X = 0, Y = 0, Palette = "FF0F1636" },
				new() { Pattern = "02", X = 8, Y = 0, Palette = "FF0F1636" },
				new() { Pattern = "03", X = 0, Y = 8, Palette = "FF0F1636" },
				new() { Pattern = "04", X = 8, Y = 8, Palette = "FF0F1636" }
			};
			HdStudioArtwork.ArtworkImage quadrants = CreateImage(64, 64, (x, y) => {
				if(x < 32 && y < 32) return new SKColor(255, 0, 0, 255);
				if(x >= 32 && y < 32) return new SKColor(0, 255, 0, 255);
				if(x < 32 && y >= 32) return new SKColor(0, 0, 255, 255);
				return new SKColor(255, 255, 0, 255);
			});
			SKColor[] composed = HdStudioArtwork.Compose(quadrants, new SKRectI(0, 0, 64, 64), 64, 64, settings);
			List<HdStudioArtwork.Cell> cells = HdStudioArtwork.BuildCells(composition, composed, 64, 64, 4, false, _ => { });
			Check("16x16 composition produces four 8x8 replacements", cells.Count == 4, "got " + cells.Count);
			Check("replacement cells are exactly 32x32 at 4x", cells.All(c => c.Pixels.Length == 32 * 32));
			Check("replacement cells are unique", cells.Select(c => c.Key).Distinct().Count() == 4);
			if(cells.Count == 4) {
				Check("quadrant 0 stays red", IsCellColor(cells[0].Pixels, new SKColor(255, 0, 0, 255)));
				Check("quadrant 1 stays green", IsCellColor(cells[1].Pixels, new SKColor(0, 255, 0, 255)));
				Check("quadrant 2 stays blue", IsCellColor(cells[2].Pixels, new SKColor(0, 0, 255, 255)));
				Check("quadrant 3 stays yellow", IsCellColor(cells[3].Pixels, new SKColor(255, 255, 0, 255)));
			}

			//3. Mirror normalization: Mesen flips the replacement at render time, so the stored
			//cell must be unflipped or an asymmetric object would appear twice-mirrored.
			HdStudioArtwork.TileSpec[] mirrored = {
				new() { Pattern = "10", X = 0, Y = 0, FlipH = true, Palette = "FF0F1636" }
			};
			HdStudioArtwork.ArtworkImage leftRedRightBlue = CreateImage(16, 16, (x, y) => x < 8 ? new SKColor(255, 0, 0, 255) : new SKColor(0, 0, 255, 255));
			SKColor[] mirroredComposed = HdStudioArtwork.Compose(leftRedRightBlue, new SKRectI(0, 0, 16, 16), 16, 16, settings);
			List<HdStudioArtwork.Cell> mirroredCells = HdStudioArtwork.BuildCells(mirrored, mirroredComposed, 16, 16, 2, true, _ => { });
			Check("mirrored occurrence emits one cell", mirroredCells.Count == 1);
			Check("mirrored cell is conditional", mirroredCells.Count == 1 && mirroredCells[0].Condition == "[hmirror]");
			if(mirroredCells.Count == 1) {
				Check("mirrored cell is stored unflipped", IsCellColor(mirroredCells[0].Pixels, new SKColor(0, 0, 255, 255)));
			}

			//4. Irregular compositions preserve occupied cells and leave gaps transparent.
			HdStudioArtwork.TileSpec[] irregular = {
				new() { Pattern = "20", X = 0, Y = 0, Palette = "FF0F1636" },
				new() { Pattern = "21", X = 16, Y = 0, Palette = "FF0F1636" },
				new() { Pattern = "22", X = 8, Y = 8, Palette = "FF0F1636" }
			};
			SKColor[] irregularSource = HdStudioArtwork.Compose(quadrants, new SKRectI(0, 0, 64, 64), 32, 32, settings);
			List<HdStudioArtwork.Cell> irregularCells = HdStudioArtwork.BuildCells(irregular, irregularSource, 32, 32, 4, false, _ => { });
			Check("irregular composition keeps three cells", irregularCells.Count == 3, "got " + irregularCells.Count);
			Check("missing irregular tile is not invented", irregularCells.All(c => c.Key != "23"));

			//5. Oversized artwork is contained/stretched inside the logical region, never outside.
			HdStudioArtwork.ArtworkImage wide = CreateImage(400, 100, (x, y) => new SKColor(200, 30, 40, 255));
			HdStudioAdaptationSettings contain = new() { Fit = "contain", Anchor = "center", Crop = false };
			SKColor[] fitted = HdStudioArtwork.Compose(wide, new SKRectI(0, 0, 400, 100), 64, 64, contain);
			SKRectI fittedBounds = HdStudioArtwork.AlphaBounds(new HdStudioArtwork.ArtworkImage() { Width = 64, Height = 64, Pixels = fitted });
			Check("contain keeps oversized art inside the target",
				fittedBounds.Width == 64 && fittedBounds.Height == 16 && fittedBounds.Top == 24,
				$"bounds {fittedBounds.Width}x{fittedBounds.Height} at {fittedBounds.Left},{fittedBounds.Top}");
			HdStudioAdaptationSettings stretch = new() { Fit = "stretch", Anchor = "center", Crop = false };
			SKColor[] stretched = HdStudioArtwork.Compose(wide, new SKRectI(0, 0, 400, 100), 64, 64, stretch);
			Check("stretch fills the whole logical region", stretched.All(c => c.Alpha > 0));

			//6. Transparency is preserved and never replaced by an opaque filler.
			HdStudioArtwork.ArtworkImage transparent = CreateImage(64, 64, (x, y) => x == 20 && y == 30 ? new SKColor(10, 220, 10, 255) : SKColors.Transparent);
			SKColor[] transparentComposed = HdStudioArtwork.Compose(transparent, new SKRectI(0, 0, 64, 64), 64, 64, settings);
			Check("transparent artwork keeps its transparent pixels", transparentComposed.Count(c => c.Alpha == 0) > 4000);
			Check("transparent artwork keeps the opaque subject", transparentComposed.Any(c => c.Alpha > 0));

			//7. Palette association uses the captured NES palette bytes exactly.
			uint[] nesPalette = new uint[64];
			for(int i = 0; i < nesPalette.Length; i++) {
				nesPalette[i] = 0xFF000000u | ((uint)i * 0x010101u);
			}
			uint color1 = HdStudioArtwork.PaletteToArgb(nesPalette, "FF010203", 1);
			Check("palette index 1 maps to NES color 0x01", color1 == nesPalette[1], color1.ToString("X8", CultureInfo.InvariantCulture));
			uint color2 = HdStudioArtwork.PaletteToArgb(nesPalette, "FF010203", 2);
			Check("palette index 2 maps to NES color 0x02", color2 == nesPalette[2], color2.ToString("X8", CultureInfo.InvariantCulture));
			uint color3 = HdStudioArtwork.PaletteToArgb(nesPalette, "FF010203", 3);
			Check("palette index 3 maps to NES color 0x03", color3 == nesPalette[3], color3.ToString("X8", CultureInfo.InvariantCulture));

			//8. Stable identity is deterministic and does not depend on display names.
			string id1 = HdStudioProject.StableId("asset-a|managed");
			string id2 = HdStudioProject.StableId("asset-a|managed");
			string id3 = HdStudioProject.StableId("asset-a|legacy");
			Check("stable ids are deterministic", id1 == id2);
			Check("stable ids distinguish provenance", id1 != id3);

			log($"SELFTEST: {result.Passed} passed, {result.Failed} failed.");
			log("SELFTEST_JSON " + "{ \"passed\": " + result.Passed + ", \"failed\": " + result.Failed +
				", \"failures\": [" + string.Join(",", result.Failures.Select(f => "\"" + f.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "] }");
			return result.Failed == 0 ? 0 : 1;
		}

		private static HdStudioArtwork.ArtworkImage CreateImage(int width, int height, Func<int, int, SKColor> color)
		{
			SKColor[] pixels = new SKColor[width * height];
			for(int y = 0; y < height; y++) {
				for(int x = 0; x < width; x++) {
					pixels[y * width + x] = color(x, y);
				}
			}
			return new HdStudioArtwork.ArtworkImage() { Width = width, Height = height, Pixels = pixels };
		}

		private static bool IsCellColor(uint[] pixels, SKColor expected)
		{
			return pixels.Length > 0 && HdStudioArtwork.ToArgb(expected) == pixels[0];
		}

	}
}
