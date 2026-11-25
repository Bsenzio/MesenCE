"""Run one controlled live-capture session and leave the raw evidence on disk.

Example:
  python scripts/live_capture_session.py --rom game.nes --pack D:\\packs\\game \
    --mode balanced --target-frame 1200 --pause-seconds 3 --timeout 90
"""
import argparse
import json
import os
import subprocess
import time


def read_status(path):
    try:
        return json.load(open(path, encoding="utf-8"))
    except Exception:
        return {}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", required=True)
    ap.add_argument("--pack", required=True)
    ap.add_argument("--mode", default="balanced")
    ap.add_argument("--target-frame", type=int, default=1200)
    ap.add_argument("--pause-seconds", type=int, default=3)
    ap.add_argument("--timeout", type=int, default=120)
    ap.add_argument("--seed", type=int, default=20261124)
    ap.add_argument("--no-generate", action="store_true")
    args = ap.parse_args()

    live = os.path.join(args.pack, "HdStudio", "live")
    os.makedirs(live, exist_ok=True)
    control = os.path.join(live, "control.txt")
    open(control, "w", encoding="utf-8").write("0|resume\n")
    env = os.environ.copy()
    env["MESEN_HOME"] = args.home
    cmd = [args.mesen, "--hdstudio", args.rom, "--live", "--live-mode=" + args.mode,
           "--frames=10000000", "--timeout=" + str(args.timeout),
           "--out=" + args.pack, "--log=" + os.path.join(args.pack, "live-session.log")]
    if not args.no_generate:
        cmd += ["--generate", "--seed=" + str(args.seed)]
    process = subprocess.Popen(cmd, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True)
    status_path = os.path.join(live, "live-status.json")
    deadline = time.time() + args.timeout
    paused = False
    while time.time() < deadline:
        s = read_status(status_path)
        if not paused and s.get("frame", 0) >= args.target_frame and s.get("samples", 0) > 0:
            open(control, "w", encoding="utf-8").write("1|pause\n")
            paused = True
            time.sleep(args.pause_seconds)
            open(control, "w", encoding="utf-8").write("2|stop\n")
            break
        time.sleep(0.5)
    if not paused:
        open(control, "w", encoding="utf-8").write("2|stop\n")
    output, _ = process.communicate(timeout=args.timeout + 90)
    if process.returncode != 0:
        print(output[-4000:])
        raise SystemExit("live capture failed")
    status = read_status(status_path)
    pause_path = os.path.join(live, "pause-frame.json")
    pause = read_status(pause_path)
    print(json.dumps({
        "pack": os.path.abspath(args.pack),
        "status": status,
        "pauseSprites": len(pause.get("sprites", [])),
        "pauseBackgroundCells": len(pause.get("background", [])),
        "pauseScreenshot": pause.get("screenshot"),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
