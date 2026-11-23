# Mesen HD Studio

HD Studio is an addition to this fork of MesenCE that automates the first steps of making an HD pack for an NES game:
it plays the game without a window, records every tile that is drawn, detects the objects on screen (metasprites)
and writes a report saying which PNG images to draw and at what size.

HD packs are a Mesen feature for **NES games only**.

## Generate (fully automatic)

```
.\hdstudio.cmd "D:\ROMs\game.nes" --generate
```

Nobody has to play: the built-in **deep explorer** (`UI/HdStudio/Scripts/explore_deep.lua`, embedded in Mesen.exe)
explores the game with savestates, unless a movie or a Lua script is given. Every time something new appears (a new
palette, a new sprite, a new screen layout) it saves the state; it plays in short random episodes and keeps going back
to the saved states that are least visited and most productive, so dying or getting stuck does not stop the
exploration. Because random play rarely finishes a level, half of the episodes start by changing 1 to 3 random bytes
of the game's RAM (level number, lives, position...). Addresses whose changes are followed by a discovery earn credit
(a lot for a new palette, i.e. a new level or area) and are chosen more often, so the explorer learns by itself which
bytes select the level. Its progress, including the best addresses found, is shown in the console
(`[lua] episode N: ...`). Then the recorder
captures every tile with its real colors, and then **every tile stored in the cartridge (CHR-ROM)** is added to the
pack too, including the ones that never appeared on screen. Those get the palette of the closest tile of the same
bank that was seen (tiles of one character are usually stored together); if that guess is wrong the tile simply never
matches and the game shows the original graphics, so it cannot break the picture. The pack also enables
`automaticFallbackTiles`: tiles with identical graphics stored at another address reuse the same HD image.

Results (in addition to the files listed below): a pack with 100% of the cartridge tiles, and
`HdStudio/sheet_objects.png` + `sheet_objects.json`, one sprite sheet with every detected character/object at its
final HD size and the list of original tiles that compose each one (the sheet to redraw and load in HD Studio).

The studio also **reads the game's code** (PRG-ROM) looking for palette tables: groups of 4 color indexes
(`$00-$3F`) with the same backdrop color repeated every 4 bytes, usually in blocks of 16 or 32 bytes. They are
listed in `HdStudio/palettes.txt` and in the report (with color swatches, how many times each one appears in the
ROM and its offset). They are also used to colorize the tiles of a 4 KB CHR bank that was never displayed at all,
instead of a gray scale. Tiles next to a tile that was seen keep using the palette of their neighbour (usually the
same character, so the best guess available).

Not every game stores its palettes this way: some compute them at runtime (for example from brightness/fade
tables), so the scan can find only candidate tables. The report shows which of the found palettes were **actually
seen** during the capture, which is the quickest way to tell whether the game uses plain palette tables (and then
the scan is exact, e.g. SMB3) or not (e.g. Batman, which generates its palettes in code).

Games that use CHR-RAM keep their graphics compressed inside the program code: for those only what is displayed
while playing can be captured (a movie of a full playthrough gives the best result).

## Capture + report

```
hdstudio.cmd game.nes [movie.mmo|movie.bk2|movie.fm2] [script.lua] [options]
```

`hdstudio.cmd` (in the repository root) runs `bin\win-x64\Release\Mesen.exe --hdstudio ...` and waits until it
finishes. Calling `Mesen.exe` directly also works, but Mesen is a windowed program, so PowerShell returns to the
prompt immediately; add `| Out-Host` to make it wait. Paths with spaces must be quoted, for example:

```
.\hdstudio.cmd "D:\ROMs\Batman - Return of the Joker (USA).nes" UI\HdStudio\Scripts\explore.lua --frames=18000
```

| Option | Default | Description |
|---|---|---|
| `--scale=N` | `4` | HD scale. Each 8×8 tile becomes an image of 8N×8N pixels |
| `--filter=NAME` | `xbrz` | Filter used to pre-upscale the captured tiles: `xbrz` (2-6×), `hqx` (2-4×), `scale2x` (2-4×), `prescale` (no smoothing, 1-10×) |
| `--frames=N` | `18000` | Frames to emulate when no movie is given (60 frames = 1 second) |
| `--out=FOLDER` | `HdPacks/<rom name>` | Output folder. The default is Mesen's HD pack folder, so the pack is used automatically when the game is opened in Mesen |
| `--timeout=SEC` | `1800` | Maximum real time for the capture |
| `--log=FILE` | | Also write the messages to a file (needed when the output is redirected: Mesen is a windowed program) |
| `--generate` | | Fully automatic mode (see above) |
| `--levels` | | Visit every level using the level select cheats of the cheat database (see below); `--frames` is per level |
| `--sweep` | | With `--levels`: reveal each whole level by moving the player position in RAM (experimental, see below) |
| `--disasm=FOLDER` | | Read a game disassembly (NESASM style `.asm` files) and use the game's own data to know which tiles its levels reference (see below) |
| `--maps` | | With `--disasm`: build a full PNG map of every level by running the game's own level loader, and count the exact background tiles each level uses (see below) |
| `--reportonly` | | Only regenerate the report from an existing capture |
| `--verify` | | Check the HD pack instead of capturing (see below) |
| `--shots=N` | `4` | Number of screenshots taken by `--verify`, spread over `--frames` (default 1200 for `--verify`) |

