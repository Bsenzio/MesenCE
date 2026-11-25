"""Generic capture/asset-discovery matrix for RetroArcDo HD Studio.

Runs the real headless Mesen capture on every NES ROM in a folder, exports the
versioned asset catalog and records measured coverage per game. It does not
require a game-specific profile and never modifies a ROM.

Example:
  python scripts/validate_matrix.py --rom-dir "D:\\NesFlasher\\ROMs" ^
    --out-root "D:\\MesenHighWork\\validation-packs" --frames 300
"""
import argparse
import json
import os
import subprocess
import sys
import time


def run_command(mesen, home, args, timeout):
    env = os.environ.copy()
    if home:
        env["MESEN_HOME"] = home
    started = time.time()
    try:
        p = subprocess.run([mesen] + args, env=env, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, text=True, timeout=timeout)
        return p.returncode, p.stdout, round(time.time() - started, 1)
    except subprocess.TimeoutExpired:
        return None, "", round(time.time() - started, 1)


def load_json(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=os.environ.get("MESEN_HOME", ""))
    ap.add_argument("--rom-dir", required=True)
    ap.add_argument("--out-root", required=True)
    ap.add_argument("--frames", type=int, default=300)
    ap.add_argument("--timeout", type=int, default=600)
    ap.add_argument("--results", default="")
    args = ap.parse_args()

    os.makedirs(args.out_root, exist_ok=True)
    results = []
    roms = sorted(name for name in os.listdir(args.rom_dir) if name.lower().endswith(".nes"))
    for name in roms:
        rom = os.path.join(args.rom_dir, name)
        stem = os.path.splitext(name)[0].strip()
        pack = os.path.join(args.out_root, stem)
        print("==> " + name, flush=True)

        code, output, seconds = run_command(args.mesen, args.home, [
            "--hdstudio", rom, "--generate", f"--frames={args.frames}",
            "--timeout=" + str(args.timeout), "--out=" + pack
        ], args.timeout + 60)
        status = "ok" if code == 0 else ("timeout" if code is None else f"capture-exit-{code}")
        assets_code, assets_output, assets_seconds = run_command(args.mesen, args.home, [
            "--hdstudio", rom, "--assets", "--out=" + pack
        ], 300)

        report = load_json(os.path.join(pack, "HdStudio", "report.json"))
        assets = load_json(os.path.join(pack, "HdStudio", "assets", "assets.json"))
        cap = (report or {}).get("capture", {})
        rom_info = (report or {}).get("rom", {})
        counts = {}
        if assets:
            for asset in assets.get("assets", []):
                counts[asset.get("kind", "?")] = counts.get(asset.get("kind", "?"), 0) + 1

        info = {
            "rom": name,
            "stem": stem,
            "status": status,
            "captureSeconds": seconds,
            "assetsStatus": "ok" if assets_code == 0 else f"exit-{assets_code}",
            "assetsSeconds": assets_seconds,
            "mapper": rom_info.get("mapper"),
            "chrRam": rom_info.get("usesChrRam"),
            "chrRomKb": rom_info.get("chrRomKb"),
            "chrTilesInPack": cap.get("chrTilesInPack"),
            "chrTilesNonBlank": cap.get("chrTilesNonBlank"),
            "chrTilesSeen": cap.get("chrTilesSeen"),
            "chrTilesWildcard": cap.get("defaultTiles"),
            "uniqueTilePaletteCombos": cap.get("uniqueTilePaletteCombos"),
            "objects": len((report or {}).get("objects", [])),
            "assetCounts": counts,
            "assetsTotal": sum(counts.values()),
            "frames": cap.get("frames"),
        }
        results.append(info)
        print(json.dumps(info, ensure_ascii=False), flush=True)

    out = args.results or os.path.join(args.out_root, "matrix-results.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("wrote " + out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
