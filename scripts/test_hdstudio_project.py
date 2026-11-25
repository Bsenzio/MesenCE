"""End-to-end regression test for the RetroArcDo HD Studio project/compiler path.

This complements test_importer.py (legacy minimal importer). It exercises the managed
path used by the native Studio window:

  * managed import records the asset in HdStudio/project.hdstudio.json and copies the
    user artwork into managed project storage
  * the compiler rebuilds a standard Mesen hires.txt from an immutable base snapshot
  * rebuilds are deterministic and idempotent
  * --validate never writes, drafts are not exported, and context conflicts stop a build
  * a minimal export contains only the standard minimal syntax and never mutates the project

Example:
  python scripts/test_hdstudio_project.py ^
    --rom "D:\\NesFlasher\\ROMs\\Super Mario Bros. 3 (USA).nes" ^
    --pack "D:\\MesenHighWork\\mesen-home\\HdPacks\\Super Mario Bros. 3 (USA)" ^
    --image "D:\\MesenHighWork\\input\\user_1.png" ^
    --asset 114
"""
import argparse
import copy
import hashlib
import json
import os
import shutil
import subprocess
import tempfile


def run(mesen, rom, pack, extra, expect=0):
    cmd = [mesen, "--hdstudio", rom] + extra
    if not any(arg.lower().startswith("--out=") for arg in extra):
        cmd.append(f"--out={pack}")
    p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if expect is not None and p.returncode != expect:
        print(p.stdout)
        raise SystemExit(f"command failed ({p.returncode}, expected {expect}): {' '.join(cmd)}")
    return p.returncode, p.stdout


def read(path):
    with open(path, "rb") as f:
        return f.read()


def sha(path):
    return hashlib.sha256(read(path)).hexdigest()


def find_asset(assets_path, selector):
    doc = json.load(open(assets_path, encoding="utf-8"))
    for asset in doc.get("assets", []):
        if asset["id"] == selector or asset.get("name") == selector or asset["id"].endswith(":" + selector):
            if asset.get("tiles"):
                return asset
    raise SystemExit("asset not found or has no tile occurrences: " + selector)


def load_project(pack):
    path = os.path.join(pack, "HdStudio", "project.hdstudio.json")
    if not os.path.exists(path):
        raise SystemExit("managed import did not create a project: " + path)
    return path, json.load(open(path, encoding="utf-8"))


def save_project(path, doc):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)


def count_img(text):
    return len([line for line in text.splitlines() if line.startswith("<img>")])


