# HD Studio workflow validation (2026-11-23)

Method: 25 ROMs from `D:\NesFlasher\ROMs`, each one with
`Mesen.exe --hdstudio ROM --generate --frames=300 --timeout=300`.
The full `hdstudio_workflow.cmd` (capture + maps + merge + report + verify) was also run
end-to-end on SMB3, Batman ROTJ, Guardian Legend (CHR-RAM) and the test ROM.

| ROM | CHR | Tiles in pack | Seen | Pack % | Objects detected | Status |
|---|---|---:|---:|---:|---:|---|
| Adventures in the Magic Kingdom (USA).nes | 128 KB | 7558 | 2320/7558 | 100% | 200 | ok |
| Batman - Return of the Joker (USA).nes | 256 KB | 15729 | 5184/15729 | 100% | 200 | ok |
| Batman .nes | 256 KB | 15729 | 262/15729 | 100% | 1 | ok (retested with `--out`, trailing-space name) |
| Don Doko Don 2 (Japan).nes | 256 KB | 14554 | 247/14554 | 100% | 3 | ok |
| Guardian Legend, The (USA).nes | CHR-RAM | n/a | n/a | n/a | 0 | ok |
| Ice Climber (U) [!].nes | 8 KB | 501 | 340/501 | 100% | 122 | ok |
| JuJu Densetsu (Japan).nes | 256 KB | 13679 | 278/13679 | 100% | 1 | ok |
| Little Mermaid - Ningyo Hime (Japan).nes | CHR-RAM | n/a | n/a | n/a | 9 | ok |
| Panic Restaurant (E) [!].nes | 128 KB | 6669 | 237/6669 | 100% | 1 | ok |
| Soreike! Anpanman - Minna de Hiking Game! (Japan).nes | 128 KB | 6706 | 531/6706 | 100% | 4 | ok |
| Super Mario Bros. 3 (Japan) (Rev A).nes | 128 KB | 7721 | 52/7721 | 100% | 8 | ok |
| Super Mario Bros. 3 (USA).nes | 128 KB | 7720 | 957/7720 | 100% | 71 | ok |
| Teenage Mutant Ninja Turtles 2 (U) [p1].nes | 256 KB | 15557 | 208/15557 | 100% | 2 | ok |
| Teenage Mutant Ninja Turtles II - The Arcade Game (USA).nes | 256 KB | 15557 | 208/15557 | 100% | 2 | ok |
| Tiny Toon Adventures (Japan).nes | 128 KB | 7410 | 290/7410 | 100% | 6 | ok |
| Toki (USA).nes | 256 KB | 13692 | 4019/13692 | 100% | 200 | ok |
| Zombie Hunter (J).nes | 32 KB | 1984 | 199/1984 | 100% | 3 | ok |
| anp.nes | CHR-RAM | n/a | n/a | n/a | 0 | ok |
| anpanman.nes | CHR-RAM | n/a | n/a | n/a | 0 | ok |
| mickey.nes | 128 KB | 8051 | 378/8051 | 100% | 3 | ok |
| mm1.nes | CHR-RAM | n/a | n/a | n/a | 9 | ok |
| mmouh.nes | 32 KB | 2036 | 175/2036 | 100% | 4 | ok |
| mnb.nes | 8 KB | 510 | 93/510 | 100% | 3 | ok |
| test.nes | CHR-RAM | n/a | n/a | n/a | 49 | ok |
| twinbee.nes | 16 KB | 977 | 84/977 | 100% | 1 | ok |

## Summary

- **25/25 runs completed** (0 timeouts).
- **18/18 CHR-ROM games: 100% of the non-blank CHR patterns are in the pack**
  (with the wildcard fallback for palettes not seen).
- **7 CHR-RAM games**: only the tiles actually shown can be captured; the report says so and
  does not claim a 100% cartridge coverage.
- Seen coverage with only 300 frames varies from 0.6% to 33%; use a movie (`--movie=`) or
  `--frames=18000` for a serious capture. The tile guarantee does not depend on the frames.
- Detected objects vary (0–200 with 300 frames); `sheet_objects.png` is **not** a bestiary.
  SMB3 is the only game with exact level maps/backgrounds (disassembly parser).

Reproduce: `hdstudio_workflow.cmd "game.nes" [--disasm=FOLDER] [--movie=FILE] [--frames=N]`.
Logs: `workflow_logs` in the current folder; raw matrix: `tmp/validation_all.json`.