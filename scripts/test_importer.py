"""Smoke/regression test for the generalized user-art importer.

Given a pack folder that already contains mapper-aware captures (HdStudio/sprites),
this script exports the semantic assets, runs the importer in dry-run mode and
verifies the preview/sheet/manifest, then writes a separate minimal pack and
verifies its standard Mesen hires.txt tile entries.

Example:
  python scripts/test_importer.py ^
    --rom "D:\\ROMs\\Super Mario Bros. 3 (USA).nes" ^
    --pack "D:\\MesenHighWork\\tmp\\gate1\\goomba2" ^
    --image "D:\\art\\goomba.png" ^
    --asset 114
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile


def run(mesen, rom, pack, extra):
    cmd = [mesen, "--hdstudio", rom] + extra + [f"--out={pack}"]
    p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    return p.returncode, p.stdout


def find_asset(assets_path, selector):
    doc = json.load(open(assets_path, encoding="utf-8"))
    for asset in doc.get("assets", []):
        if asset["id"] == selector or asset["name"] == selector or asset["id"].endswith(":" + selector):
            return asset
    raise SystemExit("asset not found: " + selector)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--rom", required=True)
    ap.add_argument("--pack", required=True)
    ap.add_argument("--image", required=True)
    ap.add_argument("--asset", required=True)
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    work = tempfile.mkdtemp(prefix="hdstudio_import_test_")
    src_pack = os.path.abspath(args.pack)
    test_pack = os.path.join(work, "source_pack")
    shutil.copytree(src_pack, test_pack)

    if os.path.isdir(os.path.join(test_pack, "HdStudio", "sprites")):
        code, log = run(args.mesen, args.rom, test_pack, ["--atlasrender"])
        if code not in (0, 3):
            print(log)
            raise SystemExit("atlasrender failed")

    code, log = run(args.mesen, args.rom, test_pack, ["--assets"])
    if code != 0:
        print(log)
        raise SystemExit("assets export failed")

    assets_path = os.path.join(test_pack, "HdStudio", "assets", "assets.json")
    asset = find_asset(assets_path, args.asset)
    if not asset.get("tiles"):
        raise SystemExit("asset has no tile occurrences")

    hires_before = None
    hires_path = os.path.join(test_pack, "hires.txt")
    if os.path.exists(hires_path):
        hires_before = open(hires_path, "rb").read()

    code, log = run(args.mesen, args.rom, test_pack, [
        "--import", f"--asset={asset['id']}", f"--image={args.image}",
        "--remove-background", "--keep-largest", "--mirror-conditions", "--dry-run",
        "--import-name=RegressionTest"])
    if code != 0:
        print(log)
        raise SystemExit("dry-run import failed")

    imports = os.path.join(test_pack, "HdStudio", "imports")
    files = os.listdir(imports)
    preview = [f for f in files if f.endswith("_preview.png")]
    original = [f for f in files if f.endswith("_original.png")]
    replacement = [f for f in files if f.endswith("_replacement.png")]
    manifests = [f for f in files if f.endswith(".json")]
    if not (preview and original and replacement and manifests):
        raise SystemExit("dry-run did not write all preview images/manifest")
    manifest = json.load(open(os.path.join(imports, manifests[0]), encoding="utf-8"))
    if len(manifest.get("tiles", [])) != len(asset["tiles"]):
        raise SystemExit("manifest tile count does not match the asset")

    if hires_before is not None and open(hires_path, "rb").read() != hires_before:
        raise SystemExit("--dry-run modified hires.txt")

    minimal = os.path.join(work, "minimal_pack")
    os.makedirs(os.path.join(minimal, "HdStudio", "assets"), exist_ok=True)
    shutil.copy2(assets_path, os.path.join(minimal, "HdStudio", "assets", "assets.json"))
    code, log = run(args.mesen, args.rom, minimal, [
        "--import", f"--asset={asset['id']}", f"--image={args.image}",
        "--remove-background", "--keep-largest", "--mirror-conditions",
        "--minimal", "--import-name=RegressionTest"])
    if code != 0:
        print(log)
        raise SystemExit("minimal import failed")

    hires = open(os.path.join(minimal, "hires.txt"), encoding="utf-8").read()
    tile_lines = [line for line in hires.splitlines() if line.startswith("<tile>") or line.startswith("[")]
    if "<img>" not in hires or len(tile_lines) != len(asset["tiles"]):
        print(hires)
        raise SystemExit("minimal hires.txt is missing images/tile entries")
    sheet = [f for f in os.listdir(minimal) if f.startswith("HdStudioImport_") and f.endswith(".png")]
    if not sheet:
        raise SystemExit("replacement sheet was not written")

    print(json.dumps({
        "passed": True,
        "asset": asset["id"],
        "tiles": len(asset["tiles"]),
        "sheet": sheet[0],
        "preview": preview[0]
    }, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
