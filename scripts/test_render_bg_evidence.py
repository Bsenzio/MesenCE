"""Regression: render-time background evidence must be used by live capture.

Runs one short live capture on the synthetic NROM test cartridge and asserts that
the paused picking frame reports `backgroundEvidence == "render-time"`, has visible
background cells, and that a per-frame rendered-background TSV exists. The test
never requires a commercial ROM.
"""
import json
import os
import subprocess
import sys
import tempfile
import time

MESEN = os.environ.get("MESEN_BIN", r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
HOME = os.environ.get("MESEN_HOME", r"D:\MesenHighWork\mesen-home")
ROM = os.environ.get("HDSTUDIO_TEST_ROM",
                     r"D:\RetroArcDo_HDStudio\source\examples\test-roms\hdstudio_test.nes")


def main():
    if not os.path.exists(ROM):
        print("SKIP: synthetic test ROM not found:", ROM)
        return 0
    root = tempfile.mkdtemp(prefix="hdstudio-bg-evidence-")
    pack = os.path.join(root, "pack")
    live = os.path.join(pack, "HdStudio", "live")
    os.makedirs(live, exist_ok=True)
    control = os.path.join(live, "control.txt")
    open(control, "w", encoding="utf-8").write("0|resume\n")
    env = os.environ.copy()
    env["MESEN_HOME"] = HOME
    proc = subprocess.Popen([MESEN, "--hdstudio", ROM, "--live", "--live-mode=balanced",
                             "--frames=100000", "--timeout=45", "--out=" + pack],
                            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    status_path = os.path.join(live, "live-status.json")
    pause_path = os.path.join(live, "pause-frame.json")
    paused = False
    try:
        deadline = time.time() + 60
        while time.time() < deadline and proc.poll() is None:
            try:
                status = json.load(open(status_path, encoding="utf-8"))
            except Exception:
                status = {}
            if not paused and status.get("frame", 0) >= 120 and status.get("samples", 0) > 0:
                open(control, "w", encoding="utf-8").write("1|pause\n")
                paused = True
            if paused:
                try:
                    pause = json.load(open(pause_path, encoding="utf-8"))
                except Exception:
                    pause = {}
                if status.get("paused") and pause:
                    open(control, "w", encoding="utf-8").write("2|stop\n")
                    break
            time.sleep(0.2)
        proc.wait(timeout=30)
    finally:
        if proc.poll() is None:
            proc.kill()
    pause = json.load(open(pause_path, encoding="utf-8"))
    assert pause.get("backgroundEvidence") == "render-time", \
        "live pause did not use render-time background evidence: " + str(pause.get("backgroundEvidence"))
    assert len(pause.get("background") or []) > 0, "pause frame has no visible background cells"
    tsv = os.path.join(live, "live-background-frames.tsv")
    assert os.path.exists(tsv), "per-frame rendered background TSV is missing"
    lines = [line for line in open(tsv, encoding="utf-8").read().splitlines() if line and not line.startswith("#")]
    assert lines, "rendered background TSV has no samples"
    print(json.dumps({"evidence": pause.get("backgroundEvidence"),
                      "pauseCells": len(pause.get("background") or []),
                      "renderedFrameRows": len(lines)}, indent=1))
    print("render-time background evidence regression: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
