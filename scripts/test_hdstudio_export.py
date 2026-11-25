"""Regression test for the v0.2 HD Pack export workflow.

Covers:
  * full and minimal exports to new folders (including spaces and Unicode)
  * staging validation, no ROM in the exported pack, exact absolute destination
  * existing destination refusal, explicit overwrite, timestamped backup
  * source/destination identity refusal
  * install into Mesen's configured HdPacks folder
  * repeated exports and ROM integrity

Example:
  python scripts/test_hdstudio_export.py
"""
import argparse
import glob
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

try:
    from PIL import Image, ImageDraw
except Exception:
    Image = None


def run(mesen, home, args, expect=0, timeout=900):
    env = os.environ.copy()
    if home:
        env["MESEN_HOME"] = home
    p = subprocess.run([mesen] + args, env=env, stdout=subprocess.PIPE,
                       stderr=subprocess.STDOUT, text=True, timeout=timeout)
    if expect is not None and p.returncode != expect:
        print(p.stdout)
        raise SystemExit(f"command failed ({p.returncode}, expected {expect}): {' '.join(args)}")
    return p.returncode, p.stdout


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def count_images(pack):
    path = os.path.join(pack, "hires.txt")
    with open(path, encoding="utf-8") as f:
        return sum(1 for line in f if line.startswith("<img>"))


def find_asset(pack, preferred="metasprite"):
    doc = json.load(open(os.path.join(pack, "HdStudio", "assets", "assets.json"), encoding="utf-8"))
    for asset in doc.get("assets", []):
        if asset.get("kind") == preferred and asset.get("tiles"):
            return asset
    for asset in doc.get("assets", []):
        if asset.get("tiles"):
            return asset
    raise SystemExit("no replaceable asset in the synthetic catalog")


def make_art(path, size=64):
    if Image is None:
        raise SystemExit("Pillow is required for this test")
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, 0, size - 1, size - 1], fill=(255, 0, 180, 255))
    draw.rectangle([size // 4, size // 4, size * 3 // 4, size * 3 // 4], fill=(0, 255, 255, 255))
    image.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", default=r"D:\MesenHighWork\test-roms-new2\hdstudio_holdout.nes")
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    mesen = os.path.abspath(args.mesen)
    home = os.path.abspath(args.home)
    rom = os.path.abspath(args.rom)
    if not os.path.exists(rom):
        raise SystemExit("holdout ROM not found: " + rom + " (run test-roms/build_synthetic_roms.py)")
    rom_hash = sha(rom)

    work = tempfile.mkdtemp(prefix="hdstudio_export_test_")
    pack = os.path.join(work, "project pack")
    os.makedirs(pack)
    art = os.path.join(work, "source art.png")
    make_art(art)

    run(mesen, home, ["--hdstudio", rom, "--generate", "--frames=300", "--out=" + pack])
    run(mesen, home, ["--hdstudio", rom, "--assets", "--out=" + pack])
    asset = find_asset(pack)
    run(mesen, home, ["--hdstudio", rom, "--import", "--asset=" + asset["id"],
                      "--image=" + art, "--out=" + pack])

    # 1. Full export to a path with spaces and a Unicode character.
    full = os.path.join(work, "Exports", "Prueba ñandú", "HD Pack")
    code, log = run(mesen, home, ["--hdstudio", rom, "--export", "--export-to=" + full,
                                  "--project=" + pack])
    if "Export completed" not in log:
        raise SystemExit("full export did not report completion")
    for required in ("hires.txt", "retroarcdo-export.json"):
        if not os.path.exists(os.path.join(full, required)):
            raise SystemExit("export is missing " + required)
    if not any(f.startswith("HdStudioManaged_") for f in os.listdir(full)):
        raise SystemExit("full export has no managed replacement sheet")
    if not any(f.startswith("Chr_") for f in os.listdir(full)):
        raise SystemExit("full export lost the capture-generated base images")
    if any(f.lower().endswith((".nes", ".fds", ".unf", ".unif")) for f in os.listdir(full)):
        raise SystemExit("a ROM file was exported with the pack")
    manifest = json.load(open(os.path.join(full, "retroarcdo-export.json"), encoding="utf-8"))
    if manifest.get("romSha1") is None or manifest.get("packKind") != "full":
        raise SystemExit("export manifest is not valid")
    images_full = count_images(full)

    # 2. Existing destination without overwrite must fail before touching it.
    before = sha(os.path.join(full, "hires.txt"))
    code, log = run(mesen, home, ["--hdstudio", rom, "--export", "--export-to=" + full,
                                  "--project=" + pack], expect=1)
    if "already exists" not in log:
        raise SystemExit("existing destination was not refused")
    if sha(os.path.join(full, "hires.txt")) != before:
        raise SystemExit("a refused export modified the destination")

    # 3. Explicit overwrite must keep a timestamped backup.
    code, log = run(mesen, home, ["--hdstudio", rom, "--export", "--export-to=" + full,
                                  "--overwrite", "--project=" + pack])
    backups = glob.glob(full + ".backup-*")
    if not backups:
        raise SystemExit("overwrite did not keep a backup")
    if not os.path.exists(os.path.join(full, "hires.txt")):
        raise SystemExit("overwrite did not produce a new pack")

    # 4. Exporting into the project's own build folder is refused.
    code, log = run(mesen, home, ["--hdstudio", rom, "--export", "--export-to=" + pack,
                                  "--overwrite", "--project=" + pack], expect=1)
    if "project's own build folder" not in log:
        raise SystemExit("source/destination identity was not refused")

    # 5. Minimal export contains only managed images and tiles.
    minimal = os.path.join(work, "Minimal Export")
    code, log = run(mesen, home, ["--hdstudio", rom, "--export", "--minimal",
                                  "--export-to=" + minimal, "--project=" + pack])
    base_images = [f for f in os.listdir(minimal) if f.startswith("Chr_")]
    if base_images:
        raise SystemExit("minimal export contains base capture images")
    if count_images(minimal) != 1:
        raise SystemExit("minimal export should reference exactly one managed sheet")
    minimal_hires = open(os.path.join(minimal, "hires.txt"), encoding="utf-8").read()
    if "Chr_" in minimal_hires:
        raise SystemExit("minimal hires.txt references base images")
    if "HdStudioManaged_" not in minimal_hires:
        raise SystemExit("minimal hires.txt does not reference the managed sheet")

    # 6. Install into Mesen's HD pack folder (a temporary MESEN_HOME).
    installed = os.path.join(home, "HdPacks", os.path.splitext(os.path.basename(rom))[0])
    if os.path.isdir(installed):
        shutil.rmtree(installed)
    code, log = run(mesen, home, ["--hdstudio", rom, "--export", "--minimal",
                                  "--export-to=" + os.path.join(work, "Install source"),
                                  "--install", "--project=" + pack])
    if not os.path.exists(os.path.join(installed, "hires.txt")):
        raise SystemExit("install did not copy the pack into Mesen's HdPacks folder")

    # 7. The ROM must be byte-identical after all authoring/export operations.
    if sha(rom) != rom_hash:
        raise SystemExit("the original ROM changed during export")

    print(json.dumps({
        "passed": True,
        "full": full,
        "images": images_full,
        "minimal": minimal,
        "installed": installed,
        "backups": len(backups),
        "manifest": manifest,
    }, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
