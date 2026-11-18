# Mesen HD Studio

HD Studio is an addition to this fork of MesenCE that automates the first steps of making an HD pack for an NES game:
it plays the game without a window, records every tile that is drawn, detects the objects on screen (metasprites)
and writes a report saying which PNG images to draw and at what size.

HD packs are a Mesen feature for **NES games only**.

## Stage 1: capture + report

```
Mesen.exe --hdstudio game.nes [movie.mmo|movie.bk2] [script.lua] [options]
```

| Option | Default | Description |
|---|---|---|
| `--scale=N` | `4` | HD scale. Each 8×8 tile becomes an image of 8N×8N pixels |
| `--filter=NAME` | `xbrz` | Filter used to pre-upscale the captured tiles: `xbrz` (2-6×), `hqx` (2-4×), `scale2x` (2-4×), `prescale` (no smoothing, 1-10×) |
| `--frames=N` | `18000` | Frames to emulate when no movie is given (60 frames = 1 second) |
| `--out=FOLDER` | `HdPacks/<rom name>` | Output folder. The default is Mesen's HD pack folder, so the pack is used automatically when the game is opened in Mesen |
| `--timeout=SEC` | `1800` | Maximum real time for the capture |
| `--reportonly` | | Only regenerate the report from an existing capture |
| `--verify` | | Check the HD pack instead of capturing (see below) |
| `--shots=N` | `4` | Number of screenshots taken by `--verify`, spread over `--frames` (default 1200 for `--verify`) |

How the game is played during the capture:

* **Movie (`.mmo` or BizHawk `.bk2`)**: recommended. The movie is a recording of the controller inputs, so the emulator
  replays the exact same game session (a full playthrough covers the whole game). The capture ends with the movie.
* **Lua script**: any Mesen Lua script can press buttons, change the game state, etc.
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
* `HdStudio/objects.txt`, `tiles.txt`, `capture.txt`: raw capture data (merged between sessions)

## Checking that an HD pack works

```
Mesen.exe --hdstudio game.nes [movie.mmo|movie.bk2] --verify
```

Plays the game twice from power-on (with and without HD packs) and stops both runs on exactly the same frames
(fast-forward, then frame by frame). It prints whether the pack is active (an active pack renders at 256×240 × scale,
e.g. 1024×960 at 4×) and writes `HdStudio/verify/verify.html` with the original and HD screenshots side by side.
The pack must be in Mesen's `HdPacks/<rom name>` folder, which is where Mesen looks for it (the default `--out`).

In the Mesen window you can also check it by hand: open the game and the picture uses the HD tiles (Mesen only shows
a "[HDPack] Loaded with N errors" message when the pack has problems). Uncheck *Enable HD packs* in the NES settings
and reload the game to compare with the original.

## Roadmap

1. **Capture + report** (this stage)
2. Automatic game profile: RAM search (player position, level, lives), OAM tracing to extract the metasprite tables, Code/Data Logger
3. Import external profiles (RAM maps, disassemblies) and cc65 `.dbg` files for homebrew
4. Automatic placement of the redrawn PNGs into `hires.txt` and side-by-side original/HD view
