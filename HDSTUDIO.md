# Mesen HD Studio

> **Project documentation, measured results and source snapshot:**
> <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio>
> The README includes full SMB3 maps, the named object atlas, GIFs, Mermaid diagrams, the 25-ROM validation matrix
> and the complete `master..hdstudio` patch.

## v0.6 Alpha - large-scale remastering validation and generalized engine work (2026-11-26)

Implemented in this release:

- **Exact-scene authored verification:** `--verify-at=FRAME` replays the same
  deterministic input to the frame where the object was picked. A replacement is
  only counted when the target scene matches, marker pixels appear inside the
  selected footprint, the HD pack is active and the HD/original gameplay state
  hashes are identical at that exact frame.
- **Deterministic, resolution-independent test input:**
  `scripts/deterministic_input.lua` reads the emulator frame counter and never
  reads the screen, so enabling the HD pack cannot change controller input.
- **Isolated test packs:** automated replacement tests compile a minimal pack with
  only the authored cells. Marker pixels outside the target therefore measure
  sharing/collateral instead of being hidden in the wildcard capture pack.
- **Resumable Tier-1 queue:** `scripts/hdstudio_v06_smoke.py` caches by ROM
  SHA-256 + engine binary SHA-256 + capture configuration, supports retries,
  worker counts and explicit failure categories.
- **Inventory and frozen splits:** `scripts/hdstudio_v06_inventory.py` records
  1,291 unique ROMs with mapper/submapper, CHR type, sizes, region/revision,
  multicart/homebrew flags and a frozen 60/25/15 split.
- **Background validation mode:** `--target-mode background` selects a tile that
  is provably visible in the paused screenshot, then verifies the exact scene.
- **Live capture lifecycle:** the capture loop stops as soon as the live Lua
  script reports `stopped`, saving the pack immediately instead of waiting for a
  long timeout.
- **Exploration fallback:** the deep explorer keeps a weak sustained-activity
  gameplay signal and periodic menu escape taps when the boot sequence did not
  produce a strong gameplay signature.

Known v0.6 limits: the native Studio still does not expose a full library/batch
workspace (large-scale runs use the resumable CLI queue); automatic nearby-rule
proposal and manual split/persistent custom names remain incomplete; pause-frame
background evidence is sampled at end of frame, which can disagree with the
rendered pattern table on some mapper 4 scenes. The Don Doko Don 2 background
path is verified; the SMB3 background key is not, and is reported as a failure
in the validation data.

See `HDSTUDIO_VALIDATION.md` and `docs/FINAL_ENGINEERING_REPORT_v0.6.md` in the
documentation repository for measured results.

## v0.5 Alpha - large-scale compatibility and replacement accuracy (2026-11-25)

- **Full v0.5 report:** <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio/blob/main/docs/FINAL_ENGINEERING_REPORT_v0.5.md>
- **Media gallery:** <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio/blob/main/GALLERY.md>

Implemented in this release:

- **Render-time sprite evidence:** `emu.getRenderedSpriteInfo` exposes the sprite
  tiles that actually contributed visible pixels. End-of-frame OAM is no longer
  treated as display proof. The capture covers sprite limit/flicker, off-screen
  entries, priority-hidden sprites, OAM DMA timing and dynamic CHR changes.
- **Generalized compositions:** exact 8x16 halves and 2x2 character geometry;
  no touch-only merges; 64-part hardware safety instead of the old 16-part split.
- **Sampling depth:** Efficient, Balanced, Exhaustive and Single-frame modes in
  the Studio and `--live-mode`.
- **No silent catalog caps:** configurable safety budgets, explicit truncation
  warnings, incremental browsing, raw-tile fallback, two-frame animations.
- **Advanced standard conditions:** validated `spriteNearby`, `tileNearby` and
  position condition definitions using the parser-supported Mesen syntax.
- **Expanded validation:** full local library, representative deep runs and
  independent homebrew holdout.

