"""Shared-pattern safety and standard-condition regression test.

Covers:
  * both-axis flips compile to the standard `[hmirror&vmirror]` condition
  * a shared hardware key without explicit approval stops validation/export
  * explicit approval builds, with an auditable warning
  * a true same-condition conflict stops the build without overwriting the pack
  * a failed build leaves the previous hires.txt byte-identical
  * ROM integrity

Example:
  python scripts/test_hdstudio_safety.py --rom "D:\\MesenHighWork\\test-roms-new2\\hdstudio_holdout.nes"
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
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
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def make_art(path):
    if Image is None:
        raise SystemExit("Pillow is required")
    image = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.ellipse([2, 2, 61, 61], fill=(255, 128, 0, 255))
    draw.rectangle([20, 20, 43, 43], fill=(0, 255, 255, 255))
    image.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", default=r"D:\MesenHighWork\test-roms-new2\hdstudio_holdout.nes")
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    rom = os.path.abspath(args.rom)
    rom_hash = sha(rom)
    work = tempfile.mkdtemp(prefix="hdstudio_safety_")
    pack = os.path.join(work, "pack")
    os.makedirs(pack)
    art = os.path.join(work, "art.png")
    make_art(art)

    run(args.mesen, args.home, ["--hdstudio", rom, "--generate", "--frames=300", "--out=" + pack])
    run(args.mesen, args.home, ["--hdstudio", rom, "--assets", "--out=" + pack])
    catalog = json.load(open(os.path.join(pack, "HdStudio", "assets", "assets.json"), encoding="utf-8"))

    asset = None
    for candidate in catalog.get("assets", []):
        tiles = candidate.get("tiles", [])
        if candidate.get("kind") == "metasprite" and tiles and all(t.get("flipH") and t.get("flipV") for t in tiles):
            asset = candidate
            break
    if asset is None:
        raise SystemExit("the holdout did not expose a both-axis-flipped composition")

    run(args.mesen, args.home, ["--hdstudio", rom, "--import", "--asset=" + asset["id"],
                                "--image=" + art, "--mirror-conditions", "--out=" + pack])
    hires_path = os.path.join(pack, "hires.txt")
    hires = open(hires_path, encoding="utf-8").read()
    if "[hmirror&vmirror]<tile>" not in hires:
        raise SystemExit("both-axis flip did not compile to the standard combined condition")

    project_path = os.path.join(pack, "HdStudio", "project.hdstudio.json")
    project = json.load(open(project_path, encoding="utf-8"))
    replacement = next(r for r in project["replacements"] if r.get("assetId") == asset["id"])

    # Shared hardware keys are not semantic identity: unapproved replacement must stop.
    replacement["allowSharedReplacement"] = False
    json.dump(project, open(project_path, "w", encoding="utf-8"), indent=2)
    before = sha(hires_path)
    code, log = run(args.mesen, args.home, ["--hdstudio", rom, "--validate", "--out=" + pack], expect=1)
    if "shares tile+palette keys" not in log:
        raise SystemExit("shared-key replacement was not rejected")
    if sha(hires_path) != before:
        raise SystemExit("validation modified the pack")

    # Explicit approval builds and reports the affected assets.
    replacement["allowSharedReplacement"] = True
    json.dump(project, open(project_path, "w", encoding="utf-8"), indent=2)
    code, log = run(args.mesen, args.home, ["--hdstudio", rom, "--compile", "--out=" + pack])
    if "shared replacement approved" not in log:
        raise SystemExit("explicit shared approval was not reported")
    approved_hires = sha(hires_path)

    # Context-specific replacement: a standard condition keeps other shared uses intact.
    replacement["allowSharedReplacement"] = False
    replacement.setdefault("adaptation", {})["extraCondition"] = "bgpriority"
    json.dump(project, open(project_path, "w", encoding="utf-8"), indent=2)
    code, log = run(args.mesen, args.home, ["--hdstudio", rom, "--compile", "--out=" + pack])
    context_hires = open(hires_path, encoding="utf-8").read()
    if "[hmirror&vmirror&bgpriority]<tile>" not in context_hires:
        raise SystemExit("context condition was not compiled into the standard tile entry")
    if "context condition" not in log:
        raise SystemExit("context-specific replacement was not reported")
    replacement["allowSharedReplacement"] = True
    replacement["adaptation"].pop("extraCondition", None)
    json.dump(project, open(project_path, "w", encoding="utf-8"), indent=2)
    run(args.mesen, args.home, ["--hdstudio", rom, "--compile", "--out=" + pack])

    # A duplicate replacement with different pixels is a true context conflict.
    conflict = json.loads(json.dumps(replacement))
    conflict["id"] = conflict["id"] + "-conflict"
    conflict.setdefault("adaptation", {})["offsetX"] = 9
    project["replacements"].append(conflict)
    json.dump(project, open(project_path, "w", encoding="utf-8"), indent=2)
    code, log = run(args.mesen, args.home, ["--hdstudio", rom, "--compile", "--out=" + pack], expect=1)
    if "Context conflict" not in log:
        raise SystemExit("true context conflict was not rejected")
    if sha(hires_path) != approved_hires:
        raise SystemExit("a failed build overwrote the previous pack")

    if sha(rom) != rom_hash:
        raise SystemExit("ROM changed during safety test")

    print(json.dumps({
        "passed": True,
        "asset": asset["id"],
        "bothAxisCondition": True,
        "sharedKeyRefused": True,
        "sharedKeyApproved": True,
        "contextConflictStopped": True,
        "packPreserved": True
    }, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
