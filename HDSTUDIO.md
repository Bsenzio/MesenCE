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
* `HdStudio/report.html`: the report: what to draw, sizes, objects, cartridge coverage
* `HdStudio/report.json`: the same data for other tools
* `HdStudio/templates/obj_NNN_WxH.png`: one template per detected object, already at the final HD size
* `HdStudio/chr_coverage.png`: every tile of the cartridge (CHR-ROM games), in red the ones that were never displayed
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

## Where Mesen keeps its files (environment variable `MESEN_HOME`)

Mesen normally stores `settings.json`, the HD packs, save states, screenshots, etc. in `Documents\MesenCE`
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
