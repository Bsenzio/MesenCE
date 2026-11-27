"""v0.8 offline acceptance test on a ROM outside the development library.

Steps: fresh home -> adaptive discovery -> visible object -> ordinary artwork
import -> logical adaptation/segmentation -> compile -> baseline-HD preview ->
save/reopen project -> continue discovery -> standard pack export -> load in
stock Mesen 2.1.1 -> window screenshot.

The ROM is the repository's synthetic homebrew test ROM, not a library entry.
No external AI service is used.
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hdstudio_v071_autoreplace as v71  # noqa: E402
import hdstudio_v08_autoreplace as v08  # noqa: E402


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def make_art(path):
    size = 64
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    # A deliberately ordinary user artwork: a bright star with a dark outline.
    points = [(32, 2), (39, 23), (62, 23), (43, 36), (50, 60),
              (32, 45), (14, 60), (21, 36), (2, 23), (25, 23)]
    draw.polygon(points, fill=(255, 214, 0, 255), outline=(120, 30, 0, 255))
    draw.ellipse([26, 26, 38, 38], fill=(255, 80, 0, 255))
    image.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rom", default=r"D:\MesenHighWork\test-roms\hdstudio_test.nes")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v08\acceptance")
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--base-home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--stock", default=r"D:\MesenHighWork\stock-mesen\Mesen_2.1.1")
    ap.add_argument("--scale", type=int, default=2)
    ap.add_argument("--skip-stock", action="store_true")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    home = os.path.join(args.out, "home")
    if os.path.isdir(home):
        shutil.rmtree(home, ignore_errors=True)
    os.makedirs(home, exist_ok=True)
    stem = os.path.splitext(os.path.basename(args.rom))[0]
    pack = os.path.join(home, "HdPacks", stem)
    v71.prepare_home(args.base_home, home, stem)
    rom = {
        "name": os.path.basename(args.rom), "path": args.rom,
        "sha256": sha256(args.rom), "chrType": "CHR-ROM", "mapper": 0,
    }
    engine = {"binary": args.mesen, "home": home}
    capture_args = argparse.Namespace(
        budgets=[900, 1800, 3600, 6000], target_mode="auto", live_timeout=300,
        base_home=args.base_home, scale=args.scale, mesen=args.mesen,
        chrram_conditions="position",
        profiles=[("default", r"D:\MesenHigh\scripts\deterministic_input.lua")],
    )
    attempt_root = os.path.join(args.out, "attempt")
    os.makedirs(attempt_root, exist_ok=True)
    capture = v08.capture_adaptive(rom, capture_args, engine, attempt_root, "default",
                                   r"D:\MesenHigh\scripts\deterministic_input.lua")
    chosen = capture.get("chosen")
    result = {"rom": args.rom, "capture": {k: capture.get(k) for k in
                                           ("profile", "probes", "seconds", "exitCode", "reasons")}}
    if not chosen:
        result["status"] = "failed-no-visible-object"
        v71.atomic_json(os.path.join(args.out, "acceptance.json"), result)
        print(json.dumps(result, indent=1))
        return 1
    pack = chosen["pack"]
    engine_attempt = chosen["engine"]
    pause = chosen["pause"]
    component = chosen["component"]
    result["selected"] = {"kind": chosen["kind"], "box": {
        "x": component["x"], "y": component["y"],
        "width": component["width"], "height": component["height"]}}
    pick, pick_error = v08.pick_and_save(engine_attempt, rom, pack, attempt_root,
                                          component, "AcceptanceArt", "acceptance")
    if pick_error:
        result["status"] = "failed-pick"
        result["error"] = pick_error
        v71.atomic_json(os.path.join(args.out, "acceptance.json"), result)
        return 1
    assets_doc, assets_error = v08.export_assets(engine_attempt, rom, pack, attempt_root, "acceptance")
    asset = v08.find_picked_asset(assets_doc, "AcceptanceArt")
    if not asset:
        result["status"] = "failed-asset"
        v71.atomic_json(os.path.join(args.out, "acceptance.json"), result)
        return 1
    art = os.path.join(args.out, "user-art.png")
    make_art(art)
    plan = v08.plan_for_asset(engine_attempt, rom, pack, attempt_root, asset, "acceptance", capture_args)
    # Managed import: create a real project replacement, not a throw-away minimal pack.
    for name in os.listdir(pack):
        path = os.path.join(pack, name)
        if os.path.isfile(path):
            try:
                os.remove(path)
            except OSError:
                pass
    import_args = ["--hdstudio", rom["path"], "--import", "--asset=" + asset["id"],
                   "--image=" + art, "--fit=stretch", "--all-palettes",
                   "--mirror-conditions", "--import-scale=" + str(args.scale),
                   "--import-name=AcceptanceArt", "--out=" + pack]
    if plan.get("conditionsApplied"):
        import_args.append("--condition-map=" + plan["planPath"])
    code, import_out, _ = v71.run(engine_attempt, import_args, 600,
                                   os.path.join(attempt_root, "import.log"))
    result["import"] = {"exitCode": code, "log": import_out[-1500:]}
    if code != 0:
        result["status"] = "failed-import"
        v71.atomic_json(os.path.join(args.out, "acceptance.json"), result)
        return 1
    code, compile_out, _ = v71.run(engine_attempt,
                                    ["--hdstudio", rom["path"], "--compile", "--out=" + pack],
                                    600, os.path.join(attempt_root, "compile.log"))
    result["compile"] = {"exitCode": code, "log": compile_out[-1500:]}
    if code != 0:
        result["status"] = "failed-compile"
        v71.atomic_json(os.path.join(args.out, "acceptance.json"), result)
        return 1
    project_path = os.path.join(pack, "HdStudio", "project.hdstudio.json")
    project = v71.load_json(project_path, {}) or {}
    result["project"] = {
        "path": project_path,
        "replacements": len(project.get("replacements") or []),
        "approved": sum(1 for r in project.get("replacements") or [] if r.get("approval") == "approved"),
        "cells": sum(len(r.get("cells") or []) for r in project.get("replacements") or []),
    }
    # Preview: authored HD vs an empty HD pack at the same scale.
    exact_frame = int(pause.get("frame") or 900)
    box = [int(component["x"]), int(component["y"]),
           int(component["width"]), int(component["height"])]
    verify = v08.run_verify(engine_attempt, rom, pack, capture_args,
                            [exact_frame], "acceptance", attempt_root,
                            r"D:\MesenHigh\scripts\deterministic_input.lua")
    baseline = v08.prepare_baseline_home(capture_args, attempt_root, rom, pack)
    baseline_verify = v08.run_verify(baseline["engine"], rom, baseline["pack"], capture_args,
                                     [exact_frame], "acceptance-baseline", attempt_root,
                                     r"D:\MesenHigh\scripts\deterministic_input.lua")
    diff = v08.baseline_diff(
        os.path.join(verify["verifyDir"], "frame_%05d_hd.png" % exact_frame),
        os.path.join(baseline_verify["verifyDir"], "frame_%05d_hd.png" % exact_frame),
        [box], args.scale)
    result["preview"] = {
        "hdPackActive": verify["hdActive"],
        "stateIdentical": verify["stateIdentical"],
        "baselineActive": baseline_verify["hdActive"],
        "changedInside": diff["changedInside"],
        "changedOutside": diff["changedOutside"],
        "screenshot": os.path.join(verify["verifyDir"], "frame_%05d_hd.png" % exact_frame),
    }
    # Save/reopen: validate the project a second time and compare the compiled pack.
    hires_path = os.path.join(pack, "hires.txt")
    before_hash = sha256(hires_path) if os.path.exists(hires_path) else ""
    code, validate_out, _ = v71.run(
        engine_attempt, ["--hdstudio", rom["path"], "--compile", "--validate", "--out=" + pack],
        300, os.path.join(attempt_root, "validate.log"))
    after_hash = sha256(hires_path) if os.path.exists(hires_path) else ""
    result["reopen"] = {
        "validateExitCode": code,
        "validateLog": validate_out[-1200:],
        "projectLoads": bool(v71.load_json(project_path, {})),
        "hiresStable": before_hash == after_hash,
    }
    # Continue discovery on a copy so the accepted project is preserved.
    discovery_home = os.path.join(args.out, "discovery-home")
    if os.path.isdir(discovery_home):
        shutil.rmtree(discovery_home, ignore_errors=True)
    shutil.copytree(home, discovery_home)
    discovery_pack = os.path.join(discovery_home, "HdPacks", stem)
    discovery_engine = {"binary": args.mesen, "home": discovery_home}
    def asset_count():
        doc = v71.load_json(os.path.join(discovery_pack, "HdStudio", "assets", "assets.json"), {}) or {}
        return len(doc.get("assets") or [])
    before_assets = asset_count()
    code, discovery_out, _ = v71.run(
        discovery_engine,
        ["--hdstudio", rom["path"], r"D:\MesenHigh\scripts\deterministic_input.lua",
         "--live", "--live-mode=balanced", "--generate", "--frames=1800",
         "--timeout=120", "--seed=20261124", "--scale=" + str(args.scale),
         "--out=" + discovery_pack],
        300, os.path.join(args.out, "discovery.log"))
    v71.run(discovery_engine, ["--hdstudio", rom["path"], "--assets", "--out=" + discovery_pack],
            420, os.path.join(args.out, "discovery-assets.log"))
    result["continueDiscovery"] = {
        "exitCode": code, "assetsBefore": before_assets, "assetsAfter": asset_count(),
        "grew": asset_count() > before_assets,
    }
    # Export a standard pack and inspect it.
    export_to = os.path.join(args.out, "export")
    if os.path.isdir(export_to):
        shutil.rmtree(export_to, ignore_errors=True)
    code, export_out, _ = v71.run(
        engine_attempt,
        ["--hdstudio", rom["path"], "--export", "--project=" + pack,
         "--export-to=" + export_to, "--overwrite"],
        600, os.path.join(args.out, "export.log"))
    files = []
    if os.path.isdir(export_to):
        for root, _, names in os.walk(export_to):
            for name in names:
                path = os.path.join(root, name)
                files.append({"name": os.path.relpath(path, export_to),
                              "bytes": os.path.getsize(path),
                              "sha256": sha256(path)})
    result["export"] = {"exitCode": code, "log": export_out[-1200:],
                        "destination": export_to, "files": files,
                        "hasHires": os.path.exists(os.path.join(export_to, "hires.txt")),
                        "hasRom": any(f["name"].lower().endswith(".nes") for f in files)}
    # Stock Mesen 2.1.1, portable copy. The window screenshot is the evidence.
    if not args.skip_stock and os.path.isdir(args.stock) and os.path.exists(os.path.join(args.stock, "Mesen.exe")):
        stock = os.path.join(args.out, "stock-mesen")
        if os.path.isdir(stock):
            shutil.rmtree(stock, ignore_errors=True)
        shutil.copytree(args.stock, stock)
        stock_pack = os.path.join(stock, "HdPacks", stem)
        if os.path.isdir(export_to):
            shutil.copytree(export_to, stock_pack, dirs_exist_ok=True)
        exe = os.path.join(stock, "Mesen.exe")
        proc = subprocess.Popen([exe, args.rom, "--portable"], cwd=stock,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(10)
        shot = os.path.join(args.out, "stock-mesen.png")
        capture_script = r"D:\MesenHighWork\scripts\capture_process_window.ps1"
        cap = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                              "-File", capture_script, "-ProcessId", str(proc.pid),
                              "-TitleContains", "Mesen", "-Output", shot],
                             capture_output=True, text=True)
        try:
            proc.terminate()
            proc.wait(timeout=5)
        except Exception:
            proc.kill()
        stock_result = {"screenshot": shot, "captureExit": cap.returncode,
                        "captureLog": (cap.stdout or "")[-800:]}
        if os.path.exists(shot):
            image = Image.open(shot).convert("RGB")
            stock_result["size"] = list(image.size)
            colors = image.getcolors(maxcolors=1000000) or []
            # The user artwork's saturated yellow/orange is not in the synthetic ROM's
            # original palette; count it as visual evidence of the replacement.
            art_pixels = 0
            for count, (r, g, b) in colors:
                if r > 200 and g > 120 and b < 80:
                    art_pixels += count
            stock_result["userArtPixels"] = art_pixels
            stock_result["artVisible"] = art_pixels > 100
        result["stockMesen"] = stock_result
    preview_ok = (result["preview"]["hdPackActive"] and result["preview"]["stateIdentical"]
                  and result["preview"]["changedInside"] > 0)
    export_ok = result["export"]["hasHires"] and not result["export"]["hasRom"]
    if not preview_ok:
        result["status"] = "failed-preview"
    elif not export_ok:
        result["status"] = "failed-export"
    else:
        result["status"] = "verified-offline-acceptance"
    v71.atomic_json(os.path.join(args.out, "acceptance.json"), result)
    print(json.dumps(result, indent=1))
    return 0 if result["status"] == "verified-offline-acceptance" else 1


if __name__ == "__main__":
    raise SystemExit(main())
