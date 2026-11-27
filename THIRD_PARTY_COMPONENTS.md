# Third-party components and prior-art decisions (v0.9)

This file records what was inspected, what was actually reused, and what was
rejected. A public repository is not permission to copy code; no source from the
external candidates below was copied into this working tree.

## Base project

| Component | Revision | License | Use |
|---|---|---|---|
| MesenCE (this repository) | `5af27baa` (v0.8) plus the v0.9 changes in the working tree | GPL-3.0 | Emulator, mapper/PPU APIs, HD Pack parser/builder, Avalonia Studio, existing discovery/import/verify/compile services. |
| Avalonia | 12.1.1 (NuGet, `UI/UI.csproj`) | MIT | Native desktop UI. |
| SkiaSharp / HarfBuzzSharp | NuGet, as pinned by the existing project | MIT / BSD-style | Artwork loading, composition and PNG rendering. |
| .NET | net10.0 | MIT | Runtime and build. |

The MesenCE GPLv3 license and notices are preserved. No ROM, save data,
commercial artwork or credential is redistributed.

## Prior-art review (section 31 of the directive)

Metadata was read from the public repositories on 2026-11-27. Pinned revisions
are the latest commit visible at that time; they are not vendored.

| Candidate | Pinned revision | License | What it actually provides | v0.9 decision |
|---|---:|---|---|---|
| [SourMesen/Mesen](https://github.com/SourMesen/Mesen) | `e1180957b319` (archived) | GPL-3.0 | Original Mesen HD Pack format, debugger/disassembler, memory callbacks. | Reference only; the local MesenCE code already contains the relevant APIs. No import. |
| [SourMesen/Mesen2](https://github.com/SourMesen/Mesen2) | `b9fa69ddc6d0` (archived) | GPL-3.0 | Mesen2 architecture and debugger; upstream ancestor. | Reference only; local base is MesenCE, not a second emulator. |
| [MesenAI](https://github.com/sbihaiko/MesenAI) | `bd25f8725512` | GPL-3.0 | Capture/replay/exploration and community pack workflow. | Rejected as a dependency: it is a large emulator fork whose feature surface overlaps the existing Studio. Its workflow ideas were reviewed, not copied. |
| [MesenCE-Metasprite](https://github.com/ModernRetroDesign/MesenCE-metasprite) | `f8329c34e4e1` | GPL-3.0 | Sprite-viewer grouping of OAM parts into one metasprite; proof of concept, AI-coded fork, not merged upstream. | Rejected for v0.9: composition grouping must remain evidence-based and compatible with the existing catalog/compiler. No code reused. |
| [NESRip](https://github.com/FitzRoyX/nesrip) | `ff9175ceb83d` | MIT | Drag-and-drop/CLI 8x8 tile extraction, per-game graphics database, finder heuristics for compressed blocks. | Concept reference for bounded tile-stream heuristics; no code imported. Per-game databases are not mandatory in Studio. |
| [NESRecomp](https://github.com/mstan/nesrecomp) | `8402db07d77e` | `NOASSERTION` (license file must be reviewed per use) | Static 6502-to-C recompilation framework and bank-aware runtime integration. | Rejected as a dependency (wrong product and unclear license). No code imported. The v0.9 structural walk is independently implemented. |
| [GhidraNes](https://github.com/kylewlacy/GhidraNes) | `4c419644c51c` | No repository license detected at review time | Ghidra loader/analyzer for iNES ROMs, mapper-aware code analysis. | Rejected as a dependency (Ghidra plugin, no detected redistribution license). Useful as a conceptual comparison only. |
| [retrodisasm](https://github.com/retroenv/retrodisasm) | `255d6c22eafa` | Apache-2.0 | Execution-flow tracing disassembler with bit-perfect reassembly. | Not integrated; an external development benchmark candidate. The local analyzer is bounded and does not require a second toolchain. |
| [NES-disasm6](https://github.com/mcgrew/nes-disasm6) | `4718964e5434` | Apache-2.0 | NES disassembly tooling. | Not integrated; reference only. |
| [MappyLand paper](https://cdn.aaai.org/ojs/18892/18892-52-22658-1-2-20211004.pdf) | 2021 paper | Paper, not code | Map reconstruction/tracking research. | Read as related work; no official implementation was found and no method was copied. |

## Integration result

- The native RetroArchaeology pipeline, evidence graph, 6502 control-flow
  walk, PRG candidate heuristics, gap scheduler and catalog projection are
  original implementations in `UI/HdStudio/HdStudio*Archaeology*.cs` and
  `UI/Windows/HdStudioArchaeologyWindow.*`.
- The existing MesenCE HD Pack loader/builder, mapper-aware PPU data, capture
  and verification services remain the runtime path. There is no second
  emulator or second discovery system.
- No external network service, LLM API or mandatory third-party analysis tool
  is used by the distributed product.
- External comparisons were not performed for v0.9 unless stated in the final
  report; unavailable tools are recorded as not evaluated, not as negative
  results.

## Notices

- GPLv3 and the existing MesenCE notices remain in `LICENSE` and `NOTICE.md`.
- Avalonia/SkiaSharp/HarfBuzzSharp notices remain in their package metadata.
- The Lucide icon geometries used by the existing Studio theme remain under
  their existing ISC attribution; the pickaxe geometry added in v0.9 follows
  the same style and does not import a new library.