def tile_entries(text):
    lines = [line for line in text.splitlines() if "<tile>" in line]
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--rom", required=True)
    ap.add_argument("--pack", required=True)
    ap.add_argument("--image", required=True)
    ap.add_argument("--asset", required=True)
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    work = tempfile.mkdtemp(prefix="hdstudio_project_test_")
    pack = os.path.join(work, "pack")
    shutil.copytree(os.path.abspath(args.pack), pack)
    if not os.path.isdir(os.path.join(pack, "HdStudio", "sprites")):
        pass
    elif not os.path.isdir(os.path.join(pack, "HdStudio", "atlas")):
        run(args.mesen, args.rom, pack, ["--atlasrender"], expect=None)

    # Make sure the semantic catalog is current for this ROM.
    run(args.mesen, args.rom, pack, ["--assets"])
    assets_path = os.path.join(pack, "HdStudio", "assets", "assets.json")
    asset = find_asset(assets_path, args.asset)

    hires_path = os.path.join(pack, "hires.txt")
    hires_was_present = os.path.exists(hires_path)

    # 1. Managed import: project + standard pack.
    code, log = run(args.mesen, args.rom, pack, [
        "--import", f"--asset={asset['id']}", f"--image={args.image}",
        "--remove-background", "--keep-largest", "--mirror-conditions",
        "--import-name=ProjectTest"])
    project_path, project = load_project(pack)
    replacement = next((r for r in project["replacements"] if r["assetId"] == asset["id"]), None)
    if replacement is None:
        raise SystemExit("managed import did not record the replacement")
    if replacement.get("approval") != "approved":
        raise SystemExit("managed import did not approve the replacement")
    if len(replacement.get("cells", [])) != len(asset["tiles"]):
        raise SystemExit("replacement cell count does not match the asset tile count")
    source = replacement.get("sourceImage", "")
    source_path = source if os.path.isabs(source) else os.path.join(pack, "HdStudio", *source.replace("/", "\\").split("\\"))
    if not os.path.exists(source_path):
        raise SystemExit("managed artwork was not copied into project storage")
    sheet_path = os.path.join(pack, replacement.get("sheet", ""))
    if not os.path.exists(sheet_path):
        raise SystemExit("compiled replacement sheet was not written")

    base_path = os.path.join(pack, "HdStudio", "base-hires.txt")
    if not os.path.exists(base_path):
        raise SystemExit("the compiler did not create the immutable base snapshot")
    base = open(base_path, encoding="utf-8").read()
    hires = open(hires_path, encoding="utf-8").read()
    if replacement["sheet"] not in hires:
        raise SystemExit("generated sheet is not referenced by hires.txt")
    if count_img(hires) != count_img(base) + 1:
        raise SystemExit("hires.txt image count is not base + 1 replacement")
    expected_tiles = len(tile_entries(base)) + len(replacement["cells"])
    if len(tile_entries(hires)) != expected_tiles:
        raise SystemExit(f"unexpected tile entry count: {len(tile_entries(hires))} != {expected_tiles}")

    # Mesen uses the first matching entry for a tile key. Authored entries must therefore
    # precede capture-generated entries, otherwise an exact capture palette shadows the art
    # and the pack loads but the replacement never appears on screen.
    image_lines = [line[5:] for line in hires.splitlines() if line.startswith("<img>")]
    managed_index = image_lines.index(replacement["sheet"])
    first_tile = next((line for line in hires.splitlines() if "<tile>" in line), "")
    if f"<tile>{managed_index}," not in first_tile:
        raise SystemExit("authored tile entries do not precede capture-generated entries")

    # 2. Rebuild twice: deterministic and idempotent, in-place.
    first = sha(hires_path)
    run(args.mesen, args.rom, pack, ["--compile"])
    second = sha(hires_path)
    run(args.mesen, args.rom, pack, ["--compile"])
    third = sha(hires_path)
    if first != second or second != third:
        raise SystemExit("repeated builds are not deterministic")
    if open(hires_path, encoding="utf-8").read().count("<img>HdStudioManaged_") != 1:
        raise SystemExit("rebuild accumulated duplicate generated images")

    # 3. Validation is read-only.
    run(args.mesen, args.rom, pack, ["--validate"])
    if sha(hires_path) != third:
        raise SystemExit("--validate modified the pack")

    # 4. Drafts are project state and are never exported.
    project = json.load(open(project_path, encoding="utf-8"))
    replacement = next(r for r in project["replacements"] if r["assetId"] == asset["id"])
    replacement["approval"] = "draft"
    save_project(project_path, project)
    run(args.mesen, args.rom, pack, ["--compile"])
    draft_hires = open(hires_path, encoding="utf-8").read()
    if "HdStudioManaged_" in draft_hires:
        raise SystemExit("a draft replacement was exported")
    replacement["approval"] = "approved"
    save_project(project_path, project)
    run(args.mesen, args.rom, pack, ["--compile"])
    if sha(hires_path) != third:
        raise SystemExit("re-approving did not restore the identical managed pack")

    # 5. A copied project must rebase onto its new pack folder, not write back into the
    # original pack recorded in its JSON. This protects portable projects and test copies.
    portable = os.path.join(work, "portable_pack")
    shutil.copytree(pack, portable)
    original_before = sha(hires_path)
    run(args.mesen, args.rom, portable, ["--compile"])
    if sha(hires_path) != original_before:
        raise SystemExit("compiling a copied project modified the original pack")
    portable_project = json.load(open(os.path.join(portable, "HdStudio", "project.hdstudio.json"), encoding="utf-8"))
    if os.path.normcase(os.path.realpath(portable_project["packFolder"])) != os.path.normcase(os.path.realpath(portable)):
        raise SystemExit(f"a copied project did not rebase its pack folder: {portable_project['packFolder']} != {os.path.realpath(portable)}")
    if sha(os.path.join(portable, "hires.txt")) != original_before:
        raise SystemExit("the copied project did not produce the same managed pack")

    # 7. A duplicated, approved assignment is a context conflict and must not overwrite the pack.
    project = json.load(open(project_path, encoding="utf-8"))
    replacement = next(r for r in project["replacements"] if r["assetId"] == asset["id"])
    conflict = copy.deepcopy(replacement)
    conflict["id"] = conflict["id"] + "-conflict"
    conflict.setdefault("adaptation", {})["offsetX"] = 9
    project["replacements"].append(conflict)
    save_project(project_path, project)
    before_conflict = sha(hires_path)
    code, log = run(args.mesen, args.rom, pack, ["--compile"], expect=1)
    if "Context conflict" not in log:
        raise SystemExit("the compiler did not report the context conflict")
    if sha(hires_path) != before_conflict:
        raise SystemExit("a failed build overwrote the pack")
    project["replacements"] = [r for r in project["replacements"] if r["id"] != conflict["id"]]
    save_project(project_path, project)

    # 8. ROM identity validation.
    project = json.load(open(project_path, encoding="utf-8"))
    expected_sha = project["romSha1"]
    project["romSha1"] = "0" * 40
    save_project(project_path, project)
    code, log = run(args.mesen, args.rom, pack, ["--validate"], expect=1)
    if "SHA1 changed" not in log:
        raise SystemExit("the compiler did not report a ROM identity mismatch")
    project["romSha1"] = expected_sha
    save_project(project_path, project)

    # 9. Minimal export: standard minimal syntax only, project untouched.
    minimal = os.path.join(work, "minimal")
    os.makedirs(minimal, exist_ok=True)
    project_hash = sha(project_path)
    run(args.mesen, args.rom, pack, ["--compile", "--minimal", f"--project={pack}", f"--out={minimal}"])
    if sha(project_path) != project_hash:
        raise SystemExit("minimal export mutated the project")
    minimal_hires = open(os.path.join(minimal, "hires.txt"), encoding="utf-8").read()
    if "Chr_" in minimal_hires:
        raise SystemExit("minimal export contains base capture image entries")
    if len(tile_entries(minimal_hires)) != len(replacement.get("cells", [])):
        raise SystemExit("minimal export does not contain exactly the replacement tiles")
    sheets = [f for f in os.listdir(minimal) if f.startswith("HdStudioManaged_") and f.endswith(".png")]
    if not sheets:
        raise SystemExit("minimal export did not write the replacement sheet")

    result = {
        "passed": True,
        "asset": asset["id"],
        "assetName": asset.get("name"),
        "tiles": len(asset["tiles"]),
        "baseImages": count_img(base),
        "images": count_img(hires),
        "tileEntries": len(tile_entries(hires)),
        "sheet": replacement["sheet"],
        "network": "offline",
    }
    print(json.dumps(result, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
