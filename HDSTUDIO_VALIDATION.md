# HD Studio validation (v0.2 Alpha, 2026-11-25)

> Project documentation and this matrix with commentary:
> <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio/blob/main/docs/validation.md>

## Fresh generic matrix (300 frames, profiles disabled)

Method: all 25 ROMs in `D:\NesFlasher\ROMs`, each with
`Mesen.exe --hdstudio ROM --generate --bgscan --frames=300 --timeout=300`
followed by `--assets`, in a fresh folder. Raw results:
`D:\MesenHighWork\validation-packs-v02\matrix300\matrix-results.json`.

| Metric | Result |
|---|---:|
| ROM entries | **25** |
| Completed captures | **25 / 25** |
| CHR-ROM ROMs | 19 |
| CHR-ROM with 100 % non-blank patterns in pack | **19 / 19** |
| CHR-RAM ROMs (observed coverage only) | 6 |
| Automatic catalog records | **14,707** |
| Generic metasprites | 248 |
| Background blocks (including observed runtime blocks) | 14,459 |
| Average 300-frame capture | **8.5 s** |
| Slowest 300-frame capture | **14.6 s** |
| Failures/timeouts | **0** |

The asset count grew from 7,750 in the prior 300-frame pass to 14,707. Most of the
growth is observed runtime background blocks from `--bgscan`; it is not a semantic
bestiary. Four CHR-RAM entries still have very few or zero assets in 300 frames
(`anp`, `anpanman`, `Little Mermaid`), which is reported exactly rather than
inflated.

## Deep scans (beyond the 300-frame matrix)

| Game | Mapper | CHR | Frames | Observed patterns | Pack patterns | Combos | Assets | Sprites | Backgrounds |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|
| Batman - Return of the Joker (USA) | 69 | ROM 256 KB | 3,542 | 1,807 | 15,729 | 18,470 | 3,545 | 640 | 2,905 |
| Don Doko Don 2 (Japan) | 33 | ROM 256 KB | 3,450 | 587 | 14,554 | 15,749 | 1,938 | 30 | 1,908 |
| Toki (USA) | 4 | ROM 256 KB | 3,633 | 175 | 13,692 | 13,701 | 618 | 5 | 613 |
| Guardian Legend, The (USA) deep | 2 | CHR-RAM | 3,603 | observed | observed | 277 | 466 | 2 | 464 |
| Guardian Legend, The (USA) replay | 2 | CHR-RAM | 2,402 | observed | observed | 1,328 | 843 | 0 | 843 |

“Observed patterns” for CHR-RAM means runtime content, not cartridge patterns.

### Actual replacement evidence

| Case | Result | Evidence |
|---|---|---|
| Batman background, gameplay | Passed: marker rendered on a visible wall block; state hashes identical | `mesen-home\HdPacks\Batman - Return of the Joker (USA)\HdStudio\verify` |
| SMB3 Goomba, block and player frame | Passed (baseline preserved) | `SMB3_HdStudioTest\HdStudio\verify` |
| Ice Climber irregular 32×30 composition | Passed (baseline preserved) | `IceClimberHdVerify\HdStudio\verify` |
| Synthetic 8×16 / mirror / CHR-RAM | Passed | `test_synthetic_suite.py` |
| CHR-RAM observed background block | Passed: magenta/cyan runtime block rendered; state hashes identical | `mesen-home\HdPacks\hdstudio_test_chrram\HdStudio\verify` |
| Don Doko Don 2, Toki, Guardian replacement | **Capture and catalog only**; no reliable visible replacement was proven in this pass | table above |

The failed/not-proven cases are explicit. They are not counted as successes.

## Unknown-homebrew holdout (profiles disabled)

`hdstudio_holdout.nes` is generated from the original source in
`test-roms/build_synthetic_roms.py` after the discovery/catalog parameters were
frozen. It is an NROM CHR-ROM scene with 8×16 sprites, three enemies sharing the
same pattern+palette in H, V and both-axis flip contexts, a 2×2 background
metatile and an animated player. No SMB3 profile, disassembly, symbol file or
ground-truth manifest is passed to the engine.

`python scripts/evaluate_holdout.py --pack PACK` result:

