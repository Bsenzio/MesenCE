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
	/// HD Studio persistent project.
	///
	/// The project is the editable authoring format. It keeps the original ROM identity, the
	/// capture/pack folder, every imported artwork source (copied into project storage), the
	/// exact adaptation parameters and the approval state. The generated Mesen HD pack is a
	/// build output and can always be regenerated from the project.
	/// </summary>
	internal sealed class HdStudioProject
	{
		public const string SchemaVersion = "1.0.0";
		public const string DefaultFileName = "project.hdstudio.json";

		public string ProjectName = "";
		public string CreatedAtUtc = "";
		public string UpdatedAtUtc = "";
		public string RomPath = "";
		public string RomPathAbsolute = "";
		public string RomSha1 = "";
		public string RomSha1NoHeader = "";
		public int Mapper;
		public string ChrMode = "";
		public long ChrBytes;
		public string PackFolder = "";
		public string OriginalPackFolder = "";
		public int OutputScale = 4;
		public string CaptureStatus = "";
		public int CaptureFrames;
		public int CapturedAssets;
		/// <summary>Stable catalog ids whose approved replacement must not be overwritten by
		/// automatic animation/sheet workflows. Stored with the project, not the pack.</summary>
		public readonly List<string> LockedAssets = new();
		public readonly List<HdStudioReplacement> Replacements = new();
		public readonly List<string> ValidationNotes = new();

		public static string GetDefaultPath(string packFolder)
		{
			return Path.Combine(packFolder, HdStudioReport.StudioFolderName, DefaultFileName);
		}

		public static HdStudioProject Create(string romPath, string packFolder, int scale, Action<string>? log = null)
		{
			NesRomData rom = NesRomData.Load(romPath);
			HdStudioProject project = new() {
				ProjectName = Path.GetFileNameWithoutExtension(romPath),
				CreatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
				UpdatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
				RomPath = Path.GetFullPath(romPath),
				RomPathAbsolute = Path.GetFullPath(romPath),
				RomSha1 = rom.Sha1,
				RomSha1NoHeader = rom.Sha1NoHeader,
				Mapper = rom.Mapper,
				ChrMode = rom.HasChrRom ? "CHR-ROM" : "CHR-RAM",
				ChrBytes = rom.ChrSize,
				PackFolder = Path.GetFullPath(packFolder),
				OriginalPackFolder = Path.GetFullPath(packFolder),
				OutputScale = Math.Clamp(scale, 1, 10)
			};
			log?.Invoke($"Project: {project.ProjectName}, mapper {project.Mapper}, {project.ChrMode}, scale {project.OutputScale}x.");
			return project;
		}

		/// <summary>Loads a project and verifies that the ROM is still the same file.</summary>
		public static HdStudioProject Load(string path)
		{
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			JsonElement root = doc.RootElement;
			string fullPath = Path.GetFullPath(path);
			//A project is a folder that can be copied/moved as a unit. The JSON records the
			//absolute folder it was created in, but the authoritative pack folder is always
			//the parent of the HdStudio directory that contains this project file. Without
			//this rebase, opening a copied project would write builds back into the original
			//pack (and silently mutate a different project's output).
			string projectFolder = Path.GetDirectoryName(fullPath) ?? ".";
			string rebasedPack = Path.GetFullPath(Path.Combine(projectFolder, ".."));
			HdStudioProject project = new() {
				ProjectName = GetString(root, "projectName"),
				CreatedAtUtc = GetString(root, "createdAtUtc"),
				UpdatedAtUtc = GetString(root, "updatedAtUtc"),
				RomPath = GetString(root, "romPath"),
				RomPathAbsolute = GetString(root, "romPathAbsolute"),
				RomSha1 = GetString(root, "romSha1"),
				RomSha1NoHeader = GetString(root, "romSha1NoHeader"),
				Mapper = GetInt(root, "mapper"),
				ChrMode = GetString(root, "chrMode"),
				ChrBytes = GetLong(root, "chrBytes"),
				PackFolder = rebasedPack,
				OriginalPackFolder = GetString(root, "packFolder"),
				OutputScale = Math.Clamp(GetInt(root, "outputScale", 4), 1, 10),
				CaptureStatus = GetString(root, "captureStatus"),
				CaptureFrames = GetInt(root, "captureFrames"),
				CapturedAssets = GetInt(root, "capturedAssets")
			};

			if(root.TryGetProperty("validationNotes", out JsonElement notes) && notes.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement note in notes.EnumerateArray()) {
					project.ValidationNotes.Add(note.GetString() ?? "");
				}
			}

			if(root.TryGetProperty("replacements", out JsonElement list) && list.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement element in list.EnumerateArray()) {
					HdStudioReplacement? replacement = HdStudioReplacement.FromJson(element);
					if(replacement != null) {
						project.Replacements.Add(replacement);
					}
				}
			}

			if(root.TryGetProperty("lockedAssets", out JsonElement locked) && locked.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement item in locked.EnumerateArray()) {
					if(item.ValueKind == JsonValueKind.String) {
						project.LockedAssets.Add(item.GetString() ?? "");
					}
				}
			}

			project.RomPath = ResolveRomPath(project.RomPath, project.RomPathAbsolute, project.PackFolder, project.OriginalPackFolder);
			if(string.IsNullOrWhiteSpace(project.RomPathAbsolute) && Path.IsPathFullyQualified(project.RomPath)) {
				project.RomPathAbsolute = project.RomPath;
			}

			//Make relative artwork paths usable regardless of the current directory.
			foreach(HdStudioReplacement replacement in project.Replacements) {
				replacement.SourceImage = project.ResolvePath(replacement.SourceImage);
			}
			return project;
		}

		public void Save(string path)
		{
			UpdatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
			string folder = Path.GetDirectoryName(path) ?? ".";
			Directory.CreateDirectory(folder);
			string temp = path + ".tmp";
			using(FileStream stream = File.Create(temp)) {
				using(Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true })) {
					json.WriteStartObject();
					json.WriteString("schemaVersion", SchemaVersion);
					json.WriteString("generatedBy", "RetroArcDo HD Studio");
					json.WriteString("projectName", ProjectName);
					json.WriteString("createdAtUtc", CreatedAtUtc);
					json.WriteString("updatedAtUtc", UpdatedAtUtc);
					string storedRom = RomPath;
					if(Path.IsPathFullyQualified(RomPath)) {
						string relativeRom = Path.GetRelativePath(PackFolder, RomPath);
						if(!Path.IsPathFullyQualified(relativeRom)) {
							storedRom = relativeRom.Replace('\\', '/');
						}
					}
					json.WriteString("romPath", storedRom);
					json.WriteString("romPathAbsolute", RomPathAbsolute.Length > 0 ? RomPathAbsolute : (Path.IsPathFullyQualified(RomPath) ? RomPath : ""));
					json.WriteString("romSha1", RomSha1);
					json.WriteString("romSha1NoHeader", RomSha1NoHeader);
					json.WriteNumber("mapper", Mapper);
					json.WriteString("chrMode", ChrMode);
					json.WriteNumber("chrBytes", ChrBytes);
					json.WriteString("packFolder", PackFolder);
					json.WriteNumber("outputScale", OutputScale);
					json.WriteString("captureStatus", CaptureStatus);
					json.WriteNumber("captureFrames", CaptureFrames);
					json.WriteNumber("capturedAssets", CapturedAssets);
					json.WriteStartArray("validationNotes");
					foreach(string note in ValidationNotes) {
						json.WriteStringValue(note);
					}
					json.WriteEndArray();
					json.WriteStartArray("replacements");
					foreach(HdStudioReplacement replacement in Replacements.OrderBy(r => r.AssetId).ThenBy(r => r.Id)) {
						replacement.WriteJson(json, path);
					}
					json.WriteEndArray();
					json.WriteStartArray("lockedAssets");
					foreach(string lockedId in LockedAssets.Distinct(StringComparer.OrdinalIgnoreCase)) {
						json.WriteStringValue(lockedId);
					}
					json.WriteEndArray();
					json.WriteEndObject();
				}
			}
			if(File.Exists(path)) {
				File.Delete(path);
			}
			File.Move(temp, path);
		}

		/// <summary>
		/// Resolves the ROM path stored in a project. New projects store it relative to the
		/// pack folder so a distribution folder can be moved or unzipped anywhere. Old
		/// projects store an absolute path; if that file is gone, the same filename is
		/// searched in the usual portable locations near the pack.
		/// </summary>
		private static string ResolveRomPath(string stored, string storedAbsolute, string packFolder, string originalPackFolder)
		{
			if(!string.IsNullOrWhiteSpace(stored) && !Path.IsPathFullyQualified(stored)) {
				foreach(string baseFolder in new[] { packFolder, originalPackFolder }) {
					if(string.IsNullOrWhiteSpace(baseFolder)) {
						continue;
					}
					string relative = Path.GetFullPath(Path.Combine(baseFolder, stored.Replace('/', Path.DirectorySeparatorChar)));
					if(File.Exists(relative)) {
						return relative;
					}
				}
			}
			if(File.Exists(stored)) {
				return stored;
			}
			if(!string.IsNullOrWhiteSpace(storedAbsolute) && File.Exists(storedAbsolute)) {
				return storedAbsolute;
			}

			//A copied/moved project may no longer resolve either path. Search the usual
			//portable locations by filename so a project and its ROM can still be reunited.
			string fileName = Path.GetFileName(!string.IsNullOrWhiteSpace(stored) ? stored : storedAbsolute);
			string? folder = packFolder;
			for(int i = 0; i < 6 && !string.IsNullOrEmpty(folder); i++) {
				foreach(string candidate in new[] {
					Path.Combine(folder, fileName),
					Path.Combine(folder, "examples", fileName),
					Path.Combine(folder, "roms", fileName)
				}) {
					if(File.Exists(candidate)) {
						return candidate;
					}
				}
				folder = Path.GetDirectoryName(folder);
			}
			return !string.IsNullOrWhiteSpace(stored) ? stored : storedAbsolute;
		}

		public string ResolvePath(string path)
		{
			if(string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path)) {
				return path;
			}
			string relative = path.Replace('/', Path.DirectorySeparatorChar);
			//Artwork is stored next to the project file (HdStudio/artwork) and written
			//relative to it, so a project folder can be moved as a unit.
			string candidate = Path.GetFullPath(Path.Combine(ProjectFolder, relative));
			if(File.Exists(candidate)) {
				return candidate;
			}
			string packRelative = Path.GetFullPath(Path.Combine(PackFolder, relative));
			return File.Exists(packRelative) ? packRelative : candidate;
		}

		public string ProjectFolder => Path.Combine(PackFolder, HdStudioReport.StudioFolderName);

		public string ArtworkFolder => Path.Combine(ProjectFolder, "artwork");

		/// <summary>Copies the user's source image into managed project storage and returns the path.</summary>
		public string StoreArtwork(string sourcePath, string replacementId)
		{
			Directory.CreateDirectory(ArtworkFolder);
			string extension = Path.GetExtension(sourcePath);
			if(string.IsNullOrWhiteSpace(extension)) {
				extension = ".png";
			}
			string hash = HashFile(sourcePath).Substring(0, 12);
			string safeId = new string(replacementId.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
			string destination = Path.Combine(ArtworkFolder, safeId + "_" + hash + extension.ToLowerInvariant());
			if(!File.Exists(destination)) {
				File.Copy(sourcePath, destination);
			}
			return destination;
		}

		public static string HashFile(string path)
		{
			using FileStream stream = File.OpenRead(path);
			return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
		}

		public static string NormalizeSha1(string path)
		{
			return Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path)));
		}

		/// <summary>
		/// Imports pre-existing CLI importer manifests into the project. This is what keeps old
		/// captures editable instead of leaving them disconnected from the project database.
		/// </summary>
		public int ImportLegacyManifests(Action<string>? log = null)
		{
			string imports = Path.Combine(ProjectFolder, "imports");
			if(!Directory.Exists(imports)) {
				return 0;
			}
			int added = 0;
			foreach(string path in Directory.GetFiles(imports, "*.json").OrderBy(p => p)) {
				try {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
					JsonElement root = doc.RootElement;
					string assetId = GetString(root, "assetId");
					if(string.IsNullOrWhiteSpace(assetId) || Replacements.Any(r => string.Equals(r.AssetId, assetId, StringComparison.OrdinalIgnoreCase))) {
						continue;
					}
					string sourceImage = GetString(root, "sourceImage");
					if(string.IsNullOrWhiteSpace(sourceImage) || !File.Exists(sourceImage)) {
						log?.Invoke("Project: importer manifest has no source image and was skipped: " + path);
						continue;
					}
					string id = "replacement:" + StableId(assetId + "|" + Path.GetFileName(path));
					string stored = StoreArtwork(sourceImage, id);
					HdStudioReplacement replacement = new() {
						Id = id,
						AssetId = assetId,
						AssetName = GetString(root, "assetName"),
						AssetKind = "imported",
						SourceImage = stored,
						Sheet = GetString(root, "sheet"),
						PackScale = GetInt(root, "packScale", OutputScale),
						OriginalWidth = GetInt(root, "originalWidth"),
						OriginalHeight = GetInt(root, "originalHeight"),
						TargetWidth = GetInt(root, "targetWidth"),
						TargetHeight = GetInt(root, "targetHeight"),
						Approval = "approved",
						CreatedAtUtc = File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture),
						UpdatedAtUtc = File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture),
						LegacyManifest = Path.GetFileName(path)
					};
					replacement.Settings.RemoveBackground = GetBool(root, "removeBackground");
					replacement.Settings.KeepLargestComponent = GetBool(root, "keepLargestComponent");
					replacement.Settings.Fit = GetString(root, "fit", "contain");
					replacement.Settings.Anchor = GetString(root, "anchor", "center");
					replacement.Settings.AllPaletteVariants = !root.TryGetProperty("allPaletteVariants", out JsonElement apv) || apv.ValueKind != JsonValueKind.False;
					if(root.TryGetProperty("tiles", out JsonElement tiles) && tiles.ValueKind == JsonValueKind.Array) {
						foreach(JsonElement tile in tiles.EnumerateArray()) {
							replacement.Cells.Add(new HdStudioReplacementCell() {
								Key = GetString(tile, "key"),
								Palette = GetString(tile, "palette"),
								Condition = GetString(tile, "condition"),
								SheetX = GetInt(tile, "sheetX")
							});
						}
					}
					Replacements.Add(replacement);
					added++;
				} catch(Exception ex) {
					log?.Invoke("Project: could not import manifest " + Path.GetFileName(path) + ": " + ex.Message);
				}
			}
			if(added > 0) {
				log?.Invoke($"Project: imported {added} existing CLI replacement manifest(s).");
			}
			return added;
		}

		public static string StableId(string value)
		{
			return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 12).ToLowerInvariant();
		}

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		private static int GetInt(JsonElement element, string name, int fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
		}

		private static long GetLong(JsonElement element, string name, long fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : fallback;
		}

		private static bool GetBool(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}
	}

	public sealed class HdStudioAdaptationSettings
	{
		public bool RemoveBackground;
		public int BackgroundTolerance = 40;
		public bool KeepLargestComponent;
		public bool Crop = true;
		public string Fit = "contain";
		public string Anchor = "center";
		public int Padding;
		public int OffsetX;
		public int OffsetY;
		public int ZoomPercent = 100;
		public bool FlipH;
		public bool FlipV;
		public bool MirrorConditions;
		public bool MatchBackgroundPriority;
		public bool AllPaletteVariants = true;
		public int CropLeft;
		public int CropTop;
		public int CropRight;
		public int CropBottom;

		public void WriteJson(Utf8JsonWriter json)
		{
			json.WriteStartObject("adaptation");
			json.WriteBoolean("removeBackground", RemoveBackground);
			json.WriteNumber("backgroundTolerance", BackgroundTolerance);
			json.WriteBoolean("keepLargestComponent", KeepLargestComponent);
			json.WriteBoolean("crop", Crop);
			json.WriteString("fit", Fit);
			json.WriteString("anchor", Anchor);
			json.WriteNumber("padding", Padding);
			json.WriteNumber("offsetX", OffsetX);
			json.WriteNumber("offsetY", OffsetY);
			json.WriteNumber("zoomPercent", ZoomPercent);
			json.WriteBoolean("flipH", FlipH);
			json.WriteBoolean("flipV", FlipV);
			json.WriteBoolean("mirrorConditions", MirrorConditions);
			json.WriteBoolean("matchBackgroundPriority", MatchBackgroundPriority);
			json.WriteBoolean("allPaletteVariants", AllPaletteVariants);
			json.WriteNumber("cropLeft", CropLeft);
			json.WriteNumber("cropTop", CropTop);
			json.WriteNumber("cropRight", CropRight);
			json.WriteNumber("cropBottom", CropBottom);
			json.WriteEndObject();
		}

		public void ReadJson(JsonElement root)
		{
			if(!root.TryGetProperty("adaptation", out JsonElement a) || a.ValueKind != JsonValueKind.Object) {
				return;
			}
			RemoveBackground = a.TryGetProperty("removeBackground", out JsonElement rb) && rb.ValueKind == JsonValueKind.True;
			BackgroundTolerance = a.TryGetProperty("backgroundTolerance", out JsonElement bt) && bt.TryGetInt32(out int t) ? t : 40;
			KeepLargestComponent = a.TryGetProperty("keepLargestComponent", out JsonElement kl) && kl.ValueKind == JsonValueKind.True;
			Crop = !a.TryGetProperty("crop", out JsonElement cr) || cr.ValueKind != JsonValueKind.False;
			if(a.TryGetProperty("fit", out JsonElement f) && f.ValueKind == JsonValueKind.String) Fit = f.GetString() ?? "contain";
			if(a.TryGetProperty("anchor", out JsonElement an) && an.ValueKind == JsonValueKind.String) Anchor = an.GetString() ?? "center";
			Padding = a.TryGetProperty("padding", out JsonElement p) && p.TryGetInt32(out int pad) ? pad : 0;
			OffsetX = a.TryGetProperty("offsetX", out JsonElement ox) && ox.TryGetInt32(out int x) ? x : 0;
			OffsetY = a.TryGetProperty("offsetY", out JsonElement oy) && oy.TryGetInt32(out int y) ? y : 0;
			ZoomPercent = a.TryGetProperty("zoomPercent", out JsonElement z) && z.TryGetInt32(out int zp) ? Math.Clamp(zp, 10, 400) : 100;
			FlipH = a.TryGetProperty("flipH", out JsonElement fh) && fh.ValueKind == JsonValueKind.True;
			FlipV = a.TryGetProperty("flipV", out JsonElement fv) && fv.ValueKind == JsonValueKind.True;
			MirrorConditions = a.TryGetProperty("mirrorConditions", out JsonElement mc) && mc.ValueKind == JsonValueKind.True;
			MatchBackgroundPriority = a.TryGetProperty("matchBackgroundPriority", out JsonElement mbp) && mbp.ValueKind == JsonValueKind.True;
			AllPaletteVariants = !a.TryGetProperty("allPaletteVariants", out JsonElement ap) || ap.ValueKind != JsonValueKind.False;
			CropLeft = a.TryGetProperty("cropLeft", out JsonElement cl) && cl.TryGetInt32(out int clv) ? Math.Max(0, clv) : 0;
			CropTop = a.TryGetProperty("cropTop", out JsonElement ct) && ct.TryGetInt32(out int ctv) ? Math.Max(0, ctv) : 0;
			CropRight = a.TryGetProperty("cropRight", out JsonElement cr2) && cr2.TryGetInt32(out int crv) ? Math.Max(0, crv) : 0;
			CropBottom = a.TryGetProperty("cropBottom", out JsonElement cb) && cb.TryGetInt32(out int cbv) ? Math.Max(0, cbv) : 0;
		}
	}

	internal sealed class HdStudioReplacementCell
	{
		public string Key = "";
		public string Palette = "";
		public string Condition = "";
		public int SheetX;
	}

	internal sealed class HdStudioReplacement
	{
		public string Id = "";
		public string AssetId = "";
		public string AssetName = "";
		public string AssetKind = "";
		public string SourceImage = "";
		public string Sheet = "";
		public int PackScale = 4;
		public int OriginalWidth;
		public int OriginalHeight;
		public int TargetWidth;
		public int TargetHeight;
		public string Approval = "draft";
		public bool AllowSharedReplacement;
		public bool Locked;
		public string CreatedAtUtc = "";
		public string UpdatedAtUtc = "";
		public string LegacyManifest = "";
		public readonly HdStudioAdaptationSettings Settings = new();
		public readonly List<HdStudioReplacementCell> Cells = new();
		public readonly List<string> Warnings = new();
		public readonly List<string> Validation = new();

		public bool Approved => string.Equals(Approval, "approved", StringComparison.OrdinalIgnoreCase);

		public void WriteJson(Utf8JsonWriter json, string projectPath)
		{
			json.WriteStartObject();
			json.WriteString("id", Id);
			json.WriteString("assetId", AssetId);
			json.WriteString("assetName", AssetName);
			json.WriteString("assetKind", AssetKind);
			string source = SourceImage;
			string projectFolder = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
			if(Path.IsPathFullyQualified(source) && source.StartsWith(projectFolder, StringComparison.OrdinalIgnoreCase)) {
				source = Path.GetRelativePath(projectFolder, source).Replace('\\', '/');
			}
			json.WriteString("sourceImage", source);
			json.WriteString("sheet", Sheet);
			json.WriteNumber("packScale", PackScale);
			json.WriteNumber("originalWidth", OriginalWidth);
			json.WriteNumber("originalHeight", OriginalHeight);
			json.WriteNumber("targetWidth", TargetWidth);
			json.WriteNumber("targetHeight", TargetHeight);
			json.WriteString("approval", Approval);
			json.WriteBoolean("allowSharedReplacement", AllowSharedReplacement);
			json.WriteBoolean("locked", Locked);
			json.WriteString("createdAtUtc", CreatedAtUtc);
			json.WriteString("updatedAtUtc", UpdatedAtUtc);
			if(!string.IsNullOrEmpty(LegacyManifest)) {
				json.WriteString("legacyManifest", LegacyManifest);
			}
			Settings.WriteJson(json);
			json.WriteStartArray("warnings");
			foreach(string warning in Warnings) json.WriteStringValue(warning);
			json.WriteEndArray();
			json.WriteStartArray("validation");
			foreach(string item in Validation) json.WriteStringValue(item);
			json.WriteEndArray();
			json.WriteStartArray("cells");
			foreach(HdStudioReplacementCell cell in Cells) {
				json.WriteStartObject();
				json.WriteString("key", cell.Key);
				json.WriteString("palette", cell.Palette);
				json.WriteString("condition", cell.Condition);
				json.WriteNumber("sheetX", cell.SheetX);
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}

		public static HdStudioReplacement? FromJson(JsonElement root)
		{
			string id = root.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
			string assetId = root.TryGetProperty("assetId", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
			if(string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(assetId)) {
				return null;
			}
			HdStudioReplacement replacement = new() {
				Id = id,
				AssetId = assetId,
				AssetName = root.TryGetProperty("assetName", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
				AssetKind = root.TryGetProperty("assetKind", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "",
				SourceImage = root.TryGetProperty("sourceImage", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "",
				Sheet = root.TryGetProperty("sheet", out JsonElement sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() ?? "" : "",
				PackScale = root.TryGetProperty("packScale", out JsonElement ps) && ps.TryGetInt32(out int scale) ? scale : 4,
				OriginalWidth = root.TryGetProperty("originalWidth", out JsonElement ow) && ow.TryGetInt32(out int w) ? w : 0,
				OriginalHeight = root.TryGetProperty("originalHeight", out JsonElement oh) && oh.TryGetInt32(out int h) ? h : 0,
				TargetWidth = root.TryGetProperty("targetWidth", out JsonElement tw) && tw.TryGetInt32(out int tWidth) ? tWidth : 0,
				TargetHeight = root.TryGetProperty("targetHeight", out JsonElement th) && th.TryGetInt32(out int tHeight) ? tHeight : 0,
				Approval = root.TryGetProperty("approval", out JsonElement ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() ?? "draft" : "draft",
				AllowSharedReplacement = !root.TryGetProperty("allowSharedReplacement", out JsonElement asr) || asr.ValueKind == JsonValueKind.True,
				Locked = root.TryGetProperty("locked", out JsonElement lk) && lk.ValueKind == JsonValueKind.True,
				CreatedAtUtc = root.TryGetProperty("createdAtUtc", out JsonElement ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() ?? "" : "",
				UpdatedAtUtc = root.TryGetProperty("updatedAtUtc", out JsonElement ua) && ua.ValueKind == JsonValueKind.String ? ua.GetString() ?? "" : "",
				LegacyManifest = root.TryGetProperty("legacyManifest", out JsonElement lm) && lm.ValueKind == JsonValueKind.String ? lm.GetString() ?? "" : ""
			};
			replacement.Settings.ReadJson(root);
			if(root.TryGetProperty("warnings", out JsonElement warnings) && warnings.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement warning in warnings.EnumerateArray()) replacement.Warnings.Add(warning.GetString() ?? "");
			}
			if(root.TryGetProperty("validation", out JsonElement validation) && validation.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement item in validation.EnumerateArray()) replacement.Validation.Add(item.GetString() ?? "");
			}
			if(root.TryGetProperty("cells", out JsonElement cells) && cells.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement cell in cells.EnumerateArray()) {
					replacement.Cells.Add(new HdStudioReplacementCell() {
						Key = cell.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String ? key.GetString() ?? "" : "",
						Palette = cell.TryGetProperty("palette", out JsonElement pal) && pal.ValueKind == JsonValueKind.String ? pal.GetString() ?? "" : "",
						Condition = cell.TryGetProperty("condition", out JsonElement cond) && cond.ValueKind == JsonValueKind.String ? cond.GetString() ?? "" : "",
						SheetX = cell.TryGetProperty("sheetX", out JsonElement sx) && sx.TryGetInt32(out int x) ? x : 0
					});
				}
			}
			return replacement;
		}
	}
}

