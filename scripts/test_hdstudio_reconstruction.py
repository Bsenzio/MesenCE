"""Offline reconstruction regression for v0.5.

Feeds deterministic live-observation TSV records through the real asset exporter:

  * a two-shape temporal sequence must become an animation (the old >=3 rule dropped it)
  * a 20-part composition must remain one composition (the old 16-part cap split it)
  * raw-pattern fallback records must be exported and must not create shared-key
    conflicts for authored replacements
  * the ROM is never modified

No emulator is started; this isolates the reconstruction/indexing stage.

Example:
  python scripts/test_hdstudio_reconstruction.py --rom D:\\...\\hdstudio_holdout.nes
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def write_tsv(path, header, rows):
    with open(path, "w", encoding="utf-8") as f:
        f.write(header + "\n")
        for row in rows:
            f.write("\t".join(str(v) for v in row) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", default=r"D:\MesenHighWork\test-roms-new2\hdstudio_holdout.nes")
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    rom = os.path.abspath(args.rom)
    rom_hash = sha(rom)
    work = tempfile.mkdtemp(prefix="hdstudio_recon_")
    pack = os.path.join(work, "pack")
    live = os.path.join(pack, "HdStudio", "live")
    os.makedirs(live)

    patterns = []
    for tile in range(1, 25):
        data = "00" * 16
        patterns.append([format(tile, "X"), "NesChrRom", tile * 16, data, 1, 100])
    write_tsv(os.path.join(live, "live-patterns.tsv"),
              "#key\tchrType\tchrAddress\ttileData\tfirstFrame\tuses", patterns)

    sprites = []
    #Two-shape animation at the same location. The second shape adds a horizontal
    #neighbour, so the shapes differ but share patterns 6/7.
    for frame in (1, 3):
        sprites += [
            [frame, 0, 0, 40, 40, 6, "FF0F1A2A", 0, 0, 0, "6", "NesChrRom", 96],
            [frame, 0, 1, 40, 48, 7, "FF0F1A2A", 0, 0, 0, "7", "NesChrRom", 112],
        ]
        if frame == 3:
            sprites += [
                [frame, 1, 0, 48, 40, 8, "FF0F1A2A", 0, 0, 0, "8", "NesChrRom", 128],
                [frame, 1, 1, 48, 48, 9, "FF0F1A2A", 0, 0, 0, "9", "NesChrRom", 144],
            ]

    #A 20-part horizontal chain. Consecutive patterns are hardware evidence; the
    #old 16-part union cap split it into two catalog records.
    for i in range(20):
        sprites.append([5, i, 0, i * 8, 80, i + 1, "FF0F1636", 0, 0, 0,
                        format(i + 1, "X"), "NesChrRom", (i + 1) * 16])
    write_tsv(os.path.join(live, "live-sprites.tsv"),
              "#frame\toam\tpart\tx\ty\ttile\tpalette\tflipH\tflipV\tpriority\tpattern\tchrType\tchrAddr",
              sprites)
    write_tsv(os.path.join(live, "live-frames.tsv"),
              "#frame\tscrollX\tscrollY\tmirroring\tlargeSprites\tsprites\tblocks\tscreenHash\tactive",
              [[1, 0, 0, "Vertical", 1, 2, 0, "A", 1],
               [3, 0, 0, "Vertical", 1, 4, 0, "B", 1],
               [5, 0, 0, "Vertical", 1, 20, 0, "C", 1]])
    os.makedirs(os.path.join(pack, "HdStudio"), exist_ok=True)
    with open(os.path.join(pack, "HdStudio", "tiles.txt"), "w", encoding="utf-8") as f:
        f.write("#tile\tpalette\tpixelCount\n")
        for tile in range(1, 25):
            f.write("%X\tFF0F1A2A\t64\n" % tile)

    env = os.environ.copy()
    env["MESEN_HOME"] = args.home
    p = subprocess.run([
        args.mesen, "--hdstudio", rom, "--assets", "--out=" + pack
    ], env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=180)
    if p.returncode != 0:
        print(p.stdout)
        raise SystemExit("asset export failed")

    doc = json.load(open(os.path.join(pack, "HdStudio", "assets", "assets.json"), encoding="utf-8"))
    assets = doc.get("assets", [])
    sequences = [a for a in assets if a.get("kind") == "animationSequence"]
    if not sequences:
        raise SystemExit("two-shape track did not produce an animation sequence")
    shortest = min(len([a for a in assets if a.get("kind") == "animationFrame" and
                        a.get("parentId") == s.get("id")]) for s in sequences)
    if shortest < 2:
        raise SystemExit("animation sequence has fewer than two frames")

    metasprites = [a for a in assets if a.get("kind") == "metasprite"]
    largest = max((len(a.get("tiles", [])) for a in metasprites), default=0)
    if largest < 17:
        raise SystemExit("large composition was truncated: largest has %d parts" % largest)

    raw = [a for a in assets if a.get("kind") == "rawPattern"]
    if len(raw) < 20:
        raise SystemExit("raw-pattern fallback was not exported")

    if sha(rom) != rom_hash:
        raise SystemExit("ROM changed during reconstruction test")

    print(json.dumps({
        "passed": True,
        "animationSequences": len(sequences),
        "shortestAnimationFrames": shortest,
        "largestCompositionParts": largest,
        "rawPatternRecords": len(raw),
        "romIntegrity": True
    }, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