| Metric | Result |
|---|---:|
| Profiles disabled | Yes (0 semantic objects / animation frames) |
| Expected hardware (pattern+palette) combinations | 16 |
| Observed combinations | 16 |
| Hardware-combination recall | **1.00** |
| Hardware-combination precision | **1.00** |
| Expected compositions `"" / H / V / HV` | 4 |
| Found compositions | 4 |
| Composition recall | **1.00** |
| Background block recalled | **Yes** (patterns 101,102,111,112 / palette 21071727) |
| Unclassified partial metasprites | 2 (sub-compositions); tiles are still ground-truth patterns |

This is a controlled fixture, not evidence of complete semantic discovery for
arbitrary commercial games.

## Stock Mesen compatibility (official release)

| Item | Value |
|---|---|
| Emulator | Official **Mesen 2.1.1** Windows release |
| Source asset | `Mesen_2.1.1_Windows.zip` from `SourMesen/Mesen2` |
| ZIP SHA-256 | `23ccc2bc060b663c68dad3a8c5d6da7d23a50f872d04f135bafa2b04ff7d5cbe` |
| EXE SHA-256 | `d2eb03c2590c648bf329f127ebcfefd70130e7690a9e2ccdba8616faea1fe96b` |
| Packs tested | holdout minimal (both-axis condition), synthetic example, SMB3 full |
| Result | **Passed** |

The holdout pack contains only:

```
[hmirror&vmirror]<tile>0,07,FF0F1A2A,0,0,1,N
...
```

In stock Mesen only the rightmost, both-flipped enemy changed; the H-only and
V-only enemies stayed original. This proves the condition is parsed and applied
by unmodified Mesen, not by a fork-only extension. The SMB3 full pack loaded in
stock Mesen 2.1.1 without crashing.

Reproduce:

```bat
python scripts\test_stock_mesen.py --rom ROM --pack PACK --shots OUTPUT
```

## Test results

| Test | Command | Result |
|---|---|---|
| Geometry/adaptation self test | `Mesen.exe --hdstudio --selftest` | **165 passed, 0 failed** |
| Managed project/compiler | `test_hdstudio_project.py ... --asset 114` | **Passed**; deterministic rebuild, copied project, draft, conflict stop, minimal export |
| Export workflow | `test_hdstudio_export.py` | **Passed**; full/minimal, Unicode path, backup, refusal, install, ROM integrity |
| Shared-pattern safety | `test_hdstudio_safety.py` | **Passed**; both-axis condition, unapproved shared key refused, approval, conflict stop, pack preserved |
| Discovery sessions | `test_hdstudio_discovery.py` | **Passed**; 600 → 1200 frame resume, restart, snapshots, stopping reasons |
| Sprite-sheet slicer | `test_hdstudio_slicer.py` | **Passed**; 4 frames, margins/spacing, derived grid, trim, invalid grid |
| Synthetic suite | `test_synthetic_suite.py` | **5/5 variants**, CHR-RAM background block observed, holdout both-axis |
| Holdout evaluation | `evaluate_holdout.py` | **1.00 precision/recall**, composition recall 1.00 |
| Legacy importer | `test_importer.py` | **Passed** |
| Mapper audit | `test_gate1_mapper.py` | **36/36 rows**, 0 failures |
| 25-ROM matrix | `validate_matrix.py --frames 300` | **25/25**, 19/19 CHR-ROM coverage, 6 CHR-RAM honest |
| Stock Mesen | `test_stock_mesen.py` | **Passed** on Mesen 2.1.1 |

## Limitations confirmed in this pass

- Live capture from an interactive Mesen session is not implemented.
- The GUI sprite-sheet assignment path was exercised against a project copy: four
  sliced frames were assigned to the first four sequence frames as drafts and
  copied into project artwork. The automated CLI slicer test also passes.
- Don Doko Don 2, Toki and Guardian Legend were captured deeply but no reliable
  visible authored replacement was proven for them in this pass.
- Saturation is not completeness. Commercial semantic denominators remain
  unknown.
- `spriteNearby`, `tileAtPosition`, memory checks and other standard conditions
  are supported by Mesen's parser but the compiler only emits mirror and
  background-priority conditions automatically. They remain advanced/manual.