How the game is played during the capture:

* **Movie (`.mmo`, BizHawk `.bk2` or FCEUX `.fm2`)**: recommended. Most NES TASes published on TASVideos are `.fm2`
  or `.bk2` files (`.fm2` support was added in this fork). The movie must have been recorded with the same ROM
  version (compare the SHA-1 in the movie header), otherwise it can desync. The movie is a recording of the controller inputs, so the emulator
  replays the exact same game session (a full playthrough covers the whole game). The capture ends with the movie.
* **Lua script**: any Mesen Lua script can press buttons, change the game state, etc.
  `UI/HdStudio/Scripts/explore.lua` is a generic explorer: it presses Start/A when nothing moves on screen
  (title, menus, pause, continue screens) and otherwise plays with seeded random inputs (mostly walking right,
  jumping and attacking). It reaches the first levels of most action games, but a movie covers much more.
* **Nothing**: the game runs with no input for `--frames` frames (useful for title screens / attract mode).

Running the command again on the same game **adds** to the existing pack: tiles, objects and statistics are merged,
so the pack can be completed by several sessions (for example one movie per level).

## Output

In the pack folder:

* `hires.txt` + `Chr_*.png`: a working HD pack (tiles upscaled with the selected filter)
* `HdStudio/report.html`: the report (in English): what to draw, sizes, objects, cartridge coverage, what is missing
* `HdStudio/report.json`: the same data for other tools
* `HdStudio/templates/obj_NNN_WxH.png`: one template per detected object, already at the final HD size
* `HdStudio/chr_coverage.png`: every tile of the cartridge (CHR-ROM games), in red the ones that were never displayed
* `HdStudio/sheet_backgrounds.png`: 16×16 background blocks (4 consecutive cartridge tiles used with the same
  palette), sorted by usage: the walls, floors, bricks and water of the levels, to redraw as whole pieces
  (`sheet_backgrounds.json` has the layout and the 4 tiles of each block)
* `HdStudio/palettes.txt`: the palettes found in the game's code (PRG ROM), with their usage count and offset
* `HdStudio/objects.txt`, `tiles.txt`, `capture.txt`: raw capture data (merged between sessions)

## Checking that an HD pack works

```
Mesen.exe --hdstudio game.nes [movie.mmo|movie.bk2|movie.fm2] --verify
```

Plays the game twice from power-on (with and without HD packs) and stops both runs on exactly the same frames
(fast-forward, then frame by frame). It prints whether the pack is active (an active pack renders at 256×240 × scale,
e.g. 1024×960 at 4×) and writes `HdStudio/verify/verify.html` with the original and HD screenshots side by side.
The pack must be in Mesen's `HdPacks/<rom name>` folder, which is where Mesen looks for it (the default `--out`).

In the Mesen window you can also check it by hand: open the game and the picture uses the HD tiles (Mesen only shows
a "[HDPack] Loaded with N errors" message when the pack has problems). Uncheck *Enable HD packs* in the NES settings
and reload the game to compare with the original.

## Level sweep (visit every level without playing)

```
.\hdstudio.cmd "D:\ROMs\game.nes" --levels --frames=1500
```

The built-in cheat database (`UI/Dependencies/Internal/CheatDb.Nes.json`) knows how to start many games on a
given level ("Start on stage 2-1" = `0400:06` in Batman, "Start on World 3" in SMB3). With `--levels` the studio:

1. finds the game in the database (by ROM hash, or by name with a warning when the dump is different),
2. collects every level select code,
3. for each level: applies the code, **resets** the game, plays `--frames` frames with a "runner" script
   (`UI/HdStudio/Scripts/explore_level.lua`: runs right, shoots, jumps, and presses Start when the picture stops
   moving, e.g. on the title screen or a death screen),
4. saves a screenshot of each level in `HdStudio/levels/NN_<level>_start.png` and `..._end.png`,
5. merges everything into the same pack, with every CHR-ROM tile (as `--generate`).

