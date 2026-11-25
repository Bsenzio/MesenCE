"""Stock Mesen compatibility evidence harness (Windows).

Launches the official Mesen 2.1.1 release with a pack installed in its own HdPacks
folder, captures the real window, then repeats without the pack. A measurable pixel
difference proves that the exported standard pack is active in unmodified Mesen.

This script does not modify Mesen's settings; it only copies the pack into the
configured HdPacks folder and restores any previous folder afterwards.

Example:
  python scripts/test_stock_mesen.py ^
    --stock "D:\\MesenHighWork\\stock-mesen\\Mesen_2.1.1\\Mesen.exe" ^
    --rom "D:\\MesenHighWork\\test-roms-new2\\hdstudio_holdout.nes" ^
    --pack "D:\\MesenHighWork\\validation-packs-final\\holdout-export-minimal" ^
    --shots "D:\\MesenHighWork\\docs-shots"
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import time

try:
    from PIL import Image, ImageChops
except Exception:
    raise SystemExit("Pillow is required for this test")

import ctypes


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def version(exe):
    # FileVersionInfo through PowerShell is reliable without extra packages.
    p = subprocess.run(["powershell", "-NoProfile", "-Command",
                        f"(Get-Item '{exe}').VersionInfo.FileVersion"],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    return p.stdout.strip()


def stock_home():
    return os.path.join(os.path.expanduser("~"), "Documents", "Mesen2")


def launch_and_capture(exe, rom, capture_script, output):
    p = subprocess.Popen([exe, rom])
    try:
        deadline = time.time() + 30
        while time.time() < deadline:
            if p.poll() is not None:
                raise SystemExit("stock Mesen exited with code " + str(p.returncode))
            # Wait until the title contains the ROM name.
            title = subprocess.run(["powershell", "-NoProfile", "-Command",
                                    f"(Get-Process -Id {p.pid}).MainWindowTitle"],
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True).stdout
            if os.path.splitext(os.path.basename(rom))[0] in title:
                break
            time.sleep(0.5)
        time.sleep(4)
        cap = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                              "-File", capture_script, "-ProcessId", str(p.pid),
                              "-Output", output],
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        if not os.path.exists(output):
            print(cap.stdout)
            raise SystemExit("window capture failed")
    finally:
        p.terminate()
        try:
            p.wait(timeout=10)
        except Exception:
            p.kill()
    return output


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stock", default=r"D:\MesenHighWork\stock-mesen\Mesen_2.1.1\Mesen.exe")
    ap.add_argument("--home", default=stock_home())
    ap.add_argument("--rom", required=True)
    ap.add_argument("--pack", required=True)
    ap.add_argument("--capture-script", default=r"D:\MesenHighWork\scripts\capture_window.ps1")
    ap.add_argument("--shots", default=r"D:\MesenHighWork\docs-shots")
    ap.add_argument("--settle-seconds", type=float, default=4.0)
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    exe = os.path.abspath(args.stock)
    rom = os.path.abspath(args.rom)
    pack = os.path.abspath(args.pack)
    home = os.path.abspath(args.home)
    if not os.path.exists(exe):
        raise SystemExit("stock Mesen not found: " + exe)
    if not os.path.exists(os.path.join(home, "settings.json")):
        raise SystemExit("stock Mesen has no settings.json in " + home +
                         "; launch it once and complete the initial setup")
    if not os.path.exists(os.path.join(pack, "hires.txt")):
        raise SystemExit("pack has no hires.txt: " + pack)

    os.makedirs(args.shots, exist_ok=True)
    name = os.path.splitext(os.path.basename(rom))[0]
    target = os.path.join(home, "HdPacks", name)
    backup = target + ".stock-test-backup"
    had_target = os.path.isdir(target)
    if had_target:
        if os.path.isdir(backup):
            shutil.rmtree(backup)
        shutil.move(target, backup)
    try:
        shutil.copytree(pack, target)
        with_pack = os.path.join(args.shots, "stock_with_pack.png")
        launch_and_capture(exe, rom, args.capture_script, with_pack)
        if os.path.isdir(target + ".off"):
            shutil.rmtree(target + ".off")
        shutil.move(target, target + ".off")
        without_pack = os.path.join(args.shots, "stock_without_pack.png")
        launch_and_capture(exe, rom, args.capture_script, without_pack)
        shutil.move(target + ".off", target)
    finally:
        if os.path.isdir(target):
            shutil.rmtree(target)
        if had_target and os.path.isdir(backup):
            shutil.move(backup, target)

    a = Image.open(with_pack).convert("RGB")
    b = Image.open(without_pack).convert("RGB")
    if a.size != b.size:
        different = a.size
        diff_pixels = a.size[0] * a.size[1]
    else:
        diff = ImageChops.difference(a, b)
        different = diff.getbbox()
        diff_pixels = sum(1 for px in diff.getdata() if px != (0, 0, 0))

    result = {
        "passed": diff_pixels > 500,
        "stockExe": exe,
        "stockVersion": version(exe),
        "stockSha256": sha(exe),
        "rom": rom,
        "pack": pack,
        "withPack": with_pack,
        "withoutPack": without_pack,
        "differentRegion": different,
        "differentPixels": diff_pixels,
        "note": "A real stock Mesen window is captured; the only variable is the pack folder."
    }
    print(json.dumps(result, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
