using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Mesen.Config;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Persistent Studio user settings. They live outside packs and projects so a copied or
	/// distributed pack never writes into the machine that authored it. Tests can redirect the
	/// file with RETROARCDO_HDSTUDIO_SETTINGS.
	/// </summary>
	internal sealed class HdStudioSettings
	{
		public string ExportRoot = "";
		public string LastExportFolder = "";
		public bool LastExportMinimal;
		public bool LastExportInstall;
		public bool LastExportBackup = true;
		public string Theme = "dark";
		public double AssetThumbnailSize = 128;
		public bool AssetGridView = true;
		public bool ShowTileGrid = true;
		public bool ShowCheckerboard = true;
		public bool BottomPanelOpen = true;
		public readonly List<string> RecentProjects = new();

		public static string SettingsPath
		{
			get {
				string? overridePath = Environment.GetEnvironmentVariable("RETROARCDO_HDSTUDIO_SETTINGS");
				if(!string.IsNullOrWhiteSpace(overridePath)) {
					return Path.GetFullPath(overridePath);
				}
				string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				if(string.IsNullOrWhiteSpace(appData)) {
					appData = AppContext.BaseDirectory;
				}
				return Path.Combine(appData, "RetroArcDo HD Studio", "studio-settings.json");
			}
		}

		public static HdStudioSettings Load()
		{
			HdStudioSettings settings = new();
			try {
				if(File.Exists(SettingsPath)) {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
					JsonElement root = doc.RootElement;
					settings.ExportRoot = GetString(root, "exportRoot");
					settings.LastExportFolder = GetString(root, "lastExportFolder");
					settings.LastExportMinimal = GetBool(root, "lastExportMinimal");
					settings.LastExportInstall = GetBool(root, "lastExportInstall");
					settings.LastExportBackup = !root.TryGetProperty("lastExportBackup", out JsonElement backup) || backup.ValueKind != JsonValueKind.False;
					settings.Theme = GetString(root, "theme");
					if(root.TryGetProperty("assetThumbnailSize", out JsonElement thumb) && thumb.TryGetDouble(out double thumbSize)) {
						settings.AssetThumbnailSize = Math.Clamp(thumbSize, 96, 220);
					}
					settings.AssetGridView = !root.TryGetProperty("assetGridView", out JsonElement grid) || grid.ValueKind != JsonValueKind.False;
					settings.ShowTileGrid = !root.TryGetProperty("showTileGrid", out JsonElement tileGrid) || tileGrid.ValueKind != JsonValueKind.False;
					settings.ShowCheckerboard = !root.TryGetProperty("showCheckerboard", out JsonElement checker) || checker.ValueKind != JsonValueKind.False;
					settings.BottomPanelOpen = !root.TryGetProperty("bottomPanelOpen", out JsonElement bottom) || bottom.ValueKind != JsonValueKind.False;
					if(root.TryGetProperty("recentProjects", out JsonElement recent) && recent.ValueKind == JsonValueKind.Array) {
						foreach(JsonElement item in recent.EnumerateArray()) {
							if(item.ValueKind == JsonValueKind.String) {
								settings.RecentProjects.Add(item.GetString() ?? "");
							}
						}
					}
				}
			} catch {
				//A corrupt settings file must not stop the Studio; defaults are safe.
			}
			if(string.IsNullOrWhiteSpace(settings.ExportRoot)) {
				settings.ExportRoot = HdStudioExport.GetDefaultExportRoot();
			}
			return settings;
		}

		public void Save()
		{
			try {
				string path = SettingsPath;
				Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
				RecentProjects.RemoveAll(string.IsNullOrWhiteSpace);
				while(RecentProjects.Count > 12) {
					RecentProjects.RemoveAt(RecentProjects.Count - 1);
				}
				string temp = path + ".tmp";
				using(FileStream stream = File.Create(temp)) {
					using(Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true })) {
						json.WriteStartObject();
						json.WriteString("schemaVersion", "1.0.0");
						json.WriteString("exportRoot", ExportRoot);
						json.WriteString("lastExportFolder", LastExportFolder);
						json.WriteBoolean("lastExportMinimal", LastExportMinimal);
						json.WriteBoolean("lastExportInstall", LastExportInstall);
						json.WriteBoolean("lastExportBackup", LastExportBackup);
						json.WriteString("theme", Theme);
						json.WriteNumber("assetThumbnailSize", AssetThumbnailSize);
						json.WriteBoolean("assetGridView", AssetGridView);
						json.WriteBoolean("showTileGrid", ShowTileGrid);
						json.WriteBoolean("showCheckerboard", ShowCheckerboard);
						json.WriteBoolean("bottomPanelOpen", BottomPanelOpen);
						json.WriteStartArray("recentProjects");
						foreach(string project in RecentProjects) {
							json.WriteStringValue(project);
						}
						json.WriteEndArray();
						json.WriteEndObject();
					}
				}
				File.Move(temp, path, true);
			} catch {
				//Settings persistence is a convenience; a read-only profile must not break export.
			}
		}

		public void RememberProject(string projectPath)
		{
			if(string.IsNullOrWhiteSpace(projectPath)) {
				return;
			}
			RecentProjects.RemoveAll(p => string.Equals(p, projectPath, StringComparison.OrdinalIgnoreCase));
			RecentProjects.Insert(0, projectPath);
		}

		private static string GetString(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
		}

		private static bool GetBool(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}
	}

	internal sealed class HdStudioExportOptions
	{
		/// <summary>Exact destination folder. When empty, RootFolder + PackName is used.</summary>
		public string DestinationFolder = "";
		/// <summary>Export root. The default is Documents/RetroArcDo HD Studio/Exports.</summary>
		public string RootFolder = "";
		public string PackName = "";
		public bool Minimal;
		public bool InstallIntoMesen;
		public bool OverwriteExisting;
		public bool KeepBackup = true;
		public bool DryRun;
	}

	internal sealed class HdStudioExportResult
	{
		public bool Success;
		public bool DestinationExists;
		public string FinalFolder = "";
		public string InstalledFolder = "";
		public string HiresPath = "";
		public string ManifestPath = "";
		public int Replacements;
		public int TileEntries;
		public int Images;
		public long Bytes;
		public int Files;
		public readonly List<string> Errors = new();
		public readonly List<string> Warnings = new();
		public readonly List<string> Log = new();
	}

	/// <summary>
	/// Safe HD pack export.
	///
	/// A build first goes to a staging directory next to the destination, is validated there
	/// (standard hires.txt, ROM hash, scale, every referenced PNG present, no ROM files), and only
	/// then replaces the destination. An existing destination is moved to a timestamped backup
	/// and restored if the final rename fails. A failed build never destroys a working pack.
	/// </summary>
	internal static class HdStudioExport
	{
		public const string ManifestName = "retroarcdo-export.json";

		public static string GetDefaultExportRoot()
		{
			string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			if(string.IsNullOrWhiteSpace(documents)) {
				string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				documents = string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData;
			}
			return Path.Combine(documents, "RetroArcDo HD Studio", "Exports");
		}

		public static string GetMesenPackFolder()
		{
			try {
				return ConfigManager.HdPackFolder;
			} catch {
				return "";
			}
		}

		public static string SanitizePackName(string value)
		{
			string name = (value ?? "").Trim();
			if(name.Length == 0) {
				name = "HD Studio pack";
			}
			HashSet<char> invalid = new(Path.GetInvalidFileNameChars());
			StringBuilder builder = new(name.Length);
			foreach(char c in name) {
				builder.Append(invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c);
			}
			name = builder.ToString().Trim().TrimEnd('.');
			if(name.Length == 0) {
				name = "HD Studio pack";
			}
			return name.Length > 120 ? name.Substring(0, 120).TrimEnd() : name;
		}

		public static string ProposeDestination(HdStudioProject project, string root, string? packName = null)
		{
			string name = SanitizePackName(string.IsNullOrWhiteSpace(packName) ? project.ProjectName : packName!);
			if(string.IsNullOrWhiteSpace(root)) {
				root = GetDefaultExportRoot();
			}
			return Path.GetFullPath(Path.Combine(root, name));
		}

		public static HdStudioExportResult Export(HdStudioProject project, HdStudioExportOptions options, Action<string>? log = null)
		{
			HdStudioExportResult result = new();
			void Report(string message)
			{
				result.Log.Add(message);
				log?.Invoke(message);
			}

			string destination;
			if(!string.IsNullOrWhiteSpace(options.DestinationFolder)) {
				destination = Path.GetFullPath(options.DestinationFolder);
			} else {
				string root = string.IsNullOrWhiteSpace(options.RootFolder) ? GetDefaultExportRoot() : options.RootFolder;
				destination = ProposeDestination(project, root, options.PackName);
			}
			result.FinalFolder = destination;
			result.HiresPath = Path.Combine(destination, "hires.txt");

			if(destination.TrimEnd('\\', '/').Equals(Path.GetFullPath(project.PackFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) {
				result.Errors.Add("The export destination is the project's own build folder. Choose a different folder; authoring files and distributable packs are kept separate.");
				return Finish(result, Report);
			}
			if(IsInside(destination, project.PackFolder) || IsInside(project.PackFolder, destination)) {
				result.Errors.Add("The export destination overlaps the project pack folder. Choose a separate folder.");
				return Finish(result, Report);
			}
			if(string.IsNullOrWhiteSpace(project.RomSha1)) {
				result.Errors.Add("The project has no ROM identity; export is unsafe.");
				return Finish(result, Report);
			}

			result.DestinationExists = Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any();
			if(result.DestinationExists && !options.OverwriteExisting) {
				result.Errors.Add("The destination already exists: " + destination + ". Enable overwrite or choose another folder.");
				return Finish(result, Report);
			}
			if(result.DestinationExists && LooksLikeForeignPack(destination)) {
				result.Warnings.Add("The destination is an existing HD pack that was not created by this project. It will be backed up before replacement.");
			}

			string parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("The export destination has no parent folder.");
			try {
				Directory.CreateDirectory(parent);
			} catch(Exception ex) {
				result.Errors.Add("The destination folder cannot be created: " + ex.Message);
				return Finish(result, Report);
			}

			string staging = Path.Combine(parent, "." + Path.GetFileName(destination) + ".hdstudio-staging-" + Guid.NewGuid().ToString("N").Substring(0, 8));
			try {
				Directory.CreateDirectory(staging);
				Report($"Export: building {(options.Minimal ? "a minimal" : "a full")} pack in staging: {staging}");
				HdStudioCompileResult compile = HdStudioCompiler.Compile(project, m => Report(m), options.Minimal, staging, options.DryRun);
				result.Replacements = compile.CompiledReplacements;
				result.TileEntries = compile.TileEntries;
				result.Images = compile.Images;
				result.Errors.AddRange(compile.Errors);
				result.Warnings.AddRange(compile.Warnings);
				if(!compile.Success) {
					return Finish(result, Report);
				}

				if(options.DryRun) {
					result.Success = result.Errors.Count == 0;
					Report("Export dry run completed; the destination was not modified.");
					return Finish(result, Report);
				}

				if(!options.Minimal) {
					CopyReferencedImages(project.PackFolder, staging, result);
				}
				ValidatePack(project, staging, result);
				WriteManifest(project, staging, result, options, Report);
				Measure(staging, result);
				if(result.Errors.Count > 0) {
					return Finish(result, Report);
				}

				if(!HasFreeSpace(parent, result.Bytes, out string freeSpaceError)) {
					result.Errors.Add(freeSpaceError);
					return Finish(result, Report);
				}

				string? backup = null;
				if(Directory.Exists(destination)) {
					backup = destination.TrimEnd('\\', '/') + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
					int suffix = 1;
					while(Directory.Exists(backup)) {
						backup = destination.TrimEnd('\\', '/') + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + suffix++;
					}
					Directory.Move(destination, backup);
					Report("Export: previous destination moved to " + backup);
				}
				try {
					Directory.Move(staging, destination);
				} catch {
					if(backup != null && Directory.Exists(backup) && !Directory.Exists(destination)) {
						Directory.Move(backup, destination);
					}
					throw;
				}
				if(backup != null) {
					if(options.KeepBackup) {
						result.Warnings.Add("Previous destination kept as a backup: " + backup);
					} else {
						try {
							Directory.Delete(backup, true);
						} catch(Exception ex) {
							result.Warnings.Add("The old destination backup could not be removed: " + ex.Message);
						}
					}
				}

				result.ManifestPath = Path.Combine(destination, ManifestName);
				if(options.InstallIntoMesen) {
					Install(project, destination, options, result, Report);
				}

				result.Success = result.Errors.Count == 0;
				if(result.Success) {
					Report($"Export completed: {result.Files} files, {result.Bytes:N0} bytes, {result.Images} images, {result.TileEntries} tile entries, {result.Replacements} replacements.");
					Report("Exported pack: " + destination);
					if(!string.IsNullOrWhiteSpace(result.InstalledFolder)) {
						Report("Installed for Mesen: " + result.InstalledFolder);
					}
				}
				return Finish(result, Report);
			} catch(Exception ex) {
				result.Errors.Add("Export failed: " + ex.Message);
				return Finish(result, Report);
			} finally {
				try {
					if(Directory.Exists(staging)) {
						Directory.Delete(staging, true);
					}
				} catch {
					//The staging folder is clearly named; it can be removed manually.
				}
			}
		}

		private static void CopyReferencedImages(string sourcePack, string staging, HdStudioExportResult result)
		{
			string hires = Path.Combine(staging, "hires.txt");
			if(!File.Exists(hires)) {
				result.Errors.Add("The compiler did not write hires.txt.");
				return;
			}
			foreach(string line in File.ReadAllLines(hires)) {
				if(!line.StartsWith("<img>", StringComparison.Ordinal)) {
					continue;
				}
				string name = line.Substring(5).Trim();
				if(name.Length == 0) {
					continue;
				}
				string source = Path.Combine(sourcePack, name);
				string target = Path.Combine(staging, name);
				if(File.Exists(target)) {
					continue;
				}
				if(!File.Exists(source)) {
					result.Errors.Add("The pack references a missing image: " + name);
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(target) ?? staging);
				File.Copy(source, target, false);
			}
		}

		private static void ValidatePack(HdStudioProject project, string folder, HdStudioExportResult result)
		{
			string hires = Path.Combine(folder, "hires.txt");
			if(!File.Exists(hires)) {
				result.Errors.Add("hires.txt is missing from the exported pack.");
				return;
			}
			List<string> images = new();
			string supportedRom = "";
			int scale = 0;
			int tileCount = 0;
			foreach(string line in File.ReadAllLines(hires)) {
				if(line.StartsWith("<img>", StringComparison.Ordinal)) {
					images.Add(line.Substring(5).Trim());
				} else if(line.StartsWith("<scale>", StringComparison.Ordinal)) {
					int.TryParse(line.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out scale);
				} else if(line.StartsWith("<supportedRom>", StringComparison.Ordinal)) {
					supportedRom = line.Substring(14).Trim();
				} else if(line.Contains("<tile>", StringComparison.Ordinal)) {
					tileCount++;
					int index = line.IndexOf("<tile>", StringComparison.Ordinal) + 6;
					int comma = line.IndexOf(',', index);
					if(comma > index && int.TryParse(line.Substring(index, comma - index), out int imageIndex) && (imageIndex < 0 || imageIndex >= images.Count)) {
						result.Errors.Add("A tile entry references image index " + imageIndex + " but the pack has " + images.Count + " images.");
						break;
					}
				}
			}
			if(scale <= 0 || scale > 10) {
				result.Errors.Add("The exported pack has an invalid scale.");
			} else if(scale != Math.Clamp(project.OutputScale, 1, 10)) {
				result.Errors.Add($"The exported pack scale {scale}x does not match the project scale {project.OutputScale}x.");
			}
			if(string.IsNullOrWhiteSpace(supportedRom)) {
				result.Errors.Add("The exported pack has no <supportedRom> identity.");
			} else if(!string.Equals(supportedRom, project.RomSha1, StringComparison.OrdinalIgnoreCase)) {
				result.Errors.Add("The exported pack is tied to a different ROM hash.");
			}
			foreach(string image in images) {
				if(!File.Exists(Path.Combine(folder, image))) {
					result.Errors.Add("The exported pack is missing image " + image + ".");
				}
			}
			foreach(string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) {
				string extension = Path.GetExtension(file);
				if(extension.Equals(".nes", StringComparison.OrdinalIgnoreCase) ||
					extension.Equals(".fds", StringComparison.OrdinalIgnoreCase) ||
					extension.Equals(".unf", StringComparison.OrdinalIgnoreCase) ||
					extension.Equals(".unif", StringComparison.OrdinalIgnoreCase)) {
					result.Errors.Add("A ROM file must never be exported with a pack: " + Path.GetFileName(file));
				}
			}
			if(tileCount == 0) {
				result.Warnings.Add("The exported pack contains no tile entries. It only carries the base pack images.");
			}
			result.Images = images.Count;
			result.TileEntries = tileCount;
		}

		private static void WriteManifest(HdStudioProject project, string folder, HdStudioExportResult result, HdStudioExportOptions options, Action<string> report)
		{
			try {
				string path = Path.Combine(folder, ManifestName);
				using(FileStream stream = File.Create(path))
				using(Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true })) {
					json.WriteStartObject();
					json.WriteString("schemaVersion", "1.0.0");
					json.WriteString("generatedBy", "RetroArcDo HD Studio");
					json.WriteString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
					json.WriteString("projectName", project.ProjectName);
					json.WriteString("romSha1", project.RomSha1);
					json.WriteNumber("scale", project.OutputScale);
					json.WriteString("packKind", options.Minimal ? "minimal" : "full");
					json.WriteNumber("replacements", result.Replacements);
					json.WriteNumber("tileEntries", result.TileEntries);
					json.WriteNumber("images", result.Images);
					json.WriteStartArray("warnings");
					foreach(string warning in result.Warnings) {
						json.WriteStringValue(warning);
					}
					json.WriteEndArray();
					json.WriteEndObject();
				}
				result.ManifestPath = path;
				report("Export: manifest written to " + path);
			} catch(Exception ex) {
				result.Warnings.Add("The export manifest could not be written: " + ex.Message);
			}
		}

		private static void Install(HdStudioProject project, string destination, HdStudioExportOptions options, HdStudioExportResult result, Action<string> report)
		{
			string mesenFolder = GetMesenPackFolder();
			if(string.IsNullOrWhiteSpace(mesenFolder)) {
				result.Errors.Add("Mesen's HD pack folder is not configured; installation was skipped.");
				return;
			}
			string name = SanitizePackName(Path.GetFileNameWithoutExtension(project.RomPath));
			if(string.IsNullOrWhiteSpace(name)) {
				result.Errors.Add("The ROM has no usable name for the Mesen pack folder.");
				return;
			}
			string target = Path.Combine(mesenFolder, name);
			if(Path.GetFullPath(target).TrimEnd('\\', '/').Equals(Path.GetFullPath(destination).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) {
				result.InstalledFolder = target;
				return;
			}
			string? parent = Path.GetDirectoryName(target);
			if(string.IsNullOrWhiteSpace(parent)) {
				result.Errors.Add("Mesen's HD pack folder has no parent.");
				return;
			}
			Directory.CreateDirectory(parent);
			string staging = Path.Combine(parent, "." + name + ".hdstudio-install-" + Guid.NewGuid().ToString("N").Substring(0, 8));
			try {
				CopyDirectory(destination, staging);
				string? backup = null;
				if(Directory.Exists(target)) {
					backup = target + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
					int suffix = 1;
					while(Directory.Exists(backup)) {
						backup = target + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + suffix++;
					}
					Directory.Move(target, backup);
				}
				try {
					Directory.Move(staging, target);
				} catch {
					if(backup != null && Directory.Exists(backup) && !Directory.Exists(target)) {
						Directory.Move(backup, target);
					}
					throw;
				}
				if(backup != null && !options.KeepBackup) {
					try { Directory.Delete(backup, true); } catch { }
				}
				result.InstalledFolder = target;
				report("Export: installed the pack into Mesen's HD pack folder.");
			} catch(Exception ex) {
				result.Errors.Add("The pack was exported but could not be installed into Mesen: " + ex.Message);
			} finally {
				try {
					if(Directory.Exists(staging)) {
						Directory.Delete(staging, true);
					}
				} catch { }
			}
		}

		private static void CopyDirectory(string source, string target)
		{
			Directory.CreateDirectory(target);
			foreach(string folder in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) {
				Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, folder)));
			}
			foreach(string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
				File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), false);
			}
		}

		private static void Measure(string folder, HdStudioExportResult result)
		{
			long bytes = 0;
			int files = 0;
			foreach(string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) {
				files++;
				try {
					bytes += new FileInfo(file).Length;
				} catch { }
			}
			result.Bytes = bytes;
			result.Files = files;
		}

		private static bool HasFreeSpace(string folder, long bytes, out string error)
		{
			error = "";
			try {
				string? root = Path.GetPathRoot(Path.GetFullPath(folder));
				if(string.IsNullOrWhiteSpace(root)) {
					return true;
				}
				DriveInfo drive = new(root);
				long required = Math.Max(bytes, 0) * 2 + 16 * 1024 * 1024;
				if(drive.AvailableFreeSpace < required) {
					error = $"Not enough free space on {root}: {drive.AvailableFreeSpace:N0} bytes available, about {required:N0} required.";
					return false;
				}
			} catch {
				//DriveInfo is unavailable on some network shares; the write itself still validates.
			}
			return true;
		}

		private static bool LooksLikeForeignPack(string folder)
		{
			string manifest = Path.Combine(folder, ManifestName);
			if(File.Exists(manifest)) {
				return false;
			}
			string hires = Path.Combine(folder, "hires.txt");
			if(!File.Exists(hires)) {
				return false;
			}
			try {
				foreach(string line in File.ReadLines(hires)) {
					if(line.StartsWith("<img>Chr_", StringComparison.OrdinalIgnoreCase)) {
						return true;
					}
				}
			} catch { }
			return false;
		}

		private static bool IsInside(string path, string parent)
		{
			string fullPath = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
			string fullParent = Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
			return fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
		}

		private static HdStudioExportResult Finish(HdStudioExportResult result, Action<string> report)
		{
			foreach(string error in result.Errors) {
				report("Export error: " + error);
			}
			foreach(string warning in result.Warnings) {
				report("Export warning: " + warning);
			}
			if(!result.Success) {
				report("Export failed: " + result.Errors.Count + " error(s). The previous pack was not modified.");
			}
			return result;
		}
	}
}
