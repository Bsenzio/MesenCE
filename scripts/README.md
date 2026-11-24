# HD Studio regression scripts

These scripts exercise the mapper-aware capture and the generalized user-art
importer against a local, legally supplied ROM. No ROM, screenshot or HD pack is
included in the repository.

## Gate 1: mapper-aware capture

`test_gate1_mapper.py` runs the SMB3 forced-object capture for Goomba (114),
Bowser (24), Boss Bass (45) and the mushroom (13). It verifies that every row has
an absolute CHR address and that the recorded 16 pattern bytes match the ROM at
that address. The Bowser case additionally requires the real `EC2..EC7` patterns,
which the old game-specific bank approximation missed.

```bat
python scripts\test_gate1_mapper.py ^
  --rom "D:\NesFlasher\ROMs\Super Mario Bros. 3 (USA).nes"
```

## Generalized importer smoke test

`test_importer.py` copies a pack that already contains `HdStudio/sprites`,
renders/audits the atlas, exports `HdStudio/assets/assets.json`, runs the importer
in dry-run mode (previews, sheet, manifest, no `hires.txt` change) and then writes
a separate minimal pack. It checks the standard Mesen `<img>`/`<tile>` entries.

```bat
python scripts\test_importer.py ^
  --rom "D:\NesFlasher\ROMs\Super Mario Bros. 3 (USA).nes" ^
  --pack "D:\MesenHighWork\tmp\gate1\goomba2" ^
  --image "D:\art\goomba.png" ^
  --asset 114
```

Both scripts use `MESEN_HOME`/`--home` from the existing build and expect the UI
and core built with `build.cmd`.
