"""Runs the synthetic NES variants through generic capture + asset discovery.

The ROMs are built from test-roms/build_synthetic_roms.py (original code and graphics).
Expected ground truth is checked directly against the exported asset schema.

Example:
  python scripts/test_synthetic_suite.py --home "D:\\MesenHighWork\\mesen-home"
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile


def run(mesen, home, args, timeout=600):
    env = os.environ.copy()
    if home:
        env["MESEN_HOME"] = home
    p = subprocess.run([mesen] + args, env=env, stdout=subprocess.PIPE,
                       stderr=subprocess.STDOUT, text=True, timeout=timeout)
    return p.returncode, p.stdout


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=os.environ.get("MESEN_HOME", ""))
    ap.add_argument("--rom-dir", default=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "examples", "test-roms"))
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    rom_dir = os.path.abspath(args.rom_dir)
    builder = os.path.join(rom_dir, "build_synthetic_roms.py")
    if not os.path.exists(builder):
        raise SystemExit("synthetic ROM builder not found: " + builder)

    work = tempfile.mkdtemp(prefix="hdstudio_synth_")
    build_dir = os.path.join(work, "roms")
    os.makedirs(build_dir)
    subprocess.run([sys.executable, builder, "--out", build_dir], check=True,
                   stdout=subprocess.PIPE, text=True)

    expectations = {
        "hdstudio_test": {"metasprites": 5, "backgroundBlocks": 1, "chrRam": False},
        "hdstudio_test_8x16": {"metasprites": 5, "backgroundBlocks": 1, "chrRam": False},
        "hdstudio_test_mirror": {"metasprites": 7, "backgroundBlocks": 1, "chrRam": False},
        #CHR-RAM background blocks are observed at runtime by bg_observe.lua.
        "hdstudio_test_chrram": {"metasprites": 6, "backgroundBlocks": 1, "chrRam": True},
        #Holdout: generated after the discovery parameters were frozen; not tuned on.
        "hdstudio_holdout": {"metasprites": 7, "backgroundBlocks": 1, "chrRam": False},
    }

    results = []
    for stem, expected in expectations.items():
        rom = os.path.join(build_dir, stem + ".nes")
        pack = os.path.join(work, "packs", stem)
        code, output = run(args.mesen, args.home, [
            "--hdstudio", rom, "--generate", "--bgscan", "--frames=300", "--out=" + pack
        ])
        if code != 0:
            print(output)
            raise SystemExit("capture failed for " + stem)
        code, output = run(args.mesen, args.home, ["--hdstudio", rom, "--assets", "--out=" + pack])
        if code != 0:
            print(output)
            raise SystemExit("asset export failed for " + stem)

        doc = load(os.path.join(pack, "HdStudio", "assets", "assets.json"))
        assets = doc.get("assets", [])
        counts = {}
        for asset in assets:
            counts[asset.get("kind")] = counts.get(asset.get("kind"), 0) + 1

        if doc.get("game", {}).get("hasChrRom") == expected["chrRam"]:
            raise SystemExit(f"{stem}: CHR-ROM/RAM type mismatch")
        if counts.get("metasprite", 0) < expected["metasprites"]:
            raise SystemExit(f"{stem}: expected at least {expected['metasprites']} metasprites, got {counts.get('metasprite', 0)}")
        if counts.get("backgroundBlock", 0) < expected["backgroundBlocks"]:
            raise SystemExit(f"{stem}: expected at least {expected['backgroundBlocks']} background block(s), got {counts.get('backgroundBlock', 0)}")

        tiles = [t for a in assets for t in a.get("tiles", [])]
        if stem == "hdstudio_test_8x16":
            if not any(a.get("dimensions", {}).get("height") == 16 and a.get("dimensions", {}).get("width") == 8 for a in assets):
                raise SystemExit("8x16 ROM did not produce an 8x16 composition")
        if stem in ("hdstudio_test_mirror", "hdstudio_holdout"):
            if not any(t.get("flipH") for t in tiles) or not any(t.get("flipV") for t in tiles):
                raise SystemExit("mirror/holdout ROM did not record both flip axes")
            if not any(t.get("flipH") and t.get("flipV") for t in tiles):
                raise SystemExit("mirror/holdout ROM did not record a both-axis composition")
        if stem == "hdstudio_test_chrram":
            if not any(len(t.get("tileData", "")) == 32 for t in tiles):
                raise SystemExit("CHR-RAM ROM did not record runtime pattern bytes")
            if not any(a.get("validation", {}).get("status") == "observed-chr-ram" for a in assets):
                raise SystemExit("CHR-RAM ROM did not produce an observed background block")
        results.append({
            "rom": stem,
            "assets": len(assets),
            "counts": counts,
            "chrRam": doc.get("game", {}).get("hasChrRom") is False,
        })
        print(json.dumps(results[-1], indent=1))

    print(json.dumps({"passed": True, "variants": results}, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
