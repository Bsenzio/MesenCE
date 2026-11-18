using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

		private NesRomData _rom = new();
		private int _scale = 1;
		private int _frameCount = 0;
		private bool _isChrRam = false;
		private List<string> _packImages = new();
		private HashSet<(string Tile, uint Palette)> _packTiles = new();
		private Dictionary<(string Tile, uint Palette), ulong> _tileUsage = new();
		private List<StudioObject> _objects = new();

		public HdStudioReport(string packFolder, uint[] nesPalette, Action<string> log)
		{
			_packFolder = packFolder;
			_studioFolder = Path.Combine(packFolder, StudioFolderName);
			_nesPalette = nesPalette;
			_log = log;
		}

		/// <returns>Path to the HTML report</returns>
		public string Generate(string romPath)
		{
			Directory.CreateDirectory(_studioFolder);

			_rom = NesRomData.Load(romPath);
			ReadCaptureInfo();
			ReadHiresFile();
			ReadTileUsage();
			ReadObjects();

			List<ObjectGroup> groups = GroupObjects();
			WriteTemplates(groups);
			WriteObjectSheet(groups);
			bool hasCoverageImage = WriteChrCoverageImage();
			WriteJson(groups);
			string htmlPath = WriteHtml(groups, hasCoverageImage);
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

		private void WriteJson(List<ObjectGroup> groups)
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

		private string WriteHtml(List<ObjectGroup> groups, bool hasCoverageImage)
		{
			HashSet<int> seen = SeenChrTiles();
			int nonBlank = NonBlankChrTileCount();
			int hdTile = 8 * _scale;
			int spriteCombos = _packTiles.Count(t => IsSpritePalette(t.Palette));
			int bgCombos = _packTiles.Count - spriteCombos;

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
			sb.AppendLine("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
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
			sb.AppendLine($"<div class=\"stat\"><b>{_frameCount:N0}</b><span>cuadros analizados</span></div>");
			sb.AppendLine($"<div class=\"stat\"><b>{_scale}×</b><span>escala · tile HD de {hdTile}×{hdTile} px</span></div>");
			sb.AppendLine($"<div class=\"stat\"><b>{_packTiles.Count:N0}</b><span>imágenes de tile a dibujar ({spriteCombos:N0} sprites, {bgCombos:N0} fondo)</span></div>");
			sb.AppendLine($"<div class=\"stat\"><b>{groups.Count:N0}</b><span>objetos (metasprites) detectados</span></div>");
			if(_rom.HasChrRom) {
				double coverage = nonBlank > 0 ? 100.0 * seen.Count / nonBlank : 0;
				sb.AppendLine($"<div class=\"stat\"><b>{coverage:0.0}%</b><span>de los tiles del cartucho vistos jugando ({seen.Count:N0} de {nonBlank:N0})</span></div>");
				int inPack = InPackChrTiles().Count;
				if(inPack > seen.Count) {
					double packCoverage = nonBlank > 0 ? 100.0 * inPack / nonBlank : 0;
					sb.AppendLine($"<div class=\"stat\"><b>{packCoverage:0.0}%</b><span>de los tiles del cartucho incluidos en el pack ({inPack:N0}); los no vistos llevan una paleta estimada</span></div>");
				}
			}
			sb.AppendLine("</div>");

			sb.AppendLine("<h2>Qué PNG dibujar</h2>");
			sb.AppendLine($"<p>Cada tile original de 8×8 se reemplaza por una imagen de <b>{hdTile}×{hdTile} px</b>. Las hojas del pack ({_packImages.Count}) miden <b>{128 * _scale}×{128 * _scale} px</b> cada una (16×16 tiles). " +
				"Para trabajar por personaje en vez de por tile, usa las plantillas de abajo: cada una ya tiene el tamaño HD final; redibújala encima respetando la cuadrícula.</p>");
			if(groups.Count > 0) {
				sb.AppendLine("<p><b>Hoja de sprites completa:</b> <a href=\"sheet_objects.png\"><code>sheet_objects.png</code></a> reúne todos los objetos de la tabla a tamaño HD " +
					"(su distribución está en <code>sheet_objects.json</code>). Es la hoja para redibujar y cargar después en HD Studio.</p>");
			}

			sb.AppendLine("<div class=\"wrap\"><table><thead><tr><th>#</th><th>Vista</th><th>Plantilla PNG</th><th>Original</th><th>Tamaño HD</th><th>Tiles</th><th>Paletas</th><th>Presencia</th></tr></thead><tbody>");
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

			if(hasCoverageImage) {
				sb.AppendLine("<h2>Cobertura del cartucho</h2>");
				sb.AppendLine("<p>Todos los tiles del CHR-ROM. En <b style=\"color:var(--warn)\">rojo</b> los que todavía no aparecieron en pantalla: hace falta jugar (o reproducir una partida) por las zonas donde se usan.</p>");
				sb.AppendLine("<img class=\"px\" src=\"chr_coverage.png\" alt=\"Cobertura de tiles del CHR-ROM\" style=\"max-width:100%;height:auto\">");
			}

			sb.AppendLine("<h2>Uso de pantalla</h2>");
			ulong total = totalSpritePixels + totalBgPixels;
			if(total > 0) {
				sb.AppendLine($"<p>Fondo: {100.0 * totalBgPixels / total:0.#}% de los píxeles dibujados · Sprites: {100.0 * totalSpritePixels / total:0.#}%.</p>");
			}
			sb.AppendLine($"<p class=\"muted\">Archivos: <code>hires.txt</code> y hojas <code>Chr_*.png</code> en la carpeta del pack; datos crudos en <code>{StudioFolderName}/objects.txt</code>, <code>tiles.txt</code> y <code>report.json</code>. Generado {DateTime.Now:yyyy-MM-dd HH:mm}.</p>");
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