`--frames` is **per level** in this mode (default 900). The levels that the database does not know are not visited;
the sweep keeps the same ROM/pack and can be repeated or combined with a normal `--generate` run (depth in one
area) because packs are merged. Games without level select codes fall back to the normal exploration.

### Camera sweep (`--sweep`, experimental)

`--levels --sweep` uses `UI/HdStudio/Scripts/explore_sweep.lua` instead of the runner. It waits until the game is
really moving (not the menus), looks for the RAM byte(s) that hold the player position (the player runs right and
then left; only bytes that follow both movements are kept), and **scores each candidate by correlating three screen
rows between frames**: a real scroll is a shifted copy of the previous frame, while copies of the position and
glitches score 0. It tries 8-bit and 16-bit (little endian) positions, both directions, and walks the best one
through the level so the game scrolls and the PPU draws every column.

Measured on Batman with 1600 frames per level: **6.9 %** of the CHR (the runner gets 8.1 %), 18/18 levels visited.
The problem is that the write is often overwritten by the game's own physics, and that the camera stops advancing
after ~9 screens. It stays opt-in; the runner is the default.

Lua scripts get `emu.getScroll()`: the NES horizontal scroll (fine scroll + nametable, 0-511), which is the exact
signal for a camera sweep (writing a copy of the position does not change it).

### Game data from a disassembly (`--disasm`, SMB3 tested)

If the game has a disassembly (NESASM / Southbird style `.asm` files), the tool can use the game's **own data** to
know exactly which tiles its levels use:

```
.\hdstudio.cmd game.nes --generate --disasm=D:\path\to\disassembly
```

Nothing is assembled: the tool parses the `Tile_Layout_TS*` tables (256 tiles of 16x16 per tileset, four 8x8
quadrants each), locates them **inside the ROM** by searching their bytes (so it also works when the disassembly is
for another revision), reads `Level_BG_Pages1` (CHR bank per level) and the level headers
(`LEVEL3_TILESET_n`, `LEVEL4_BGBANK_INDEX(n)`), and builds the exact list of background tiles the game references.

The report gets a **"Game data (from the disassembly)"** section and `report.json` a `gameData` object with
`referencedTiles`, `capturedReferencedTiles`, `referencedCoveragePercent`, `levelsParsed` and `tilesetsFound`.
This is only a **layout estimate**: it counts the tile layouts referenced by the level headers, not the tiles the
generators really place. For SMB3 (USA) it currently reports 2,584 layout tiles over 361 level files.

### Full level maps from the game's generators (`--maps`, SMB3)

```
.\hdstudio.cmd game.nes --maps --disasm=D:\path\to\smb3-master
```

This is the exact version of the previous check. Instead of reimplementing the generators, HD Studio **runs the
game's own level loader** in the emulator:

1. `game.xml` names every level and its tileset; the `.asm` files give the layout bytes. The tool finds each level
   in the ROM by content (so a different revision is fine) and gets the pointer to its 9-byte header and the
   `Level_Tileset` from the world context.
2. A small Lua driver runs at 60 fps, waits for the title screen, then calls the loader (`$897B` in SMB3: switch
   pages, `LevelLoad_ByTileset`) once per level with NMI and MMC3 IRQ disabled. After the call it dumps the
   6,480-byte tile memory (`$6000-$794F`, 15 screens of 16×27 tiles) and starts the next level.
3. The one broken generator in the disassembly (`LoadLevel_LittleCloudSolidRun`, whose `BEQ` should be `BNE` and
   otherwise drops into a table and hangs) is patched in the emulator's PRG copy. The map then shows what the
   designer intended; the original game's fallback is not used.
4. HD Studio paints every 16×16 tile from CHR-ROM using the tileset's `Tile_Layout_TS*` plus the 2 KB background
   windows (`Level_BG_Pages1` = patterns 0-127, `Level_BG_Pages2` = patterns 128-255) and the level's palette
   (`Palette_By_Tileset` + `LEVEL2_BGPAL_n`). One PNG per level and a `maps.json`/`maps.txt` index are written to
   `HdStudio/maps`.

Measured on SMB3 (USA), 2026-11-23:

| Metric | Value |
|---|---|
| Levels with a full map | **217** (main stages, fortresses, castles, airships, bonus rooms) |
| Non-blank background CHR patterns referenced | **1,518** of 7,720 (19.7 % of the cartridge) |
| Exact (pattern + palette) background combinations | **4,505** |
| Those combinations in the pack after `--generate` | **4,505 / 4,505 = 100 %** (4,340 were added by the map pass) |
| Old layout estimate (for comparison) | 2,584; it counted layout patterns, not the real maps |
| Not mapped | 10 "EMPTY" placeholder levels + 8 Big [?] Block sub-areas + 2 unused W8 Hammer Bro rooms + 12 pipe-junction fragments (alternate layouts entered through a pipe, not standalone levels) |

