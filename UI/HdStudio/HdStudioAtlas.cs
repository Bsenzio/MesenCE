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
	/// HD Studio: renders the named object atlas from the raw mapper-aware capture files.
	///
	/// The Lua capture writes one CSV per object with the physical CHR ROM/RAM address, the
	/// actual 16 pattern bytes the PPU fetched, OAM metadata and the real in-game screenshot.
	/// This renderer never guesses a CHR bank: it draws the recorded bytes. It also audits
	/// every CHR-ROM tile against the ROM to distinguish verified and unverified compositions.
	/// </summary>
	internal static class HdStudioAtlas
	{
		private class AtlasSprite
		{
			public int ObjectId;
			public int OamIndex;
			public int Part;
			public int PpuAddr;
			public int ChrAddr;
			public string ChrType = "";
			public byte[]? TileData;
			public int Pattern;
			public uint Palette;
			public int X;
			public int Y;
			public int Attr;
			public int Frame;
			public int MapperId;
			public bool LargeSprites;
			public bool Legacy;
		}

		private class CatalogEntry
		{
			public string Label = "";
			public string Name = "";
		}

		private class AtlasEntry
		{
			public int Id;
			public string Label = "";
			public string Name = "";
			public string Png = "";
			public int Width;
			public int Height;
			public int Sprites;
			public string Status = "";
			public string CapturePng = "";
			public string Source = "";
			public List<AtlasTile> Tiles = new();
		}

		private class AtlasTile
		{
			public int OamIndex;
			public int Part;
			public int PpuAddr;
			public int ChrAddr;
			public string ChrType = "";
			public int Pattern;
			public uint Palette;
			public bool FlipH;
			public bool FlipV;
			public bool Priority;
			public int X;
			public int Y;
		}

		public static int Render(string romPath, string outFolder, string? catalogPath, uint[] nesPalette, Action<string> log)
		{
			string studioFolder = Path.Combine(outFolder, HdStudioReport.StudioFolderName);
			string spritesFolder = Path.Combine(studioFolder, "sprites");
			string atlasFolder = Path.Combine(studioFolder, "atlas");
			if(!Directory.Exists(spritesFolder)) {
				log("Atlas: no capture folder at " + spritesFolder + ". Run --spriteid first.");
				return 1;
			}

			Directory.CreateDirectory(atlasFolder);
			NesRomData rom = NesRomData.Load(romPath);
			Dictionary<int, CatalogEntry> catalog = ReadCatalog(catalogPath, Path.Combine(atlasFolder, "atlas.json"), log);

			List<AtlasSprite> allSprites = new();
			Dictionary<int, List<AtlasSprite>> byObject = new();
			foreach(string csv in Directory.GetFiles(spritesFolder, "*.csv").OrderBy(p => p)) {
				List<AtlasSprite> rows = ReadCsv(csv);
				if(rows.Count == 0) {
					continue;
				}
				int id = rows[0].ObjectId;
				allSprites.AddRange(rows);
				byObject[id] = rows;
			}

			if(allSprites.Count == 0) {
				log("Atlas: the capture folder contains no sprites.");
				return 2;
			}

			List<AtlasEntry> entries = new();
			int verifiedObjects = 0;
			int unverifiedObjects = 0;
			int chrRamObjects = 0;
			int noSpriteObjects = 0;
			int totalSprites = 0;
			int mismatchedTiles = 0;
			int missingPatternBytes = 0;

			foreach(int objectId in byObject.Keys.OrderBy(id => id)) {
				List<AtlasSprite> sprites = byObject[objectId];
				totalSprites += sprites.Count;

				bool legacy = sprites.Any(s => s.Legacy);
				bool chrRam = sprites.Any(s => s.ChrType.Contains("ChrRam", StringComparison.OrdinalIgnoreCase));
				bool verified = !legacy;

				foreach(AtlasSprite sprite in sprites) {
					byte[]? data = sprite.TileData;
					if(data == null || data.Length != 16) {
						verified = false;
						missingPatternBytes++;
						continue;
					}
					if(sprite.ChrAddr < 0) {
						verified = false;
						continue;
					}
					if(!chrRam && rom.HasChrRom) {
						int index = sprite.ChrAddr / 16;
						if(sprite.ChrAddr % 16 != 0 || index < 0 || index >= rom.ChrTileCount) {
							verified = false;
							mismatchedTiles++;
						} else {
							byte[] expected = rom.GetChrTile(index);
							if(!expected.AsSpan().SequenceEqual(data)) {
								//The mapper returned a different physical tile than the ROM at this offset.
								verified = false;
								mismatchedTiles++;
							}
						}
					} else if(!chrRam) {
						verified = false;
					}
				}

				if(sprites.Count == 0) {
					noSpriteObjects++;
					continue;
				}

				string status = !verified ? "unverified" : (chrRam ? "verified-chr-ram" : "verified");
				if(verified) {
					if(chrRam) {
						chrRamObjects++;
					} else {
						verifiedObjects++;
					}
				} else {
					unverifiedObjects++;
				}

				string png = $"{objectId:000}_{SafeName(GetName(catalog, objectId))}.png";
				(int Width, int Height) size = DrawObject(Path.Combine(atlasFolder, png), sprites, rom, nesPalette);
				if(size.Width == 0) {
					status = "unverified";
					continue;
				}

				CatalogEntry info = catalog.TryGetValue(objectId, out CatalogEntry? found) ? found : new CatalogEntry();
				AtlasEntry entry = new() {
					Id = objectId,
					Label = info.Label,
					Name = GetName(catalog, objectId),
					Png = png,
					Width = size.Width,
					Height = size.Height,
					Sprites = sprites.Count,
					Status = status,
					CapturePng = File.Exists(Path.Combine(spritesFolder, $"{objectId:000}.csv.png")) ? $"{objectId:000}.csv.png" : "",
					Source = legacy ? "legacy-game-bank-table" : "mapper-resolved",
					Tiles = sprites.Select(s => new AtlasTile() {
						OamIndex = s.OamIndex,
						Part = s.Part,
						PpuAddr = s.PpuAddr,
						ChrAddr = s.ChrAddr,
						ChrType = s.ChrType,
						Pattern = s.Pattern,
						Palette = s.Palette,
						FlipH = (s.Attr & 0x40) != 0,
						FlipV = (s.Attr & 0x80) != 0,
						Priority = (s.Attr & 0x20) != 0,
						X = s.X,
						Y = s.Y
					}).ToList()
				};
				entries.Add(entry);
			}

			List<(int Pattern, uint Palette)> allExact = allSprites
				.Where(s => s.Pattern >= 0 && s.TileData != null)
				.Select(s => (s.Pattern, s.Palette))
				.Distinct()
				.OrderBy(e => e.Pattern).ThenBy(e => e.Palette)
				.ToList();
			using(StreamWriter writer = new(Path.Combine(atlasFolder, "atlas_exact.txt"), false, new UTF8Encoding(false))) {
				foreach((int pattern, uint palette) in allExact) {
					writer.WriteLine($"{pattern:X} {palette:X8}");
				}
			}

			using(FileStream stream = File.Create(Path.Combine(atlasFolder, "atlas.json"))) {
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
				json.WriteStartObject();
				json.WriteString("generatedBy", "Mesen HD Studio mapper-aware atlas renderer");
				json.WriteString("romSha1", rom.Sha1);
				json.WriteNumber("mapper", rom.Mapper);
				json.WriteStartArray("objects");
				foreach(AtlasEntry entry in entries) {
					json.WriteStartObject();
					json.WriteNumber("id", entry.Id);
					json.WriteString("label", entry.Label);
					json.WriteString("name", entry.Name);
					json.WriteString("png", entry.Png);
					json.WriteNumber("width", entry.Width);
					json.WriteNumber("height", entry.Height);
					json.WriteNumber("sprites", entry.Sprites);
					json.WriteString("status", entry.Status);
					json.WriteString("capturePng", entry.CapturePng);
					json.WriteString("source", entry.Source);
					json.WriteStartArray("tiles");
					foreach(AtlasTile tile in entry.Tiles) {
						json.WriteStartObject();
						json.WriteNumber("oamIndex", tile.OamIndex);
						json.WriteNumber("part", tile.Part);
						json.WriteNumber("ppuAddr", tile.PpuAddr);
						json.WriteNumber("chrAddr", tile.ChrAddr);
						json.WriteString("chrType", tile.ChrType);
						json.WriteNumber("pattern", tile.Pattern);
						json.WriteString("palette", tile.Palette.ToString("X8", CultureInfo.InvariantCulture));
						json.WriteBoolean("flipH", tile.FlipH);
						json.WriteBoolean("flipV", tile.FlipV);
						json.WriteBoolean("priority", tile.Priority);
						json.WriteNumber("x", tile.X);
						json.WriteNumber("y", tile.Y);
						json.WriteEndObject();
					}
					json.WriteEndArray();
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteStartObject("audit");
				json.WriteNumber("objectsWithSprites", byObject.Count);
				json.WriteNumber("verifiedObjects", verifiedObjects);
				json.WriteNumber("verifiedChrRamObjects", chrRamObjects);
				json.WriteNumber("unverifiedObjects", unverifiedObjects);
				json.WriteNumber("noSpriteObjects", noSpriteObjects);
				json.WriteNumber("sprites", totalSprites);
				json.WriteNumber("mismatchedTiles", mismatchedTiles);
				json.WriteNumber("missingPatternBytes", missingPatternBytes);
				json.WriteEndObject();
				json.WriteEndObject();
			}

			log($"Atlas: {byObject.Count} objects, {totalSprites} sprites; verified {verifiedObjects}, CHR-RAM {chrRamObjects}, unverified {unverifiedObjects}, mismatched tiles {mismatchedTiles} -> {atlasFolder}");
			return mismatchedTiles == 0 ? 0 : 3;
		}

		private static Dictionary<int, CatalogEntry> ReadCatalog(string? catalogPath, string existingAtlasPath, Action<string> log)
		{
			Dictionary<int, CatalogEntry> catalog = new();
			string? path = catalogPath;
			if(string.IsNullOrEmpty(path) && File.Exists(existingAtlasPath)) {
				//Preserve the SMB3 names from a previous atlas build instead of asking for a profile.
				path = existingAtlasPath;
			}
			if(string.IsNullOrEmpty(path) || !File.Exists(path)) {
				return catalog;
			}

			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				JsonElement root = doc.RootElement;
				JsonElement objects;
				if(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("objects", out JsonElement objArray)) {
					objects = objArray;
				} else {
					objects = root;
				}
				if(objects.ValueKind != JsonValueKind.Array) {
					return catalog;
				}
				foreach(JsonElement obj in objects.EnumerateArray()) {
					if(!obj.TryGetProperty("id", out JsonElement idElement)) {
						continue;
					}
					int id = idElement.GetInt32();
					//Several SMB3 contexts reuse an object id; keep the first catalog entry so a
					//regenerated atlas does not inherit the last context's name.
					if(!catalog.ContainsKey(id)) {
						catalog[id] = new CatalogEntry() {
							Label = obj.TryGetProperty("label", out JsonElement label) ? label.GetString() ?? "" : "",
							Name = obj.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : ""
						};
					}
				}
			} catch(Exception ex) {
				log("Atlas: could not read the catalog: " + ex.Message);
			}
			return catalog;
		}

		private static string GetName(Dictionary<int, CatalogEntry> catalog, int id)
		{
			if(catalog.TryGetValue(id, out CatalogEntry? entry)) {
				if(!string.IsNullOrWhiteSpace(entry.Name)) {
					return entry.Name;
				}
				if(!string.IsNullOrWhiteSpace(entry.Label)) {
					return entry.Label;
				}
			}
			return "Object " + id;
		}

		private static List<AtlasSprite> ReadCsv(string path)
		{
			List<AtlasSprite> result = new();
			string[] lines = File.ReadAllLines(path);
			if(lines.Length == 0) {
				return result;
			}

			string header = lines[0];
			bool mapperAware = header.Contains("chrAddr", StringComparison.OrdinalIgnoreCase);
			foreach(string line in lines.Skip(1)) {
				if(string.IsNullOrWhiteSpace(line)) {
					continue;
				}
				string[] p = line.Split(',');
				if(!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int objectId)) {
					continue;
				}

				AtlasSprite sprite = new() { ObjectId = objectId };
				if(mapperAware && p.Length >= 16) {
					sprite.OamIndex = ParseInt(p[1]);
					sprite.Part = ParseInt(p[2]);
					sprite.PpuAddr = ParseHex(p[3]);
					sprite.ChrAddr = ParseHex(p[4]);
					sprite.ChrType = p[5];
					sprite.TileData = p[6].Length == 32 ? Convert.FromHexString(p[6]) : null;
					sprite.Pattern = ParseHex(p[7]);
					sprite.Palette = ParseUIntHex(p[8]);
					sprite.X = ParseInt(p[9]);
					sprite.Y = ParseInt(p[10]);
					sprite.Attr = ParseHex(p[13]);
					sprite.Frame = ParseInt(p[14]);
					sprite.MapperId = ParseInt(p[15]);
					sprite.LargeSprites = p.Length >= 17 && p[16].Trim() == "1";
					sprite.Legacy = false;
				} else if(p.Length >= 6) {
					//Old capture format: objectId,pattern,palette,x,y,attr (8x16 CHR ROM approximation).
					sprite.Pattern = ParseHex(p[1]);
					sprite.Palette = (uint)ParseHex(p[2]);
					sprite.X = ParseInt(p[3]);
					sprite.Y = ParseInt(p[4]);
					sprite.Attr = ParseHex(p[5]);
					sprite.LargeSprites = true;
					sprite.Legacy = true;
				} else {
					continue;
				}
				result.Add(sprite);
			}
			return result;
		}

		private static (int Width, int Height) DrawObject(string path, List<AtlasSprite> sprites, NesRomData rom, uint[] nesPalette)
		{
			bool legacy = sprites.Any(s => s.Legacy);
			List<(int X, int Y, int W, int H, byte[] Data, AtlasSprite Sprite)> parts = new();
			foreach(AtlasSprite sprite in sprites) {
				if(sprite.TileData != null && sprite.TileData.Length == 16) {
					parts.Add((sprite.X, sprite.Y, 8, 8, sprite.TileData, sprite));
				} else if(sprite.Legacy && rom.HasChrRom) {
					int first = sprite.Pattern & ~1;
					for(int half = 0; half < 2; half++) {
						int pattern = first + (((sprite.Attr & 0x80) != 0) ? 1 - half : half);
						if(pattern < 0 || pattern >= rom.ChrTileCount) {
							continue;
						}
						parts.Add((sprite.X, sprite.Y + half * 8, 8, 8, rom.GetChrTile(pattern), sprite));
					}
				}
			}

			//A tile can be all transparent; it cannot define a bounding box.
			List<(int X, int Y, int W, int H, byte[] Data, AtlasSprite Sprite)> visible = new();
			foreach(var part in parts) {
				if(HasPixels(part.Data)) {
					visible.Add(part);
				}
			}
			if(visible.Count == 0) {
				return (0, 0);
			}

			int minX = visible.Min(p => p.X);
			int minY = visible.Min(p => p.Y);
			int maxX = visible.Max(p => p.X + 8);
			int maxY = visible.Max(p => p.Y + 8);
			int width = Math.Clamp(maxX - minX, 8, 128);
			int height = Math.Clamp(maxY - minY, 8, 128);
			uint[] pixels = new uint[width * height];

			foreach(var part in visible) {
				AtlasSprite sprite = part.Sprite;
				bool hflip = (sprite.Attr & 0x40) != 0;
				bool vflip = (sprite.Attr & 0x80) != 0;
				for(int sy = 0; sy < 8; sy++) {
					for(int sx = 0; sx < 8; sx++) {
						int srcX = hflip ? 7 - sx : sx;
						int srcY = vflip ? 7 - sy : sy;
						int color = GetTilePixel(part.Data, srcX, srcY);
						if(color == 0) {
							continue;
						}
						int px = part.X - minX + sx;
						int py = part.Y - minY + sy;
						if(px >= 0 && px < width && py >= 0 && py < height) {
							pixels[py * width + px] = PaletteToArgb(nesPalette, sprite.Palette, color);
						}
					}
				}
			}

			HdStudioPng.Write(path, HdStudioPng.Upscale(pixels, width, height, 3), width * 3, height * 3);
			return (width, height);
		}

		private static bool HasPixels(byte[] data)
		{
			for(int i = 0; i < 16 && i < data.Length; i++) {
				if(data[i] != 0) {
					return true;
				}
			}
			return false;
		}

		private static int GetTilePixel(byte[] data, int x, int y)
		{
			int low = (data[y] >> (7 - x)) & 1;
			int high = (data[y + 8] >> (7 - x)) & 1;
			return low | (high << 1);
		}

		private static uint PaletteToArgb(uint[] nesPalette, uint palette, int colorIndex)
		{
			int shift = (3 - colorIndex) * 8;
			int nesColor = (int)((palette >> shift) & 0x3F);
			return nesPalette[nesColor] | 0xFF000000;
		}

		private static int ParseInt(string value)
		{
			return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
		}

		private static int ParseHex(string value)
		{
			return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int result) ? result : 0;
		}

		private static uint ParseUIntHex(string value)
		{
			return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint result) ? result : 0;
		}

		private static string SafeName(string value)
		{
			StringBuilder sb = new();
			foreach(char c in value) {
				sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
			}
			return sb.ToString().Trim('_');
		}
	}
}
