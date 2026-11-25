# HD Studio validation (2026-11-25)

> Project documentation and this matrix with commentary:
> <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio/blob/main/docs/validation.md>

## Current headless matrix

Method: 25 ROMs from `D:\NesFlasher\ROMs`, each one with
`Mesen.exe --hdstudio ROM --generate --frames=300 --timeout=600` followed by
`--assets`, in a **fresh output folder**. Raw results:
`D:\MesenHighWork\validation-packs-final\matrix-results-final.json`.

| ROM | Mapper | CHR | Tiles in pack | Seen | Assets | Status |
|---|---:|---|---:|---:|---:|---|
| Adventures in the Magic Kingdom (USA).nes | 1 | 128 KB | 7558 | 382 | 410 | ok |
| Batman - Return of the Joker (USA).nes | 69 | 256 KB | 15729 | 262 | 401 | ok |
| Batman .nes | 69 | 256 KB | 15729 | 262 | 401 | ok |
| Don Doko Don 2 (Japan).nes | 33 | 256 KB | 14554 | 247 | 404 | ok |
| Guardian Legend, The (USA).nes | 2 | CHR-RAM | n/a | n/a | 0 | ok |
| Ice Climber (U) [!].nes | 0 | 8 KB | 501 | 189 | 308 | ok |
| JuJu Densetsu (Japan).nes | 4 | 256 KB | 13679 | 278 | 401 | ok |
| Little Mermaid - Ningyo Hime (Japan).nes | 2 | CHR-RAM | n/a | n/a | 9 | ok |
| Panic Restaurant (E) [!].nes | 4 | 128 KB | 6669 | 237 | 400 | ok |
| Soreike! Anpanman - Minna de Hiking Game! (Japan).nes | 4 | 128 KB | 6706 | 531 | 404 | ok |
| Super Mario Bros. 3 (Japan) (Rev A).nes | 4 | 128 KB | 7721 | 52 | 416 | ok |
| Super Mario Bros. 3 (USA).nes | 4 | 128 KB | 7720 | 52 | 416 | ok |
| Teenage Mutant Ninja Turtles 2 (U) [p1].nes | 4 | 256 KB | 15557 | 208 | 401 | ok |
| Teenage Mutant Ninja Turtles II - The Arcade Game (USA).nes | 4 | 256 KB | 15557 | 208 | 401 | ok |
| Tiny Toon Adventures (Japan).nes | 23 | 128 KB | 7410 | 290 | 406 | ok |
| Toki (USA).nes | 4 | 256 KB | 13692 | 174 | 403 | ok |
| Zombie Hunter (J).nes | 1 | 32 KB | 1984 | 199 | 403 | ok |
| anp.nes | 96 | CHR-RAM | n/a | n/a | 0 | ok |
| anpanman.nes | 96 | CHR-RAM | n/a | n/a | 0 | ok |
| mickey.nes | 4 | 128 KB | 8051 | 378 | 402 | ok |
| mm1.nes | 2 | CHR-RAM | n/a | n/a | 9 | ok |
| mmouh.nes | 3 | 32 KB | 2036 | 175 | 404 | ok |
| mnb.nes | 0 | 8 KB | 510 | 93 | 403 | ok |
| test.nes | 30 | CHR-RAM | n/a | n/a | 149 | ok |
| twinbee.nes | 3 | 16 KB | 977 | 84 | 401 | ok |

## Summary

- **25/25 runs completed**, with no capture failures or timeouts.
- **19/19 CHR-ROM games: 100% of the non-blank CHR patterns are in the pack**
  (with the wildcard palette fallback for unseen palettes). This is cartridge
  coverage, not semantic coverage.
- **6 CHR-RAM games** captured only the patterns actually observed. The report
  does not claim 100% cartridge coverage.
- Seen coverage with 300 frames is intentionally low (52–531 tiles); use a
  movie, `--frames=18000`, or the deep explorer for a serious capture. The
  tile guarantee does not depend on frame count.
- Automatic asset discovery produced **7,750 records** in this short pass
  (7,503 background blocks, 247 generic metasprites). This is not a semantic
  bestiary, and SMB3 is still the only game with a profile-specific object
  atlas / level maps.
- Average capture time was **7.0 s** per ROM (300 frames, headless); the
  slowest was 9.3 s. This includes emulator start, exploration, PNG output and
  the report.

## Actual replacement validation (not only capture)

| Case | Result | Evidence |
|---|---|---|
| SMB3 Goomba, 4× | Replacement visible in real gameplay; full 16×16 logical object | `mesen-home\HdPacks\SMB3_HdStudioTest\HdStudio\verify` |
| SMB3 background block | Asset imported and pack built | same project |
| Ice Climber 16×16 enemy / irregular 32×30 composition | 24 cells (12 tiles × 2 palettes), visible in movie playback, mapper 0 | `mesen-home\HdPacks\IceClimberHdVerify\HdStudio\verify` |
| Synthetic 8×16 sprite mode | Four 32×32 cells at 4×; player is exactly 16×16 logical | `mesen-home\HdPacks\hdstudio_test_8x16\HdStudio\verify` |
| Synthetic mirror scene | Same pattern+palette replaced independently for H-flipped and V-flipped enemies | `mesen-home\HdPacks\hdstudio_test_mirror\HdStudio\verify` |
| Synthetic CHR-RAM scene | Runtime content keys and an animated CHR update captured/replaced | `mesen-home\HdPacks\hdstudio_test_chrram\HdStudio\verify` |
| Synthetic background metatile | 16×16 block replaced; shared ground tiles are replaced too, as documented | `mesen-home\HdPacks\hdstudio_bgtest\HdStudio\verify` |

Gameplay-state hashes (CPU RAM + nametables + OAM + palette + PPU memory) were
identical between the HD and original passes at every checkpoint for the
SMB3 movie run and all synthetic runs.

## Reproduce

```bat
python scripts\validate_matrix.py --rom-dir "D:\NesFlasher\ROMs" ^
  --out-root "D:\MesenHighWork\validation-packs-final" --frames 300

python scripts\test_hdstudio_geometry.py --home D:\MesenHighWork\mesen-home
python scripts\test_hdstudio_project.py --rom "...\Super Mario Bros. 3 (USA).nes" ^
  --pack "...\mesen-home\HdPacks\Super Mario Bros. 3 (USA)" --image "...\input\user_1.png" --asset 114
python scripts\test_importer.py --rom "...\Super Mario Bros. 3 (USA).nes" ^
  --pack "...\mesen-home\HdPacks\Super Mario Bros. 3 (USA)" --image "...\input\user_0.png" --asset 114
python scripts\test_gate1_mapper.py --rom "...\Super Mario Bros. 3 (USA).nes"
python scripts\test_synthetic_suite.py --home D:\MesenHighWork\mesen-home
```
