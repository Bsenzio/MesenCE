using System;
using System.Collections.Generic;
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
	/// The same import is recorded in the editable HdStudio project when one can be created.
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
			public bool MatchBackgroundPriority;
			public bool AllPaletteVariants;
			public string ExtraCondition = "";
			public bool Minimal;
			public bool DryRun;
		}

		public static int Run(string romPath, string outFolder, Options options, uint[] nesPalette, Action<string> log)
		{
			if(string.IsNullOrWhiteSpace(options.AssetId) || string.IsNullOrWhiteSpace(options.ImagePath)) {
				log("Import: --asset=ID and --image=FILE are required.");
				return 1;
			}

			HdStudioCatalog catalog = HdStudioCatalog.Load(outFolder);
			string assetsPath = catalog.AssetsPath;
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

			HdStudioCatalogAsset? asset = catalog.Find(options.AssetId);
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
			int width = asset.Width > 0 ? asset.Width : asset.Tiles.Max(t => t.X + 8);
			int height = asset.Height > 0 ? asset.Height : asset.Tiles.Max(t => t.Y + 8);
			int targetWidth = width * packScale;
			int targetHeight = height * packScale;

			HdStudioAdaptationSettings settings = new() {
				RemoveBackground = options.RemoveBackground,
				BackgroundTolerance = options.BackgroundTolerance,
				KeepLargestComponent = options.KeepLargestComponent,
				Crop = options.Crop,
				Fit = options.Fit,
				Anchor = options.Anchor,
				Padding = options.Padding,
				MirrorConditions = options.MirrorConditions,
				MatchBackgroundPriority = options.MatchBackgroundPriority,
				AllPaletteVariants = options.AllPaletteVariants,
				ExtraCondition = options.ExtraCondition
			};

			HdStudioArtwork.ArtworkImage source = HdStudioArtwork.LoadImage(options.ImagePath);
			if(settings.RemoveBackground) {
				HdStudioArtwork.RemoveBackground(source, settings.BackgroundTolerance);
			}
			if(settings.KeepLargestComponent) {
				HdStudioArtwork.KeepLargestComponent(source);
			}
			SKRectI bounds = settings.Crop ? HdStudioArtwork.AlphaBounds(source) : new SKRectI(0, 0, source.Width, source.Height);
			if(bounds.Width <= 0 || bounds.Height <= 0) {
				log("Import: the image is empty or fully transparent.");
				return 1;
			}

			SKColor[] composed = HdStudioArtwork.Compose(source, bounds, targetWidth, targetHeight, settings);
			string slug = HdStudioArtwork.SafeSlug(string.IsNullOrWhiteSpace(options.ImportName) ? asset.Name : options.ImportName) + "_" + asset.Id.Split(':').Last();
			string importsFolder = Path.Combine(outFolder, HdStudioReport.StudioFolderName, "imports");
			Directory.CreateDirectory(importsFolder);

			List<HdStudioArtwork.TileSpec> tileSpecs = asset.Tiles.Select(t => new HdStudioArtwork.TileSpec() {
				Pattern = t.Pattern,
				TileData = t.TileData,
				ChrType = t.ChrType,
				X = t.X,
				Y = t.Y,
				FlipH = t.FlipH,
				FlipV = t.FlipV,
				BackgroundPriority = t.BackgroundPriority,
				Palette = t.Palette
			}).ToList();

			uint[] original = HdStudioArtwork.RenderOriginal(tileSpecs, rom, nesPalette, Math.Max(8, asset.Width), Math.Max(8, asset.Height));
			uint[] replacementPixels = composed.Select(HdStudioArtwork.ToArgb).ToArray();
			HdStudioArtwork.WritePreviews(importsFolder, slug, original, Math.Max(8, asset.Width), Math.Max(8, asset.Height), replacementPixels, targetWidth, targetHeight, log);

			//Segment the one composed image into replacement tiles.
			List<HdStudioArtwork.Cell> cells = HdStudioArtwork.BuildCells(tileSpecs, composed, targetWidth, targetHeight, packScale, settings.MirrorConditions, log, settings.AllPaletteVariants ? asset.PaletteVariants : null, settings.MatchBackgroundPriority, settings.ExtraCondition);
			if(cells.Count == 0) {
				log("Import: no replacement cells were produced.");
				return 1;
			}
			List<HdStudioCatalogAsset> sharedAssets = HdStudioCatalog.FindAssetsSharingKeys(catalog, asset.Id,
				cells.Select(c => (c.Key, c.Palette)));
			if(sharedAssets.Count > 0) {
				string names = string.Join(", ", sharedAssets.Take(6).Select(a => a.Name));
				if(sharedAssets.Count > 6) {
					names += ", ...";
				}
				string message = $"The same tile+palette keys are also used by {sharedAssets.Count} other captured asset(s): {names}. Standard Mesen cannot distinguish them unless a supported condition applies, so replacing them together is intentional.";
				log("Import: warning: " + message);
			}

			int tileSize = 8 * packScale;
			bool managed = !options.DryRun && !options.Minimal;
			string sheetName = $"HdStudioImport_{slug}.png";
			if(!managed) {
				string sheetPath = Path.Combine(outFolder, sheetName);
				uint[] sheet = new uint[cells.Count * tileSize * tileSize];
				for(int i = 0; i < cells.Count; i++) {
					HdStudioArtwork.Blit(sheet, cells.Count * tileSize, tileSize, cells[i].X, 0, cells[i].Pixels, tileSize);
				}
				HdStudioPng.Write(sheetPath, sheet, cells.Count * tileSize, tileSize);
			}

			string hiresPath = Path.Combine(outFolder, "hires.txt");
			if(!options.DryRun && !managed) {
				WriteHires(hiresPath, sheetName, cells, packScale, rom, options.Minimal, settings.ExtraCondition, log);
			}

			string manifestPath = WriteManifest(importsFolder, slug, options, asset, cells, sheetName, width, height, targetWidth, targetHeight, packScale);
			log($"Import: {asset.Name} ({asset.Kind}) -> {cells.Count} replacement tiles, pack scale {packScale}x{(options.DryRun ? " (dry run)" : "")}.");

			if(!options.DryRun) {
				HdStudioProject? project = RecordInProject(romPath, outFolder, options, asset, cells, sharedAssets, sheetName, manifestPath, width, height, targetWidth, targetHeight, packScale, settings, managed, log);
				if(managed) {
					if(project == null) {
						log("Import: the project could not be created, so the pack was not modified.");
						return 1;
					}
					HdStudioCompileResult result = HdStudioCompiler.Compile(project, log);
					if(!result.Success) {
						log("Import: the standard pack build failed.");
						return 1;
					}
				}
			}
			return 0;
		}

		private static HdStudioProject? RecordInProject(string romPath, string outFolder, Options options, HdStudioCatalogAsset asset, List<HdStudioArtwork.Cell> cells, List<HdStudioCatalogAsset> sharedAssets, string sheetName, string manifestPath, int width, int height, int targetWidth, int targetHeight, int packScale, HdStudioAdaptationSettings settings, bool managed, Action<string> log)
		{
			try {
				string projectPath = HdStudioProject.GetDefaultPath(outFolder);
				HdStudioProject project = File.Exists(projectPath) ? HdStudioProject.Load(projectPath) : HdStudioProject.Create(romPath, outFolder, packScale, log);
				string id = "replacement:" + HdStudioProject.StableId(asset.Id + "|" + (managed ? "managed" : "legacy"));
				HdStudioReplacement? replacement = project.Replacements.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
				if(replacement == null) {
					replacement = new HdStudioReplacement() {
						Id = id,
						AssetId = asset.Id,
						AssetName = asset.Name,
						AssetKind = asset.Kind,
						CreatedAtUtc = DateTime.UtcNow.ToString("O")
					};
					project.Replacements.Add(replacement);
				}
				replacement.Sheet = sheetName;
				replacement.SourceImage = project.StoreArtwork(options.ImagePath, id);
				replacement.PackScale = packScale;
				replacement.OriginalWidth = width;
				replacement.OriginalHeight = height;
				replacement.TargetWidth = targetWidth;
				replacement.TargetHeight = targetHeight;
				replacement.Approval = "approved";
				replacement.AllowSharedReplacement = sharedAssets.Count > 0;
				replacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
				//A managed import is emitted by the compiler, so it is not part of the immutable
				//base snapshot. A legacy/minimal import was written straight into hires.txt and
				//is kept in the base instead.
				replacement.LegacyManifest = managed ? "" : Path.GetFileName(manifestPath);
				replacement.Settings.RemoveBackground = settings.RemoveBackground;
				replacement.Settings.BackgroundTolerance = settings.BackgroundTolerance;
				replacement.Settings.KeepLargestComponent = settings.KeepLargestComponent;
				replacement.Settings.Crop = settings.Crop;
				replacement.Settings.Fit = settings.Fit;
				replacement.Settings.Anchor = settings.Anchor;
				replacement.Settings.Padding = settings.Padding;
				replacement.Settings.MirrorConditions = settings.MirrorConditions;
				replacement.Settings.MatchBackgroundPriority = settings.MatchBackgroundPriority;
				replacement.Settings.AllPaletteVariants = settings.AllPaletteVariants;
				replacement.Settings.ExtraCondition = settings.ExtraCondition;
				replacement.Warnings.Clear();
				if(sharedAssets.Count > 0) {
					string names = string.Join(", ", sharedAssets.Take(6).Select(a => a.Name));
					if(sharedAssets.Count > 6) {
						names += ", ...";
					}
					replacement.Warnings.Add($"Shared tile+palette keys with {sharedAssets.Count} other asset(s): {names}. They are replaced together unless a supported condition distinguishes them.");
				}
				replacement.Cells.Clear();
				foreach(HdStudioArtwork.Cell cell in cells) {
					replacement.Cells.Add(new HdStudioReplacementCell() {
						Key = cell.Key,
						Palette = cell.Palette,
						Condition = cell.Condition,
						SheetX = cell.X
					});
				}
				project.Save(projectPath);
				log("Import: recorded in project " + projectPath);
				return project;
			} catch(Exception ex) {
				log("Import: warning: the project file could not be updated: " + ex.Message);
				return null;
			}
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

		private static void WriteHires(string path, string sheetName, List<HdStudioArtwork.Cell> cells, int scale, NesRomData rom, bool minimal, string extraCondition, Action<string> log)
		{
			List<string> lines = new();
			if(!minimal && File.Exists(path)) {
				lines.AddRange(File.ReadAllLines(path));
			} else {
				lines.Add("<ver>109");
				lines.Add($"<scale>{scale}");
				lines.Add("<supportedRom>" + rom.Sha1);
			}

			List<string> conditionErrors = new();
			List<string> conditions = HdStudioArtwork.GetCustomConditionDefinitions(extraCondition, conditionErrors);
			foreach(string error in conditionErrors) {
				log("Import: condition error: " + error);
			}
			if(conditions.Count > 0) {
				int insertCondition = lines.FindIndex(l => l.StartsWith("<supportedRom>", StringComparison.Ordinal));
				lines.InsertRange(insertCondition < 0 ? lines.Count : insertCondition + 1, conditions);
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
			foreach(HdStudioArtwork.Cell cell in cells) {
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

		private static string WriteManifest(string folder, string slug, Options options, HdStudioCatalogAsset asset, List<HdStudioArtwork.Cell> cells, string sheetName, int width, int height, int targetWidth, int targetHeight, int scale)
		{
			string path = Path.Combine(folder, slug + ".json");
			using(FileStream stream = File.Create(path)) {
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
				json.WriteBoolean("allPaletteVariants", options.AllPaletteVariants);
				json.WriteStartArray("tiles");
				foreach(HdStudioArtwork.Cell cell in cells) {
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
			return path;
		}
	}
}
