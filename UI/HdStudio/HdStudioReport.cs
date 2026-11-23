using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Analyzes the ROM and the data recorded by the HD pack builder, and writes a report
	/// (HTML + JSON + PNG templates) into the "HdStudio" subfolder of the HD pack.
	/// </summary>
	internal class HdStudioReport
	{
		public const string StudioFolderName = "HdStudio";
		private const int MaxReportedObjects = 200;

		private readonly string _packFolder;
		private readonly string _studioFolder;
		private readonly uint[] _nesPalette;
		private readonly Action<string> _log;
		private readonly string? _disasmPath;
		private HdStudioDisasm? _disasm;

		private NesRomData _rom = new();
		private int _scale = 1;
		private int _frameCount = 0;
		private bool _isChrRam = false;
		private List<string> _packImages = new();
		private HashSet<(string Tile, uint Palette)> _packTiles = new();
		private int _packDefaultTiles = 0;
		private Dictionary<(string Tile, uint Palette), ulong> _tileUsage = new();
		private List<StudioObject> _objects = new();
		private List<StudioPrgPalette> _prgPalettes = new();
		private int _unseenTilesWithPrgPalette = 0;
		private int _unseenTilesWithNeighbourPalette = 0;
		private int _unseenTilesWithDefaultPalette = 0;
		private List<string> _levelsVisited = new();
		private int _mapLevels = 0;
		private int _mapPatterns = 0;
		private int _mapNonBlankPatterns = 0;
		private int _mapSeenPatterns = 0;
		private double _mapCoverage = 0;
		private HashSet<(string Tile, uint Palette)> _exactLevelTiles = new();
		private HashSet<(string Tile, uint Palette)> _exactSpriteTiles = new();
		private int _mapExactInPack = 0;
		private int _mapExactPatternsInPack = 0;
		private int _spriteExactInPack = 0;
		private int _seenSpriteCombos = 0;
		private int _seenBackgroundCombos = 0;
		private List<string> _mapImages = new();
		private List<(string Name, string Png, int Patterns, int Tileset)> _mapIndex = new();
		private List<(int Id, string Label, string Name)> _gameObjects = new();

		public HdStudioReport(string packFolder, uint[] nesPalette, Action<string> log, string? disasmPath = null)
		{
			_packFolder = packFolder;
			_studioFolder = Path.Combine(packFolder, StudioFolderName);
			_nesPalette = nesPalette;
			_log = log;
			_disasmPath = disasmPath;
		}

		/// <returns>Path to the HTML report</returns>
		public string Generate(string romPath)
		{
			if(!string.IsNullOrWhiteSpace(_disasmPath)) {
				_disasm = HdStudioDisasm.Load(_disasmPath!, romPath, _nesPalette);
				if(_disasm.Error != null) {
					_log("Disassembly: " + _disasm.Error);
				} else {
					_log($"Disassembly: {_disasm.TilesetsFound} tilesets, {_disasm.LevelsParsed} levels, {_disasm.ReferencedTiles.Count:N0} layout tiles (estimate; use --maps for the exact usage)");
				}
				ReadGameObjectCatalog();
			}
			Directory.CreateDirectory(_studioFolder);

			_rom = NesRomData.Load(romPath);
			ReadCaptureInfo();
			ReadPrgPalettes();
			ReadLevelsVisited();
			ReadLevelMaps();
			ReadSpriteSweep();
			ReadHiresFile();
			_mapExactInPack = _exactLevelTiles.Count(t => _packTiles.Contains(t));
			_mapExactPatternsInPack = _exactLevelTiles.Where(t => _packTiles.Contains(t)).Select(t => t.Tile).Distinct().Count();
			_spriteExactInPack = _exactSpriteTiles.Count(t => _packTiles.Contains(t));
			ReadTileUsage();
			_seenSpriteCombos = _tileUsage.Keys.Count(k => IsSpritePalette(k.Palette));
			_seenBackgroundCombos = _tileUsage.Count - _seenSpriteCombos;
			ReadObjects();

			List<ObjectGroup> groups = GroupObjects();
			WriteTemplates(groups);
			WriteObjectSheet(groups);
			bool hasCoverageImage = WriteChrCoverageImage();
			bool hasBackgrounds = WriteBackgroundSheet();
			WriteJson(groups, hasBackgrounds);
			string htmlPath = WriteHtml(groups, hasCoverageImage, hasBackgrounds);
			return htmlPath;
		}

		#region Input files

		private void ReadCaptureInfo()
		{
			string path = Path.Combine(_studioFolder, "capture.txt");
			if(!File.Exists(path)) {
				return;
			}

			foreach(string line in File.ReadAllLines(path)) {
				string[] kv = line.Split('=', 2);
				if(kv.Length != 2) {
					continue;
				}
				switch(kv[0]) {
					case "frames": int.TryParse(kv[1], out _frameCount); break;
					case "chrRam": _isChrRam = kv[1] == "1"; break;
					case "unseenTilesWithPrgPalette": int.TryParse(kv[1], out _unseenTilesWithPrgPalette); break;
					case "unseenTilesWithNeighbourPalette": int.TryParse(kv[1], out _unseenTilesWithNeighbourPalette); break;
					case "unseenTilesWithDefaultPalette": int.TryParse(kv[1], out _unseenTilesWithDefaultPalette); break;
				}
			}
		}

		/// <summary>Levels visited by --levels: one screenshot per level in HdStudio/levels</summary>
		private void ReadLevelsVisited()
		{
			string folder = Path.Combine(_studioFolder, "levels");
			if(!Directory.Exists(folder)) {
				return;
			}

			foreach(string file in Directory.GetFiles(folder, "*_start.png").OrderBy(f => f)) {
				string name = Path.GetFileNameWithoutExtension(file);
				int separator = name.IndexOf('_');
				name = separator > 0 ? name.Substring(separator + 1) : name;
				if(name.EndsWith("_start")) {
					name = name.Substring(0, name.Length - "_start".Length);
				}
				_levelsVisited.Add(name.Replace('_', ' '));
			}
		}

		/// <summary>
		/// Object types defined by the game's own data (game.xml, used by the level editor).
		/// This is a catalog of what the game can spawn; it is not the same as the metasprites
		/// detected during the recording, which only include things that appeared on screen.
		/// </summary>
		private void ReadGameObjectCatalog()
		{
			try {
				string? folder = Directory.Exists(_disasmPath) ? _disasmPath : Path.GetDirectoryName(_disasmPath);
				if(folder == null) {
					return;
				}
				string gameXml = Path.Combine(folder, "game.xml");
				if(!File.Exists(gameXml)) {
					return;
				}
				Regex regex = new(@"<object\s+id=""0x([0-9A-Fa-f]+)""[^>]*label=""([^""]+)""[^>]*name=""([^""]+)""");
				foreach(string line in File.ReadLines(gameXml)) {
					Match m = regex.Match(line);
					if(m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int id)) {
						_gameObjects.Add((id, m.Groups[2].Value, m.Groups[3].Value));
					}
				}
				_log($"Object catalog: {_gameObjects.Count} object types defined by game.xml");
			} catch(Exception ex) {
				_log("Object catalog: " + ex.Message);
			}
		}

		/// <summary>
		/// Level maps written by --maps: HdStudio/maps/maps.json has the exact background patterns
		/// referenced by each level (the map was built by the game's own level loader).
		/// </summary>
		private void ReadLevelMaps()
		{
			string folder = Path.Combine(_studioFolder, "maps");
			string path = Path.Combine(folder, "maps.json");
			if(!File.Exists(path)) {
				return;
			}

			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				JsonElement root = doc.RootElement;
				if(root.TryGetProperty("summary", out JsonElement summary)) {
					_mapLevels = summary.TryGetProperty("levels", out JsonElement levels) ? levels.GetInt32() : 0;
					_mapPatterns = summary.TryGetProperty("patterns", out JsonElement patterns) ? patterns.GetInt32() : 0;
					_mapNonBlankPatterns = summary.TryGetProperty("nonBlankPatterns", out JsonElement nonBlank) ? nonBlank.GetInt32() : 0;
					_mapSeenPatterns = summary.TryGetProperty("seenPatterns", out JsonElement seen) ? seen.GetInt32() : 0;
					_mapCoverage = summary.TryGetProperty("coveragePercent", out JsonElement coverage) ? coverage.GetDouble() : 0;
				}
				if(root.TryGetProperty("levels", out JsonElement list)) {
					foreach(JsonElement level in list.EnumerateArray()) {
						string name = level.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
						string png = level.TryGetProperty("png", out JsonElement p) ? p.GetString() ?? "" : "";
						int patterns = level.TryGetProperty("patterns", out JsonElement pc) ? pc.GetInt32() : 0;
						int tileset = level.TryGetProperty("tileset", out JsonElement ts) ? ts.GetInt32() : 0;
						if(png.Length > 0) {
							_mapImages.Add("maps/" + png);
							_mapIndex.Add((name, png, patterns, tileset));
						}
					}
				}

				string exactPath = Path.Combine(folder, "tiles_exact.txt");
				if(File.Exists(exactPath)) {
					foreach(string line in File.ReadAllLines(exactPath)) {
						if(line.Length == 0 || line[0] == '#') {
							continue;
						}
						string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
						if(parts.Length >= 2 && TryParseHex(parts[1], out uint palette)) {
							_exactLevelTiles.Add((NormalizeTileKey(parts[0]), palette));
						}
					}
				}
			} catch(Exception ex) {
				_log("Level maps: maps.json could not be read: " + ex.Message);
			}
		}

		/// <summary>
		/// Sprite sweep written next to the level maps: every seen sprite pattern with every real
		/// sprite palette. The pack should contain all of these exact (pattern + palette) keys.
		/// </summary>
		private void ReadSpriteSweep()
		{
			string path = Path.Combine(_studioFolder, "maps", "sprites_exact.txt");
			if(!File.Exists(path)) {
				return;
			}
			foreach(string line in File.ReadAllLines(path)) {
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if(parts.Length >= 2 && TryParseHex(parts[1], out uint palette)) {
					_exactSpriteTiles.Add((NormalizeTileKey(parts[0]), palette));
				}
			}
		}

		/// <summary>
		/// Palettes found in the game's code (PRG ROM): 4-color tables that the game writes to the PPU.
		/// They are used to colorize the cartridge tiles that were never displayed.
		/// </summary>
		private void ReadPrgPalettes()
		{
			string path = Path.Combine(_studioFolder, "palettes.txt");
			if(!File.Exists(path)) {
				return;
			}

			foreach(string line in File.ReadAllLines(path)) {
				if(line.Length == 0 || line.StartsWith("#")) {
					continue;
				}

				//palette \t uses \t firstOffset
				string[] parts = line.Split('\t');
				if(parts.Length == 3 && TryParseHex(parts[0], out uint colors) && uint.TryParse(parts[1], out uint uses) && TryParseHex(parts[2], out uint offset)) {
					_prgPalettes.Add(new StudioPrgPalette() { Colors = colors, Uses = uses, Offset = offset });
				}
			}
		}

		private void ReadHiresFile()
		{
			string path = Path.Combine(_packFolder, "hires.txt");
			if(!File.Exists(path)) {
				throw new Exception("hires.txt not found in " + _packFolder);
			}

			foreach(string rawLine in File.ReadAllLines(path)) {
				string line = rawLine.Trim();
				if(line.StartsWith("[")) {
					int end = line.IndexOf(']');
					line = end >= 0 ? line.Substring(end + 1) : line;
				}

				if(line.StartsWith("<scale>")) {
					int.TryParse(line.Substring(7), out _scale);
				} else if(line.StartsWith("<img>")) {
					_packImages.Add(line.Substring(5));
				} else if(line.StartsWith("<tile>")) {
					string[] parts = line.Substring(6).Split(',');
					if(parts.Length >= 3 && TryParseHex(parts[2], out uint palette)) {
						_packTiles.Add((NormalizeTileKey(parts[1]), palette));
					}
					if(parts.Length >= 7 && parts[6].Trim().Equals("Y", StringComparison.OrdinalIgnoreCase)) {
						_packDefaultTiles++;
					}
				}
			}
			_scale = Math.Max(1, _scale);
		}

		private void ReadTileUsage()
		{
			string path = Path.Combine(_studioFolder, "tiles.txt");
			if(!File.Exists(path)) {
				return;
			}

			foreach(string line in File.ReadAllLines(path)) {
				if(line.StartsWith("#")) {
					continue;
				}
				string[] parts = line.Split('\t');
				if(parts.Length == 3 && TryParseHex(parts[1], out uint palette) && ulong.TryParse(parts[2], out ulong count)) {
					var key = (NormalizeTileKey(parts[0]), palette);
					_tileUsage[key] = _tileUsage.GetValueOrDefault(key) + count;
				}
			}
		}

		private void ReadObjects()
		{
			string path = Path.Combine(_studioFolder, "objects.txt");
			if(!File.Exists(path)) {
				return;
			}

			foreach(string line in File.ReadAllLines(path)) {
				if(line.StartsWith("#")) {
					continue;
				}
				string[] parts = line.Split('\t');
				if(parts.Length != 5) {
					continue;
				}

				StudioObject obj = new() {
					FrameCount = uint.Parse(parts[0], CultureInfo.InvariantCulture),
					Width = int.Parse(parts[2], CultureInfo.InvariantCulture),
					Height = int.Parse(parts[3], CultureInfo.InvariantCulture),
				};

				foreach(string spriteDef in parts[4].Split(';', StringSplitOptions.RemoveEmptyEntries)) {
					string[] f = spriteDef.Split(',');
					if(f.Length != 5 || !TryParseHex(f[3], out uint palette)) {
						continue;
					}
					obj.Sprites.Add(new StudioSprite() {
						X = int.Parse(f[0], CultureInfo.InvariantCulture),
						Y = int.Parse(f[1], CultureInfo.InvariantCulture),
						Tile = NormalizeTileKey(f[2]),
						Palette = palette,
						Flags = int.Parse(f[4], CultureInfo.InvariantCulture)
					});
				}

				if(obj.Sprites.Count > 0) {
					_objects.Add(obj);
				}
			}
		}

		#endregion

		#region Analysis

		/// <summary>Merges objects that only differ by their palette (e.g. same enemy in another color)</summary>
		private List<ObjectGroup> GroupObjects()
		{
			Dictionary<string, ObjectGroup> groups = new();
			foreach(StudioObject obj in _objects) {
				string shape = string.Join(";", obj.Sprites.Select(s => $"{s.X},{s.Y},{s.Tile},{s.Flags}"));
				if(!groups.TryGetValue(shape, out ObjectGroup? group)) {
					group = new ObjectGroup() { Width = obj.Width, Height = obj.Height };
					groups[shape] = group;
				}
				group.Variants.Add(obj);
				group.FrameCount += obj.FrameCount;
			}

			//Ignore objects that only appeared for a couple of frames (usually transitions/glitches)
			uint minFrames = (uint)Math.Max(2, _frameCount / 5000);
			List<ObjectGroup> result = groups.Values
				.Where(g => g.FrameCount >= minFrames)
				.OrderByDescending(g => g.FrameCount)
				.Take(MaxReportedObjects)
				.ToList();

			for(int i = 0; i < result.Count; i++) {
				ObjectGroup g = result[i];
				g.Id = i + 1;
				g.Variants.Sort((a, b) => b.FrameCount.CompareTo(a.FrameCount));
				g.TemplateName = $"obj_{g.Id:000}_{g.Width * _scale}x{g.Height * _scale}.png";
			}
			return result;
		}

		private int NonBlankChrTileCount()
		{
			int count = 0;
			for(int i = 0; i < _rom.ChrTileCount; i++) {
				if(!_rom.IsBlankTile(i)) {
					count++;
				}
			}
			return count;
		}

		private HashSet<int> SeenChrTiles()
		{
			//Tiles that were actually displayed while the game was played (tiles.txt only lists those)
			return ChrTileIndexes(_tileUsage.Keys);
		}

		private HashSet<int> InPackChrTiles()
		{
			//Tiles present in hires.txt: with --generate this includes the tiles that were never displayed
			return ChrTileIndexes(_packTiles);
		}

		private HashSet<int> ChrTileIndexes(IEnumerable<(string Tile, uint Palette)> tiles)
		{
			HashSet<int> result = new();
			if(!_rom.HasChrRom) {
				return result;
			}
			foreach(var tile in tiles) {
				if(int.TryParse(tile.Tile, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int index) && index < _rom.ChrTileCount && !_rom.IsBlankTile(index)) {
					result.Add(index);
				}
			}
			return result;
		}

		#endregion

		#region Output

		private void WriteTemplates(List<ObjectGroup> groups)
		{
			string folder = Path.Combine(_studioFolder, "templates");
			Directory.CreateDirectory(folder);

			//Remove templates from a previous report so the folder never accumulates stale files
			foreach(string oldFile in Directory.GetFiles(folder, "obj_*.png")) {
				File.Delete(oldFile);
			}

			foreach(ObjectGroup group in groups) {
				uint[] pixels = RenderObject(group.Variants[0], group.Width, group.Height);
				uint[] scaled = HdStudioPng.Upscale(pixels, group.Width, group.Height, _scale);
				HdStudioPng.Write(Path.Combine(folder, group.TemplateName), scaled, group.Width * _scale, group.Height * _scale);
			}
			_log($"Templates: {groups.Count} PNG files written to {folder}");
		}

		/// <summary>
		/// One sprite sheet with every detected object at its final HD size (sheet_objects.png), and its layout
		/// (sheet_objects.json): position of each object in the sheet and the original 8x8 tiles that compose it.
		/// This is the sheet an artist redraws, and what HD Studio uses to put the redrawn art back into the pack.
		/// </summary>
		private void WriteObjectSheet(List<ObjectGroup> groups)
		{
			string pngPath = Path.Combine(_studioFolder, "sheet_objects.png");
			string jsonPath = Path.Combine(_studioFolder, "sheet_objects.json");
			if(groups.Count == 0) {
				File.Delete(pngPath);
				File.Delete(jsonPath);
				return;
			}

			//Shelf packing, tallest objects first
			int padding = 2 * _scale;
			int maxWidth = Math.Max(1024, groups.Max(g => g.Width * _scale) + padding * 2);
			List<(ObjectGroup Group, int X, int Y)> placed = new();
			int x = padding, y = padding, shelfHeight = 0, sheetWidth = 0;
			foreach(ObjectGroup g in groups.OrderByDescending(g => g.Height).ThenByDescending(g => g.Width)) {
				int w = g.Width * _scale;
				int h = g.Height * _scale;
				if(x + w + padding > maxWidth) {
					x = padding;
					y += shelfHeight + padding;
					shelfHeight = 0;
				}
				placed.Add((g, x, y));
				x += w + padding;
				shelfHeight = Math.Max(shelfHeight, h);
				sheetWidth = Math.Max(sheetWidth, x);
			}
			int sheetHeight = y + shelfHeight + padding;

			uint[] sheet = new uint[sheetWidth * sheetHeight];
			foreach(var p in placed) {
				uint[] pixels = HdStudioPng.Upscale(RenderObject(p.Group.Variants[0], p.Group.Width, p.Group.Height), p.Group.Width, p.Group.Height, _scale);
				int w = p.Group.Width * _scale;
				for(int py = 0; py < p.Group.Height * _scale; py++) {
					Array.Copy(pixels, py * w, sheet, (p.Y + py) * sheetWidth + p.X, w);
				}
			}
			HdStudioPng.Write(pngPath, sheet, sheetWidth, sheetHeight);

			using FileStream stream = File.Create(jsonPath);
			using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
			json.WriteStartObject();
			json.WriteString("image", "sheet_objects.png");
			json.WriteNumber("scale", _scale);
			json.WriteBoolean("chrRam", _isChrRam || !_rom.HasChrRom);
			json.WriteStartArray("objects");
			foreach(var p in placed.OrderBy(p => p.Group.Id)) {
				json.WriteStartObject();
				json.WriteNumber("id", p.Group.Id);
				json.WriteNumber("x", p.X);
				json.WriteNumber("y", p.Y);
				json.WriteNumber("width", p.Group.Width * _scale);
				json.WriteNumber("height", p.Group.Height * _scale);
				json.WriteStartArray("palettes");
				foreach(StudioObject variant in p.Group.Variants) {
					json.WriteStartArray();
					foreach(StudioSprite s in variant.Sprites) {
						json.WriteStringValue(s.Palette.ToString("X8"));
					}
					json.WriteEndArray();
				}
				json.WriteEndArray();
				json.WriteStartArray("tiles");
				foreach(StudioSprite s in p.Group.Variants[0].Sprites) {
					json.WriteStartObject();
					json.WriteNumber("x", s.X * _scale);
					json.WriteNumber("y", s.Y * _scale);
					json.WriteString("tile", s.Tile);
					json.WriteBoolean("flipH", (s.Flags & 1) != 0);
					json.WriteBoolean("flipV", (s.Flags & 2) != 0);
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
			_log($"Object sheet: {sheetWidth}x{sheetHeight} px with {placed.Count} objects -> {pngPath}");
		}

		private uint[] RenderObject(StudioObject obj, int width, int height)
		{
			uint[] pixels = new uint[width * height];

			//Draw in reverse so the first sprite ends up on top
			for(int i = obj.Sprites.Count - 1; i >= 0; i--) {
				StudioSprite sprite = obj.Sprites[i];
				byte[]? tileData = GetTileData(sprite.Tile);
				if(tileData == null) {
					continue;
				}

				for(int y = 0; y < 8; y++) {
					for(int x = 0; x < 8; x++) {
						int srcX = (sprite.Flags & 1) != 0 ? 7 - x : x;
						int srcY = (sprite.Flags & 2) != 0 ? 7 - y : y;
						int color = GetTilePixel(tileData, srcX, srcY);
						if(color == 0) {
							continue;
						}

						int px = sprite.X + x;
						int py = sprite.Y + y;
						if(px < width && py < height) {
							pixels[py * width + px] = PaletteToArgb(sprite.Palette, color);
						}
					}
				}
			}
			return pixels;
		}

		private bool WriteChrCoverageImage()
		{
			string path = Path.Combine(_studioFolder, "chr_coverage.png");
			if(!_rom.HasChrRom || _rom.ChrTileCount == 0) {
				if(File.Exists(path)) {
					File.Delete(path);
				}
				return false;
			}

			HashSet<int> seen = SeenChrTiles();
			int columns = 16;
			int rows = (_rom.ChrTileCount + columns - 1) / columns;
			int width = columns * 8;
			int height = rows * 8;
			uint[] pixels = new uint[width * height];
			uint[] grays = { 0xFF101010, 0xFF707070, 0xFFB0B0B0, 0xFFF0F0F0 };
			uint[] reds = { 0xFF300808, 0xFF902020, 0xFFC04040, 0xFFF07070 };

			for(int tile = 0; tile < _rom.ChrTileCount; tile++) {
				byte[] data = _rom.GetChrTile(tile);
				bool isSeen = seen.Contains(tile) || _rom.IsBlankTile(tile);
				uint[] colors = isSeen ? grays : reds;
				int baseX = (tile % columns) * 8;
				int baseY = (tile / columns) * 8;
				for(int y = 0; y < 8; y++) {
					for(int x = 0; x < 8; x++) {
						pixels[(baseY + y) * width + baseX + x] = colors[GetTilePixel(data, x, y)];
					}
				}
			}

			HdStudioPng.Write(path, HdStudioPng.Upscale(pixels, width, height, 2), width * 2, height * 2);
			return true;
		}

		/// <summary>
		/// Background blocks (16x16, the usual NES metatile): groups of 4 tiles that are consecutive in CHR
		/// and used with the same palette. Redrawing these blocks instead of single 8x8 tiles is how the
		/// scenery of a level is re-imagined: walls, floors, bricks, water...
		/// </summary>
		private bool WriteBackgroundSheet()
		{
			string pngPath = Path.Combine(_studioFolder, "sheet_backgrounds.png");
			string jsonPath = Path.Combine(_studioFolder, "sheet_backgrounds.json");
			if(!_rom.HasChrRom) {
				if(File.Exists(pngPath)) {
					File.Delete(pngPath);
				}
				if(File.Exists(jsonPath)) {
					File.Delete(jsonPath);
				}
				return false;
			}

			//Tiles of the pack per palette, to find complete 2x2 blocks
			Dictionary<uint, HashSet<int>> byPalette = new();
			foreach((string tile, uint palette) in _packTiles) {
				if(int.TryParse(tile, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int index) && index < _rom.ChrTileCount) {
					if(!byPalette.TryGetValue(palette, out HashSet<int>? set)) {
						byPalette[palette] = set = new HashSet<int>();
					}
					set.Add(index);
				}
			}

			List<BackgroundBlock> blocks = new();
			foreach(var kvp in byPalette) {
				HashSet<int> tiles = kvp.Value;
				foreach(int index in tiles) {
					if(index % 16 == 15) {
						continue;
					}
					if(tiles.Contains(index + 1) && tiles.Contains(index + 16) && tiles.Contains(index + 17)) {
						blocks.Add(new BackgroundBlock() { Tile = index, Palette = kvp.Key, Usage = BlockUsage(index, kvp.Key) });
					}
				}
			}

			//The most used blocks first: those are the walls and floors of the level, not one-off decorations
			blocks = blocks
				.GroupBy(b => (b.Tile, b.Palette))
				.Select(g => g.OrderByDescending(b => b.Usage).First())
				.OrderByDescending(b => b.Usage)
				.Take(400)
				.ToList();

			if(blocks.Count == 0) {
				File.Delete(pngPath);
				File.Delete(jsonPath);
				_log("Background blocks: none found (no 2x2 blocks share a palette)");
				return false;
			}

			//Shelf packing, rendered at the HD size of the pack so the artist can draw over them
			int size = 16 * _scale;
			int padding = 2 * _scale;
			int maxWidth = Math.Max(1024, size + padding * 2);
			int x = padding, y = padding, shelfHeight = 0, sheetWidth = 0;
			List<(BackgroundBlock Block, int X, int Y)> placed = new();
			foreach(BackgroundBlock block in blocks) {
				if(x + size + padding > maxWidth) {
					x = padding;
					y += shelfHeight + padding;
					shelfHeight = 0;
				}
				placed.Add((block, x, y));
				x += size + padding;
				shelfHeight = Math.Max(shelfHeight, size);
				sheetWidth = Math.Max(sheetWidth, x);
			}
			int sheetHeight = y + shelfHeight + padding;

			uint[] sheet = new uint[sheetWidth * sheetHeight];
			foreach(var p in placed) {
				uint[] pixels = RenderBlock(p.Block);
				uint[] scaled = HdStudioPng.Upscale(pixels, 16, 16, _scale);
				int w = 16 * _scale;
				for(int py = 0; py < w; py++) {
					Array.Copy(scaled, py * w, sheet, (p.Y + py) * sheetWidth + p.X, w);
				}
			}
			HdStudioPng.Write(pngPath, sheet, sheetWidth, sheetHeight);

			using(FileStream stream = File.Create(jsonPath))
			using(Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true })) {
				json.WriteStartObject();
				json.WriteString("image", "sheet_backgrounds.png");
				json.WriteNumber("scale", _scale);
				json.WriteString("blockSize", "16x16");
				json.WriteStartArray("blocks");
				foreach(var p in placed) {
					json.WriteStartObject();
					json.WriteNumber("x", p.X);
					json.WriteNumber("y", p.Y);
					json.WriteNumber("width", 16 * _scale);
					json.WriteNumber("height", 16 * _scale);
					json.WriteString("palette", p.Block.Palette.ToString("X8"));
					json.WriteNumber("uses", p.Block.Usage);
					json.WriteStartArray("tiles");
					foreach(int t in new[] { p.Block.Tile, p.Block.Tile + 1, p.Block.Tile + 16, p.Block.Tile + 17 }) {
						json.WriteStringValue(t.ToString("X"));
					}
					json.WriteEndArray();
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}

			_log($"Background blocks: {placed.Count} blocks of 16x16 -> {pngPath}");
			return true;
		}

		private uint BlockUsage(int tile, uint palette)
		{
			ulong total = 0;
			foreach(int t in new[] { tile, tile + 1, tile + 16, tile + 17 }) {
				total += _tileUsage.GetValueOrDefault((t.ToString("X"), palette), 0UL);
			}
			return (uint)Math.Min(total / 4, uint.MaxValue);
		}

		private uint[] RenderBlock(BackgroundBlock block)
		{
			uint[] pixels = new uint[16 * 16];
			int[] tiles = { block.Tile, block.Tile + 1, block.Tile + 16, block.Tile + 17 };
			for(int i = 0; i < 4; i++) {
				int index = tiles[i];
				if(index >= _rom.ChrTileCount) {
					continue;
				}
				byte[] data = _rom.GetChrTile(index);
				int baseX = (i % 2) * 8;
				int baseY = (i / 2) * 8;
				for(int y = 0; y < 8; y++) {
					for(int x = 0; x < 8; x++) {
						pixels[(baseY + y) * 16 + baseX + x] = PaletteToArgb(block.Palette, GetTilePixel(data, x, y));
					}
				}
			}
			return pixels;
		}

		private void WriteJson(List<ObjectGroup> groups, bool hasBackgrounds)
		{
			HashSet<int> seen = SeenChrTiles();
			int nonBlank = NonBlankChrTileCount();

			using FileStream stream = File.Create(Path.Combine(_studioFolder, "report.json"));
			using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });

			json.WriteStartObject();
			json.WriteString("generatedBy", "Mesen HD Studio");
			json.WriteString("generatedAt", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));

			json.WriteStartObject("rom");
			json.WriteString("file", _rom.FileName);
			json.WriteString("sha1", _rom.Sha1);
			json.WriteString("sha1NoHeader", _rom.Sha1NoHeader);
			json.WriteNumber("mapper", _rom.Mapper);
			json.WriteNumber("prgRomKb", _rom.PrgSize / 1024);
			json.WriteNumber("chrRomKb", _rom.ChrSize / 1024);
			json.WriteBoolean("usesChrRam", !_rom.HasChrRom);
			json.WriteEndObject();

			json.WriteStartObject("capture");
			json.WriteNumber("frames", _frameCount);
			json.WriteNumber("scale", _scale);
			json.WriteNumber("hdTileSize", 8 * _scale);
			json.WriteNumber("uniqueTilePaletteCombos", _packTiles.Count);
			json.WriteNumber("spriteCombos", _packTiles.Count(t => IsSpritePalette(t.Palette)));
			json.WriteNumber("backgroundCombos", _packTiles.Count(t => !IsSpritePalette(t.Palette)));
			json.WriteNumber("seenSpriteCombos", _seenSpriteCombos);
			json.WriteNumber("seenBackgroundCombos", _seenBackgroundCombos);
			json.WriteNumber("defaultTiles", _packDefaultTiles);
			json.WriteNumber("prgPalettes", _prgPalettes.Count);
			json.WriteNumber("unseenTilesWithPrgPalette", _unseenTilesWithPrgPalette);
			if(_rom.HasChrRom) {
				json.WriteNumber("chrTiles", _rom.ChrTileCount);
				json.WriteNumber("chrTilesNonBlank", nonBlank);
				json.WriteNumber("chrTilesSeen", seen.Count);
				json.WriteNumber("chrTilesInPack", InPackChrTiles().Count);
				json.WriteNumber("chrCoveragePercent", nonBlank > 0 ? Math.Round(100.0 * seen.Count / nonBlank, 1) : 0);
			}
			json.WriteStartArray("packImages");
			foreach(string img in _packImages) {
				json.WriteStringValue(img);
			}
			json.WriteEndArray();
			json.WriteEndObject();

			HashSet<uint> packPalettes = _packTiles.Select(t => t.Palette & 0x00FFFFFF).ToHashSet();
			json.WriteStartArray("prgPalettes");
			foreach(StudioPrgPalette p in _prgPalettes) {
				json.WriteStartObject();
				json.WriteString("colors", p.Colors.ToString("X8"));
				json.WriteNumber("uses", p.Uses);
				json.WriteString("prgOffset", p.Offset.ToString("X"));
				json.WriteBoolean("inPack", IsPaletteInPack(p.Colors, packPalettes));
				json.WriteEndObject();
			}
			json.WriteEndArray();

			if(hasBackgrounds) {
				json.WriteStartObject("backgrounds");
				json.WriteString("image", "sheet_backgrounds.png");
				json.WriteString("layout", "sheet_backgrounds.json");
				json.WriteString("blockSize", "16x16");
				json.WriteEndObject();
			}

			if(_mapLevels > 0) {
				json.WriteStartObject("levelMaps");
				json.WriteNumber("levels", _mapLevels);
				json.WriteNumber("patterns", _mapPatterns);
				json.WriteNumber("nonBlankPatterns", _mapNonBlankPatterns);
				json.WriteNumber("exactCombos", _exactLevelTiles.Count);
				json.WriteNumber("exactCombosInPack", _mapExactInPack);
				json.WriteNumber("exactPatternsInPack", _mapExactPatternsInPack);
				json.WriteNumber("seenPatterns", _mapSeenPatterns);
				json.WriteNumber("coveragePercent", _mapCoverage);
				json.WriteEndObject();
			}

			if(_exactSpriteTiles.Count > 0) {
				json.WriteStartObject("sprites");
				json.WriteNumber("exactCombos", _exactSpriteTiles.Count);
				json.WriteNumber("exactCombosInPack", _spriteExactInPack);
				json.WriteNumber("seenCombos", _seenSpriteCombos);
				json.WriteNumber("wildcardPatterns", _packDefaultTiles);
				json.WriteEndObject();
			}

			if(_disasm != null && _disasm.Error == null) {
				int referenced = _disasm.ReferencedTiles.Count;
				int captured = _disasm.ReferencedTiles.Count(t => seen.Contains(t));
				json.WriteStartObject("gameData");
				json.WriteNumber("referencedTiles", referenced);
				json.WriteNumber("capturedReferencedTiles", captured);
				json.WriteNumber("referencedCoveragePercent", referenced > 0 ? Math.Round(100.0 * captured / referenced, 1) : 0);
				json.WriteNumber("levelsParsed", _disasm.LevelsParsed);
				json.WriteNumber("tilesetsFound", _disasm.TilesetsFound);
				json.WriteNumber("exactMapLevels", _mapLevels);
				json.WriteNumber("exactMapNonBlankPatterns", _mapNonBlankPatterns);
				json.WriteNumber("exactMapSeenPatterns", _mapSeenPatterns);
				json.WriteNumber("exactMapCoveragePercent", _mapCoverage);
				json.WriteEndObject();
			}

			if(_gameObjects.Count > 0) {
				json.WriteStartArray("gameObjects");
				foreach((int id, string label, string name) in _gameObjects) {
					json.WriteStartObject();
					json.WriteNumber("id", id);
					json.WriteString("label", label);
					json.WriteString("name", name);
					json.WriteEndObject();
				}
				json.WriteEndArray();
			}

			json.WriteStartArray("objects");
			foreach(ObjectGroup g in groups) {
				json.WriteStartObject();
				json.WriteNumber("id", g.Id);
				json.WriteString("template", "templates/" + g.TemplateName);
				json.WriteNumber("width", g.Width);
				json.WriteNumber("height", g.Height);
				json.WriteNumber("hdWidth", g.Width * _scale);
				json.WriteNumber("hdHeight", g.Height * _scale);
				json.WriteNumber("spriteTiles", g.Variants[0].Sprites.Count);
				json.WriteNumber("frames", g.FrameCount);
				json.WriteStartArray("palettes");
				foreach(StudioObject variant in g.Variants) {
					json.WriteStringValue(string.Join(" ", variant.Sprites.Select(s => s.Palette.ToString("X8")).Distinct()));
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}

		private string WriteHtml(List<ObjectGroup> groups, bool hasCoverageImage, bool hasBackgrounds)
		{
			HashSet<int> seen = SeenChrTiles();
			int nonBlank = NonBlankChrTileCount();
			int hdTile = 8 * _scale;
			int spriteCombos = _packTiles.Count(t => IsSpritePalette(t.Palette));
			int bgCombos = _packTiles.Count - spriteCombos;
			HashSet<uint> packPalettes = _packTiles.Select(t => t.Palette & 0x00FFFFFF).ToHashSet();
			ulong totalSpritePixels = 0, totalBgPixels = 0;
			foreach(var kvp in _tileUsage) {
				if(IsSpritePalette(kvp.Key.Palette)) {
					totalSpritePixels += kvp.Value;
				} else {
					totalBgPixels += kvp.Value;
				}
			}

			string E(string s) => WebUtility.HtmlEncode(s);

			StringBuilder sb = new();
			sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
			sb.AppendLine($"<title>HD Studio — {E(_rom.FileName)}</title>");
			sb.AppendLine("<style>");
			sb.AppendLine(":root{--bg:#f6f7f5;--card:#fff;--fg:#1d211c;--muted:#5d665a;--line:#dfe3dc;--accent:#2f8f2a;--warn:#b23b2e}");
			sb.AppendLine("@media (prefers-color-scheme:dark){:root{--bg:#141713;--card:#1d211c;--fg:#e7ebe4;--muted:#9aa595;--line:#2f352d;--accent:#62c45a;--warn:#ef7a6c}}");
			sb.AppendLine("body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,Segoe UI,sans-serif}main{max-width:1100px;margin:0 auto;padding:24px 16px}");
			sb.AppendLine("h1{font-size:24px;margin:0 0 4px}h2{font-size:18px;margin:32px 0 12px}.muted{color:var(--muted)}");
			sb.AppendLine(".stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px}.stat{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px}");
			sb.AppendLine(".stat b{display:block;font-size:22px}.stat span{color:var(--muted);font-size:13px}");
			sb.AppendLine("table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden}");
			sb.AppendLine("th,td{padding:8px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:middle}th{font-size:13px;color:var(--muted);font-weight:600}");
			sb.AppendLine("td.n{font-variant-numeric:tabular-nums;text-align:right}img.px{image-rendering:pixelated;background:repeating-conic-gradient(#8883 0 25%,transparent 0 50%) 0 0/8px 8px;border-radius:4px}");
			sb.AppendLine("code{font-size:13px}.wrap{overflow-x:auto}");
			sb.AppendLine("</style></head><body><main>");

			sb.AppendLine($"<h1>HD Studio — {E(_rom.FileName)}</h1>");
			sb.AppendLine($"<p class=\"muted\">Mapper {_rom.Mapper} · PRG {_rom.PrgSize / 1024} KB · {(_rom.HasChrRom ? $"CHR-ROM {_rom.ChrSize / 1024} KB" : "CHR-RAM")} · SHA-1 <code>{E(_rom.Sha1)}</code></p>");

			sb.AppendLine("<div class=\"stats\">");
			sb.AppendLine($"<div class=\"stat\"><b>{_frameCount:N0}</b><span>frames analyzed</span></div>");
			sb.AppendLine($"<div class=\"stat\"><b>{_scale}×</b><span>scale · {hdTile}×{hdTile} px HD tiles</span></div>");
			sb.AppendLine($"<div class=\"stat\"><b>{_packTiles.Count:N0}</b><span>tile images to draw ({spriteCombos:N0} sprites, {bgCombos:N0} background)</span></div>");
			if(spriteCombos > 0) {
				sb.AppendLine($"<div class=\"stat\"><b>{spriteCombos:N0}</b><span>sprite combinations in the pack ({_seenSpriteCombos:N0} seen with their exact palette; every pattern also has a wildcard fallback)</span></div>");
			}
			if(_exactSpriteTiles.Count > 0) {
				double spriteExactPct = 100.0 * _spriteExactInPack / _exactSpriteTiles.Count;
				sb.AppendLine($"<div class=\"stat\"><b>{spriteExactPct:0.0}%</b><span>of the exact sprite (pattern+palette) sweep is in the pack ({_spriteExactInPack:N0} of {_exactSpriteTiles.Count:N0}, covering all seen sprite patterns with all 42 real sprite palettes)</span></div>");
			}
			sb.AppendLine($"<div class=\"stat\"><b>{groups.Count:N0}</b><span>objects (metasprites) detected</span></div>");
			if(_rom.HasChrRom) {
				double coverage = nonBlank > 0 ? 100.0 * seen.Count / nonBlank : 0;
				sb.AppendLine($"<div class=\"stat\"><b>{coverage:0.0}%</b><span>of the cartridge tiles seen while playing ({seen.Count:N0} of {nonBlank:N0})</span></div>");
				int inPack = InPackChrTiles().Count;
				if(inPack > seen.Count) {
					double packCoverage = nonBlank > 0 ? 100.0 * inPack / nonBlank : 0;
					sb.AppendLine($"<div class=\"stat\"><b>{packCoverage:0.0}%</b><span>of the cartridge tiles in the pack ({inPack:N0}); the ones never seen carry a guessed palette</span></div>");
				}
			}
			if(_prgPalettes.Count > 0) {
				sb.AppendLine($"<div class=\"stat\"><b>{_prgPalettes.Count:N0}</b><span>palettes found in the game's code ({_prgPalettes.Count(p => IsPaletteInPack(p.Colors, packPalettes)):N0} used by the pack)</span></div>");
			}
			if(_mapLevels > 0) {
				double exactPct = _exactLevelTiles.Count > 0 ? 100.0 * _mapExactInPack / _exactLevelTiles.Count : 0;
				sb.AppendLine($"<div class=\"stat\"><b>{exactPct:0.0}%</b><span>of the exact background (pattern+palette) combinations are in the pack ({_mapExactInPack:N0} of {_exactLevelTiles.Count:N0}, covering {_mapExactPatternsInPack:N0} patterns)</span></div>");
			}
			if(_packDefaultTiles > 0) {
				sb.AppendLine($"<div class=\"stat\"><b>{_packDefaultTiles:N0}</b><span>CHR patterns with a wildcard fallback image (all sprites and UI can be replaced even when their exact palette was never seen)</span></div>");
			}
			sb.AppendLine("</div>");

			//Single coverage table: the three things the user asked to see (sprites, backgrounds, tiles)
			sb.AppendLine("<h2>Coverage</h2>");
			sb.AppendLine("<div class=\"wrap\"><table><thead><tr><th>Category</th><th>In the pack</th><th>Total</th><th>Coverage</th><th>Notes</th></tr></thead><tbody>");
			if(_mapLevels > 0) {
				double bgPct = _exactLevelTiles.Count > 0 ? 100.0 * _mapExactInPack / _exactLevelTiles.Count : 0;
				sb.AppendLine($"<tr><td><b>Backgrounds</b> (exact pattern + palette)</td><td class=\"n\">{_mapExactInPack:N0}</td><td class=\"n\">{_exactLevelTiles.Count:N0}</td><td class=\"n\"><b>{bgPct:0.0}%</b></td><td>217 level maps, every placed background tile has the game's palette.</td></tr>");
			}
			if(_exactSpriteTiles.Count > 0) {
				double spPct = 100.0 * _spriteExactInPack / _exactSpriteTiles.Count;
				sb.AppendLine($"<tr><td><b>Sprites</b> (exact pattern + palette sweep)</td><td class=\"n\">{_spriteExactInPack:N0}</td><td class=\"n\">{_exactSpriteTiles.Count:N0}</td><td class=\"n\"><b>{spPct:0.0}%</b></td><td>Every seen sprite pattern with all 42 real sprite palettes; {_seenSpriteCombos:N0} combinations seen on screen.</td></tr>");
				sb.AppendLine($"<tr><td><b>Sprites</b> (wildcard fallback)</td><td class=\"n\">{_packDefaultTiles:N0}</td><td class=\"n\">{_packDefaultTiles:N0}</td><td class=\"n\"><b>100.0%</b></td><td>Any remaining sprite palette uses the representative image (all sprites appear in HD).</td></tr>");
			}
			if(_rom.HasChrRom) {
				int inPack = InPackChrTiles().Count;
				int nonBlankTiles = NonBlankChrTileCount();
				sb.AppendLine($"<tr><td><b>Tiles</b> (non-blank cartridge CHR)</td><td class=\"n\">{inPack:N0}</td><td class=\"n\">{nonBlankTiles:N0}</td><td class=\"n\"><b>{(nonBlankTiles > 0 ? 100.0 * inPack / nonBlankTiles : 0):0.0}%</b></td><td>{seen.Count:N0} seen exactly, {Math.Max(0, nonBlankTiles - seen.Count):N0} only via the wildcard image.</td></tr>");
			}
			sb.AppendLine("</tbody></table></div>");

			sb.AppendLine("<h2>What to draw</h2>");
			sb.AppendLine($"<p>Every original 8×8 tile is replaced by a <b>{hdTile}×{hdTile} px</b> image. The pack sheets ({_packImages.Count}) are <b>{128 * _scale}×{128 * _scale} px</b> each (16×16 tiles). " +
				"To work per character instead of per tile, use the templates below: each one already has the final HD size; redraw on top of it, respecting the grid.</p>");
			if(groups.Count > 0) {
				sb.AppendLine("<p><b>Full sprite sheet:</b> <a href=\"sheet_objects.png\"><code>sheet_objects.png</code></a> has every object of the table at HD size " +
					"(its layout is in <code>sheet_objects.json</code>). This is the sheet to redraw and later load in HD Studio.</p>");
			}

			sb.AppendLine("<div class=\"wrap\"><table><thead><tr><th>#</th><th>Preview</th><th>PNG template</th><th>Original</th><th>HD size</th><th>Tiles</th><th>Palettes</th><th>Presence</th></tr></thead><tbody>");
			foreach(ObjectGroup g in groups) {
				double presence = _frameCount > 0 ? Math.Min(100.0, 100.0 * g.FrameCount / _frameCount) : 0;
				int previewScale = Math.Max(1, Math.Min(4, 64 / Math.Max(g.Width, g.Height)));
				sb.Append("<tr>");
				sb.Append($"<td class=\"n\">{g.Id}</td>");
				sb.Append($"<td><img class=\"px\" src=\"templates/{E(g.TemplateName)}\" width=\"{g.Width * previewScale}\" height=\"{g.Height * previewScale}\" alt=\"objeto {g.Id}\"></td>");
				sb.Append($"<td><code>{E(g.TemplateName)}</code></td>");
				sb.Append($"<td class=\"n\">{g.Width}×{g.Height}</td>");
				sb.Append($"<td class=\"n\"><b>{g.Width * _scale}×{g.Height * _scale}</b></td>");
				sb.Append($"<td class=\"n\">{g.Variants[0].Sprites.Count}</td>");
				sb.Append($"<td class=\"n\">{g.Variants.Count}</td>");
				sb.Append($"<td class=\"n\">{presence:0.#}%</td>");
				sb.AppendLine("</tr>");
			}
			sb.AppendLine("</tbody></table></div>");

			//Clarify the difference between "all tiles" and "all game objects"
			if(_gameObjects.Count > 0) {
				sb.AppendLine("<h2>Object catalog: tiles vs. detected metasprites</h2>");
				sb.AppendLine($"<p>The pack covers <b>100 % of the CHR tiles</b> and <b>100 % of the exact background combinations</b>, but that is not the same as a complete bestiary. " +
					$"The game defines <b>{_gameObjects.Count}</b> object types; this recording detected <b>{groups.Count}</b> metasprites (the table above and <code>sheet_objects.png</code>). " +
					$"Enemies or forms that never appeared in the recording are not in that sheet: their cartridge tiles are still in the pack (with exact or wildcard palette), but HD Studio has not grouped or named them. " +
					$"<b>The player forms</b> — Small, Super, Fire, Raccoon, <b>Tanuki</b>, Frog, Hammer and the Tanuki Statue — are drawn by the player code, not by an object ID, so they appear in the sheet as generic player templates (top row of <code>sheet_objects.png</code>, 16×16/16×32) without individual “Tanuki”/“Frog” labels. " +
					$"The complete tile atlas, including unseen tiles in red, is <a href=\"chr_coverage.png\"><code>chr_coverage.png</code></a>.</p>");
				sb.AppendLine($"<details><summary>All {_gameObjects.Count} object types defined by the game data</summary><div class=\"wrap\"><table><thead><tr><th>ID</th><th>Label</th><th>Name</th></tr></thead><tbody>");
				foreach((int id, string label, string name) in _gameObjects) {
					sb.AppendLine($"<tr><td class=\"n\">${id:X2}</td><td><code>{E(label)}</code></td><td>{E(name)}</td></tr>");
				}
				sb.AppendLine("</tbody></table></div></details>");
			}

			//Background blocks: the scenery to redraw as 16x16 metatiles
			if(hasBackgrounds) {
				sb.AppendLine("<h2>Backgrounds to redraw</h2>");
				sb.AppendLine("<p><a href=\"sheet_backgrounds.png\"><code>sheet_backgrounds.png</code></a>: <b>16×16</b> blocks (the usual NES metatile) made of 4 consecutive cartridge tiles used with the same palette. " +
					"These are the walls, floors, bricks, water… of the levels: if you redraw the whole block, the pack replaces the 4 pieces at once. The layout is in <code>sheet_backgrounds.json</code>.</p>");
				sb.AppendLine("<img class=\"px\" src=\"sheet_backgrounds.png\" alt=\"16x16 background blocks\" style=\"max-width:100%;height:auto\">");
			}

			//Sprites: exact seen combinations + the wildcard fallback that covers the rest
			if(spriteCombos > 0) {
				sb.AppendLine("<h2>Sprites</h2>");
				sb.AppendLine($"<p>The pack has <b>{spriteCombos:N0}</b> sprite (pattern + palette) entries. <b>{_seenSpriteCombos:N0}</b> were seen on screen with their exact palette, and the sprite sweep adds " +
					$"<b>{_exactSpriteTiles.Count:N0}</b> exact combinations (every seen sprite pattern with all 42 real sprite palettes; <b>{_spriteExactInPack:N0}</b> present in the pack). " +
					$"The remaining sprite patterns are covered by the <b>{_packDefaultTiles:N0}</b> wildcard <code>DefaultTile=Y</code> entries, so every sprite can be replaced even if its exact palette was never captured. " +
					"The object atlas (forcing every object ID through the game's drawing code) is the next step for exact colors on sprite patterns that were never seen at all.</p>");
			}

			//Data-driven check: exact level maps built by the game's own level loader (--maps)
			if(_mapLevels > 0) {
				sb.AppendLine("<h2>Level maps (exact, from the game's generators)</h2>");
				sb.AppendLine($"<p>The game's own level loader was executed once per level (the generators that place blocks, pipes, bushes, clouds…), " +
					$"and the resulting tile memory was painted with the cartridge CHR and each level's palette. <b>{_mapLevels:N0}</b> levels produced a full map. " +
					$"They reference <b>{_mapNonBlankPatterns:N0}</b> non-blank background tiles ({_mapPatterns:N0} including blank ones); the pack has " +
					$"<b>{_mapSeenPatterns:N0}</b> of them (<b>{_mapCoverage:0.0} %</b>) during the recording. This is the exact target list, not a layout estimate. " +
					$"At the (pattern + palette) level, the maps use <b>{_exactLevelTiles.Count:N0}</b> exact combinations and the pack now has <b>{_mapExactInPack:N0}</b> " +
					$"({(_exactLevelTiles.Count > 0 ? 100.0 * _mapExactInPack / _exactLevelTiles.Count : 0):0.0} %), covering <b>{_mapExactPatternsInPack:N0}</b> patterns: every background tile placed by a level has the palette the game really uses, even if the recording never showed that level.</p>");
				sb.AppendLine("<p><a href=\"maps/maps.txt\"><code>maps/maps.txt</code></a> lists every level, its tileset/palette and how many background tiles it uses; " +
					"<a href=\"maps/maps_tiles.txt\"><code>maps/maps_tiles.txt</code></a> has the exact CHR pattern index list of each level; " +
					"<a href=\"maps/tiles_exact.txt\"><code>maps/tiles_exact.txt</code></a> has the exact pattern+palette combinations; " +
					"<a href=\"maps/maps.json\"><code>maps/maps.json</code></a> has the machine-readable version.</p>");
				int shown = 0;
				sb.AppendLine("<div class=\"wrap\"><table><thead><tr><th>Level</th><th>Map</th><th>Tileset</th><th>Background tiles</th></tr></thead><tbody>");
				foreach((string name, string png, int patterns, int tileset) in _mapIndex) {
					if(shown >= 24) {
						break;
					}
					shown++;
					sb.AppendLine($"<tr><td>{E(name)}</td><td><a href=\"maps/{E(png)}\"><img class=\"px\" src=\"maps/{E(png)}\" alt=\"{E(name)}\" style=\"max-height:64px;max-width:100%\"></a></td>" +
						$"<td class=\"n\">{tileset}</td><td class=\"n\">{patterns:N0}</td></tr>");
				}
				sb.AppendLine("</tbody></table></div>");
				if(_mapIndex.Count > shown) {
					sb.AppendLine($"<p class=\"muted\" style=\"margin-top:8px\">Showing {shown} of {_mapIndex.Count:N0} maps; all PNGs are in <code>HdStudio/maps</code>.</p>");
				}
			} else if(_disasm != null && _disasm.Error == null) {
				int referenced = _disasm.ReferencedTiles.Count;
				int captured = _disasm.ReferencedTiles.Count(t => seen.Contains(t));
				double pct = referenced > 0 ? 100.0 * captured / referenced : 0;
				sb.AppendLine("<h2>Game data (layout estimate)</h2>");
				sb.AppendLine($"<p>Without <code>--maps</code>, the level layout tables give a rough estimate: <b>{referenced:N0}</b> tile layouts " +
					$"({_disasm.LevelsParsed:N0} levels parsed). The pack has <b>{captured:N0}</b> of them (<b>{pct:0.0} %</b>). " +
					$"Run <code>--maps --disasm=FOLDER</code> to load each level in the emulator and replace this estimate with the exact map.</p>");
			}

			//What is missing: this is what tells the user whether the capture is complete
			if(_rom.HasChrRom) {
				int inPack = InPackChrTiles().Count;
				int unseen = Math.Max(0, inPack - seen.Count);
				int missing = Math.Max(0, nonBlank - seen.Count);
				sb.AppendLine("<h2>What is missing</h2>");
				sb.AppendLine($"<p><b>{seen.Count:N0}</b> of the <b>{nonBlank:N0}</b> cartridge tiles have been seen on screen " +
					$"({(nonBlank > 0 ? 100.0 * seen.Count / nonBlank : 0):0.0} %). <b>{missing:N0}</b> were never captured " +
					$"({(nonBlank > 0 ? 100.0 * missing / nonBlank : 0):0.0} %), but <b>{_packDefaultTiles:N0}</b> patterns have a wildcard HD image, " +
					$"so the pack still replaces them (the color may be the representative palette until the exact one is captured).</p>");
				if(unseen > 0) {
					sb.AppendLine("<p>Of the tiles that are in the pack without ever being seen:</p><ul>");
					sb.AppendLine($"<li>{_unseenTilesWithPrgPalette:N0} use a <b>real palette</b> found in the game's code.</li>");
					sb.AppendLine($"<li>{_unseenTilesWithNeighbourPalette:N0} use the palette of the <b>neighbour tile</b> (likely, not certain).</li>");
					sb.AppendLine($"<li>{_unseenTilesWithDefaultPalette:N0} are <b>gray</b>: no palette can be guessed for them.</li>");
					sb.AppendLine("</ul>");
				}
				sb.AppendLine("<p class=\"muted\">To complete the pack those areas must be seen on screen: the coverage map below marks the missing tiles in red. " +
					"Play or replay a movie of the levels that have not been visited and run the capture again: the pack is completed without starting over.</p>");
				if(_levelsVisited.Count > 0) {
					sb.AppendLine($"<p><b>Levels visited in this capture ({_levelsVisited.Count}):</b> {E(string.Join(" · ", _levelsVisited))}</p>");
				}
			}

			if(_prgPalettes.Count > 0) {
				int inPack = _prgPalettes.Count(p => IsPaletteInPack(p.Colors, packPalettes));
				sb.AppendLine("<h2>Game palettes (found in the code)</h2>");
				sb.AppendLine($"<p>4-color tables found in the PRG-ROM: these are the palettes the game writes to the PPU. " +
					$"<b>{_prgPalettes.Count:N0}</b> were found and <b>{inPack:N0}</b> are already used by the pack. " +
					"When a whole CHR bank was never displayed, the tiles added by <code>--generate</code> use one of these palettes" +
					(_unseenTilesWithPrgPalette > 0 ? $" ({_unseenTilesWithPrgPalette:N0} tiles)" : "") + " instead of a gray scale.</p>");
				sb.AppendLine("<div class=\"wrap\"><table><thead><tr><th>#</th><th>Colors</th><th>Times in ROM</th><th>PRG offset</th><th>In the pack</th></tr></thead><tbody>");
				int shown = 0;
				foreach(StudioPrgPalette p in _prgPalettes) {
					if(shown++ >= 128) {
						break;
					}
					bool used = IsPaletteInPack(p.Colors, packPalettes);
					sb.Append("<tr>");
					sb.Append($"<td class=\"n\">{shown}</td>");
					sb.Append($"<td>{PaletteSwatches(p.Colors)}</td>");
					sb.Append($"<td class=\"n\">{p.Uses:N0}</td>");
					sb.Append($"<td class=\"n\"><code>${p.Offset:X}</code></td>");
					sb.Append($"<td class=\"n\">{(used ? "yes" : "—")}</td>");
					sb.AppendLine("</tr>");
				}
				sb.AppendLine("</tbody></table></div>");
				if(_prgPalettes.Count > 128) {
					sb.AppendLine($"<p class=\"muted\">The 128 most repeated are shown; the full list is in <code>{StudioFolderName}/palettes.txt</code>.</p>");
				}
			}

			if(hasCoverageImage) {
				sb.AppendLine("<h2>Cartridge coverage</h2>");
				sb.AppendLine("<p>Every CHR-ROM tile. The ones in <b style=\"color:var(--warn)\">red</b> have not appeared on screen yet: those areas must be played (or a movie replayed).</p>");
				sb.AppendLine("<img class=\"px\" src=\"chr_coverage.png\" alt=\"CHR-ROM tile coverage\" style=\"max-width:100%;height:auto\">");
			}

			sb.AppendLine("<h2>Screen usage</h2>");
			ulong total = totalSpritePixels + totalBgPixels;
			if(total > 0) {
				sb.AppendLine($"<p>Background: {100.0 * totalBgPixels / total:0.#}% of the drawn pixels · Sprites: {100.0 * totalSpritePixels / total:0.#}%.</p>");
			}
			sb.AppendLine($"<p class=\"muted\">Files: <code>hires.txt</code> and <code>Chr_*.png</code> sheets in the pack folder; raw data in <code>{StudioFolderName}/objects.txt</code>, <code>tiles.txt</code>, <code>palettes.txt</code> and <code>report.json</code>. Generated {DateTime.Now:yyyy-MM-dd HH:mm}.</p>");
			sb.AppendLine("</main></body></html>");

			string path = Path.Combine(_studioFolder, "report.html");
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
			return path;
		}

		#endregion

		#region Helpers

		private byte[]? GetTileData(string tileKey)
		{
			if(_isChrRam || !_rom.HasChrRom) {
				if(tileKey.Length != 32) {
					return null;
				}
				return Convert.FromHexString(tileKey);
			}

			if(int.TryParse(tileKey, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int index) && index < _rom.ChrTileCount) {
				return _rom.GetChrTile(index);
			}
			return null;
		}

		private static int GetTilePixel(byte[] tileData, int x, int y)
		{
			int low = (tileData[y] >> (7 - x)) & 1;
			int high = (tileData[y + 8] >> (7 - x)) & 1;
			return low | (high << 1);
		}

		/// <summary>Palette format (same as hires.txt): [color0][color1][color2][color3], one NES color index per byte</summary>
		private uint PaletteToArgb(uint palette, int colorIndex)
		{
			int shift = (3 - colorIndex) * 8;
			int nesColor = (int)((palette >> shift) & 0x3F);
			return _nesPalette[nesColor] | 0xFF000000;
		}

		private static bool IsSpritePalette(uint palette)
		{
			return (palette >> 24) == 0xFF;
		}

		/// <summary>
		/// True when the pack uses this ROM palette. Only the last 3 colors are compared: the game often takes
		/// the first color (the backdrop) from another table or from a global $3F00 write.
		/// </summary>
		private static bool IsPaletteInPack(uint colors, HashSet<uint> packColors)
		{
			return packColors.Contains(colors & 0x00FFFFFF);
		}

		/// <summary>Four colored squares, one per NES color of the palette</summary>
		private string PaletteSwatches(uint colors)
		{
			StringBuilder sb = new("<span style=\"display:inline-flex;gap:2px;vertical-align:middle\">");
			for(int i = 0; i < 4; i++) {
				int colorIndex = (int)((colors >> ((3 - i) * 8)) & 0x3F);
				string hex = (_nesPalette[colorIndex] & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
				sb.Append($"<span title=\"${colorIndex:X2}\" style=\"width:18px;height:18px;background:#{hex};border:1px solid #0003;border-radius:3px\"></span>");
			}
			return sb.Append("</span>").ToString();
		}

		private string NormalizeTileKey(string key)
		{
			key = key.Trim();
			if(key.Length == 32) {
				return key.ToUpperInvariant();
			}
			return int.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value.ToString("X") : key.ToUpperInvariant();
		}

		private static bool TryParseHex(string value, out uint result)
		{
			return uint.TryParse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
		}

		#endregion

		private class StudioSprite
		{
			public int X;
			public int Y;
			public string Tile = "";
			public uint Palette;
			public int Flags;
		}

		private class StudioObject
		{
			public uint FrameCount;
			public int Width;
			public int Height;
			public List<StudioSprite> Sprites = new();
		}

		/// <summary>A 16x16 background block: 4 consecutive CHR tiles used with the same palette</summary>
		private class BackgroundBlock
		{
			public int Tile;
			public uint Palette;
			public uint Usage;
		}

		/// <summary>A 4-color palette found in the game's code (PRG ROM)</summary>
		private class StudioPrgPalette
		{
			/// <summary>Background form: [color0][color1][color2][color3], one NES color index per byte</summary>
			public uint Colors;
			/// <summary>How many times the table appears in the ROM</summary>
			public uint Uses;
			/// <summary>Offset in the PRG ROM of the first occurrence</summary>
			public uint Offset;
		}

		private class ObjectGroup
		{
			public int Id;
			public int Width;
			public int Height;
			public uint FrameCount;
			public string TemplateName = "";
			public List<StudioObject> Variants = new();
		}
	}

	/// <summary>Reads the iNES / NES 2.0 header and CHR data of a .nes file</summary>
	internal class NesRomData
	{
		public string FileName = "";
		public string Sha1 = "";
		public string Sha1NoHeader = "";
		public int Mapper;
		public int PrgSize;
		public int ChrSize;
		public byte[] Chr = Array.Empty<byte>();

		public bool HasChrRom => ChrSize > 0;
		public int ChrTileCount => Chr.Length / 16;

		public byte[] GetChrTile(int index)
		{
			byte[] data = new byte[16];
			Array.Copy(Chr, index * 16, data, 0, 16);
			return data;
		}

		public bool IsBlankTile(int index)
		{
			for(int i = 0; i < 16; i++) {
				if(Chr[index * 16 + i] != 0) {
					return false;
				}
			}
			return true;
		}

		public static NesRomData Load(string path)
		{
			NesRomData rom = new() { FileName = Path.GetFileName(path) };
			byte[] data = File.ReadAllBytes(path);
			rom.Sha1 = Convert.ToHexString(SHA1.HashData(data));

			if(data.Length < 16 || data[0] != 'N' || data[1] != 'E' || data[2] != 'S' || data[3] != 0x1A) {
				//Not an iNES file (e.g. FDS or UNIF): no CHR information available
				return rom;
			}

			bool isNes20 = (data[7] & 0x0C) == 0x08;
			int prgUnits = data[4];
			int chrUnits = data[5];
			rom.Mapper = (data[6] >> 4) | (data[7] & 0xF0);
			if(isNes20) {
				prgUnits |= (data[9] & 0x0F) << 8;
				chrUnits |= (data[9] & 0xF0) << 4;
				rom.Mapper |= (data[8] & 0x0F) << 8;
			}

			bool hasTrainer = (data[6] & 0x04) != 0;
			int offset = 16 + (hasTrainer ? 512 : 0);
			rom.PrgSize = prgUnits * 16384;
			rom.ChrSize = chrUnits * 8192;

			int chrOffset = offset + rom.PrgSize;
			int available = Math.Max(0, Math.Min(rom.ChrSize, data.Length - chrOffset));
			rom.Chr = new byte[available];
			Array.Copy(data, chrOffset, rom.Chr, 0, available);

			rom.Sha1NoHeader = Convert.ToHexString(SHA1.HashData(data.AsSpan(offset)));
			return rom;
		}
	}
}
