# Algorithm provenance and confidence (v0.9)

This document separates extraction facts, heuristics, published methods and
validated results. Confidence values in the evidence graph are qualitative,
not calibrated probabilities. No algorithm in this file is claimed to be novel
or patentable.

## 1. ROM identity and physical inventory

| Step | Source | Status |
|---|---|---|
| iNES/NES 2.0 header parse | Public NES 2.0 header specification; local implementation in `HdStudioRomImage` | Deterministic extraction |
| SHA-256 / SHA-1 identity | .NET cryptographic hashes over the original file / headerless image | Deterministic |
| PRG/CHR boundaries and trainer | Header fields, clamped to actual file length | Deterministic; malformed input is explicit |
| Physical CHR 8 KB banks and 1 KB pages | Byte offsets in the CHR-ROM image | Deterministic extraction |
| 8x8 pattern identity | 16-byte two-bitplane tile content, SHA-256 truncated in the graph key; full hash in properties | Deterministic extraction |
| Blank/duplicate counting | Content hash comparison | Deterministic extraction; not semantic classification |

No semantic object is inferred from a tile count. A pattern node starts as
`unclassified`.

## 2. Structural 6502 analysis

- Decoder: documented NMOS 6502 opcodes only. Undocumented opcodes end the
  basic block and are recorded as unresolved; they are not guessed.
- Control flow: worklist seeded from the reset/NMI/IRQ vectors in the last
  physical PRG bank, plus conservative direct JSR/JMP targets. Switchable
  16 KB banks receive cross-bank entry hypotheses at low confidence.
- Mapping assumptions: mapper-family windows are hypotheses (`$C000` fixed for
  the last 16 KB, MMC3 `$E000` fixed for the last 8 KB, etc.). A bank switch
  instruction is recorded but not simulated. Cross-bank flow is a gap.
- Evidence emitted: basic blocks, PPU register stores (`$2000-$2007`), OAM DMA
  (`$4014`), mapper-register writes, absolute data references, pointer-table
  candidates and OAM-table candidates.
- Rejections: absolute operands that land on decoded instruction starts are not
  treated as data; PRG streams with more than 20% reachable-code overlap are
  rejected as graphics candidates.

This is a bounded conservative analysis, not a proof of reachability or code
coverage. Indirect jumps, dynamic bank changes and ambiguous code/data
boundaries remain explicit gaps.

## 3. Static PRG heuristics

| Heuristic | Basis | Confidence | Failure mode |
|---|---|---|---|
| Palette-like table | 16/32-byte window, every value < `$40`, at least four distinct colours, repeated/backdrop first colour | Low (`0.25-0.65`) | Code/data can satisfy the byte constraints; usage is never claimed without runtime evidence |
| Tile-like PRG stream | Consecutive 16-byte blocks with plausible two-bitplane shape, >= 6 blocks, >= 4 unique blocks, >= 53% mean score | Low (`0.25-0.55`) | Random data or code can look tile-like; reachable-code overlap rejects the worst cases |
| Pointer table | Absolute indexed reference to PRG data; >= 4 little-endian entries with >= 80% plausible PRG/RAM targets | Low (`0.45`) | A data table can contain pointer-shaped values |
| OAM/metasprite table | Referenced 4-byte-stride data, >= 8 entries, >= 75% attribute bytes with low five bits clear | Very low (`0.30`) | Attribute coincidence and non-OAM 4-byte records |
| Map/metatile/decoder | Not promoted. No universal NES map format or decoder was assumed | Unresolved gap | A plausible byte pattern is not a map |

The catalog marks these as candidates/diagnostics. They are not counted as
verified assets and cannot be imported as replacements until they acquire a
mapper/CHR association or runtime witness.

## 4. Runtime evidence correlation

- Existing `tiles.txt` observations are mapped to static CHR-ROM tile indices
  and to CHR-RAM content hashes. The static pattern becomes
  `runtime-observed`; the observation retains the palette and pixel count.
- Historical captures are labelled as historical evidence. They are not a
  fresh final-engine run and do not replace final replacement verification.
- CHR-RAM identity is the observed content hash, not a cartridge tile.
  PositionCheck remains a scene-isolated fallback, not a global identity.
- Absence of observation is recorded as a gap, never as proof that a pattern
  is unused.

## 5. Evidence graph

- Persistent JSON per ROM, schema `1.0.0`, engine `0.9.0-archaeology`,
  algorithm `static-first-1`.
- Node/edge identifiers are content-derived from the ROM SHA-256 plus type and
  key; they do not depend on timestamps or array order.
- Status ladder: `rejected < unresolved < raw < inferred < corroborated <
  runtime-observed < replay-ready < replacement-verified`.
- Merge policy: a later static pass never downgrades a higher-status node;
  user/observed properties and supporting witnesses are retained.
- Transactions: temp file + replace + `.prev` backup.
- Confidence is explicitly `certain` or `heuristic`; no calibrated probability
  is claimed.

## 6. Catalog projection

- Observed patterns are projected as `rawPattern` records with canonical
  existing catalog ids where the same pattern+palette already exists.
- Static-only patterns are projected as `staticPattern` records, with the
  neutral palette `0F001020` and an explicit "palette unresolved" warning.
- Diversity selection uses an 8x8 silhouette bit signature and rejects
  near-identical variants for the exposed set; the graph still retains every
  physical pattern.
- `assets.json` merge is additive. Existing ids and approved artwork win;
  the merge is idempotent and writes a `.prev` backup.

## 7. What was not validated

- No universal map/metatile/animation decoder.
- No claim that every semantic object or every observed asset in a game was
  found.
- No calibrated confidence, no per-ROM LLM inference and no whole-game
  completeness percentage.
- External tool comparisons were not run; they are recorded as not evaluated.
