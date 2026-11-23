using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Reads a game disassembly (NESASM / Southbird style .asm files) and uses it to know exactly which
	/// cartridge tiles the game's levels reference, so the report can say how much of the game is captured
	/// instead of guessing. Nothing is assembled: the data labels are parsed from the .asm files and located
	/// inside the ROM by searching their bytes, which works even when the disassembly is for another revision.
	/// </summary>
	internal sealed class HdStudioDisasm
	{
		public HashSet<int> ReferencedTiles { get; private set; } = new();
		public int LevelsParsed { get; private set; }
		public int TilesetsFound { get; private set; }
		public string GameName { get; private set; } = "";
		public string? Error { get; private set; }

		private static readonly Regex _labelRegex = new(@"^([A-Za-z_.][\w.]*)\s*:", RegexOptions.Compiled);
		private static readonly Regex _byteRegex = new(@"(?i)^\.(byte|db)\s+(.*)$", RegexOptions.Compiled);

		/// <summary>Loads a disassembly from the main .asm file or from its folder</summary>
		public static HdStudioDisasm Load(string path, string romPath, uint[] nesPalette)
		{
			HdStudioDisasm result = new();
			try {
				string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
				if(folder == null || !Directory.Exists(folder)) {
					result.Error = "disassembly folder not found";
					return result;
				}

				string[] asmFiles = Directory.GetFiles(folder, "*.asm", SearchOption.AllDirectories);
				if(asmFiles.Length == 0) {
					result.Error = "no .asm files in " + folder;
					return result;
				}
				result.GameName = new DirectoryInfo(folder).Name;

				byte[] rom = File.ReadAllBytes(romPath);
				int headerSize = rom.Length >= 16 && rom[0] == 'N' && rom[1] == 'E' && rom[2] == 'S' && rom[3] == 0x1A ? 16 + ((rom[6] & 0x04) != 0 ? 512 : 0) : 0;
				int prgSize = rom.Length >= 16 ? rom[4] * 0x4000 : 0;
				ReadOnlySpan<byte> prg = rom.AsSpan(headerSize, Math.Min(prgSize, rom.Length - headerSize));

				//Tile layouts: 1 KB each, 256 tiles of 16x16 (four 256-byte blocks per quadrant)
				Dictionary<int, byte[]> layoutByTileset = new();
				int layoutCount = 0;
				foreach(string file in asmFiles) {
					foreach(string line in File.ReadLines(file)) {
						Match m = Regex.Match(line, @"^(Tile_Layout_([A-Za-z0-9_]+)):");
						if(!m.Success) {
							continue;
						}

						byte[]? data = ParseBytes(file, m.Groups[1].Value, 1024);
						if(data == null || data.Length != 1024 || prg.IndexOf(data.AsSpan(0, Math.Min(48, data.Length))) < 0) {
							continue;
						}

						layoutCount++;
						foreach(Match ts in Regex.Matches(m.Groups[2].Value, @"TS(\d+)")) {
							if(int.TryParse(ts.Groups[1].Value, out int tileset)) {
								layoutByTileset[tileset] = data;
							}
						}
					}
				}
				result.TilesetsFound = layoutCount;
				if(layoutCount == 0) {
					result.Error = "no tile layout tables found in the ROM (is this the right disassembly?)";
					return result;
				}

				//CHR bank per level (Level_BG_Pages1), and the level headers (tileset + BG bank)
				string? prg030 = asmFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("prg030.asm", StringComparison.OrdinalIgnoreCase));
				byte[]? pages = prg030 != null ? ParseBytes(prg030, "Level_BG_Pages1", 64) : null;
				if(pages == null) {
					result.Error = "Level_BG_Pages1 not found";
					return result;
				}

				Regex tilesetRegex = new(@"LEVEL3_TILESET_(\d+)");
				Regex bankRegex = new(@"LEVEL4_BGBANK_INDEX\((\d+)\)");
				foreach(string file in asmFiles) {
					string text = File.ReadAllText(file);
					Match ts = tilesetRegex.Match(text);
					Match bank = bankRegex.Match(text);
					if(!ts.Success || !bank.Success) {
						continue;
					}
					if(!int.TryParse(ts.Groups[1].Value, out int tileset) || !int.TryParse(bank.Groups[1].Value, out int bgBank)) {
						continue;
					}
					if(!layoutByTileset.TryGetValue(tileset, out byte[]? layout) || bgBank >= pages.Length) {
						continue;
					}

					result.LevelsParsed++;
					AddLayoutTiles(result.ReferencedTiles, layout, pages[bgBank]);
				}
			} catch(Exception ex) {
				result.Error = ex.Message;
			}
			return result;
		}

		private static void AddLayoutTiles(HashSet<int> referenced, byte[] layout, byte page)
		{
			int baseTile = page * 64;
			for(int quadrant = 0; quadrant < 4; quadrant++) {
				int offset = quadrant * 256;
				for(int i = 0; i < 256; i++) {
					referenced.Add(baseTile + layout[offset + i]);
				}
			}
		}

		/// <summary>Assembles the .byte/.db data of a label (no 6502 code is needed: the data blocks are plain bytes)</summary>
		private static byte[]? ParseBytes(string file, string label, int maxBytes)
		{
			string[] lines = File.ReadAllLines(file);
			int start = -1;
			for(int i = 0; i < lines.Length; i++) {
				if(_labelRegex.Match(lines[i].TrimStart()).Groups[1].Value == label) {
					start = i + 1;
					break;
				}
			}
			if(start < 0) {
				return null;
			}

			List<byte> data = new();
			for(int i = start; i < lines.Length; i++) {
				string line = lines[i];
				int comment = line.IndexOf(';');
				if(comment >= 0) {
					line = line.Substring(0, comment);
				}
				line = line.Trim();
				if(line.Length == 0) {
					continue;
				}
				if(_labelRegex.IsMatch(line) || line.StartsWith(".org", StringComparison.OrdinalIgnoreCase) || line.StartsWith(".incbin", StringComparison.OrdinalIgnoreCase)) {
					break;
				}

				Match m = _byteRegex.Match(line);
				if(!m.Success) {
					break;
				}
				foreach(string value in m.Groups[2].Value.Split(',')) {
					string v = value.Trim().TrimStart('$');
					if(v.Length > 0 && byte.TryParse(v, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte b)) {
						data.Add(b);
					}
				}
				if(data.Count >= maxBytes) {
					break;
				}
			}
			return data.ToArray();
		}
	}
}