The exact number of patterns is lower than the old estimate because the previous code mapped every layout pattern
with the first CHR page (`page1*64 + pattern`). Patterns 128-255 live in the second 2 KB window (`page2`), so the
estimate was pointing at the wrong cartridge tiles for half of the table. `--maps` uses the real two-window
mapping and only counts patterns that a level actually places.

The map pass also writes `HdStudio/maps/tiles_exact.txt`: one line per `(CHR pattern, 4-color palette)` pair
that a level places. `HdPackBuilder::AddKnownLevelTiles` reads it when saving any pack and adds the missing HD
tiles using CHR-ROM + the selected filter. This is what turns "the pattern exists with a guessed palette" into
"the game finds the exact palette it uses": after one short `--generate` run the SMB3 pack has all 4,505
background combinations (the pack went from 10,962 to 15,302 tile+palette entries).

### Sprites, UI and the wildcard fallback

Backgrounds are exact now, but a sprite is drawn from the object code and its palette is selected by the OAM
attribute at runtime. The pack has all CHR patterns, and the capture has seen **587 exact sprite
(pattern + palette) combinations (382 patterns)**; the rest of the sprite patterns only had a guessed palette.
To make sure **no sprite or UI tile is left un-replaced**, the builder now marks one representative entry per CHR
pattern as `<tile>...,Y` (a *DefaultTile*): Mesen registers it under a wildcard palette key and uses it when no
exact `(pattern, palette)` entry matches. SMB3 ends with **7,831 wildcard patterns** (all patterns with an image),
while exact entries always take precedence.

This guarantees that every sprite appears in HD. SMB3 measurement after the update: **23,361 sprite combinations**
in the pack, **587 seen with their exact palette**, and **7,873 wildcard patterns**. `--maps` also generates a
**sprite palette sweep** from `tiles.txt` and `objects.txt`: every sprite pattern seen on screen or in an object
template (454 patterns) with **all 42 real sprite palettes** of the 14 tilesets, i.e. **19,068 exact
combinations, 100 % present in the pack**. The report has a
dedicated Sprites section and cards that read **100 % of the exact sprite sweep**, **100 % of the exact background
combinations** (4,505 / 4,505) and **100.0 % of the non-blank CHR tiles in the pack** (7,720); the 6,763
never-seen tiles are explicitly marked as using the wildcard colors.

The caveat is color: a sprite pattern whose exact palette was never seen uses the representative image, which
may have the wrong colors. The next step for sprites is the **object atlas**: force every object ID/frame through
the game's own drawing code and capture the OAM sprites with their exact palettes, as `--maps` does for the level
loader. A prototype already loads 1-1 from a hijacked NMI, enters the normal level-init path with a clean stack,
and forces an object into slot 0; a Goomba (ID `$72`) draws its two OAM sprites at the expected position. The
remaining work is initializing the more complex objects (bosses, platforms, power-ups), reading the local
`Sprite_RAM` buffer correctly instead of assuming the PPU OAM offset, and skipping the object IDs that are not
standalone. The experimental `--spriteid=N` mode isolates one object per Mesen run so a bad ID cannot stop the
sweep.

## Where Mesen keeps its files (environment variable `MESEN_HOME`)Mesen normally stores `settings.json`, the HD packs, save states, screenshots, etc. in `Documents\MesenCE`
(or next to `Mesen.exe` if a portable `settings.json` is found there). This fork also accepts the environment
variable **`MESEN_HOME`**, which wins over both and can point anywhere (for example a drive with more free space):

```
set MESEN_HOME=D:\MesenHighWork\mesen-home
Mesen.exe game.nes
```

The folder is created if it does not exist, and the emulator core (`MesenCore.dll`) and the rest of the native
dependencies are copied into it, exactly like in the other two cases. The HD pack folder used by default by
`--out` and `--verify` is `%MESEN_HOME%\HdPacks`, so the same launcher that sets the variable also makes Mesen find
the generated packs.

## Roadmap

1. **Capture + report** (this stage)
2. Automatic game profile: RAM search (player position, level, lives), OAM tracing to extract the metasprite tables, Code/Data Logger
3. Import external profiles (RAM maps, disassemblies) and cc65 `.dbg` files for homebrew
4. Automatic placement of the redrawn PNGs into `hires.txt` and side-by-side original/HD view
