# RetroArchaeology (v0.9)

RetroArchaeology is the native, static-first graphical discovery path in
RetroArcDo HD Studio. Open an NES ROM, press the pickaxe button, and the local
engine builds a persistent visual catalog without requiring you to play the
game. It does not generate artwork and it does not replace the existing
Artwork Workshop, importer, compiler or verifier.

## Native workflow

1. Open a ROM in RetroArcDo HD Studio.
2. Press the pickaxe in the top toolbar, or **RetroArchaeology** on the Home
   page.
3. Choose **Quick**, **Deep** or **Exhaustive**.
4. Optionally enable **Run bounded capture** to add a short deterministic
   dynamic confirmation pass. Leave it off to use existing capture evidence or
   static-only analysis.
5. Press **Run Archaeology**. The workspace shows static, structural, runtime
   evidence and catalog phases. Pause holds at the next safe checkpoint;
   Cancel keeps completed evidence on disk.
6. Browse **Assets**, **Candidates** and **Gaps**. Observed assets can be
   opened in the Artwork Workshop, where the normal import, preview, verify and
   export workflow continues.

The workspace reads:

- `HdStudio/archaeology/evidence-graph.json` — persistent, versioned evidence
  graph.
- `HdStudio/archaeology/catalog.json` — artist-facing projection with
  thumbnails, provenance, candidates and prioritized gaps.
- `HdStudio/assets/assets.json` — the existing Studio catalog. Archaeology
  merges additive `rawPattern` / `staticPattern` records; existing ids and
  approved artwork always win.

## Command line

```bat
:: one ROM, static-first, use an existing read-only capture as evidence
Mesen.exe --hdstudio "game.nes" --archaeology --arch-mode=deep ^
  --arch-evidence="D:\captures\game" --out="D:\work\game"

:: one ROM with a short dynamic confirmation pass
Mesen.exe --hdstudio "game.nes" --archaeology --arch-dynamic --arch-frames=1800 ^
  --out="D:\work\game"

:: a resumable batch with a title/hash -> capture-folder map
Mesen.exe --hdstudio --arch-batch="D:\NESGames\Library" ^
  --arch-out-root="D:\work\library-v09" --arch-mode=deep --arch-parallel=6 ^
  --arch-evidence-map="D:\work\evidence-map.json"

:: offline fixtures (no commercial ROM or emulator)
Mesen.exe --hdstudio --arch-selftest
```

`--arch-evidence` is read-only. Historical captures are labelled as historical
evidence and do not replace a final-engine replacement verification.

## Modes and budgets

| Mode | Catalog asset cap | PRG stream cap | Structural instruction cap | Per-ROM time cap |
|---|---:|---:|---:|---:|
| Quick | 64 | 16 | 150,000 | 60 s |
| Deep | 256 | 128 | 600,000 | 180 s |
| Exhaustive | 1,024 | 512 | 1,800,000 | 600 s |

Batch mode defaults to 64 assets and 32 streams per ROM to keep the
full-library evidence pack manageable. The graph still contains every unique
physical CHR pattern. Budgets are finite; stopping at a budget is not proof of
saturation or completeness.

## What the statuses mean

| Status | Meaning |
|---|---|
| `raw` | Extracted physical fact (ROM/header/CHR pattern/source offset). |
| `inferred` | Structural or statistical hypothesis (pointer table, OAM table, PRG stream, code block). |
| `corroborated` | More than one evidence source agrees; still not a replacement test. |
| `runtime-observed` | The pattern+palette was seen in a capture. The capture can be historical. |
| `replay-ready` | A deterministic witness exists for a scene. |
| `replacement-verified` | A real original-vs-HD run verified intended and unrelated changes and state integrity. |

Confidence is marked `certain` or `heuristic`; it is not a calibrated
probability. The engine never promotes a candidate stream, pointer table or
palette table to a replaceable asset merely because it looks plausible.

## CHR-RAM

CHR-RAM is not statically extractable as cartridge tiles. The engine records
observed CHR-RAM content hashes from captures and marks the static upload path
as an unresolved gap. A positionCheck replacement is scene-isolated by
construction; it is never reported as global identity.

## Replay and verification

Discovery evidence is not replacement proof. After selecting discovered
assets, import artwork and use **Verify Replacements**/`--verify` in the
existing Studio. The verifier replays a deterministic scene, compares
original-vs-HD state hashes and reports intended footprint and collateral
changes. Official unmodified Mesen is the compatibility reference for exported
packs.

## Known limitations

- No universal NES map/metatile/animation decoder is assumed.
- Indirect control flow, dynamic bank switching and ambiguous code/data
  boundaries remain explicit gaps.
- A missing observation is not evidence that a pattern is unused.
- The catalog reports observed counts and unknown scope, not a whole-game
  completeness percentage.
- Optional new family-disjoint ROMs are quarantined and evaluated separately
  after the engine freeze; their absence is not a v0.9 blocker.