See `HDSTUDIO_VALIDATION.md` and the docs repository for measured results.

## v0.4 Alpha - generalized gameplay discovery and complete asset editing (historical)

Implemented in this release:

- **Real live capture:** `UI/HdStudio/Scripts/live_capture.lua` samples the running
  emulator (OAM, mapper-resolved CHR, palettes, flips, priority, scroll, mirroring,
  visible nametable) with pause/resume/stop control and a frozen frame for picking.
- **Hardware-aware picking:** OAM sprite, reconstructed composition, background
  tile/block and manual grouping; persisted as editable assets with pattern
  references, not screenshot crops (`HdStudioPicking.cs`).
- **Live asset reconstruction:** palette-variant merging, temporal animation tracks
  and scroll-aware background blocks (`HdStudioLiveAssets.cs`).
- **Context resolution:** standard `hmirror`/`vmirror`/`bgpriority` proposals with
  measured affected occurrences (`HdStudioContextResolver.cs`).
- **Core fixes:** OAM is captured even when a game disables rendering during vblank
  for OAM DMA; conservative composition grouping; explorer boot/gameplay detection.
- **API additions:** `getOamInfo`, `getBackgroundTile`, extended `getPpuInfo`
  (scroll, mirroring, CHR-RAM, pattern bases).

Measured on the previously weak cases: Guardian Legend 1 → 200 compositions
(6,000 frames), Don Doko Don 2 200, Little Mermaid 146, Toki 41. Guardian has a
verified authored background replacement at 1024×960 with identical gameplay
state hashes. Toki remains incomplete; see the report's limitations section.

## v0.3 Alpha - graphical editor redesign (2026-11-25)

- **Media gallery:** <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio/blob/main/GALLERY.md>
- **Game gallery:** <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio/blob/main/GAMES.md>
- **AI-enhanced experiments:** <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio/blob/main/AI_ENHANCEMENTS.md>
- **Full v0.3 report:** <https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio/blob/main/docs/FINAL_ENGINEERING_REPORT_v0.3.md>

The native Studio is now a graphical RetroArcDo editor. The complete report with
screenshots and limits is in the documentation repository and in
`FINAL_ENGINEERING_REPORT_v0.3.md` (docs repo).

- Central dark design system (`UI/HdStudio/HdStudioTheme.axaml`,
  `HdStudioStyles.axaml`) with Lucide icons (ISC), consistent panels, typography,
  hover/selected/disabled/focus states and accessible icon names.
- Editor shell: icon toolbar, workspace rail, thumbnail Asset Library, live
  original/replacement canvases with grid/checkerboard/zoom/pan/drag/resize/crop,
  contextual Inspector, replacement approval dock and status bar.
- Graphical discovery: record/pause/resume/stop, real paused frame, grid-aligned
  object selection, candidate matching, open in Artwork and add discovered region.
- Animation filmstrip with approve/lock, and preserved sprite-sheet slicing.
- Export screen with graphical folder selector, remembered destination, HD Pack/ZIP,
  install/play and a confirmation card.
- Responsive toolbar at 1366×768; asynchronous batched thumbnails for large catalogs.
- Crop insets and locked assets are persisted in `project.hdstudio.json`.
- Light appearance is reserved but not shipped in v0.3; dark is validated.

Regression evidence: full build 0 errors/0 warnings, 165/165 self-tests, export,
safety, discovery, slicer, project/compiler and mapper tests passed. Actual
reimagined gameplay was captured at 1024×960.

## v0.2 Alpha — generalized discovery, export and safety (2026-11-25)

This release turns the native application into a complete authoring workflow and
addresses the main correctness limitations of v0.1.

### Native user experience

- **Welcome** tab with Open ROM, Open project, the portable synthetic example,
  recent projects and a first-run guide.
- Workflow navigation: **Discover → Artwork → Animations → Backgrounds →
  Replacements → Preview → Export**.
