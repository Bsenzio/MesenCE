using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Offline archaeology fixtures. No emulator, no commercial ROM and no UI
	/// framework is needed: the synthetic cartridge exercises header parsing, CHR
	/// deduplication, vector-seeded 6502 flow, PPU/OAM/mapper references, palette
	/// candidates, PRG tile-stream candidates, graph persistence/merge, runtime
	/// correlation, catalog projection, approved-artwork preservation and batch resume.
	/// </summary>
	internal static class HdStudioArchaeologySelfTest
	{
		private sealed class Result
		{
			public int Passed;
			public int Failed;
			public readonly List<string> Failures = new();
			public readonly List<string> Checks = new();
		}

		public static int Run(Action<string> log)
		{
			Result result = new();
			string root = Path.Combine(Path.GetTempPath(), "hdstudio-v09-selftest-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			void Check(string name, bool condition, string detail = "")
			{
				if(condition) {
					result.Passed++;
					result.Checks.Add("PASS " + name);
					log("PASS " + name);
				} else {
					result.Failed++;
					string message = name + (detail.Length > 0 ? ": " + detail : "");
					result.Failures.Add(message);
					result.Checks.Add("FAIL " + message);
					log("FAIL " + message);
				}
			}

			try {
				byte[] romBytes = BuildSyntheticRom(0x00);
				string romPath = Path.Combine(root, "selftest.nes");
				File.WriteAllBytes(romPath, romBytes);
				HdStudioRomImage rom = HdStudioRomImage.Load(romPath);
				Check("rom-header", rom.Valid && rom.Mapper == 0 && rom.ChrTileCount == 512,
					rom.Error + " mapper=" + rom.Mapper + " chrTiles=" + rom.ChrTileCount);
				string exponentPath = Path.Combine(root, "nes2-exponent.nes");
				File.WriteAllBytes(exponentPath, BuildExponentRom());
				HdStudioRomImage exponentRom = HdStudioRomImage.Load(exponentPath);
				Check("rom-nes2-exponent-size", exponentRom.Valid && exponentRom.PrgSize == 8192 && exponentRom.ChrSize == 8192,
					"prg=" + exponentRom.PrgSize + " chr=" + exponentRom.ChrSize + " error=" + exponentRom.Error);
				Check("rom-vectors", rom.GetVectors().Count == 3 && rom.GetVectors()[1].Target == 0xC010,
					"vector target=" + rom.GetVectors().ElementAtOrDefault(1)?.Target.ToString("X4", CultureInfo.InvariantCulture));
				Check("rom-prg-windows", rom.GetPrgWindows().Count == 2, "windows=" + rom.GetPrgWindows().Count);

				HdStudioCpu6502.TryDecode(new byte[] { 0xA9, 0x3F }, 0, 0xC000, 0, out HdStudioInstruction lda);
				Check("cpu-lda-imm", lda.Mnemonic == "LDA" && lda.Immediate == 0x3F && lda.Length == 2, lda.Text);
				HdStudioCpu6502.TryDecode(new byte[] { 0xD0, 0xFE }, 0, 0xC000, 0, out HdStudioInstruction bne);
				Check("cpu-branch", bne.Flow == HdStudioCpuFlow.Branch && bne.BranchTarget == 0xC000, bne.Text);
				Check("cpu-undocumented-stops", !HdStudioCpu6502.Get(0x02).Documented, "0x02 must remain unresolved");

				HdStudioArchaeologyOptions options = HdStudioArchaeologyOptions.ForMode("deep");
				HdStudioEvidenceGraph graph = NewGraph(rom);
				HdStudioArchaeologyResult staticResult = new();
				Dictionary<int, HdStudioEvidenceNode> tileNodes = new();
				HdStudioStaticArchaeology.AnalyzeChr(rom, graph, options, staticResult, tileNodes, CancellationToken.None);
				Check("chr-unique", staticResult.UniquePatterns >= 4, "unique=" + staticResult.UniquePatterns);
				Check("chr-duplicate", staticResult.DuplicateOccurrences >= 1, "duplicates=" + staticResult.DuplicateOccurrences);
				Check("chr-offsets", graph.Nodes.Values.Where(n => n.Type == "pattern" && n.Properties.GetValueOrDefault("isBlank") != "true")
					.All(n => n.Properties.ContainsKey("firstPhysicalOffset") && n.Properties.ContainsKey("firstBank")), "pattern source offsets");

				List<HdStudioPaletteCandidate> palettes = HdStudioStaticArchaeology.AnalyzePrgPalettes(rom, graph, options, staticResult, CancellationToken.None);
				Check("prg-palette-candidate", palettes.Count >= 1, "palettes=" + palettes.Count);

				HdStudioStructuralResult structural = HdStudioStructuralArchaeology.Analyze(rom, graph, new HdStudioStructuralOptions());
				Check("struct-code", structural.CodeBlocks >= 1 && structural.Instructions >= 5,
					"blocks=" + structural.CodeBlocks + " instructions=" + structural.Instructions);
				Check("struct-ppu-oam", structural.PpuWrites >= 2 && structural.OamDmaWrites >= 1,
					"ppu=" + structural.PpuWrites + " oam=" + structural.OamDmaWrites);
				Check("struct-pointer-table", structural.PointerTables >= 1, "tables=" + structural.PointerTables);

				string previewFolder = Path.Combine(root, "preview");
				List<HdStudioPrgStreamCandidate> streams = HdStudioStaticArchaeology.AnalyzePrgTileStreams(rom, graph, options, staticResult,
					structural.Blocks, previewFolder, null, CancellationToken.None);
				Check("prg-stream-candidate", streams.Count >= 1, "streams=" + streams.Count + " rejected=" + staticResult.RejectedCandidates);
				Check("prg-stream-preview", streams.Count == 0 || (streams[0].Preview.Length > 0 && File.Exists(Path.Combine(previewFolder, Path.GetFileName(streams[0].Preview)))),
					streams.Count > 0 ? streams[0].Preview : "no stream");

				string graphPath = Path.Combine(root, "graph.json");
				graph.Save(graphPath);
				HdStudioEvidenceGraph loaded = HdStudioEvidenceGraph.Load(graphPath);
				Check("graph-roundtrip", loaded.Nodes.Count == graph.Nodes.Count && loaded.Edges.Count == graph.Edges.Count,
					"nodes " + loaded.Nodes.Count + "/" + graph.Nodes.Count);

				HdStudioEvidenceGraph determinismA = NewGraph(rom);
				HdStudioArchaeologyResult determinismAResult = new();
				Dictionary<int, HdStudioEvidenceNode> determinismATiles = new();
				HdStudioStaticArchaeology.AnalyzeChr(rom, determinismA, options, determinismAResult, determinismATiles, CancellationToken.None);
				HdStudioEvidenceGraph second = NewGraph(rom);
				HdStudioArchaeologyResult secondResult = new();
				Dictionary<int, HdStudioEvidenceNode> secondTiles = new();
				HdStudioStaticArchaeology.AnalyzeChr(rom, second, options, secondResult, secondTiles, CancellationToken.None);
				Check("graph-deterministic-ids", second.Nodes.Keys.OrderBy(k => k).SequenceEqual(determinismA.Nodes.Keys.OrderBy(k => k)),
					"node id sets differ");

				string observedId = tileNodes[1].Id;
				second.Nodes[observedId].Status = "runtime-observed";
				second.Nodes[observedId].ObservedOccurrences = 7;
				HdStudioEvidenceGraph merged = NewGraph(rom);
				HdStudioArchaeologyResult mergedResult = new();
				Dictionary<int, HdStudioEvidenceNode> mergedTiles = new();
				HdStudioStaticArchaeology.AnalyzeChr(rom, merged, options, mergedResult, mergedTiles, CancellationToken.None);
				merged.MergePrevious(second);
				Check("graph-merge-status", merged.Nodes[observedId].Status == "runtime-observed" && merged.Nodes[observedId].ObservedOccurrences >= 7,
					"status=" + merged.Nodes[observedId].Status);

				string pack = Path.Combine(root, "pack");
				Directory.CreateDirectory(Path.Combine(pack, HdStudioReport.StudioFolderName, "assets"));
				File.WriteAllText(Path.Combine(pack, HdStudioReport.StudioFolderName, "tiles.txt"),
					"#tile\tpalette\tpixelCount\n01\t0F001020\t1234\n02\t0F001020\t99\n1F\t0F1A2A3A\t44\n");
				File.WriteAllText(Path.Combine(pack, HdStudioReport.StudioFolderName, "capture.txt"),
					"frames=120\nscale=2\nchrRam=0\nrom=selftest\n");
				File.WriteAllText(Path.Combine(pack, HdStudioReport.StudioFolderName, "palettes.txt"),
					"#palette\tuses\tfirstOffset\n0F001020\t3\t10\n");
				File.WriteAllText(Path.Combine(pack, HdStudioReport.StudioFolderName, "assets", "assets.json"),
					"{\"schemaVersion\":\"1.0.0\",\"game\":{\"romSha1\":\"" + rom.Sha1 + "\",\"mapper\":0,\"hasChrRom\":true},\"assets\":[{\"id\":\"approved:selftest\",\"kind\":\"rawPattern\",\"name\":\"Approved sentinel\"}],\"replacementAssets\":[],\"replacementConditions\":[]}");
				HdStudioRuntimeEvidence runtime = HdStudioRuntimeEvidence.Load(pack);
				HdStudioArchaeologyResult correlation = new();
				HdStudioStaticArchaeology.CorrelateRuntime(rom, merged, runtime, options, correlation, mergedTiles, CancellationToken.None);
				Check("runtime-correlation", correlation.RuntimeObservedPatterns >= 2 && correlation.RuntimeObservations == 3,
					"observed=" + correlation.RuntimeObservedPatterns + " rows=" + correlation.RuntimeObservations);

				HdStudioEvidenceGraph catalogGraph = NewGraph(rom);
				HdStudioArchaeologyResult catalogStatic = new();
				Dictionary<int, HdStudioEvidenceNode> catalogTiles = new();
				HdStudioStaticArchaeology.AnalyzeChr(rom, catalogGraph, options, catalogStatic, catalogTiles, CancellationToken.None);
				HdStudioStaticArchaeology.CorrelateRuntime(rom, catalogGraph, HdStudioRuntimeEvidence.Load(pack), options, catalogStatic, catalogTiles, CancellationToken.None);
				HdStudioArchaeologyCatalogResult catalog = HdStudioArchaeologyCatalog.Build(pack, rom, catalogGraph, options, Palette(), log);
				Check("catalog-build", catalog.AssetsExposed > 0 && File.Exists(catalog.CatalogPath), "assets=" + catalog.AssetsExposed);
				string assetsPath = Path.Combine(pack, HdStudioReport.StudioFolderName, "assets", "assets.json");
				string assetsText = File.Exists(assetsPath) ? File.ReadAllText(assetsPath) : "";
				Check("assets-merge", assetsText.Contains("approved:selftest", StringComparison.Ordinal) && catalog.AssetsMerged > 0,
					"merged=" + catalog.AssetsMerged);
				int secondMerge = HdStudioArchaeologyCatalog.MergeIntoAssets(pack, null);
				Check("assets-merge-idempotent", secondMerge == 0, "second merge=" + secondMerge);
				Check("catalog-gaps", catalogGraph.Gaps.Values.Any(g => g.Kind == "map-not-reconstructed") &&
					catalogGraph.Gaps.Values.Any(g => g.Kind == "animation-not-reconstructed"), "missing honest reconstruction gaps");

				string batchRoot = Path.Combine(root, "batch");
				string batchOut = Path.Combine(root, "batch-out");
				Directory.CreateDirectory(batchRoot);
				File.WriteAllBytes(Path.Combine(batchRoot, "a.nes"), BuildSyntheticRom(0x11));
				File.WriteAllBytes(Path.Combine(batchRoot, "b.nes"), BuildSyntheticRom(0x22));
				HdStudioArchaeologyBatchResult batch1 = HdStudioArchaeologyBatch.Run(batchRoot, batchOut, options, 2, null, CancellationToken.None);
				Check("batch-run", batch1.Attempted == 2 && batch1.Completed == 2 && File.Exists(batch1.SummaryPath),
					"attempted=" + batch1.Attempted + " complete=" + batch1.Completed + " errors=" + batch1.Errors);
				HdStudioArchaeologyBatchResult batch2 = HdStudioArchaeologyBatch.Run(batchRoot, batchOut, options, 2, null, CancellationToken.None);
				Check("batch-resume", batch2.Skipped == 2 && batch2.Completed == 0,
					"skipped=" + batch2.Skipped + " complete=" + batch2.Completed);

				Check("no-fabricated-claims", !graph.Nodes.Values.Any(n => n.Type == "pattern" && n.Properties.GetValueOrDefault("semanticLabel", "unclassified") != "unclassified"),
					"a static pattern was assigned a semantic label");
			} catch(Exception ex) {
				Check("selftest-exception", false, ex.ToString());
			} finally {
				try { Directory.Delete(root, true); } catch { }
			}

			using MemoryStream summaryStream = new();
			using(Utf8JsonWriter json = new(summaryStream, new JsonWriterOptions() { Indented = false })) {
				json.WriteStartObject();
				json.WriteNumber("passed", result.Passed);
				json.WriteNumber("failed", result.Failed);
				json.WriteStartArray("checks");
				foreach(string check in result.Checks) {
					json.WriteStringValue(check);
				}
				json.WriteEndArray();
				json.WriteStartArray("failures");
				foreach(string failure in result.Failures) {
					json.WriteStringValue(failure);
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			log("ARCH_SELFTEST_JSON " + Encoding.UTF8.GetString(summaryStream.ToArray()));
			return result.Failed == 0 ? 0 : 1;
		}

		private static HdStudioEvidenceGraph NewGraph(HdStudioRomImage rom)
		{
			return new HdStudioEvidenceGraph() {
				GeneratedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
				EngineFingerprint = HdStudioEvidenceGraph.ComputeFingerprint("selftest"),
				RomSha256 = rom.Sha256,
				RomSha1 = rom.Sha1,
				RomFileName = rom.FileName,
				RomPath = rom.FilePath,
				Mapper = rom.Mapper,
				Submapper = rom.Submapper,
				ChrMode = rom.ChrMode,
				PrgBytes = rom.Prg.Length,
				ChrBytes = rom.Chr.Length,
				Nes20 = rom.Nes20,
				HasTrainer = rom.HasTrainer,
				FileBytes = rom.Bytes.Length
			};
		}

		private static uint[] Palette()
		{
			uint[] palette = new uint[64];
			for(int i = 0; i < palette.Length; i++) {
				palette[i] = 0xFF000000u | ((uint)((i * 37) & 0xFF) << 16) | ((uint)((i * 73) & 0xFF) << 8) | (uint)((i * 109) & 0xFF);
			}
			return palette;
		}

		private static byte[] BuildExponentRom()
		{
			byte[] data = new byte[16 + 8192 + 8192];
			data[0] = (byte)'N'; data[1] = (byte)'E'; data[2] = (byte)'S'; data[3] = 0x1A;
			data[4] = 0x34; //E=13, M=0 -> 8 KB PRG
			data[5] = 1;    //8 KB CHR
			data[6] = 0;
			data[7] = 0x08; //NES 2.0
			data[8] = 0;
			data[9] = 0x0F; //PRG exponent-multiplier, CHR normal 1 unit
			return data;
		}

		private static byte[] BuildSyntheticRom(byte salt)
		{
			const int prgBytes = 32768;
			const int chrBytes = 8192;
			byte[] data = new byte[16 + prgBytes + chrBytes];
			data[0] = (byte)'N'; data[1] = (byte)'E'; data[2] = (byte)'S'; data[3] = 0x1A;
			data[4] = 2; //32 KB PRG
			data[5] = 1; //8 KB CHR
			data[6] = 0;
			data[7] = 0;
			int prg = 16;
			//Palette-like table at CPU $A000 (bank 0, physical 0x2000)
			byte[] colors = { 0x0F, 0x01, 0x11, 0x21, 0x0F, 0x06, 0x16, 0x26, 0x0F, 0x09, 0x19, 0x29, 0x0F, 0x00, 0x10, 0x20,
				0x0F, 0x02, 0x12, 0x22, 0x0F, 0x04, 0x14, 0x24, 0x0F, 0x08, 0x18, 0x28, 0x0F, 0x0C, 0x1C, 0x2C };
			Array.Copy(colors, 0, data, prg + 0x2000, colors.Length);
			//Tile-like stream at CPU $9000 (bank 0, physical 0x1000)
			Random random = new(1234 + salt);
			for(int t = 0; t < 24; t++) {
				for(int b = 0; b < 16; b++) {
					data[prg + 0x1000 + t * 16 + b] = (byte)(random.Next(0, 4) == 0 ? 0 : random.Next(1, 256));
				}
			}
			//Pointer table at CPU $B000 (bank 0, physical 0x3000)
			int[] pointers = { 0xC010, 0xC020, 0xC030, 0xC040, 0xC050, 0xC060, 0xC070, 0xC080 };
			for(int i = 0; i < pointers.Length; i++) {
				data[prg + 0x3000 + i * 2] = (byte)(pointers[i] & 0xFF);
				data[prg + 0x3000 + i * 2 + 1] = (byte)(pointers[i] >> 8);
			}
			//Code in bank 1 at CPU $C010 (PRG offset 0x4010)
			byte[] code = {
				0xA9, 0x3F,       //LDA #$3F
				0x8D, 0x06, 0x20, //STA $2006
				0xA9, 0x00,       //LDA #$00
				0x8D, 0x06, 0x20, //STA $2006
				0xA9, 0x02,       //LDA #$02
				0x8D, 0x14, 0x40, //STA $4014 (OAM DMA)
				0xA9, 0x00,       //LDA #$00
				0x8D, 0x07, 0x20, //STA $2007 (PPU data)
				0xBD, 0x00, 0xB0, //LDA $B000,X (pointer table reference)
				0x4C, 0x10, 0xC0  //JMP $C010
			};
			Array.Copy(code, 0, data, prg + 0x4010, code.Length);
			//Small return stubs for pointer targets.
			for(int i = 0; i < 8; i++) {
				data[prg + 0x4020 + i * 0x10] = 0x60;
			}
			//Vectors in the last PRG bank (CPU $FFFA-$FFFF)
			int vectors = prg + prgBytes - 6;
			data[vectors] = 0x10; data[vectors + 1] = 0xC0; //NMI
			data[vectors + 2] = 0x10; data[vectors + 3] = 0xC0; //RESET
			data[vectors + 4] = 0x10; data[vectors + 5] = 0xC0; //IRQ
			//CHR: tile 0 blank, tile 1 pattern, tile 2 duplicate, tile 3+ varied.
			int chr = prg + prgBytes;
			for(int t = 0; t < 32; t++) {
				if(t == 0) {
					continue;
				}
				for(int b = 0; b < 16; b++) {
					byte value = (byte)(((t * 37 + b * 13 + salt) & 0x7F) | 0x01);
					if((b & 1) == 0 && t % 3 == 0) {
						value = 0;
					}
					data[chr + t * 16 + b] = value;
				}
				if(t == 2) {
					Array.Copy(data, chr + 16, data, chr + 32, 16);
				}
			}
			return data;
		}
	}
}
