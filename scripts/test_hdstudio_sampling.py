"""Live sampling-mode regression and overhead measurement.

Runs the same live-capture Lua host in headless Mesen with explicit control
files. Efficient/Balanced/Exhaustive differ only in sampling cadence; the
single-frame mode must freeze one displayed frame and stop sampling by itself.

The result reports measured samples, per-sample CPU time and render-time evidence.
It does not claim wall-clock game performance from a maximum-speed headless run.

Example:
  python scripts/test_hdstudio_sampling.py
"""
import argparse
import json
import os
import shutil
import subprocess
import tempfile
import threading
import time


def status(path):
    try:
        return json.load(open(path, encoding="utf-8"))
    except Exception:
        return {}


def run_mode(args, rom, mode, target_frame=120, timeout=30):
    work = tempfile.mkdtemp(prefix="hdstudio_sampling_" + mode + "_")
    pack = os.path.join(work, "pack")
    live = os.path.join(pack, "HdStudio", "live")
    os.makedirs(live)
    control = os.path.join(live, "control.txt")
    open(control, "w", encoding="utf-8").write("0|resume\n")
    env = os.environ.copy()
    env["MESEN_HOME"] = args.home
    command = [args.mesen, "--hdstudio", rom, "--live", "--live-mode=" + mode,
               "--generate", "--seed=20261124", "--frames=10000000",
               "--timeout=" + str(timeout), "--out=" + pack,
               "--log=" + os.path.join(work, "run.log")]
    process = subprocess.Popen(command, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True)

    def drive():
        path = os.path.join(live, "live-status.json")
        deadline = time.time() + timeout
        while time.time() < deadline:
            s = status(path)
            if mode == "single-frame":
                if s.get("stopped") or s.get("paused"):
                    break
            elif s.get("frame", 0) >= target_frame:
                break
            time.sleep(0.25)
        open(control, "w", encoding="utf-8").write("1|stop\n")

    threading.Thread(target=drive, daemon=True).start()
    output, _ = process.communicate(timeout=timeout + 60)
    if process.returncode != 0:
        print(output)
        raise SystemExit("live mode failed: " + mode)
    s = status(os.path.join(live, "live-status.json"))
    pause_path = os.path.join(live, "pause-frame.json")
    pause = status(pause_path)
    result = {
        "mode": mode,
        "samples": s.get("samples", 0),
        "frame": s.get("frame", 0),
        "patterns": s.get("patterns", 0),
        "spriteOccurrences": s.get("spriteOccurrences", 0),
        "backgroundBlocks": s.get("backgroundBlocks", 0),
        "displaySpriteSamples": s.get("displaySpriteSamples", 0),
        "oamFallbackSamples": s.get("oamFallbackSamples", 0),
        "sampleMicros": s.get("sampleMicros", 0),
        "millisecondsPerSample": round((s.get("sampleMicros", 0) / 1000.0) /
                                      max(1, s.get("samples", 0)), 3),
        "paused": s.get("paused", False),
        "pauseSprites": len(pause.get("sprites", [])),
        "pauseEvidence": sorted({p.get("evidence") for p in pause.get("sprites", [])}),
        "pack": pack,
    }
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", default=r"D:\MesenHighWork\test-roms-new2\hdstudio_holdout.nes")
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--out", default="", help="optional JSON evidence path")
    ap.add_argument("--label", default="synthetic-holdout", help="measurement label")
    args = ap.parse_args()
    rom = os.path.abspath(args.rom)

    results = []
    for mode in ("efficient", "balanced", "exhaustive", "single-frame"):
        results.append(run_mode(args, rom, mode))
        print(json.dumps(results[-1], indent=2), flush=True)

    efficient = results[0]
    balanced = results[1]
    exhaustive = results[2]
    single = results[3]
    if balanced["samples"] < efficient["samples"]:
        raise SystemExit("balanced sampled fewer frames than efficient")
    if exhaustive["samples"] < balanced["samples"]:
        raise SystemExit("exhaustive sampled fewer frames than balanced")
    if not single.get("paused") or single.get("samples", 0) < 1:
        raise SystemExit("single-frame mode did not freeze one frame")
    if any(r["oamFallbackSamples"] for r in results):
        raise SystemExit("a mode fell back to end-of-frame OAM despite render-time support")

    summary = {
        "passed": True,
        "modes": [r["mode"] for r in results],
        "sampleCounts": {r["mode"]: r["samples"] for r in results},
        "msPerSample": {r["mode"]: r["millisecondsPerSample"] for r in results},
        "renderTimeEvidence": True,
        "measurement": "maximum-speed headless CPU time; not interactive wall-clock",
        "label": args.label,
        "rom": rom,
        "rows": results,
    }
    if args.out:
        os.makedirs(os.path.dirname(args.out), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(summary, f, indent=1)
    print(json.dumps(summary, indent=2))
    if not args.keep:
        for r in results:
            shutil.rmtree(os.path.dirname(r["pack"]), ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