- The asset catalog supports category, search, replacement-status and shared-key
  filters. Replaced/draft assets are labelled; unknown assets stay visible.
- **Artwork Workshop** keeps the original NES appearance next to the HD preview,
  with source art, crop/fit/anchor/offset/zoom/flip controls, all-palette scope,
  mirror and background-priority conditions, explicit shared-key approval, undo
  via draft state and approval.
- **Preview** shows the last real original/HD verification frames and can build,
  install and launch the pack in Mesen, or run a deterministic verification pass.
- **Export** has a predictable destination, staging, validation, backup, install
  and Open/Copy/Play actions.

### Resumable discovery

`--discover=quick|deep|replay|assisted` runs complete capture chunks in separate
headless Mesen processes and merges them into the same pack. After every chunk the
semantic catalog is exported and a `HdStudio/discovery-session.json` snapshot
records frames, deterministic seed, asset/pattern growth and the stopping reason.
A second run resumes; `--discover-restart` starts again. Quick scans are labelled
incomplete. Saturation means "no measured growth for N chunks", not proof of
completeness. Live capture from an already-running interactive Mesen window is not
claimed in this version.

### Shared-pattern safety and standard conditions

- The compiler now refuses a replacement whose tile+palette keys are shared with
  other captured assets unless the project explicitly approves the shared
  replacement. Affected assets are listed in the Studio.
- Both-axis flips compile to the standard Mesen `[hmirror&vmirror]` condition;
  background priority can use `[bgpriority]`. H/V-only conditions remain
  supported. A true same-condition conflict still stops the build.
- Authored entries still precede capture entries (Mesen uses the first match).
- A failed build never overwrites the previous `hires.txt`.

### CHR-RAM background blocks

`--bgscan` loads a runtime observer (`Scripts/bg_observe.lua`) that samples
nametable, attribute and mapper-resolved pattern context. The asset exporter emits
editable 16×16 background blocks identified by their runtime pattern bytes. Blank
cells are deliberately omitted because a blank pattern is shared by every empty
area and cannot be replaced privately. This is observed runtime coverage, not a
promise that every background state was reached.

### Sprite-sheet slicing

The Animations tab can slice a source sheet by grid, frame size, margin and
spacing, preview the result and assign frames to a captured animation sequence.
Assignments are drafts until approved. The CLI equivalent is:

```bat
Mesen.exe --hdstudio --slice-sheet=sheet.png --grid=4x2 --slice-frame=64x64 ^
  --slice-margin=0 --slice-spacing=0 --slice-out=frames
```

### Export

```bat
Mesen.exe --hdstudio game.nes --export --project=PACK ^
  --export-root="D:\Packs" [--minimal] [--install] [--overwrite]
:: or an exact destination:
Mesen.exe --hdstudio game.nes --export --project=PACK --export-to="D:\Packs\My Pack"
```

Default root: `Documents\RetroArcDo HD Studio\Exports`. The exporter builds in a
staging folder, validates `<supportedRom>`, scale, every referenced PNG and the
absence of ROM files, then replaces the destination atomically. An existing pack
is backed up by default. `--install` copies the result to Mesen's `HdPacks`
folder under the ROM name.

### Stock Mesen compatibility

The standard output was tested with the official **Mesen 2.1.1** Windows release
(SHA-256 `23ccc2bc060b663c68dad3a8c5d6da7d23a50f872d04f135bafa2b04ff7d5cbe`).
A both-axis conditional replacement renders only on the both-flipped enemy in
stock Mesen, while H/V enemies remain original. See
`HDSTUDIO_VALIDATION.md` and the docs repository for screenshots and commands.

### Regression commands

