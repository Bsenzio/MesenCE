"""Live interactive capture + hardware-aware picking regression test.

Covers:
  * the live_capture.lua script running against the real emulator frame callbacks
  * Record/Pause/Resume/Stop control file semantics
  * a paused frame with OAM, mapper-resolved patterns, background cells and a screenshot
  * asset import from live observations (compositions + animation tracks + background blocks)
  * hardware-aware picking persisted as an editable asset with pattern references
  * a later --assets run preserves the manual pick
  * ROM integrity

The headless harness drives the same script the Studio loads into the interactive
emulator; the deep explorer only provides reproducible input.

Example:
  python scripts/test_hdstudio_live.py
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import threading
import time


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", default=r"D:\NESGames\Library\S\Super Mario Bros. 3 (USA).nes")
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    rom = os.path.abspath(args.rom)
    if not os.path.exists(rom):
        raise SystemExit("test ROM not found: " + rom)
    rom_hash = sha(rom)
    work = tempfile.mkdtemp(prefix="hdstudio_live_")
    pack = os.path.join(work, "pack")
    live = os.path.join(pack, "HdStudio", "live")
    os.makedirs(live)
    control = os.path.join(live, "control.txt")
    open(control, "w", encoding="utf-8").write("0|resume\n")

    env = os.environ.copy()
    env["MESEN_HOME"] = args.home
    command = [args.mesen, "--hdstudio", rom, "--live", "--generate",
               "--seed=20261124", "--frames=10000000", "--timeout=16",
               "--out=" + pack, "--log=" + os.path.join(work, "run.log")]
    process = subprocess.Popen(command, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True)

    def drive():
        #Wait for measurement, not wall-clock startup: the deep explorer may spend
        #several seconds booting the game before the live script sees gameplay frames.
        status_path = os.path.join(live, "live-status.json")
        deadline = time.time() + 40
        while time.time() < deadline:
            try:
                status = json.load(open(status_path, encoding="utf-8"))
                if status.get("samples", 0) >= 60 and status.get("frame", 0) >= 240:
                    break
            except Exception:
                pass
            time.sleep(0.75)
        open(control, "w", encoding="utf-8").write("1|pause\n")
        time.sleep(4)
        open(control, "w", encoding="utf-8").write("2|stop\n")

    threading.Thread(target=drive, daemon=True).start()
    output, _ = process.communicate(timeout=120)
    if process.returncode != 0:
        print(output)
        raise SystemExit("live capture run failed")

    status = json.load(open(os.path.join(live, "live-status.json"), encoding="utf-8"))
    if not status.get("stopped"):
        raise SystemExit("live capture did not stop through the control file")
    if status.get("samples", 0) < 20:
        raise SystemExit("live capture sampled too few frames: " + str(status))
    if status.get("patterns", 0) < 5:
        raise SystemExit("live capture observed too few patterns")
    if status.get("spriteOccurrences", 0) < 50:
        raise SystemExit("live capture observed too few sprite occurrences")
    if status.get("backgroundBlocks", 0) < 1:
        raise SystemExit("live capture observed no background blocks")

    pause_path = os.path.join(live, "pause-frame.json")
    if not os.path.exists(pause_path):
        raise SystemExit("pause did not produce a frozen frame")
    pause = json.load(open(pause_path, encoding="utf-8"))
    if not pause.get("sprites") or not pause.get("background"):
        raise SystemExit("frozen frame has no hardware sprites or background cells")
    sprite = pause["sprites"][0]
    for key in ("renderSlot", "x", "y", "tile", "palette", "pattern", "chrType", "data"):
        if key not in sprite:
            raise SystemExit("frozen sprite is missing " + key)
    if len(sprite.get("data", "")) != 32:
        raise SystemExit("frozen sprite has no mapper-resolved pattern bytes")
    shot = os.path.join(live, pause.get("screenshot", ""))
    if not os.path.exists(shot):
        raise SystemExit("paused frame screenshot is missing")

    # Asset import from live observations.
    env_args = ["--hdstudio", rom, "--assets", "--out=" + pack,
                "--log=" + os.path.join(work, "assets.log")]
    subprocess.run([args.mesen] + env_args, env=env, check=True,
                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    catalog = json.load(open(os.path.join(pack, "HdStudio", "assets", "assets.json"), encoding="utf-8"))
    live_assets = [a for a in catalog["assets"] if a.get("source") == "liveCapture"]
    if not live_assets:
        raise SystemExit("asset export did not include live compositions")
    if not any(a.get("kind") == "backgroundBlock" for a in live_assets):
        raise SystemExit("asset export did not include live background blocks")

    # Hardware-aware picking at the first sprite's centre.
    px = sprite["x"] + 3
    py = sprite["y"] + 3
    subprocess.run([args.mesen, "--hdstudio", "--pick=%d,%d" % (px, py),
                    "--pick-save=1", "--pick-name=LiveTestPick", "--out=" + pack],
                   env=env, check=True, stdout=subprocess.PIPE,
                   stderr=subprocess.STDOUT, text=True)
    picked_path = os.path.join(live, "picked-assets.json")
    if not os.path.exists(picked_path):
        raise SystemExit("hardware pick was not persisted")
    picked = json.load(open(picked_path, encoding="utf-8"))["assets"]
    if not picked or not picked[0].get("tiles"):
        raise SystemExit("picked asset has no hardware tile references")
    if picked[0]["tiles"][0].get("chrType") not in ("NesChrRom", "NesChrRam"):
        raise SystemExit("picked asset lost its CHR identity")

    # Re-export must merge, not erase, the manual pick.
    subprocess.run([args.mesen] + env_args, env=env, check=True,
                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    catalog = json.load(open(os.path.join(pack, "HdStudio", "assets", "assets.json"), encoding="utf-8"))
    if not any(a.get("id") == picked[0]["id"] for a in catalog["assets"]):
        raise SystemExit("a later asset export lost the manual pick")

    if sha(rom) != rom_hash:
        raise SystemExit("ROM changed during live capture")

    print(json.dumps({
        "passed": True,
        "samples": status.get("samples"),
        "patterns": status.get("patterns"),
        "spriteOccurrences": status.get("spriteOccurrences"),
        "backgroundBlocks": status.get("backgroundBlocks"),
        "liveAssets": len(live_assets),
        "pickedAsset": picked[0]["id"],
        "pauseFrame": pause.get("frame"),
        "romIntegrity": True
    }, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
