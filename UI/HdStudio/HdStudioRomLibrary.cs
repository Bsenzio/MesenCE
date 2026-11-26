using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Native ROM catalog used by the Library and Batch workspaces.
	///
	/// Indexing reads only the 16-byte iNES/NES 2.0 header of each file. It does not
	/// modify, copy or hash every ROM during browsing, so a multi-thousand-entry folder
	/// stays responsive. SHA-256 values are taken from the frozen inventory when one is
	/// available and are otherwise computed only when a ROM is selected for a batch.
	/// </summary>
	internal static class HdStudioRomLibrary
	{
		internal sealed class RomEntry
		{
			public string Name = "";
			public string Path = "";
			public string Stem = "";
			public string Sha256 = "";
			public int Mapper = -1;
			public int Submapper;
			public bool ChrRam;
			public long Size;
			public bool Nes20;
			public string ParserNote = "";
		}

		internal sealed class Catalog
		{
			public string Root = "";
			public readonly List<RomEntry> Entries = new();
			public string Error = "";
		}

		public static Catalog Load(string root, string? inventoryPath = null, Action<string>? log = null)
		{
			Catalog catalog = new() { Root = Path.GetFullPath(root) };
			if(!Directory.Exists(catalog.Root)) {
				catalog.Error = "The folder does not exist: " + catalog.Root;
				return catalog;
			}

			List<string> files;
			try {
				files = Directory.EnumerateFiles(catalog.Root, "*.nes", SearchOption.AllDirectories)
					.OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
					.ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
					.ToList();
			} catch(Exception ex) {
				catalog.Error = "The folder could not be read: " + ex.Message;
				return catalog;
			}

			Dictionary<string, (int Mapper, int Submapper, bool ChrRam, string Sha)> inventory = LoadInventory(inventoryPath);
			foreach(string file in files) {
				RomEntry? entry = ReadHeader(file);
				if(entry == null) {
					continue;
				}
				if(inventory.TryGetValue(Normalize(entry.Path), out var info)) {
					entry.Mapper = info.Mapper;
					entry.Submapper = info.Submapper;
					entry.ChrRam = info.ChrRam;
					entry.Sha256 = info.Sha;
				}
				catalog.Entries.Add(entry);
			}
			log?.Invoke($"Library: {catalog.Entries.Count} ROM(s) indexed in {catalog.Root}.");
			return catalog;
		}

		private static Dictionary<string, (int, int, bool, string)> LoadInventory(string? path)
		{
			Dictionary<string, (int, int, bool, string)> result = new(StringComparer.OrdinalIgnoreCase);
			if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
				return result;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				if(doc.RootElement.TryGetProperty("roms", out JsonElement roms) && roms.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement rom in roms.EnumerateArray()) {
						string? file = rom.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
						if(string.IsNullOrWhiteSpace(file)) {
							continue;
						}
						int mapper = rom.TryGetProperty("mapper", out JsonElement m) && m.TryGetInt32(out int mv) ? mv : -1;
						int submapper = rom.TryGetProperty("submapper", out JsonElement sm) && sm.TryGetInt32(out int sv) ? sv : 0;
						bool chrRam = rom.TryGetProperty("chrType", out JsonElement ct) && ct.ValueKind == JsonValueKind.String &&
							(ct.GetString() ?? "").Contains("RAM", StringComparison.OrdinalIgnoreCase);
						string sha = rom.TryGetProperty("sha256", out JsonElement sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() ?? "" : "";
						result[Normalize(file)] = (mapper, submapper, chrRam, sha);
					}
				}
			} catch {
				//A missing or malformed inventory only removes hash enrichment; browsing still works.
			}
			return result;
		}

		private static string Normalize(string path)
		{
			try {
				return Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant();
			} catch {
				return path.ToUpperInvariant();
			}
		}

		private static RomEntry? ReadHeader(string file)
		{
			try {
				using FileStream stream = File.OpenRead(file);
				if(stream.Length < 16) {
					return null;
				}
				byte[] header = new byte[16];
				if(stream.Read(header, 0, 16) != 16 || header[0] != 'N' || header[1] != 'E' || header[2] != 'S' || header[3] != 0x1A) {
					return null;
				}
				bool nes20 = (header[7] & 0x0C) == 0x08;
				int mapper = (header[6] >> 4) | (header[7] & 0xF0);
				int submapper = 0;
				if(nes20) {
					mapper |= (header[8] & 0x0F) << 8;
					submapper = (header[8] >> 4) & 0x0F;
				}
				bool chrRam = (header[5] & 0x0F) == 0;
				return new RomEntry() {
					Name = Path.GetFileName(file),
					Path = Path.GetFullPath(file),
					Stem = Path.GetFileNameWithoutExtension(file),
					Mapper = mapper,
					Submapper = submapper,
					ChrRam = chrRam,
					Size = stream.Length,
					Nes20 = nes20
				};
			} catch {
				return null;
			}
		}

		public static string Sha256(string path)
		{
			using FileStream stream = File.OpenRead(path);
			using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
			return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
		}

		public static string FormatChrType(RomEntry entry) => entry.ChrRam ? "CHR-RAM" : "CHR-ROM";
		public static string FormatMapper(RomEntry entry)
		{
			if(entry.Mapper < 0) {
				return "Unknown mapper";
			}
			return "Mapper " + entry.Mapper.ToString(CultureInfo.InvariantCulture) + (entry.Submapper > 0 ? "." + entry.Submapper : "");
		}

		/// <summary>Returns the HD Pack folder Mesen uses for this ROM, if it exists.</summary>
		public static string FindPackFolder(RomEntry entry)
		{
			string folder = Path.Combine(Mesen.Config.ConfigManager.HdPackFolder, entry.Stem);
			return Directory.Exists(folder) ? folder : "";
		}

		public static string FindProject(this Catalog catalog, RomEntry entry)
		{
			string pack = FindPackFolder(entry);
			if(string.IsNullOrWhiteSpace(pack)) {
				return "";
			}
			string project = Path.Combine(pack, HdStudioReport.StudioFolderName, "project.hdstudio.json");
			return File.Exists(project) ? project : "";
		}

		public static string FindThumbnail(this Catalog catalog, RomEntry entry)
		{
			string pack = FindPackFolder(entry);
			if(string.IsNullOrWhiteSpace(pack)) {
				return "";
			}
			string live = Path.Combine(pack, HdStudioReport.StudioFolderName, "live");
			if(Directory.Exists(live)) {
				string? shot = Directory.EnumerateFiles(live, "pause-*.png").OrderByDescending(f => f).FirstOrDefault();
				if(shot != null) {
					return shot;
				}
			}
			string original = Path.Combine(pack, HdStudioReport.StudioFolderName, "original-preview.png");
			return File.Exists(original) ? original : "";
		}
	}
}
