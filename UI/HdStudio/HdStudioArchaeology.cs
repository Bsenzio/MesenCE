using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Mesen.HdStudio
{
	/// <summary>
	/// RetroArchaeology orchestrator. Native UI, CLI and batch mode all call this
	/// single deterministic pipeline; there is no second research-only discovery
	/// implementation.
	/// </summary>
	internal static class HdStudioArchaeology
	{
		public static HdStudioArchaeologyResult Run(string romPath, string packFolder, HdStudioArchaeologyOptions options,
			uint[] palette, Action<string>? log, IProgress<HdStudioArchaeologyProgress>? progress, CancellationToken cancellation)
		{
			Stopwatch watch = Stopwatch.StartNew();
			HdStudioArchaeologyResult result = new() {
				RomPath = Path.GetFullPath(romPath),
				PackFolder = Path.GetFullPath(packFolder),
				EngineFingerprint = options.EngineFingerprint
			};
			result.EngineFingerprint = options.EngineFingerprint.Length > 0 ? options.EngineFingerprint : HdStudioEvidenceGraph.ComputeEngineFingerprint();
			result.RunFingerprint = HdStudioEvidenceGraph.ComputeFingerprint(
				options.Mode + "|struct=" + options.IncludeStructural + "|prg=" + options.IncludePrgCandidates +
				"|assets=" + options.MaxAssets + "|streams=" + options.MaxCandidateStreams +
				"|resolve=" + options.ResolveGaps + "|gapsex=" + options.MaxGapResolutionSeconds +
				"|evidence=" + options.EvidencePackFolder);

			void Report(string phase, int index, int count, double percent, string message)
			{
				progress?.Report(new HdStudioArchaeologyProgress() {
					Phase = phase,
					PhaseIndex = index,
					PhaseCount = count,
					Percent = percent,
					Message = message,
					ElapsedSeconds = watch.Elapsed.TotalSeconds,
					UniquePatterns = result.UniquePatterns,
					ObservedPatterns = result.RuntimeObservedPatterns,
					Assets = result.AssetsExposed,
					Gaps = result.Gaps
				});
				log?.Invoke("[archaeology] " + phase + ": " + message);
			}

			Report("ROM", 0, 6, 0.02, "reading iNES/NES 2.0 header and physical banks");
			HdStudioRomImage rom = HdStudioRomImage.Load(result.RomPath);
			result.RomSha256 = rom.Sha256;
			if(!rom.Valid) {
				result.StopReason = "unsupported";
				result.Errors.Add(rom.Error.Length > 0 ? rom.Error : "ROM could not be parsed");
				result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
				Report("ROM", 0, 6, 1.0, "unsupported: " + result.Errors[0]);
				return result;
			}
			Directory.CreateDirectory(HdStudioEvidenceGraph.GraphFolder(result.PackFolder));
			string graphPath = HdStudioEvidenceGraph.GetPath(result.PackFolder);
			HdStudioEvidenceGraph? previous = File.Exists(graphPath) ? HdStudioEvidenceGraph.Load(graphPath) : null;

			HdStudioEvidenceGraph graph = new() {
				GeneratedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
				EngineFingerprint = result.EngineFingerprint,
				RunFingerprint = result.RunFingerprint,
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
			graph.Notes.Add("Mode: " + options.Mode + ". Static-first pipeline; no emulator was started unless dynamic confirmation was explicitly requested.");
			graph.Notes.Add("Assets exposed: " + (options.MaxAssets <= 0 ? "all discovered nonblank patterns, paginated" : "at most " + options.MaxAssets + " diverse static/observed patterns") + "; the graph keeps the full physical inventory.");

			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			if(options.MaxSecondsPerRom > 0) {
				timeout.CancelAfter(TimeSpan.FromSeconds(options.MaxSecondsPerRom));
			}
			CancellationToken token = timeout.Token;
			bool cancelledByUser = false;

			try {
				Report("Static", 1, 6, 0.08, "enumerating physical CHR banks, pages and 8x8 patterns");
				Dictionary<int, HdStudioEvidenceNode> tileNodes = new();
				HdStudioStaticArchaeology.AnalyzeChr(rom, graph, options, result, tileNodes, token);
				if(token.IsCancellationRequested) {
					cancelledByUser = cancellation.IsCancellationRequested;
					throw new OperationCanceledException(token);
				}
				Report("Static", 1, 6, 0.32, result.UniquePatterns.ToString("N0", CultureInfo.InvariantCulture) + " unique CHR patterns; " + result.DuplicateOccurrences.ToString("N0", CultureInfo.InvariantCulture) + " duplicate occurrences");

				Report("Palettes", 2, 6, 0.38, "scanning PRG for palette-like tables");
				HdStudioStaticArchaeology.AnalyzePrgPalettes(rom, graph, options, result, token);

				HdStudioStructuralResult structural = new();
				if(options.IncludeStructural) {
					Report("Structural", 3, 6, 0.48, "walking reachable 6502 code from reset/NMI/IRQ vectors");
					structural = HdStudioStructuralArchaeology.Analyze(rom, graph, new HdStudioStructuralOptions() {
						MaxInstructions = options.MaxInstructions,
						PauseGate = options.PauseGate
					});
					result.CodeBlocks = structural.CodeBlocks;
					result.Instructions = structural.Instructions;
					result.PpuWrites = structural.PpuWrites;
					result.OamDmaWrites = structural.OamDmaWrites;
					result.MapperWrites = structural.MapperWrites;
					result.DataReferences = structural.DataReferences;
					result.PointerTables = structural.PointerTables;
					result.UnresolvedIndirectJumps = structural.UnresolvedIndirectJumps;
					result.UnresolvedCrossBankTargets = structural.UnresolvedCrossBankTargets;
					HdStudioStaticArchaeology.ScheduleStructuralGaps(graph, structural);
					graph.Notes.Add("Structural: " + structural.CodeBlocks.ToString(CultureInfo.InvariantCulture) + " blocks, " +
						structural.Instructions.ToString(CultureInfo.InvariantCulture) + " instructions, " +
						structural.PpuWrites.ToString(CultureInfo.InvariantCulture) + " PPU writes, " +
						structural.PointerTables.ToString(CultureInfo.InvariantCulture) + " pointer-table candidates.");
				} else {
					graph.Notes.Add("Structural phase disabled by options; PRG candidate streams are weaker because reachable-code exclusion was not available.");
				}

				if(token.IsCancellationRequested) {
					cancelledByUser = cancellation.IsCancellationRequested;
					throw new OperationCanceledException(token);
				}
				result.PrgBanks = rom.GetPrgWindows().Count;

				if(options.IncludePrgCandidates) {
					Report("Static", 3, 6, 0.62, "checking PRG for tile-like 2bpp streams and excluding reachable code");
					string previewFolder = Path.Combine(HdStudioEvidenceGraph.GraphFolder(result.PackFolder), "candidates");
					HdStudioStaticArchaeology.AnalyzePrgTileStreams(rom, graph, options, result, structural.Blocks, previewFolder, palette, token);
				}

				if(token.IsCancellationRequested) {
					cancelledByUser = cancellation.IsCancellationRequested;
					throw new OperationCanceledException(token);
				}
				HdStudioRuntimeEvidence runtime = new();
				if(options.RunDynamicConfirmation) {
					Report("Dynamic", 4, 6, 0.7, "starting a bounded deterministic capture for runtime confirmation");
					runtime = RunDynamicConfirmation(result.RomPath, result.PackFolder, options, log, token);
				}
				if(options.UseExistingRuntimeEvidence || options.RunDynamicConfirmation) {
					if(runtime.Observations.Count == 0) {
						string evidenceFolder = options.EvidencePackFolder.Length > 0 ? options.EvidencePackFolder : result.PackFolder;
						runtime = HdStudioRuntimeEvidence.Load(evidenceFolder);
					}
					Report("Runtime", 4, 6, 0.78, runtime.HasCapture
						? "correlating " + runtime.Observations.Count.ToString("N0", CultureInfo.InvariantCulture) + " observed pattern+palette rows with static patterns"
						: "no existing capture; runtime confirmation remains a gap");
					HdStudioStaticArchaeology.CorrelateRuntime(rom, graph, runtime, options, result, tileNodes, token);
				}

				if(previous != null) {
					graph.MergePrevious(previous);
				}

				if(options.ResolveGaps) {
					Report("Gaps", 5, 7, 0.84, "executing bounded automatic gap-resolution analyses");
					HdStudioGapResolutionResult resolution = HdStudioGapResolver.Run(rom, graph, result.PackFolder,
						options, options.EvidencePackFolder.Length > 0 ? options.EvidencePackFolder : result.PackFolder,
						log, token);
					result.GapResolutionPath = resolution.Path;
					result.GapsResolved = resolution.Resolved;
					result.GapsPartiallyResolved = resolution.PartiallyResolved;
					result.GapsUnresolved = resolution.Unresolved;
					result.GapsUnsupported = resolution.Unsupported;
					result.Compositions = resolution.Compositions;
					result.AnimationSequences = resolution.AnimationSequences;
					result.Metatiles = resolution.Metatiles;
				}

				result.Gaps = graph.Gaps.Count;
				result.GraphPath = graphPath;
				graph.Save(graphPath);

				Report("Catalog", 6, 7, 0.9, "building the paginated native catalog projection and preserving approved artwork");
				HdStudioArchaeologyCatalogResult catalog = HdStudioArchaeologyCatalog.Build(result.PackFolder, rom, graph, options, palette, log);
				result.AssetsExposed = catalog.AssetsExposed;
				result.CatalogPath = catalog.CatalogPath;
				result.AssetsPath = catalog.AssetsPath;
				foreach(string error in catalog.Errors) {
					result.Errors.Add(error);
				}
				result.Success = true;
				result.StopReason = "complete";
				Report("Catalog", 6, 7, 1.0, catalog.AssetsExposed.ToString("N0", CultureInfo.InvariantCulture) + " catalog assets in " +
					catalog.PageCount.ToString(CultureInfo.InvariantCulture) + " page(s); " +
					result.GapsResolved.ToString("N0", CultureInfo.InvariantCulture) + " gaps resolved, " +
					result.GapsPartiallyResolved.ToString("N0", CultureInfo.InvariantCulture) + " partial, " +
					result.GapsUnresolved.ToString("N0", CultureInfo.InvariantCulture) + " unresolved");
			} catch(OperationCanceledException) {
				result.Success = false;
				result.StopReason = cancelledByUser ? "cancelled" : "budget";
				result.Errors.Add(cancelledByUser ? "cancelled by the user" : "per-ROM time budget reached");
				result.Gaps = graph.Gaps.Count;
				result.GraphPath = graphPath;
				try {
					if(previous != null) {
						graph.MergePrevious(previous);
					}
					graph.Save(graphPath);
				} catch {
				}
			} catch(Exception ex) {
				result.Success = false;
				result.StopReason = "error";
				result.Errors.Add(ex.Message);
			}

			result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
			progress?.Report(new HdStudioArchaeologyProgress() {
				Phase = "Done",
				PhaseIndex = 6,
				PhaseCount = 6,
				Percent = 1.0,
				Message = result.StopReason,
				StopReason = result.StopReason,
				ElapsedSeconds = result.Seconds,
				UniquePatterns = result.UniquePatterns,
				ObservedPatterns = result.RuntimeObservedPatterns,
				Assets = result.AssetsExposed,
				Gaps = result.Gaps
			});
			return result;
		}

		private static HdStudioRuntimeEvidence RunDynamicConfirmation(string romPath, string packFolder, HdStudioArchaeologyOptions options,
			Action<string>? log, CancellationToken token)
		{
			HdStudioRuntimeEvidence runtime = new();
			try {
				string projectPath = HdStudioProject.GetDefaultPath(packFolder);
				HdStudioProject project = File.Exists(projectPath)
					? HdStudioProject.Load(projectPath)
					: HdStudioProject.Create(romPath, packFolder, 4, log);
				if(!File.Exists(projectPath)) {
					project.Save(projectPath);
				}
				HdStudioDiscoveryOptions discovery = new() {
					Mode = HdStudioDiscoveryModes.Quick,
					Frames = Math.Max(900, options.DynamicFrames),
					ChunkFrames = 900,
					Seed = 20261127,
					SaturationChunks = 1,
					Scale = 4,
					ObserveBackgrounds = false
				};
				HdStudioDiscoveryResult discoveryResult = HdStudioDiscovery.RunAsync(project, discovery, log, null, token).GetAwaiter().GetResult();
				if(discoveryResult.Success) {
					runtime = HdStudioRuntimeEvidence.Load(packFolder);
				} else {
					log?.Invoke("[archaeology] Dynamic confirmation did not complete: " + discoveryResult.StopReason);
				}
			} catch(Exception ex) {
				log?.Invoke("[archaeology] Dynamic confirmation failed: " + ex.Message);
			}
			return runtime;
		}
	}
}
