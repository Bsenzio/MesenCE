"""Run one complete live-pick + authored-replacement + emulator verification case.

The caller chooses a gameplay point. The script:
  1. runs live capture with the generic explorer and pauses at a target frame;
  2. exports the catalog and lists hardware-aware candidates at the point;
  3. saves the first composition (or hardware sprite) candidate;
  4. imports controlled artwork and compiles a standard pack;
  5. runs the deterministic HD/original verification in a separate Mesen home;
  6. preserves the pack, screenshots and logs for the report.

This is an automation aid, not proof of human play. It never writes ROM bytes.

Example:
  python scripts/run_replacement_demo.py --name guardian \
    --rom "D:\\NESGames\\Library\\G\\Guardian Legend, The (USA).nes" \
    --point 128,176 --image D:\\MesenHighWork\\test-art\\v05_star.png \
    --target-frame 900
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import time


def run(args, env=None, timeout=600):
    p = subprocess.run(args, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                       text=True, timeout=timeout)
    return p.returncode, p.stdout


def parse_status(path):
    try:
        return json.load(open(path, encoding="utf-8"))
    except Exception:
        return {}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--root", default=r"D:\MesenHighWork\validation-v05\replacements")
    ap.add_argument("--home", default=r"D:\MesenHighWork\validation-v05\home")
    ap.add_argument("--name", required=True)
    ap.add_argument("--rom", required=True)
    ap.add_argument("--point", required=True, help="NES pixel x,y used for hardware picking")
    ap.add_argument("--image", required=True)
    ap.add_argument("--target-frame", type=int, default=900)
    ap.add_argument("--capture-timeout", type=int, default=75)
    ap.add_argument("--verify-frames", type=int, default=1800)
    ap.add_argument("--condition", default="")
    args = ap.parse_args()

    stem = os.path.splitext(os.path.basename(args.rom))[0]
    home = args.home
    pack = os.path.join(home, "HdPacks", stem)
    os.makedirs(pack, exist_ok=True)
    live = os.path.join(pack, "HdStudio", "live")
    os.makedirs(live, exist_ok=True)
    env = os.environ.copy()
    env["MESEN_HOME"] = home
    control = os.path.join(live, "control.txt")
    open(control, "w", encoding="utf-8").write("0|resume\n")

    live_log = os.path.join(args.root, args.name + "-live.log")
    cmd = [args.mesen, "--hdstudio", args.rom, "--live", "--live-mode=balanced",
           "--generate", "--seed=20261124", "--frames=10000000",
           "--timeout=" + str(args.capture_timeout), "--out=" + pack, "--log=" + live_log]
    proc = subprocess.Popen(cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    status_path = os.path.join(live, "live-status.json")
    deadline = time.time() + args.capture_timeout
    paused = False
    while time.time() < deadline:
        s = parse_status(status_path)
        if not paused and s.get("frame", 0) >= args.target_frame and s.get("samples", 0) > 0:
            open(control, "w", encoding="utf-8").write("1|pause\n")
            paused = True
            time.sleep(2)
            open(control, "w", encoding="utf-8").write("2|stop\n")
            break
        time.sleep(0.4)
    if not paused:
        open(control, "w", encoding="utf-8").write("2|stop\n")
    output, _ = proc.communicate(timeout=args.capture_timeout + 120)
    if proc.returncode != 0:
        print(output[-5000:])
        raise SystemExit("live capture failed")
    if not paused:
        raise SystemExit("target frame was not reached before the capture timeout")

    code, out = run([args.mesen, "--hdstudio", args.rom, "--assets", "--out=" + pack,
                     "--log=" + os.path.join(args.root, args.name + "-assets.log")], env)
    if code != 0:
        print(out[-5000:])
        raise SystemExit("asset export failed")

    x, y = [int(v) for v in args.point.split(",")]
    code, candidates = run([args.mesen, "--hdstudio", "--pick=%d,%d" % (x, y),
                            "--out=" + pack], env)
    lines = [line.strip() for line in candidates.splitlines() if re.match(r"^\s*\[\d+\]", line)]
    if not lines:
        print(candidates[-5000:])
        raise SystemExit("no pick candidates at " + args.point)
    choice = None
    for line in lines:
        m = re.match(r"\[(\d+)\]\s+metasprite:", line)
        if m:
            choice = int(m.group(1))
            break
    if choice is None:
        m = re.match(r"\[(\d+)\]", lines[0])
        choice = int(m.group(1))

    code, out = run([args.mesen, "--hdstudio", "--pick=%d,%d" % (x, y),
                     "--pick-save=%d" % choice, "--pick-name=%s" % args.name,
                     "--out=" + pack], env)
    if code != 0:
        print(out[-5000:])
        raise SystemExit("hardware pick save failed")
    picked = json.load(open(os.path.join(live, "picked-assets.json"), encoding="utf-8"))["assets"]
    asset = picked[-1]

    #The exporter merges picked-assets into assets.json; refresh it before import.
    code, out = run([args.mesen, "--hdstudio", args.rom, "--assets", "--out=" + pack,
                     "--log=" + os.path.join(args.root, args.name + "-assets-after-pick.log")], env)
    if code != 0:
        print(out[-5000:])
        raise SystemExit("asset export after pick failed")

    import_args = [args.mesen, "--hdstudio", args.rom, "--import",
                   "--asset=" + asset["id"], "--image=" + args.image,
                   "--keep-largest", "--all-palettes", "--mirror-conditions",
                   "--import-name=" + args.name, "--out=" + pack]
    if args.condition:
        import_args.append("--condition=" + args.condition)
    code, out = run(import_args, env)
    if code != 0:
        print(out[-8000:])
        raise SystemExit("artwork import/compile failed")

    verify_script = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                                 "scripts", "preview_input.lua")
    code, verify_out = run([args.mesen, "--hdstudio", args.rom, verify_script,
                            "--verify", "--shots=2", "--frames=" + str(args.verify_frames),
                            "--out=" + pack], env, timeout=900)
    open(os.path.join(args.root, args.name + "-verify.log"), "w", encoding="utf-8").write(verify_out)
    if code != 0:
        print(verify_out[-8000:])
        raise SystemExit("emulator verify failed")

    report = {
        "name": args.name,
        "rom": args.rom,
        "pack": pack,
        "assetId": asset["id"],
        "assetName": asset.get("name"),
        "assetTiles": len(asset.get("tiles", [])),
        "assetDimensions": asset.get("dimensions"),
        "point": args.point,
        "candidateChoice": choice,
        "verifyFrames": args.verify_frames,
        "stateIdentical": "IDENTICAL" in verify_out,
        "verifyLog": os.path.join(args.root, args.name + "-verify.log"),
        "pauseFrame": parse_status(os.path.join(live, "pause-frame.json")).get("frame"),
        "status": parse_status(status_path),
    }
    json.dump(report, open(os.path.join(args.root, args.name + "-result.json"), "w", encoding="utf-8"), indent=2)
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
