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

## Automatic evidence-gap resolution

The graph does not stop at a list of recommended actions. A bounded native
resolver selects an analysis for each open gap, executes it, and writes
`HdStudio/archaeology/gap-resolutions.json` with a terminal outcome and a
reason. Examples:

- palette tables are validated against observed palettes;
- PRG 2bpp streams are matched byte-for-byte to CHR/runtime patterns;
- CHR-RAM upload routines are isolated from reachable `$2006`/`$2007` writes;
- cross-bank targets are mapped to one physical PRG window or reported
  ambiguous;
- pointer tables must pass a consistency bound;
- runtime `objects.txt`, `live-sprites.tsv`, `bg_observed.tsv` and
  `live-background-frames.tsv` reconstruct observed compositions, animation
  identities, 16x16 metatiles and observed nametable placements;
- maps and animation frames without a runtime witness remain explicitly
  unresolved; adjacent CHR bytes are never promoted into sequences.

A resolved gap is not a claim that the whole game is reconstructed. The status
ladder and the `reconstructionScope` property keep "observed composition",
"observed metatile" and "observed map placement" separate from a static decoder.

## Paginated catalogs

The previous 64-asset full-library cap is removed. `deep` and `exhaustive`
runs write every discovered nonblank pattern. `catalog.json` is page 0;
additional pages are written to `archaeology/catalog-pages/page-NNNN.json`
(512 assets per page). The native workspace loads pages progressively and
materializes a selected deep-page asset into `assets.json` on demand, so large
inventories do not duplicate the evidence store. A per-ROM asset cap remains
available only when the caller explicitly asks for one (`--arch-assets=N`);
`--arch-assets=0` means all.

## Modes and budgets

| Mode | Catalog assets | PRG stream cap | Structural instruction cap | Per-ROM time cap |
|---|---:|---:|---:|---:|
| Quick | 256 | 16 | 150,000 | 60 s |
| Deep | all (paginated) | 128 | 600,000 | 180 s |
| Exhaustive | all (paginated) | 512 | 1,800,000 | 600 s |

Batch mode uses the same paginated catalog and no longer applies a fixed
64-asset cap. Budgets are finite; stopping at a budget is not proof of
saturation or completeness. `--arch-gap-seconds=N` and `--arch-no-resolve`
control the automatic resolver.


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
