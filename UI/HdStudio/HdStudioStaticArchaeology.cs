using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Mesen.HdStudio
{
	internal sealed class HdStudioArchaeologyOptions
	{
		public string Mode = "deep";
		public bool IncludeStructural = true;
		public bool IncludePrgCandidates = true;
		public bool UseExistingRuntimeEvidence = true;
		public bool RunDynamicConfirmation = false;
		public int DynamicFrames = 1800;
		public int MaxAssets = 256;
		public int MaxCandidateStreams = 128;
		public int MaxPaletteCandidates = 128;
		public int MaxInstructions = 600000;
		public int MaxSecondsPerRom = 180;
		public int ThumbnailScale = 4;
		public bool ExportCatalog = true;
		public bool MergeIntoAssets = true;
		public bool Force = false;
		public string EngineFingerprint = "";
		public string EvidencePackFolder = "";
		public string EvidenceMapPath = "";
		public ManualResetEventSlim? PauseGate = null;

		public HdStudioArchaeologyOptions Clone()
		{
			return new HdStudioArchaeologyOptions() {
				Mode = Mode,
				IncludeStructural = IncludeStructural,
				IncludePrgCandidates = IncludePrgCandidates,
				UseExistingRuntimeEvidence = UseExistingRuntimeEvidence,
				RunDynamicConfirmation = RunDynamicConfirmation,
				DynamicFrames = DynamicFrames,
				MaxAssets = MaxAssets,
				MaxCandidateStreams = MaxCandidateStreams,
				MaxPaletteCandidates = MaxPaletteCandidates,
				MaxInstructions = MaxInstructions,
				MaxSecondsPerRom = MaxSecondsPerRom,
				ThumbnailScale = ThumbnailScale,
				ExportCatalog = ExportCatalog,
				MergeIntoAssets = MergeIntoAssets,
				Force = Force,
				EngineFingerprint = EngineFingerprint,
				EvidencePackFolder = EvidencePackFolder,
				EvidenceMapPath = EvidenceMapPath,
				PauseGate = PauseGate
			};
		}

		public void WaitIfPaused(CancellationToken token)
		{
			PauseGate?.Wait(token);
		}

		public static HdStudioArchaeologyOptions ForMode(string mode)
		{
			HdStudioArchaeologyOptions options = new() { Mode = NormalizeMode(mode) };
			switch(options.Mode) {
				case "quick":
					options.MaxAssets = 64;
					options.MaxCandidateStreams = 16;
					options.MaxPaletteCandidates = 32;
					options.MaxInstructions = 150000;
					options.MaxSecondsPerRom = 60;
					break;
				case "exhaustive":
					options.MaxAssets = 1024;
					options.MaxCandidateStreams = 512;
					options.MaxPaletteCandidates = 512;
					options.MaxInstructions = 1800000;
					options.MaxSecondsPerRom = 600;
					break;
				default:
					options.MaxAssets = 256;
					options.MaxCandidateStreams = 128;
					options.MaxPaletteCandidates = 128;
					options.MaxInstructions = 600000;
					options.MaxSecondsPerRom = 180;
					break;
			}
			return options;
		}

		public static string NormalizeMode(string mode)
		{
			return mode.ToLowerInvariant() switch {
				"quick" => "quick",
				"exhaustive" => "exhaustive",
				_ => "deep"
			};
		}
	}

	internal sealed class HdStudioArchaeologyProgress
	{
		public string Phase = "";
		public int PhaseIndex;
		public int PhaseCount;
		public double Percent;
		public string Message = "";
		public string StopReason = "";
		public double ElapsedSeconds;
		public int UniquePatterns;
		public int ObservedPatterns;
		public int Assets;
		public int Gaps;
	}

	internal sealed class HdStudioArchaeologyResult
	{
		public bool Success;
		public string StopReason = "";
		public string RomPath = "";
		public string RomSha256 = "";
		public string PackFolder = "";
		public string GraphPath = "";
		public string CatalogPath = "";
		public string AssetsPath = "";
		public string EngineFingerprint = "";
		public string RunFingerprint = "";
		public double Seconds;
		public int ChrBanks;
		public int ChrPages;
		public int ChrTiles;
		public int UniquePatterns;
		public int BlankPatterns;
		public int DuplicateOccurrences;
		public int PrgBanks;
		public int CodeBlocks;
		public int Instructions;
		public int PpuWrites;
		public int OamDmaWrites;
		public int MapperWrites;
		public int DataReferences;
		public int PointerTables;
		public int PaletteCandidates;
		public int StreamCandidates;
		public int RuntimeObservedPatterns;
		public int RuntimeObservedChrRam;
		public int RuntimeObservations;
		public int AssetsExposed;
		public int Gaps;
		public int UnresolvedIndirectJumps;
		public int UnresolvedCrossBankTargets;
		public int RejectedCandidates;
		public readonly List<string> Errors = new();
		public readonly List<string> Notes = new();

		public int UnobservedPatterns => Math.Max(0, UniquePatterns - RuntimeObservedPatterns);
		public int EligibleObservedAssets => RuntimeObservedPatterns + RuntimeObservedChrRam;
	}

	internal sealed class HdStudioPaletteCandidate
	{
		public int PhysicalOffset;
		public int Length;
		public double Score;
		public readonly List<string> Palettes = new();
		public string NodeId = "";
	}

	internal sealed class HdStudioPrgStreamCandidate
	{
		public int PhysicalOffset;
		public int TileCount;
		public double MeanScore;
		public double CodeOverlap;
		public int UniqueTiles;
		public string Hash = "";
		public string Preview = "";
		public string NodeId = "";
	}

	internal sealed class HdStudioRuntimeObservation
	{
		public string Key = "";
		public string Palette = "";
		public int PixelCount;
		public bool ChrRam;
	}

	internal sealed class HdStudioRuntimeEvidence
	{
		public bool HasCapture;
		public string CaptureText = "";
		public int Frames;
		public int Scale;
		public bool ChrRam;
		public string CaptureRom = "";
		public string SceneNodeId = "";
		public readonly List<HdStudioRuntimeObservation> Observations = new();
		public readonly Dictionary<string, int> PaletteUses = new(StringComparer.OrdinalIgnoreCase);
		public int UnseenPrgPalette;
		public int UnseenNeighbourPalette;
		public int UnseenDefaultPalette;

		public static HdStudioRuntimeEvidence Load(string packFolder)
		{
			HdStudioRuntimeEvidence evidence = new();
			string studio = Path.Combine(packFolder, HdStudioReport.StudioFolderName);
			string capture = Path.Combine(studio, "capture.txt");
			string tiles = Path.Combine(studio, "tiles.txt");
			string palettes = Path.Combine(studio, "palettes.txt");
			string unseen = Path.Combine(studio, "unseen.txt");
			if(File.Exists(capture)) {
				evidence.HasCapture = true;
				evidence.CaptureText = File.ReadAllText(capture);
				foreach(string line in evidence.CaptureText.Split('\n')) {
					string[] parts = line.Trim().Split('=', 2);
					if(parts.Length != 2) {
						continue;
					}
					switch(parts[0]) {
						case "frames":
							int.TryParse(parts[1], out evidence.Frames);
							break;
						case "scale":
							int.TryParse(parts[1], out evidence.Scale);
							break;
						case "chrRam":
							evidence.ChrRam = parts[1].Trim() == "1";
							break;
						case "rom":
							evidence.CaptureRom = parts[1].Trim();
							break;
					}
				}
			}
			if(File.Exists(tiles)) {
				evidence.HasCapture = true;
				foreach(string line in File.ReadLines(tiles)) {
					if(line.Length == 0 || line[0] == '#') {
						continue;
					}
					string[] parts = line.Split('\t');
					if(parts.Length < 3) {
						continue;
					}
					string key = parts[0].Trim();
					string palette = parts[1].Trim().ToUpperInvariant();
					if(!int.TryParse(parts[2], out int pixels) || key.Length == 0 || palette.Length != 8) {
						continue;
					}
					evidence.Observations.Add(new HdStudioRuntimeObservation() {
						Key = key,
						Palette = palette,
						PixelCount = pixels,
						ChrRam = key.Length == 32
					});
				}
				evidence.Observations.Sort((a, b) => b.PixelCount.CompareTo(a.PixelCount));
			}
			if(File.Exists(palettes)) {
				foreach(string line in File.ReadLines(palettes)) {
					if(line.Length == 0 || line[0] == '#') {
						continue;
					}
					string[] parts = line.Split('\t');
					if(parts.Length >= 2 && int.TryParse(parts[1], out int uses) && parts[0].Trim().Length == 8) {
						evidence.PaletteUses[parts[0].Trim().ToUpperInvariant()] = uses;
					}
				}
			}
			if(File.Exists(unseen)) {
				foreach(string line in File.ReadLines(unseen)) {
					string[] parts = line.Trim().Split('=', 2);
					if(parts.Length != 2 || !int.TryParse(parts[1], out int count)) {
						continue;
					}
					switch(parts[0]) {
						case "prgPalette": evidence.UnseenPrgPalette = count; break;
						case "neighbourPalette": evidence.UnseenNeighbourPalette = count; break;
						case "defaultPalette": evidence.UnseenDefaultPalette = count; break;
					}
				}
			}
			return evidence;
		}
	}

	/// <summary>
	/// Static-first archaeology: physical CHR enumeration, PRG palette/pattern
	/// candidates and runtime-evidence correlation. This stage never runs the
	/// emulator and never changes the ROM.
	/// </summary>
	internal static class HdStudioStaticArchaeology
	{
		public static void AnalyzeChr(HdStudioRomImage rom, HdStudioEvidenceGraph graph, HdStudioArchaeologyOptions options,
			HdStudioArchaeologyResult result, Dictionary<int, HdStudioEvidenceNode> tileNodes, CancellationToken cancellation)
		{
			HdStudioEvidenceNode romNode = graph.AddNode("rom", "identity", rom.FileName, "raw", 1.0, "certain",
				"iNES/NES 2.0 header parse; SHA-256 identity");
			romNode.Properties["sha256"] = rom.Sha256;
			romNode.Properties["sha1"] = rom.Sha1;
			romNode.Properties["mapper"] = HdStudioRomImage.DescribeMapper(rom.Mapper, rom.Submapper);
			romNode.Properties["prgBytes"] = rom.Prg.Length.ToString(CultureInfo.InvariantCulture);
			romNode.Properties["chrBytes"] = rom.Chr.Length.ToString(CultureInfo.InvariantCulture);
			romNode.Properties["chrMode"] = rom.ChrMode;
			romNode.Properties["nes20"] = rom.Nes20 ? "true" : "false";

			if(!rom.HasChrRom) {
				graph.Notes.Add("CHR-RAM cartridge: no physical CHR-ROM bank exists. Static extraction cannot claim the runtime pattern identity; upload paths are a dynamic gap.");
				graph.AddGap("chr-ram-upload-unknown", "CHR-RAM content is produced at runtime; the ROM source/upload path is not statically established",
					"targeted-chr-ram-upload-trace", 1.0, 4.0, new[] { romNode.Id });
				result.Notes.Add("CHR-RAM cartridge; static CHR inventory is not applicable.");
				return;
			}

			int blankCount = 0;
			int duplicateCount = 0;
			int uniqueCount = 0;
			int globalTile = 0;
			List<HdStudioChrPage> pages = rom.GetChrPages();
			Dictionary<int, HdStudioEvidenceNode> pageNodes = new();
			Dictionary<int, HdStudioEvidenceNode> bankNodes = new();
			Dictionary<string, int> firstSeen = new(StringComparer.Ordinal);

			foreach(HdStudioChrPage page in pages) {
				if(cancellation.IsCancellationRequested) {
					return;
				}
				options.WaitIfPaused(cancellation);
				HdStudioEvidenceNode pageNode = graph.AddNode("chrPage", "page:" + page.Index,
					"CHR page " + page.Index + " (bank " + page.BankIndex + ", $" + (page.Index * 0x400).ToString("X4", CultureInfo.InvariantCulture) + ")",
					"raw", 1.0, "certain", "physical CHR-ROM 1 KB page");
				pageNode.Properties["bank"] = page.BankIndex.ToString(CultureInfo.InvariantCulture);
				pageNode.Properties["physicalOffset"] = "0x" + page.PhysicalOffset.ToString("X", CultureInfo.InvariantCulture);
				pageNode.Properties["tiles"] = page.Tiles.ToString(CultureInfo.InvariantCulture);
				pageNode.Properties["sha256"] = HashBytes(rom.Chr, page.PhysicalOffset, Math.Min(1024, rom.Chr.Length - page.PhysicalOffset));
				pageNodes[page.Index] = pageNode;

				if(!bankNodes.ContainsKey(page.BankIndex)) {
					HdStudioEvidenceNode bankNode = graph.AddNode("chrBank", "bank:" + page.BankIndex,
						"CHR bank " + page.BankIndex + " (physical 8 KB)",
						"raw", 1.0, "certain", "physical CHR-ROM 8 KB bank; mapper window is a separate hypothesis");
					bankNode.Properties["physicalOffset"] = "0x" + (page.BankIndex * 8192).ToString("X", CultureInfo.InvariantCulture);
					bankNodes[page.BankIndex] = bankNode;
				}
				graph.AddEdge(bankNodes[page.BankIndex].Id, pageNode.Id, "contains", "raw", 1.0, "page belongs to physical CHR bank");

				int pageUnique = 0;
				int pageBlank = 0;
				int pageDuplicate = 0;
				StringBuilder pageHashes = new();
				for(int i = 0; i < page.Tiles; i++, globalTile++) {
					int tileIndex = page.Index * 64 + i;
					byte[] tile = rom.GetChrTile(tileIndex);
					bool blank = tile.All(b => b == 0);
					string hash = HashBytes(tile, 0, 16);
					if(blank) {
						blankCount++;
						pageBlank++;
						HdStudioEvidenceNode blankNode = graph.AddNode("pattern", "content:blank",
							"Blank 8x8 pattern", "raw", 1.0, "certain", "all 16 CHR bytes are zero");
						blankNode.Properties["isBlank"] = "true";
						blankNode.Properties["occurrenceCount"] = (int.TryParse(blankNode.Properties.GetValueOrDefault("occurrenceCount", "0"), out int blankOcc) ? blankOcc + 1 : 1).ToString(CultureInfo.InvariantCulture);
						blankNode.Properties["tileIndices"] = AppendCapped(blankNode.Properties.GetValueOrDefault("tileIndices", ""), tileIndex, 32);
						tileNodes[tileIndex] = blankNode;
						continue;
					}
					if(firstSeen.TryGetValue(hash, out int firstIndex)) {
						duplicateCount++;
						pageDuplicate++;
						if(tileNodes.TryGetValue(firstIndex, out HdStudioEvidenceNode? existing)) {
							existing.Properties["occurrenceCount"] = (int.TryParse(existing.Properties.GetValueOrDefault("occurrenceCount", "1"), out int occ) ? occ + 1 : 1).ToString(CultureInfo.InvariantCulture);
							existing.Properties["tileIndices"] = AppendCapped(existing.Properties.GetValueOrDefault("tileIndices", ""), tileIndex, 48);
							existing.ObservedOccurrences++;
							tileNodes[tileIndex] = existing;
							graph.AddEdge(pageNode.Id, existing.Id, "contains", "raw", 1.0, "duplicate pattern occurrence in this CHR page");
						}
						continue;
					}

					firstSeen[hash] = tileIndex;
					uniqueCount++;
					pageUnique++;
					int colors = ColorMask(tile);
					HdStudioEvidenceNode node = graph.AddNode("pattern", "content:" + hash,
						"CHR pattern " + hash.Substring(0, 8) + " (bank " + page.BankIndex + ")",
						"raw", 1.0, "certain", "physical 8x8 CHR-ROM pattern; two-bitplane tile bytes are present in the ROM");
					node.Properties["sha256"] = FullHash(tile, 16);
					node.Properties["firstBank"] = page.BankIndex.ToString(CultureInfo.InvariantCulture);
					node.Properties["firstPage"] = page.Index.ToString(CultureInfo.InvariantCulture);
					node.Properties["firstPhysicalOffset"] = "0x" + (tileIndex * 16).ToString("X", CultureInfo.InvariantCulture);
					node.Properties["firstTileIndex"] = tileIndex.ToString(CultureInfo.InvariantCulture);
					node.Properties["tileIndices"] = tileIndex.ToString(CultureInfo.InvariantCulture);
					node.Properties["occurrenceCount"] = "1";
					node.Properties["colorsUsed"] = colors.ToString(CultureInfo.InvariantCulture);
					node.Properties["opaquePixels"] = OpaquePixels(tile).ToString(CultureInfo.InvariantCulture);
					node.Properties["isBlank"] = "false";
					node.Properties["semanticLabel"] = "unclassified";
					tileNodes[tileIndex] = node;
					graph.AddEdge(pageNode.Id, node.Id, "contains", "raw", 1.0, "first observed occurrence in this CHR page");
					pageHashes.Append(hash, 0, 4);
				}
				pageNode.Properties["uniquePatterns"] = pageUnique.ToString(CultureInfo.InvariantCulture);
				pageNode.Properties["blankPatterns"] = pageBlank.ToString(CultureInfo.InvariantCulture);
				pageNode.Properties["duplicateOccurrences"] = pageDuplicate.ToString(CultureInfo.InvariantCulture);
				pageNode.Properties["patternHashSummary"] = pageHashes.ToString();
			}

			foreach(KeyValuePair<int, HdStudioEvidenceNode> bank in bankNodes) {
				bank.Value.Properties["uniquePatterns"] = graph.Nodes.Values.Count(n => n.Type == "pattern" &&
					n.Properties.GetValueOrDefault("firstBank") == bank.Key.ToString(CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
			}

			result.ChrBanks = bankNodes.Count;
			result.ChrPages = pages.Count;
			result.ChrTiles = rom.ChrTileCount;
			result.UniquePatterns = uniqueCount;
			result.BlankPatterns = blankCount;
			result.DuplicateOccurrences = duplicateCount;
			graph.Notes.Add("CHR inventory: " + uniqueCount.ToString(CultureInfo.InvariantCulture) + " unique nonblank patterns, " +
				blankCount.ToString(CultureInfo.InvariantCulture) + " blank occurrences, " + duplicateCount.ToString(CultureInfo.InvariantCulture) + " duplicate occurrences. Tile counts are not semantic objects.");
			graph.AddGap("composition-not-reconstructed", "Physical CHR patterns are enumerated, but semantic compositions and nametable placement still require structural/runtime evidence",
				"structural-composition-inference", 0.8, 5.0, Array.Empty<string>());
			graph.AddGap("animation-not-reconstructed", "No animation sequence is claimed from CHR tiles alone; successor frames need structural or observed evidence",
				"observed-animation-sequence", 0.7, 6.0, Array.Empty<string>());
			graph.AddGap("map-not-reconstructed", "No universal NES map format is assumed; a map/decoder is only reported when its structure is validated",
				"structural-map-inference", 0.7, 8.0, Array.Empty<string>());
		}

		public static List<HdStudioPaletteCandidate> AnalyzePrgPalettes(HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			HdStudioArchaeologyOptions options, HdStudioArchaeologyResult result, CancellationToken cancellation)
		{
			List<HdStudioPaletteCandidate> candidates = new();
			byte[] prg = rom.Prg;
			int limit = options.MaxPaletteCandidates;
			int offset = 0;
			while(offset + 16 <= prg.Length && candidates.Count < limit) {
				if((offset & 0x3FF) == 0 && cancellation.IsCancellationRequested) {
					break;
				}
				if((offset & 0xFFF) == 0) {
					options.WaitIfPaused(cancellation);
				}
				int length = 0;
				double score = 0;
				if(IsPaletteWindow(prg, offset, 16, out double score16)) {
					length = 16;
					score = score16;
					if(offset + 32 <= prg.Length && IsPaletteWindow(prg, offset + 16, 16, out double score32)) {
						length = 32;
						score = (score16 + score32) / 2;
					}
				}
				if(length == 0) {
					offset++;
					continue;
				}
				if(score < 0.55) {
					offset++;
					continue;
				}
				HdStudioPaletteCandidate candidate = new() {
					PhysicalOffset = offset,
					Length = length,
					Score = score
				};
				for(int p = 0; p < length; p += 4) {
					candidate.Palettes.Add(
						prg[offset + p].ToString("X2", CultureInfo.InvariantCulture) +
						prg[offset + p + 1].ToString("X2", CultureInfo.InvariantCulture) +
						prg[offset + p + 2].ToString("X2", CultureInfo.InvariantCulture) +
						prg[offset + p + 3].ToString("X2", CultureInfo.InvariantCulture));
				}
				HdStudioEvidenceNode node = graph.AddNode("candidatePaletteTable", "prg:" + offset,
					"Candidate palette table at PRG 0x" + offset.ToString("X", CultureInfo.InvariantCulture),
					"inferred", Math.Round(0.25 + score * 0.4, 4), "heuristic",
					"16/32-byte window with all values below 0x40, repeated backdrop and four-colour groups; usage is not proven");
				node.Properties["physicalOffset"] = "0x" + offset.ToString("X", CultureInfo.InvariantCulture);
				node.Properties["length"] = length.ToString(CultureInfo.InvariantCulture);
				node.Properties["palettes"] = string.Join(",", candidate.Palettes);
				node.Properties["score"] = score.ToString("0.000", CultureInfo.InvariantCulture);
				candidate.NodeId = node.Id;
				candidates.Add(candidate);
				offset += Math.Max(16, length);
			}
			result.PaletteCandidates = candidates.Count;
			if(candidates.Count > 0) {
				graph.AddGap("palette-table-unvalidated", candidates.Count.ToString(CultureInfo.InvariantCulture) +
					" palette-like PRG tables were found; their actual PPU palette assignment still needs runtime confirmation",
					"observed-palette-assignment", Math.Min(1.0, candidates.Count / 32.0), 3.0,
					candidates.Take(8).Select(c => c.NodeId));
			}
			return candidates;
		}

		public static List<HdStudioPrgStreamCandidate> AnalyzePrgTileStreams(HdStudioRomImage rom, HdStudioEvidenceGraph graph,
			HdStudioArchaeologyOptions options, HdStudioArchaeologyResult result, List<HdStudioCodeBlock> codeBlocks,
			string previewFolder, uint[]? palette, CancellationToken cancellation)
		{
			List<HdStudioPrgStreamCandidate> streams = new();
			byte[] prg = rom.Prg;
			if(prg.Length < 96) {
				return streams;
			}
			//Reachable code is evidence against treating the same bytes as graphics.
			List<(int Offset, int End)> codeRanges = new();
			foreach(HdStudioPrgWindow window in rom.GetPrgWindows()) {
				foreach(HdStudioCodeBlock block in codeBlocks.Where(b => b.WindowIndex == window.Index)) {
					int start = window.PhysicalOffset + (block.StartCpu - window.CpuBase);
					int end = window.PhysicalOffset + (block.EndCpu - window.CpuBase);
					if(start >= 0 && end <= prg.Length && end > start) {
						codeRanges.Add((start, end));
					}
				}
			}
			codeRanges.Sort((a, b) => a.Offset.CompareTo(b.Offset));

			int offset = 0;
			int maxStreams = options.MaxCandidateStreams;
			while(offset + 96 <= prg.Length && streams.Count < maxStreams) {
				if((offset & 0x7FFF) == 0 && cancellation.IsCancellationRequested) {
					break;
				}
				if((offset & 0x7FFF) == 0) {
					options.WaitIfPaused(cancellation);
				}
				double score = TileScore(prg, offset);
				if(score < 0.5) {
					offset += 16;
					continue;
				}
				int start = offset;
				double sum = 0;
				int count = 0;
				HashSet<string> hashes = new(StringComparer.Ordinal);
				int nonblank = 0;
				while(offset + 16 <= prg.Length && count < 256) {
					double s = TileScore(prg, offset);
					if(s < 0.43) {
						break;
					}
					sum += s;
					count++;
					if(s >= 0.55) {
						nonblank++;
					}
					string h = HashBytes(prg, offset, 16);
					hashes.Add(h);
					offset += 16;
				}
				if(count < 6 || hashes.Count < 4) {
					offset = Math.Max(offset, start + 16);
					continue;
				}
				double mean = sum / count;
				if(mean < 0.53 || nonblank < 4) {
					continue;
				}
				int overlap = 0;
				for(int i = 0; i < count; i++) {
					int blockOffset = start + i * 16;
					if(OverlapsAny(codeRanges, blockOffset, 16)) {
						overlap++;
					}
				}
				double codeOverlap = (double)overlap / count;
				if(codeOverlap > 0.20) {
					result.RejectedCandidates++;
					continue;
				}
				string hash = HashBytes(prg, start, count * 16);
				HdStudioPrgStreamCandidate candidate = new() {
					PhysicalOffset = start,
					TileCount = count,
					MeanScore = mean,
					CodeOverlap = codeOverlap,
					UniqueTiles = hashes.Count,
					Hash = hash
				};
				HdStudioEvidenceNode node = graph.AddNode("candidatePatternStream", "prg:" + start + ":" + hash.Substring(0, 12),
					"PRG candidate 2bpp stream at 0x" + start.ToString("X", CultureInfo.InvariantCulture) + " (" + count.ToString(CultureInfo.InvariantCulture) + " tiles)",
					"inferred", Math.Round(0.25 + mean * 0.3, 4), "heuristic",
					"consecutive 16-byte blocks with tile-like bitplanes; " + (codeOverlap * 100).ToString("0.0", CultureInfo.InvariantCulture) + "% reachable-code overlap; this is not a semantic asset");
				node.Properties["physicalOffset"] = "0x" + start.ToString("X", CultureInfo.InvariantCulture);
				node.Properties["tiles"] = count.ToString(CultureInfo.InvariantCulture);
				node.Properties["uniqueTiles"] = hashes.Count.ToString(CultureInfo.InvariantCulture);
				node.Properties["meanScore"] = mean.ToString("0.000", CultureInfo.InvariantCulture);
				node.Properties["codeOverlap"] = codeOverlap.ToString("0.000", CultureInfo.InvariantCulture);
				node.Properties["contentHash"] = hash;
				node.Properties["class"] = "unconfirmed-pattern-stream";
				candidate.NodeId = node.Id;
				if(!string.IsNullOrWhiteSpace(previewFolder)) {
					string preview = RenderStreamPreview(previewFolder, "stream-" + hash.Substring(0, 12) + ".png", prg, start, Math.Min(count, 32), palette);
					if(preview.Length > 0) {
						candidate.Preview = preview;
						node.Properties["preview"] = preview;
					}
				}
				streams.Add(candidate);
				offset = start + count * 16;
			}
			result.StreamCandidates = streams.Count;
			if(streams.Count > 0) {
				graph.AddGap("prg-stream-unvalidated", streams.Count.ToString(CultureInfo.InvariantCulture) +
					" tile-like PRG streams are candidates only; none is promoted to a replaceable pattern without mapper/CHR association or runtime confirmation",
					"targeted-prg-stream-confirmation", Math.Min(1.0, streams.Count / 16.0), 4.0,
					streams.Take(8).Select(s => s.NodeId));
			}
			return streams;
		}

		public static void CorrelateRuntime(HdStudioRomImage rom, HdStudioEvidenceGraph graph, HdStudioRuntimeEvidence runtime,
			HdStudioArchaeologyOptions options, HdStudioArchaeologyResult result, Dictionary<int, HdStudioEvidenceNode> tileNodes,
			CancellationToken cancellation)
		{
			if(!runtime.HasCapture) {
				graph.AddGap("runtime-confirmation-missing", "No capture/tiles evidence exists for this project; every static candidate is still unconfirmed for replacement",
					"run-targeted-capture", 1.0, 5.0, Array.Empty<string>());
				return;
			}
			HdStudioEvidenceNode scene = graph.AddNode("scene", "capture:" + HdStudioEvidenceGraph.StableHash(runtime.CaptureText),
				"Observed capture (" + runtime.Frames.ToString("N0", CultureInfo.InvariantCulture) + " frames)",
				"runtime-observed", 0.9, "heuristic", "capture.txt written by the existing deterministic capture pipeline");
			scene.Properties["frames"] = runtime.Frames.ToString(CultureInfo.InvariantCulture);
			scene.Properties["scale"] = runtime.Scale.ToString(CultureInfo.InvariantCulture);
			scene.Properties["chrRam"] = runtime.ChrRam ? "true" : "false";
			scene.Properties["romName"] = runtime.CaptureRom;
			runtime.SceneNodeId = scene.Id;

			HashSet<string> observedChrRom = new(StringComparer.Ordinal);
			HashSet<string> observedChrRam = new(StringComparer.Ordinal);
			HashSet<string> observedPalettes = new(StringComparer.OrdinalIgnoreCase);
			foreach(HdStudioRuntimeObservation observation in runtime.Observations) {
				if(cancellation.IsCancellationRequested) {
					return;
				}
				options.WaitIfPaused(cancellation);
				if(observation.ChrRam) {
					observedChrRam.Add(observation.Key);
					HdStudioEvidenceNode ramNode = graph.AddNode("chrRamObservation", "content:" + observation.Key,
						"Observed CHR-RAM content " + observation.Key.Substring(0, 8),
						"runtime-observed", 0.8, "heuristic", "tiles.txt runtime pattern content; static source/upload path unknown");
					ramNode.Properties["tileData"] = observation.Key;
					ramNode.Properties["palette"] = observation.Palette;
					ramNode.Properties["observedPixels"] = observation.PixelCount.ToString(CultureInfo.InvariantCulture);
					ramNode.Properties["chrType"] = "NesChrRam";
					ramNode.ObservedOccurrences++;
					HdStudioEvidenceNode ramObsNode = graph.AddNode("observation", "tiles:" + observation.Key + ":" + observation.Palette,
						"CHR-RAM observation / " + observation.Palette, "runtime-observed", 0.8, "heuristic", "tiles.txt observed pattern+palette");
					ramObsNode.Properties["pixelCount"] = observation.PixelCount.ToString(CultureInfo.InvariantCulture);
					ramObsNode.Properties["palette"] = observation.Palette;
					graph.AddEdge(scene.Id, ramObsNode.Id, "contains", "runtime-observed", 0.9, "observation came from this capture");
					graph.AddEdge(ramObsNode.Id, ramNode.Id, "observed-in", "runtime-observed", 0.8, "runtime content identity");
					observedPalettes.Add(observation.Palette);
					continue;
				}
				if(!int.TryParse(observation.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int tileIndex)) {
					continue;
				}
				if(!tileNodes.TryGetValue(tileIndex, out HdStudioEvidenceNode? pattern) || pattern.Type != "pattern") {
					continue;
				}
				pattern.Status = HdStudioEvidenceGraph.StatusRank("runtime-observed") > HdStudioEvidenceGraph.StatusRank(pattern.Status) ? "runtime-observed" : pattern.Status;
				pattern.Confidence = Math.Max(pattern.Confidence, 0.7);
				pattern.ConfidenceKind = "heuristic";
				pattern.ObservedOccurrences++;
				pattern.Properties["runtimeTileIndex"] = tileIndex.ToString(CultureInfo.InvariantCulture);
				pattern.Properties["observedPixels"] = (int.TryParse(pattern.Properties.GetValueOrDefault("observedPixels", "0"), out int pixelTotal) ? pixelTotal + observation.PixelCount : observation.PixelCount).ToString(CultureInfo.InvariantCulture);
				pattern.Properties["observedPalettes"] = AppendCapped(pattern.Properties.GetValueOrDefault("observedPalettes", ""), observation.Palette, 8, ',');
				HdStudioEvidenceNode obsNode = graph.AddNode("observation", "tiles:" + observation.Key + ":" + observation.Palette,
					"Observed pattern " + observation.Key + " / " + observation.Palette, "runtime-observed", 0.9, "heuristic", "tiles.txt observed pattern+palette");
				obsNode.Properties["pixelCount"] = observation.PixelCount.ToString(CultureInfo.InvariantCulture);
				obsNode.Properties["palette"] = observation.Palette;
				obsNode.Properties["tileIndex"] = tileIndex.ToString(CultureInfo.InvariantCulture);
				graph.AddEdge(scene.Id, obsNode.Id, "contains", "runtime-observed", 0.9, "observation came from this capture");
				graph.AddEdge(obsNode.Id, pattern.Id, "observed-in", "runtime-observed", 0.85, "CHR-ROM tile identity");
				if(pattern.Properties.GetValueOrDefault("isBlank") != "true") {
					observedChrRom.Add(pattern.Id);
				}
				observedPalettes.Add(observation.Palette);
			}
			result.RuntimeObservedPatterns = observedChrRom.Count;
			result.RuntimeObservedChrRam = observedChrRam.Count;
			result.RuntimeObservations = runtime.Observations.Count;

			if(observedChrRom.Count == 0 && rom.HasChrRom) {
				graph.AddGap("capture-no-chr-rom-patterns", "The existing capture did not map any CHR-ROM tile to a static pattern; the run may be menu/blank or the capture is from another ROM revision",
					"verify-capture-rom-and-scene", 0.9, 2.0, new[] { scene.Id });
			}
			if(runtime.UnseenPrgPalette + runtime.UnseenNeighbourPalette + runtime.UnseenDefaultPalette > 0) {
				HdStudioEvidenceNode paletteNode = graph.AddNode("paletteCoverage", "unseen",
					"Unseen CHR palette coverage", "runtime-observed", 0.7, "heuristic", "unseen.txt counts patterns without a proven palette assignment");
				paletteNode.Properties["prgPalette"] = runtime.UnseenPrgPalette.ToString(CultureInfo.InvariantCulture);
				paletteNode.Properties["neighbourPalette"] = runtime.UnseenNeighbourPalette.ToString(CultureInfo.InvariantCulture);
				paletteNode.Properties["defaultPalette"] = runtime.UnseenDefaultPalette.ToString(CultureInfo.InvariantCulture);
			}
			graph.Notes.Add("Runtime correlation: " + result.RuntimeObservedPatterns.ToString(CultureInfo.InvariantCulture) +
				" static CHR-ROM patterns observed, " + result.RuntimeObservedChrRam.ToString(CultureInfo.InvariantCulture) +
				" CHR-RAM contents observed, " + result.RuntimeObservations.ToString(CultureInfo.InvariantCulture) + " pattern+palette rows read from the existing capture.");
			if(result.RuntimeObservedPatterns < result.UniquePatterns && result.UniquePatterns > 0) {
				graph.AddGap("unobserved-static-patterns", (result.UniquePatterns - result.RuntimeObservedPatterns).ToString(CultureInfo.InvariantCulture) +
					" unique static CHR patterns were not observed in the existing capture; absence of observation is not evidence that they are unused",
					"deep-targeted-exploration", 0.6, 6.0, Array.Empty<string>());
			}
		}

		public static void ScheduleStructuralGaps(HdStudioEvidenceGraph graph, HdStudioStructuralResult structural)
		{
			if(structural.UnresolvedIndirectJumps > 0) {
				graph.AddGap("structural-indirect-flow", structural.UnresolvedIndirectJumps.ToString(CultureInfo.InvariantCulture) +
					" indirect jumps remain unresolved by the static walk",
					"read-only-trace-indirect-jump", Math.Min(1.0, structural.UnresolvedIndirectJumps / 8.0), 4.0, Array.Empty<string>());
			}
			if(structural.UnresolvedCrossBankTargets > 0) {
				graph.AddGap("structural-cross-bank-flow", structural.UnresolvedCrossBankTargets.ToString(CultureInfo.InvariantCulture) +
					" direct targets leave the assumed bank window",
					"dynamic-bank-trace", Math.Min(1.0, structural.UnresolvedCrossBankTargets / 16.0), 3.0, Array.Empty<string>());
			}
			if(structural.PointerTables == 0) {
				graph.AddGap("structural-pointer-tables", "No referenced pointer table was validated; compositions/maps/animation cannot be promoted from static bytes alone",
					"structural-table-inference", 0.6, 5.0, Array.Empty<string>());
			}
		}

		private static bool IsPaletteWindow(byte[] prg, int offset, int length, out double score)
		{
			score = 0;
			if(offset < 0 || offset + length > prg.Length) {
				return false;
			}
			HashSet<int> distinct = new();
			for(int i = 0; i < length; i++) {
				int value = prg[offset + i];
				if(value >= 0x40) {
					return false;
				}
				distinct.Add(value);
			}
			if(distinct.Count < 4) {
				return false;
			}
			int backdrop = 0;
			for(int group = 0; group < length; group += 4) {
				int first = prg[offset + group];
				if(first == prg[offset] || first == 0x0F) {
					backdrop++;
				}
			}
			double valid = 1.0;
			double variety = Math.Min(1.0, distinct.Count / 8.0);
			double backdropScore = backdrop / Math.Max(1.0, length / 4.0);
			score = Math.Round(0.45 * valid + 0.25 * variety + 0.30 * backdropScore, 4);
			return true;
		}

		private static double TileScore(byte[] data, int offset)
		{
			if(offset < 0 || offset + 16 > data.Length) {
				return 0;
			}
			int nonZeroBytes = 0;
			int distinct = 0;
			HashSet<int> values = new();
			int opaque = 0;
			int rows = 0;
			for(int row = 0; row < 8; row++) {
				int lo = data[offset + row];
				int hi = data[offset + row + 8];
				if(lo != 0 || hi != 0) {
					rows++;
				}
				for(int bit = 0; bit < 8; bit++) {
					int pixel = ((lo >> bit) & 1) | (((hi >> bit) & 1) << 1);
					if(pixel != 0) {
						opaque++;
						distinct |= 1 << pixel;
					}
				}
				values.Add(lo);
				values.Add(hi);
				if(lo != 0) nonZeroBytes++;
				if(hi != 0) nonZeroBytes++;
			}
			if(nonZeroBytes == 0 || values.Count <= 1) {
				return 0;
			}
			double ratio = opaque / 64.0;
			if(ratio < 0.02 || ratio > 0.98) {
				return 0;
			}
			int colorCount = 0;
			for(int i = 1; i < 4; i++) {
				if((distinct & (1 << i)) != 0) colorCount++;
			}
			double shape = 1.0 - Math.Abs(ratio - 0.42) / 0.55;
			return Math.Max(0, 0.20 * Math.Min(1.0, colorCount / 2.0) + 0.32 * Math.Max(0, shape) + 0.24 * (rows / 8.0) + 0.24 * (nonZeroBytes / 16.0));
		}

		private static bool OverlapsAny(List<(int Offset, int End)> ranges, int offset, int length)
		{
			foreach((int start, int end) in ranges) {
				if(offset < end && offset + length > start) {
					return true;
				}
				if(start > offset + length) {
					break;
				}
			}
			return false;
		}

		private static string RenderStreamPreview(string folder, string name, byte[] data, int offset, int tiles, uint[]? palette)
		{
			try {
				Directory.CreateDirectory(folder);
				int width = 8 * 8;
				int rows = Math.Max(1, (tiles + 7) / 8);
				int height = rows * 8;
				uint[] pixels = new uint[width * height];
				uint[] colors = palette is { Length: >= 64 } ? palette : DefaultPalette();
				for(int t = 0; t < tiles; t++) {
					int tx = (t % 8) * 8;
					int ty = (t / 8) * 8;
					int o = offset + t * 16;
					if(o + 16 > data.Length) {
						break;
					}
					for(int y = 0; y < 8; y++) {
						int lo = data[o + y];
						int hi = data[o + y + 8];
						for(int x = 0; x < 8; x++) {
							int pixel = ((lo >> (7 - x)) & 1) | (((hi >> (7 - x)) & 1) << 1);
							if(pixel == 0) {
								continue;
							}
							uint color = colors[Math.Min(colors.Length - 1, pixel == 1 ? 0x10 : pixel == 2 ? 0x20 : 0x30)];
							pixels[(ty + y) * width + tx + x] = color | 0xFF000000;
						}
					}
				}
				uint[] scaled = HdStudioPng.Upscale(pixels, width, height, 4);
				string path = Path.Combine(folder, name);
				HdStudioPng.Write(path, scaled, width * 4, height * 4);
				return Path.Combine("archaeology", "candidates", name).Replace('\\', '/');
			} catch {
				return "";
			}
		}

		private static uint[] DefaultPalette()
		{
			uint[] palette = new uint[64];
			for(int i = 0; i < palette.Length; i++) {
				int r = (i * 37) & 0xFF;
				int g = (i * 73) & 0xFF;
				int b = (i * 109) & 0xFF;
				palette[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
			}
			return palette;
		}

		private static string HashBytes(byte[] data, int offset, int length)
		{
			return Convert.ToHexString(SHA256.HashData(data.AsSpan(offset, length))).Substring(0, 16).ToLowerInvariant();
		}

		private static string FullHash(byte[] data, int length)
		{
			return Convert.ToHexString(SHA256.HashData(data.AsSpan(0, length))).ToLowerInvariant();
		}

		private static int ColorMask(byte[] tile)
		{
			int mask = 0;
			for(int row = 0; row < 8; row++) {
				int lo = tile[row];
				int hi = tile[row + 8];
				for(int bit = 0; bit < 8; bit++) {
					int pixel = ((lo >> bit) & 1) | (((hi >> bit) & 1) << 1);
					if(pixel != 0) {
						mask |= 1 << pixel;
					}
				}
			}
			return mask;
		}

		private static int OpaquePixels(byte[] tile)
		{
			int count = 0;
			for(int row = 0; row < 8; row++) {
				int lo = tile[row];
				int hi = tile[row + 8];
				for(int bit = 0; bit < 8; bit++) {
					if((((lo >> bit) & 1) | (((hi >> bit) & 1) << 1)) != 0) {
						count++;
					}
				}
			}
			return count;
		}

		private static string AppendCapped(string existing, int value, int cap, char separator = ',')
		{
			return AppendCapped(existing, value.ToString(CultureInfo.InvariantCulture), cap, separator);
		}

		private static string AppendCapped(string existing, string value, int cap, char separator = ',')
		{
			if(existing.Length == 0) {
				return value;
			}
			string[] parts = existing.Split(separator, StringSplitOptions.RemoveEmptyEntries);
			if(parts.Length >= cap || parts.Contains(value, StringComparer.OrdinalIgnoreCase)) {
				return existing;
			}
			return existing + separator + value;
		}
	}
}