```bat
python scripts\test_hdstudio_geometry.py --home D:\MesenHighWork\mesen-home
python scripts\test_hdstudio_project.py ... --asset 114
python scripts\test_hdstudio_export.py
python scripts\test_hdstudio_safety.py
python scripts\test_hdstudio_discovery.py
python scripts\test_hdstudio_slicer.py
python scripts\test_synthetic_suite.py
python scripts\evaluate_holdout.py --pack PACK
python scripts\validate_matrix.py --frames 300
python scripts\test_stock_mesen.py --rom ROM --pack PACK --shots OUTPUT
```

## Native application foundation (preserved in v0.2)

The fork now contains a native graphical application in addition to the headless pipeline:

| Item | Location |
|---|---|
| Integrated entry point | **Tools → RetroArcDo HD Studio** while a ROM is loaded |
| Standalone executable | `bin\win-x64\Release\RetroArcDoHDStudio.exe` (a copy of the same apphost) |
| Native window | `UI/Windows/HdStudioWindow.axaml` + `.axaml.cs` |
| Shared services | `UI/HdStudio/HdStudioProject.cs`, `HdStudioCatalog.cs`, `HdStudioArtwork.cs`, `HdStudioCompiler.cs` |
| Project file | `<HD pack folder>\HdStudio\project.hdstudio.json` |
| Portable package | `D:\MesenHighWork\dist\RetroArcDoHDStudio-win64.zip` (local build output) |

The window provides ROM Explorer, asset browser/search/categories, original/replacement
previews, artwork import and adaptation controls, all-palette replacement, animation frame
selection, background blocks, project replacements/approval, Build & Capture, Validation and
Project/Export tabs. Captures run in a separate headless Mesen process; edits are stored in
the project and compiled into a standard `hires.txt` + PNG pack.

### Compiler precedence (important correctness fix)

Mesen checks candidate tile entries in file order and uses the first one whose condition
matches. The compiler now inserts authored replacements **before** capture-generated tile
entries. Without this, an exact `(pattern, palette)` row written by the capture shadowed the
user's art: the pack reported success but the replacement never appeared on screen. This was
reproduced on the synthetic CHR-ROM scene and on SMB3, then fixed and regression-tested.

### Project portability

`project.hdstudio.json` stores the ROM path relative to the pack folder when possible and
rebases the pack folder from the project file location on load. Copying or unzipping a
project/pack folder no longer writes builds back into the original folder. A regression test
copies an approved project to a second pack folder and verifies that the original `hires.txt`
is byte-identical afterwards.

### Geometry and self test

`Mesen.exe --hdstudio --selftest` runs 165 offline checks with no ROM or emulator:
logical-to-physical dimensions at 1–8×, 8×8 grid alignment, four-tile segmentation, quadrant
colors, mirror normalization, irregular compositions, oversized contain/stretch, transparent
pixels, palette association and stable ids.

### Verification state hash

`--verify` now hashes CPU RAM, nametables, OAM, palette RAM and PPU memory at every
checkpoint in both passes. With a deterministic movie or fixed input script it reports:

```
Gameplay state comparison: IDENTICAL at every checkpoint.
```

This is independent evidence that HD rendering did not alter gameplay state. It is not
claimed for the built-in adaptive explorer, whose screen sampling intentionally differs
between HD and original resolutions; use a movie or `scripts/preview_input.lua` for
comparisons.

### Commands

```bat
:: geometry / adaptation (no ROM)
Mesen.exe --hdstudio --selftest

:: capture + semantic asset catalog
Mesen.exe --hdstudio game.nes --generate --frames=18000 --out=PACK
Mesen.exe --hdstudio game.nes --assets --out=PACK

:: one approved replacement (standard pack)
Mesen.exe --hdstudio game.nes --import --asset=ID --image=ART.png ^
  --keep-largest --all-palettes --import-name=MyArt --out=PACK

:: repeatable build / validation
Mesen.exe --hdstudio game.nes --compile --out=PACK
Mesen.exe --hdstudio game.nes --validate --out=PACK

:: real emulator comparison (standard pack must be in HdPacks/<rom name>)
Mesen.exe --hdstudio game.nes [movie.fm2|script.lua] --verify --shots=3 --frames=1200 --out=PACK
```

