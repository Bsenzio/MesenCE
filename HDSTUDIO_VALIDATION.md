# HD Studio validation

**Current release:** v0.8 (2026-11-27)  
**Machine-readable evidence:** `D:\RetroArcDo_HDStudio\validation\v0.8\` after publication.

v0.8 keeps the v0.7.1 evidence as the historical baseline and adds staged adaptive
recovery, background-only fallback, emphasis-aware baseline verification,
multi-asset combined packs and true per-frame animation identity.

## v0.8 headline

| Check | Actual |
|---|---:|
| Verified unique ROMs after recovery | 1,268 / 1,291 (98.2%) |
| Development / validation / holdout | 758/772 / 317/324 / 193/195 |
| Non-holdout recovery | 194 / 213 |
| Holdout recovery | 37 / 39 |
| Distinct background replacement rows | 328 |
| Animation frame-identity packs | 43 / 60 |
| Multi-asset combined packs | 19 / 30 |
| Regression suite | 17 / 17 |
| Offline acceptance incl. stock Mesen 2.1.1 | passed |
| Remaining unresolved | 15 no-visible, 3 partial, 3 failed, 2 unsupported mapper |

See `docs/FINAL_ENGINEERING_REPORT_v0.8.md`, `docs/VALIDATION_v0.8.md` and
`validation/v0.8/metrics-v08.json`.

The v0.6 numbers below remain historical until reproduced. v0.7 adds render-time
background evidence, automatic standard context plans, multi-scene exact replay
and native Library/Batch workspaces.

## What is measured

| Area | Method |
|---|---|
| Library | v0.7 inventory updater keeps the frozen splits; new SHA-256 values go to development only |
| Capture | Deterministic live capture, render-time sprite and background evidence, per-frame TSV |
| Replacement | Hardware picking, isolated minimal pack, marker artwork, exact frame replay, per-occurrence bbox, HD/original state hashes, ROM SHA-256 |
| Context | Intended/unrelated cells across captured scenes, standard-only condition syntax, render-order restriction |
| Background | Render-time tile identity; rare-object selection preferred over repeated terrain |
| Animation | Observed animation sequences and multi-frame replay harness |
| Integrity | ROM hash before/after every row |
| Compatibility | Official Mesen 2.1.1 accepts representative standard packs |

## Result classes

- `verified-independent-multi-scene` - no shared keys in the captured evidence,
  multiple exact frames, zero marker outside.
- `verified-context-independent-multi-scene` - standard rule matches all intended
  occurrences across at least two captured scenes and no unrelated cells.
- `verified-scene-isolated` - real replacement at one verified scene or an absolute
  position rule; not global.
- `verified-...shared...` - same hardware keys/marker pixels change elsewhere;
  explicit shared measurement, never counted as independent.
- `failed-context-unresolved`, `failed-context-rule`, `failed-not-replaced`,
  `failed-no-visible-nonblank-sprite`, `failed-verify`, `partial-*` - failures are
  retained with their category.

## v0.7 headline

| Check | Actual |
|---|---:|
| Main development batch | 300 attempted / 240 verified / 240 unique ROMs |
| Combined evidence | 286 verified rows / 266 unique ROMs / 254 base titles |
| Strict independent rows | 13 |
| Context-independent multi-scene rows | 18 |
| Scene-independent shared-key rows | 211 |
| Explicit shared rows | 26 |
| Background pass | 10 attempted / 9 verified / 9 unique games |
| Homebrew candidates | 16 / 22 rows |
| Frozen holdout replacement subset | 13 / 16 rows |
| Animation runtime | 3 tested / 0 multi-frame verified |
| ROM integrity rows | 306 / 306 passed |
| Scale mix | 298 rows at 2x / 8 cached rows at 4x |

The 300-unique-game target was missed by 34; the 75-background and 30-animation
targets were not achieved. Counts separate unique games, rows and graphical assets.
See `docs/FINAL_ENGINEERING_REPORT_v0.7.md` and `validation/v0.7/metrics.json`.

## Rendering-time background proof

Synthetic NROM fixture: 260 visible background cells in the paused frame and
1,040 rendered per-frame rows, with `backgroundEvidence: "render-time"`. The
regression test fails if the recorder falls back to end-of-frame state.

## Context planner proof

Offline synthetic fixture: a vertical two-tile composition is found in two scenes;
the planner emits a standard `spriteNearby` rule, matches 4/4 intended cells,
matches 0 unrelated cells and never emits a below/right offset.

## Honest limits

- Not all shared hardware keys can be isolated. Unresolved and shared rows are
  reported.
- The main throughput batch uses 2x; representative evidence uses 4x.
- CHR-RAM context plans are measured but the main batch applies unconditional
  entries, so those rows remain shared-key labelled.
- No physical human-controller session was profiled.
- Animation runtime coverage is limited to observed sequences in tested packs.

## Commands

```powershell
# Context plan for a picked asset
Mesen.exe --hdstudio --asset=picked:ID --propose-context=plan.json --out=PACK

# Apply the plan during import
Mesen.exe --hdstudio game.nes --import --asset=picked:ID --image=art.png `
  --condition-map=plan.json --minimal --out=PACK

# Replay several exact frames
Mesen.exe --hdstudio game.nes deterministic_input.lua --verify `
  --verify-at=1210,1400,1800 --out=PACK
```
