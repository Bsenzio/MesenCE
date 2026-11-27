using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Mesen.HdStudio
{
	internal sealed class HdStudioStructuralOptions
	{
		public int MaxInstructions = 600000;
		public int MaxBlocks = 30000;
		public int MaxBlockInstructions = 512;
		public int MaxPointerEntries = 64;
		public bool DetectCandidateTables = true;
		public bool DetectOamTables = true;
		public ManualResetEventSlim? PauseGate = null;
	}

	internal sealed class HdStudioStructuralResult
	{
		public int CodeBlocks;
		public int Instructions;
		public int ReachableBytes;
		public int UnresolvedIndirectJumps;
		public int UnresolvedCrossBankTargets;
		public int UndocumentedOpcodes;
		public int PpuWrites;
		public int OamDmaWrites;
		public int MapperWrites;
		public int DataReferences;
		public int PointerTables;
		public int OamTableCandidates = 0;
		public int AnimationCandidates = 0;
		public int VectorSeeds = 0;
		public int RejectedCandidates = 0;
		public readonly List<HdStudioCodeBlock> Blocks = new();
		public readonly List<string> Errors = new();
	}

	internal sealed class HdStudioCodeBlock
	{
		public int WindowIndex;
		public int BankIndex;
		public int StartCpu;
		public int StartOffset;
		public int EndCpu;
		public int InstructionCount;
		public string Exit = "next";
		public string Seed = "";
		public double Confidence = 0.5;
		public readonly List<int> TargetCpu = new();
		public readonly List<string> PpUReferences = new();
		public string NodeId = "";
	}

	/// <summary>
	/// Structural archaeology: a bounded, conservative 6502 control-flow walk seeded
	/// from the reset/NMI/IRQ vectors. It records what the code actually references
	/// (PPU registers, OAM DMA, mapper writes, absolute data tables) and refuses to
	/// promote a byte pattern to "graphics" just because it looks like a tile.
	///
	/// Indirect flow, dynamic bank switching and ambiguous code/data boundaries remain
	/// explicit unresolved evidence and become gaps for the dynamic stage.
	/// </summary>
	internal static class HdStudioStructuralArchaeology
	{
		public static HdStudioStructuralResult Analyze(HdStudioRomImage rom, HdStudioEvidenceGraph graph, HdStudioStructuralOptions options)
		{
			HdStudioStructuralResult result = new();
			List<HdStudioPrgWindow> windows = rom.GetPrgWindows();
			if(windows.Count == 0) {
				result.Errors.Add("no PRG windows");
				return result;
			}

			Dictionary<int, HdStudioEvidenceNode> bankNodes = new();
			foreach(HdStudioPrgWindow window in windows) {
				HdStudioEvidenceNode bankNode = graph.AddNode("prgBank", "bank:" + window.Index,
					"PRG bank " + window.Index + " at $" + window.CpuBase.ToString("X4", CultureInfo.InvariantCulture),
					"raw", 1.0, "certain", "iNES physical PRG bank " + window.Index + "; " + window.Description);
				bankNode.Properties["physicalOffset"] = "0x" + window.PhysicalOffset.ToString("X", CultureInfo.InvariantCulture);
				bankNode.Properties["length"] = window.Length.ToString(CultureInfo.InvariantCulture);
				bankNode.Properties["cpuBase"] = "0x" + window.CpuBase.ToString("X4", CultureInfo.InvariantCulture);
				bankNode.Properties["fixed"] = window.Fixed ? "true" : "false";
				bankNode.Properties["mappingAssumption"] = window.Description;
				bankNodes[window.Index] = bankNode;
			}

			List<HdStudioCodeBlock> allBlocks = new();
			Dictionary<string, HashSet<int>> dataRefs = new(StringComparer.Ordinal);
			Dictionary<string, List<HdStudioInstruction>> dataRefEvidence = new(StringComparer.Ordinal);
			HashSet<string> oamDmaWindows = new(StringComparer.Ordinal);
			HashSet<int> crossBankTargets = new();

			List<HdStudioVector> vectors = rom.GetVectors();
			foreach(HdStudioVector vector in vectors) {
				HdStudioEvidenceNode vectorNode = graph.AddNode("vector", vector.Name + ":" + vector.CpuAddress.ToString("X4", CultureInfo.InvariantCulture),
					vector.Name + " vector -> $" + vector.Target.ToString("X4", CultureInfo.InvariantCulture), "raw", 1.0, "certain",
					"CPU vector at $" + vector.CpuAddress.ToString("X4", CultureInfo.InvariantCulture) + ", physical PRG offset 0x" + vector.PhysicalOffset.ToString("X", CultureInfo.InvariantCulture));
				vectorNode.Properties["target"] = "0x" + vector.Target.ToString("X4", CultureInfo.InvariantCulture);
				vectorNode.Properties["bank"] = vector.BankIndex.ToString(CultureInfo.InvariantCulture);
			}

			//Fixed banks first: their reachable JSR/JMP destinations become conservative
			//entry hypotheses for 16 KB switchable banks. This is not a claim that every
			//bank is mapped at the same time; the seed retains low confidence and the
			//assumption is recorded on the block.
			foreach(HdStudioPrgWindow window in windows.OrderByDescending(w => w.Fixed).ThenBy(w => w.Index)) {
				options.PauseGate?.Wait();
				byte[] code = new byte[window.Length];
				Array.Copy(rom.Prg, window.PhysicalOffset, code, 0, Math.Min(window.Length, rom.Prg.Length - window.PhysicalOffset));
				bool[] covered = new bool[code.Length];
				Dictionary<int, HdStudioInstruction> instructionStarts = new();
				Queue<(int Offset, string Seed, double Confidence)> work = new();

				if(window.Fixed) {
					foreach(HdStudioVector vector in vectors) {
						if(vector.Target >= window.CpuBase && vector.Target < window.CpuEnd) {
							work.Enqueue((vector.Target - window.CpuBase, vector.Name + " vector", 0.95));
							result.VectorSeeds++;
						}
					}
				} else if(window.Length == 16384) {
					foreach(int target in crossBankTargets) {
						if(target >= window.CpuBase && target < window.CpuEnd) {
							work.Enqueue((target - window.CpuBase, "cross-bank entry hypothesis", 0.45));
						}
					}
				}

				while(work.Count > 0 && result.Instructions < options.MaxInstructions && allBlocks.Count < options.MaxBlocks) {
					(int startOffset, string seed, double seedConfidence) = work.Dequeue();
					if(startOffset < 0 || startOffset >= code.Length || covered[startOffset]) {
						continue;
					}
					int offset = startOffset;
					int startCpu = window.CpuBase + startOffset;
					int blockInstructions = 0;
					HdStudioCodeBlock block = new() {
						WindowIndex = window.Index,
						BankIndex = window.Index,
						StartCpu = startCpu,
						StartOffset = startOffset,
						EndCpu = startCpu,
						Seed = seed,
						Confidence = seedConfidence
					};
					string exit = "next";
					int lastImmediateA = -1;
					int lastImmediateX = -1;
					int lastImmediateY = -1;

					while(offset < code.Length && blockInstructions < options.MaxBlockInstructions && result.Instructions < options.MaxInstructions) {
						if(covered[offset]) {
							exit = "merge";
							break;
						}
						int cpu = window.CpuBase + offset;
						if(!HdStudioCpu6502.TryDecode(code, offset, cpu, window.Index, out HdStudioInstruction instruction)) {
							exit = "truncated";
							break;
						}
						HdStudioOpcode info = HdStudioCpu6502.Get(code[offset]);
						if(!info.Documented) {
							result.UndocumentedOpcodes++;
							exit = "undocumented-opcode";
							break;
						}

						//A branch into the middle of an instruction means the assumed seed was
						//not code. Do not create overlapping blocks or invent a target.
						for(int b = 0; b < instruction.Length; b++) {
							if(covered[offset + b] && !instructionStarts.ContainsKey(cpu + b)) {
								exit = "overlap-mid-instruction";
								break;
							}
						}
						if(exit == "overlap-mid-instruction") {
							break;
						}

						instructionStarts[cpu] = instruction;
						for(int b = 0; b < instruction.Length; b++) {
							covered[offset + b] = true;
						}
						blockInstructions++;
						result.Instructions++;
						if((result.Instructions & 0xFFF) == 0) {
							options.PauseGate?.Wait();
						}
						block.EndCpu = cpu + instruction.Length;

						if(instruction.Mnemonic == "LDA" && instruction.Mode == HdStudioAddressMode.Immediate) {
							lastImmediateA = instruction.Immediate ?? -1;
						} else if(instruction.Mnemonic == "LDX" && instruction.Mode == HdStudioAddressMode.Immediate) {
							lastImmediateX = instruction.Immediate ?? -1;
						} else if(instruction.Mnemonic == "LDY" && instruction.Mode == HdStudioAddressMode.Immediate) {
							lastImmediateY = instruction.Immediate ?? -1;
						}

						ObserveInstruction(rom, graph, window, windows, instruction, lastImmediateA, lastImmediateX, lastImmediateY,
							dataRefs, dataRefEvidence, oamDmaWindows, block, result);

						if(instruction.Flow == HdStudioCpuFlow.Branch) {
							exit = "branch";
							if(instruction.BranchTarget.HasValue && instruction.BranchTarget.Value >= window.CpuBase && instruction.BranchTarget.Value < window.CpuEnd) {
								int targetOffset = instruction.BranchTarget.Value - window.CpuBase;
								block.TargetCpu.Add(instruction.BranchTarget.Value);
								if(!covered[targetOffset]) {
									work.Enqueue((targetOffset, "branch", Math.Min(seedConfidence, 0.85)));
								}
							}
							offset += instruction.Length;
							continue;
						}
						if(instruction.Flow == HdStudioCpuFlow.Jump) {
							exit = "jump";
							if(instruction.AbsoluteAddress.HasValue) {
								int target = instruction.AbsoluteAddress.Value;
								block.TargetCpu.Add(target);
								if(target >= window.CpuBase && target < window.CpuEnd) {
									int targetOffset = target - window.CpuBase;
									if(!covered[targetOffset]) {
										work.Enqueue((targetOffset, "jump", Math.Min(seedConfidence, 0.9)));
									}
								} else if(target >= 0x8000 && target < 0x10000) {
									result.UnresolvedCrossBankTargets++;
									crossBankTargets.Add(target);
									graph.AddGap("cross-bank-jump", "JMP $" + target.ToString("X4", CultureInfo.InvariantCulture) + " leaves the assumed bank window of PRG bank " + window.Index,
										"dynamic-bank-trace", 0.7, 2.0, Array.Empty<string>());
								}
							}
							break;
						}
						if(instruction.Flow == HdStudioCpuFlow.JumpIndirect) {
							exit = "indirect-jump";
							result.UnresolvedIndirectJumps++;
							graph.AddGap("indirect-jump", "JMP ($" + (instruction.IndirectPointer ?? 0).ToString("X4", CultureInfo.InvariantCulture) + ") in PRG bank " + window.Index + " has no statically resolved target",
								"read-only-trace-indirect-jump", 0.9, 3.0, Array.Empty<string>());
							break;
						}
						if(instruction.Flow == HdStudioCpuFlow.Call) {
							exit = "call";
							if(instruction.AbsoluteAddress.HasValue) {
								int target = instruction.AbsoluteAddress.Value;
								block.TargetCpu.Add(target);
								if(target >= window.CpuBase && target < window.CpuEnd) {
									int targetOffset = target - window.CpuBase;
									if(!covered[targetOffset]) {
										work.Enqueue((targetOffset, "jsr", Math.Min(seedConfidence, 0.8)));
									}
								} else if(target >= 0x8000 && target < 0x10000) {
									result.UnresolvedCrossBankTargets++;
									crossBankTargets.Add(target);
									graph.AddGap("cross-bank-call", "JSR $" + target.ToString("X4", CultureInfo.InvariantCulture) + " leaves the assumed bank window of PRG bank " + window.Index,
										"dynamic-bank-trace", 0.7, 2.0, Array.Empty<string>());
								}
							}
							offset += instruction.Length;
							continue;
						}
						if(instruction.Flow == HdStudioCpuFlow.Interrupt || instruction.Flow == HdStudioCpuFlow.Return) {
							exit = instruction.Mnemonic == "BRK" ? "brk" : "return";
							break;
						}
						offset += instruction.Length;
					}

					block.InstructionCount = blockInstructions;
					block.Exit = exit;
					if(blockInstructions > 0) {
						allBlocks.Add(block);
						result.ReachableBytes += block.EndCpu - block.StartCpu;
					}
				}

				//Absolute operands that land on a decoded instruction are control-flow
				//targets, not data. Do not feed them to table inference.
				foreach(string key in dataRefs.Keys.ToList()) {
					string[] parts = key.Split(':');
					if(parts.Length != 2 || !int.TryParse(parts[0], out int refBank) || !int.TryParse(parts[1], out int refAddress)) {
						continue;
					}
					if(refBank != window.Index) {
						continue;
					}
					if(instructionStarts.ContainsKey(refAddress)) {
						dataRefs.Remove(key);
						dataRefEvidence.Remove(key);
					}
				}
			}
			result.CodeBlocks = allBlocks.Count;

			//Persist blocks and resolve block-to-block edges once all starts are known.
			foreach(HdStudioCodeBlock block in allBlocks) {
				HdStudioEvidenceNode node = graph.AddNode("codeBlock", "bank:" + block.WindowIndex + ":addr:" + block.StartCpu.ToString("X4", CultureInfo.InvariantCulture),
					"Code block $" + block.StartCpu.ToString("X4", CultureInfo.InvariantCulture) + " (bank " + block.WindowIndex + ")",
					"inferred", block.Confidence, "heuristic", "6502 control-flow walk seeded by " + block.Seed + "; " + block.InstructionCount + " instruction(s)");
				node.Properties["bank"] = block.WindowIndex.ToString(CultureInfo.InvariantCulture);
				node.Properties["startCpu"] = "0x" + block.StartCpu.ToString("X4", CultureInfo.InvariantCulture);
				node.Properties["endCpu"] = "0x" + block.EndCpu.ToString("X4", CultureInfo.InvariantCulture);
				node.Properties["startOffset"] = "0x" + block.StartOffset.ToString("X", CultureInfo.InvariantCulture);
				node.Properties["instructions"] = block.InstructionCount.ToString(CultureInfo.InvariantCulture);
				node.Properties["exit"] = block.Exit;
				node.Properties["seed"] = block.Seed;
				node.Properties["mappingAssumption"] = "bank window base " + (block.StartCpu - block.StartOffset).ToString("X4", CultureInfo.InvariantCulture);
				block.NodeId = node.Id;
				if(bankNodes.TryGetValue(block.WindowIndex, out HdStudioEvidenceNode? bankNode)) {
					graph.AddEdge(bankNode.Id, node.Id, "contains", "inferred", block.Confidence, "code block belongs to physical PRG bank");
				}
			}
			foreach(HdStudioCodeBlock block in allBlocks) {
				foreach(int target in block.TargetCpu.Distinct()) {
					HdStudioCodeBlock? targetBlock = allBlocks.FirstOrDefault(b => b.WindowIndex == block.WindowIndex && b.StartCpu == target);
					if(targetBlock != null && targetBlock.NodeId.Length > 0 && block.NodeId.Length > 0) {
						graph.AddEdge(block.NodeId, targetBlock.NodeId, "precedes", "inferred", 0.8, "direct 6502 control-flow edge");
					}
				}
			}

			//Candidate data structures: referenced absolute data plus pointer/OAM tables.
			foreach(KeyValuePair<string, HashSet<int>> reference in dataRefs) {
				string[] parts = reference.Key.Split(':');
				if(parts.Length != 2 || !int.TryParse(parts[0], out int bankIndex) || !int.TryParse(parts[1], out int address)) {
					continue;
				}
				HdStudioPrgWindow? window = windows.FirstOrDefault(w => w.Index == bankIndex);
				if(window == null) {
					continue;
				}
				int physical = window.PhysicalOffset + (address - window.CpuBase);
				HdStudioEvidenceNode node = graph.AddNode("dataReference", "bank:" + bankIndex + ":addr:" + address.ToString("X4", CultureInfo.InvariantCulture),
					"Referenced data $" + address.ToString("X4", CultureInfo.InvariantCulture) + " (bank " + bankIndex + ")",
					"inferred", 0.55, "heuristic", "absolute operand referenced by reachable code");
				node.Properties["bank"] = bankIndex.ToString(CultureInfo.InvariantCulture);
				node.Properties["cpuAddress"] = "0x" + address.ToString("X4", CultureInfo.InvariantCulture);
				node.Properties["physicalOffset"] = "0x" + physical.ToString("X", CultureInfo.InvariantCulture);
				node.Properties["referenceCount"] = reference.Value.Count.ToString(CultureInfo.InvariantCulture);
				if(bankNodes.TryGetValue(bankIndex, out HdStudioEvidenceNode? bankNode)) {
					graph.AddEdge(bankNode.Id, node.Id, "contains", "inferred", 0.55, "referenced data inside physical PRG bank");
				}
				result.DataReferences++;
				if(options.DetectCandidateTables) {
					InspectReferencedTable(rom, graph, window, address, node, options, result);
				}
			}

			//OAM DMA itself is a strong hint that a nearby referenced table is metasprite data.
			if(oamDmaWindows.Count > 0) {
				graph.AddGap("oam-table-confirmation", "OAM DMA was found, but the 4-byte metasprite tables still need runtime confirmation of their stride and origin",
					"targeted-oam-observation", 0.8, 4.0, Array.Empty<string>());
			}

			result.Blocks.AddRange(allBlocks);
			graph.Notes.Add("Structural walk is a bounded hypothesis: indirect flow and switchable-bank entry points remain unresolved and are listed as gaps.");
			return result;
		}

		private static void ObserveInstruction(HdStudioRomImage rom, HdStudioEvidenceGraph graph, HdStudioPrgWindow window,
			List<HdStudioPrgWindow> windows, HdStudioInstruction instruction, int lastA, int lastX, int lastY,
			Dictionary<string, HashSet<int>> dataRefs, Dictionary<string, List<HdStudioInstruction>> dataRefEvidence,
			HashSet<string> oamDmaWindows, HdStudioCodeBlock block, HdStudioStructuralResult result)
		{
			if(!instruction.AbsoluteAddress.HasValue) {
				return;
			}
			int address = instruction.AbsoluteAddress.Value;
			bool store = instruction.Mnemonic is "STA" or "STX" or "STY";
			if(store && address >= 0x2000 && address <= 0x2007) {
				int value = instruction.Mnemonic == "STX" ? lastX : instruction.Mnemonic == "STY" ? lastY : lastA;
				HdStudioEvidenceNode node = graph.AddNode("ppuReference", "ppu:" + address.ToString("X4", CultureInfo.InvariantCulture) + ":bank:" + window.Index + ":cpu:" + instruction.CpuAddress.ToString("X4", CultureInfo.InvariantCulture),
					"PPU write $" + address.ToString("X4", CultureInfo.InvariantCulture) + " at $" + instruction.CpuAddress.ToString("X4", CultureInfo.InvariantCulture),
					"inferred", 0.9, "heuristic", "store to a PPU register in reachable code");
				node.Properties["register"] = "0x" + address.ToString("X4", CultureInfo.InvariantCulture);
				node.Properties["instruction"] = instruction.Text;
				node.Properties["block"] = block.StartCpu.ToString("X4", CultureInfo.InvariantCulture);
				node.Properties["value"] = value >= 0 ? "0x" + value.ToString("X2", CultureInfo.InvariantCulture) : "unknown";
				result.PpuWrites++;
				block.PpUReferences.Add(node.Id);
				return;
			}
			if(store && address == 0x4014) {
				int value = instruction.Mnemonic == "STX" ? lastX : instruction.Mnemonic == "STY" ? lastY : lastA;
				HdStudioEvidenceNode node = graph.AddNode("oamDma", "oam:" + window.Index + ":" + instruction.CpuAddress.ToString("X4", CultureInfo.InvariantCulture),
					"OAM DMA at $" + instruction.CpuAddress.ToString("X4", CultureInfo.InvariantCulture), "inferred", 0.95, "heuristic", "store to $4014 in reachable code");
				node.Properties["page"] = value >= 0 ? "0x" + value.ToString("X2", CultureInfo.InvariantCulture) : "unknown";
				node.Properties["block"] = block.StartCpu.ToString("X4", CultureInfo.InvariantCulture);
				result.OamDmaWrites++;
				oamDmaWindows.Add(window.Index + ":" + block.StartCpu);
				return;
			}
			if(store && IsMapperRegister(rom.Mapper, address)) {
				HdStudioEvidenceNode node = graph.AddNode("mapperWrite", "mapper:" + window.Index + ":" + instruction.CpuAddress.ToString("X4", CultureInfo.InvariantCulture),
					"Mapper write at $" + instruction.CpuAddress.ToString("X4", CultureInfo.InvariantCulture), "inferred", 0.7, "heuristic", "store to an address used by this mapper for banking");
				node.Properties["address"] = "0x" + address.ToString("X4", CultureInfo.InvariantCulture);
				node.Properties["instruction"] = instruction.Text;
				result.MapperWrites++;
				return;
			}
			if(address >= 0x8000 && address < 0x10000) {
				HdStudioPrgWindow? dataWindow = windows.FirstOrDefault(w => w.Fixed && address >= w.CpuBase && address < w.CpuEnd)
					?? windows.FirstOrDefault(w => address >= w.CpuBase && address < w.CpuEnd);
				if(dataWindow == null) {
					return;
				}
				string key = dataWindow.Index + ":" + address;
				if(!dataRefs.TryGetValue(key, out HashSet<int>? origins)) {
					dataRefs[key] = origins = new HashSet<int>();
					dataRefEvidence[key] = new List<HdStudioInstruction>();
				}
				origins.Add(instruction.CpuAddress);
				dataRefEvidence[key].Add(instruction);
			}
		}

		private static bool IsMapperRegister(int mapper, int address)
		{
			if(mapper == 4 && address >= 0x8000 && address <= 0xFFFF) {
				return true;
			}
			if((mapper == 1 || mapper == 5 || mapper == 69 || mapper == 159 || mapper == 105) && address >= 0x8000 && address <= 0xFFFF) {
				return true;
			}
			if((mapper == 2 || mapper == 7 || mapper == 11 || mapper == 34 || mapper == 66 || mapper == 71 || mapper == 79 || mapper == 113 || mapper == 180) && address >= 0x8000 && address <= 0xFFFF) {
				return true;
			}
			if(mapper == 3 && address >= 0x8000 && address <= 0xFFFF) {
				return true;
			}
			return false;
		}

		private static void InspectReferencedTable(HdStudioRomImage rom, HdStudioEvidenceGraph graph, HdStudioPrgWindow window,
			int address, HdStudioEvidenceNode dataNode, HdStudioStructuralOptions options, HdStudioStructuralResult result)
		{
			int relative = address - window.CpuBase;
			if(relative < 0 || relative + 2 > window.Length) {
				return;
			}
			int physical = window.PhysicalOffset + relative;
			if(physical + 8 > rom.Prg.Length) {
				return;
			}
			List<int> words = new();
			for(int i = 0; i < options.MaxPointerEntries && physical + i * 2 + 2 <= rom.Prg.Length && i * 2 + 2 <= window.Length - relative; i++) {
				words.Add(rom.Prg[physical + i * 2] | (rom.Prg[physical + i * 2 + 1] << 8));
			}
			if(words.Count < 4) {
				return;
			}
			int prgPointers = words.Count(w => w >= 0x8000 && w < 0x10000);
			int ramPointers = words.Count(w => w >= 0x0000 && w < 0x0800);
			int zeroPagePointers = words.Count(w => w >= 0x0000 && w < 0x0100);
			int plausible = Math.Max(prgPointers, Math.Max(ramPointers, zeroPagePointers));
			if(plausible * 100 / words.Count >= 80) {
				HdStudioEvidenceNode table = graph.AddNode("candidateTable", "pointer:" + window.Index + ":" + address.ToString("X4", CultureInfo.InvariantCulture),
					"Candidate pointer table at $" + address.ToString("X4", CultureInfo.InvariantCulture), "inferred", 0.45, "heuristic",
					"absolute indexed reference + " + plausible + "/" + words.Count + " plausible little-endian pointers");
				table.Properties["bank"] = window.Index.ToString(CultureInfo.InvariantCulture);
				table.Properties["physicalOffset"] = "0x" + physical.ToString("X", CultureInfo.InvariantCulture);
				table.Properties["entries"] = words.Count.ToString(CultureInfo.InvariantCulture);
				table.Properties["prgPointers"] = prgPointers.ToString(CultureInfo.InvariantCulture);
				table.Properties["ramPointers"] = ramPointers.ToString(CultureInfo.InvariantCulture);
				table.Properties["targetSpace"] = prgPointers >= ramPointers ? "PRG" : "RAM";
				table.Properties["targets"] = string.Join(",", words.Take(16).Select(w => "0x" + w.ToString("X4", CultureInfo.InvariantCulture)));
				graph.AddEdge(dataNode.Id, table.Id, "candidate-equivalent", "inferred", 0.4, "same referenced offset interpreted as a pointer table");
				result.PointerTables++;
				return;
			}

			//OAM entries are Y, tile, attribute, X. A table of at least 8 entries whose
			//attribute bytes only use the three OAM bits is a conservative candidate.
			int entries = Math.Min(32, (window.Length - relative) / 4);
			int plausibleOam = 0;
			for(int i = 0; i < entries; i++) {
				int o = physical + i * 4;
				if(o + 4 > rom.Prg.Length) {
					break;
				}
				int tile = rom.Prg[o + 1];
				int attr = rom.Prg[o + 2];
				if((attr & 0x1F) == 0 && tile > 0) {
					plausibleOam++;
				}
			}
			if(entries >= 8 && plausibleOam * 100 / entries >= 75) {
				HdStudioEvidenceNode oam = graph.AddNode("candidateOamTable", "oamtable:" + window.Index + ":" + address.ToString("X4", CultureInfo.InvariantCulture),
					"Candidate OAM/metasprite table at $" + address.ToString("X4", CultureInfo.InvariantCulture), "inferred", 0.3, "heuristic",
					"referenced 4-byte-stride table with " + plausibleOam + "/" + entries + " plausible OAM attribute bytes");
				oam.Properties["physicalOffset"] = "0x" + physical.ToString("X", CultureInfo.InvariantCulture);
				oam.Properties["entries"] = entries.ToString(CultureInfo.InvariantCulture);
				oam.Properties["stride"] = "4";
				graph.AddEdge(dataNode.Id, oam.Id, "candidate-equivalent", "inferred", 0.3, "referenced data interpreted as OAM entries");
				result.OamTableCandidates++;
			}
		}
	}
}