Regression scripts: `scripts/test_hdstudio_geometry.py`, `test_hdstudio_project.py`,
`test_importer.py`, `test_gate1_mapper.py`, `test_synthetic_suite.py` and
`validate_matrix.py`. Synthetic ROM sources are in `test-roms/build_synthetic_roms.py`.

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
| `--verify-at=N` | | One exact verification checkpoint at frame N; used to replay the same scene where an object was picked |

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

## Complete workflow for any NES game

```
hdstudio_workflow.cmd "game.nes" [--disasm=FOLDER] [--movie=FILE] [--frames=N]
```

The script runs each step in its own Mesen process and stops on the first failure:

1. **Capture** with the automatic explorer (or the movie) and `--generate`: records what is played,
   then adds every CHR-ROM tile with a wildcard fallback.
2. **Exact data** (optional, requires a disassembly): `--maps` runs the game's level loader, writes
   the full maps, the exact background combinations and the sprite palette sweep; a second
   `--generate` merges them into the pack.
3. **Report**: `HdStudio/report.html` with the Coverage table, sprites, backgrounds, level maps and
   the complete object catalog from the game data.
4. **Verify**: plays with and without the pack, checks that the HD pack is active and writes
   side-by-side screenshots.

What is guaranteed for every NES game:

| Game type | Guarantee |
|---|---|
| **CHR-ROM** | 100 % of the non-blank CHR patterns have an HD image (seen exactly or via the wildcard `DefaultTile=Y`); all backgrounds/sprites can be replaced |
| **CHR-ROM + disassembly** | Same, plus exact level maps, exact background `(pattern+palette)` combinations and the exact sprite palette sweep where the parser knows the game (SMB3 today) |
| **CHR-RAM** | Only the tiles actually shown during the capture; a 100 % tile guarantee is impossible without the game's code. The report says so |

The 25-ROM validation matrix (18 CHR-ROM + 7 CHR-RAM) is in
[HDSTUDIO_VALIDATION.md](HDSTUDIO_VALIDATION.md): every CHR-ROM title ended with **100 % of its non-blank CHR
patterns in the pack**; the CHR-RAM titles honestly report only the tiles that appeared.

## Named object atlas (SMB3, game-specific)

The generic workflow cannot name the objects of an arbitrary game. For SMB3 the disassembly provides `game.xml`
(196 object IDs) and the RAM addresses of the object slots; `--spriteid=N` (internal, one object per Mesen
process) loads 1-1, places object N in a free slot at the player's visible position, and captures the sprites
its own drawing code writes to `Sprite_RAM`. A per-ID timeout isolates objects that need a specific state or
hang. The images are rendered with CHR + the exact sprite palette into `HdStudio/atlas/` and listed in
`atlas.json`; each object is captured on **one frame** (not accumulated over animation frames, which made
walking characters look doubled). The report shows an **Object atlas (named sprites)** table with all 196 IDs,
names and the
captured images. IDs that need a special state (doors, controllers, some bosses’ phases) show “no visible
sprite in the forced state” instead of a wrong image. Current mapper-aware audit: **140 captured OAM
compositions, 138 verified, 2 blank/unverified, 49 unique IDs with no sprite, 1,070 OAM rows and
444/444 exact object (pattern+palette) combinations present in the pack**. The retired 147/196 and
200/200 claims are no longer used. SMB3 uses **8×16 sprites**, so the renderer combines each OAM pattern
pair; a few forced-state boss captures are still partial (Bowser, Boss Bass) because they need their real
level/state. The 49 without sprites are 7 world-map objects, invisible objects (hidden whistle, door, lift,
currents), and event/cannon controllers that spawn other objects instead of drawing themselves. Player
forms (Tanuki, Frog, Hammer…) are not object IDs; they are captured by the separate `--playerforms` atlas
(see below).

**What “100 %” does not mean:** it is tile/palette coverage, not a named bestiary. `sheet_objects.png`
contains the metasprites detected in the recording (for SMB3: 71 groups / 464 variants); the game data
defines 196 object types. Enemies/forms that never appeared are still replaced in game (their tiles are in
the pack), but they are not grouped or named in the sheet. Player forms are captured by `--playerforms`. The
full tile atlas is `HdStudio/chr_coverage.png`; `report.json` has the 196-object catalog from `game.xml`.

### Player-form atlas (`--playerforms`, SMB3 USA)

The player is not an object ID: SMB3 draws it from `Player_Suit`, `Player_Statue` and `Player_Frame`.
`--playerforms` captures all eight suits (Small, Super, Fire, Raccoon, Frog, **Tanuki**, Hammer and the
**Tanuki Statue**) plus the **Death** state in one Mesen process:

```
.\hdstudio.cmd game.nes --playerforms
```

1. Load 1-1 through the game's own level loader and clear the object slots.
2. Queue each suit with `Player_QueueSuit = suit + 1`; `Player_Update` calls `Level_SetPlayerPUpPal`.
3. For the statue, queue `$0F` and hold **DOWN** at `Player_TanookiStatue` so it is maintained.
4. Force `Player_Frame` at the `Player_Draw` entry (`$CE9F`) for each named pose, so the game's own drawing
   code builds the 8×16 OAM sprites.
5. Capture `Sprite_RAM` + the exact sprite palette, render one PNG per pose and write
   `HdStudio/playerforms/playerforms_exact.txt`.

Measured on SMB3 (USA): **9 entries (8 suits + Death), 122 named poses, 532 visible sprites, 738/738 exact
(pattern + palette) combinations in the pack**. The report adds a **Player forms (named sprites)** section
with every form and pose. The pose set covers walk, run, jump, fall, duck, swim, climb, tail, flutter/fly,
frog hop/swim, hammer throw, the statue and the Death state; a few edge/scripted states are intentionally
omitted because they need a different game state.

### Demo GIF (`--demo`, SMB3)

After the pack exists, this records a short real-Mesen GIF with the HD pack active:

```
.\hdstudio.cmd game.nes --demo --frames=600
```

The Lua driver loads 1-1, walks Mario a little, clears the object slots and forces a Goomba next to him. The
recording uses Mesen's GIF codec, so the output is the actual emulator video at the HD resolution. The
repository's `assets/gifs/hd_goomba_demo.gif` used a temporarily retouched Goomba tile; the pack sheets were
restored afterwards. The browser preview in `demo/` is a separate visual mockup (no ROM, no emulator).

### Custom art import

The companion documentation repository includes `scripts/import_hd_art.py`, which builds a new HD sheet, prepends
exact entries for a supplied Goomba/background tile, records the `--demo` GIF and restores the pack. See the
repository's `docs/custom-art.md`. The native emulator remains the source of truth; the browser preview is only
a visual mockup.

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

- **Map tile fix:** the four 8×8 chunks of a 16×16 tile are stored as upper-left, lower-left, upper-right,
  lower-right. A first renderer placed them row-major and swapped the diagonal quadrants, making pipes/blocks
  look shifted; the final renderer uses the stored order (World 1-1 compared with the original layout).

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

1. **Capture + report + exact SMB3 data** (done): level maps, backgrounds, object atlas, player forms
2. Automatic game profile: RAM search (player position, level, lives), OAM tracing to extract the metasprite tables, Code/Data Logger
3. Import external profiles (RAM maps, disassemblies) and cc65 `.dbg` files for homebrew
4. Automatic placement of the redrawn PNGs into `hires.txt` and side-by-side original/HD view
5. Full player animation set (remaining edge/scripted states) and better boss object captures
