"""Resumable discovery session regression test.

Covers:
  * a deep scan runs capture chunks and exports the catalog after each chunk
  * discovery-session.json records frames, seeds, growth and a stopping reason
  * a second invocation resumes the session instead of restarting
  * --discover-restart explicitly starts a new budget
  * saturation is labelled as measured stagnation, not completeness proof

Example:
  python scripts/test_hdstudio_discovery.py
"""
import argparse
import json
import os
import shutil
import subprocess
import tempfile


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


def session(pack):
    return json.load(open(os.path.join(pack, "HdStudio", "discovery-session.json"), encoding="utf-8"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", default=r"D:\MesenHighWork\test-roms-new2\hdstudio_holdout.nes")
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    work = tempfile.mkdtemp(prefix="hdstudio_discovery_")
    pack = os.path.join(work, "pack")
    common = ["--hdstudio", args.rom, "--discover=deep", "--discover-chunk=300",
              "--discover-budget=600", "--out=" + pack]

    code, log = run(args.mesen, args.home, common + ["--discover-restart"])
    first = session(pack)
    if first["framesCaptured"] < 600:
        raise SystemExit("first discovery did not reach its budget")
    if first["chunks"] < 2:
        raise SystemExit("deep discovery did not split the budget into chunks")
    if first["stopReason"] not in ("budget", "saturation"):
        raise SystemExit("unexpected first stop reason: " + first["stopReason"])
    if not first.get("snapshots"):
        raise SystemExit("session has no per-chunk snapshots")
    for snap in first["snapshots"]:
        if "newAssets" not in snap or "seed" not in snap:
            raise SystemExit("snapshot is missing growth/seed evidence")

    code, log = run(args.mesen, args.home, common)
    second = session(pack)
    if second["requestedFrames"] <= first["requestedFrames"]:
        raise SystemExit("resume did not extend the requested budget")
    if second["framesCaptured"] <= first["framesCaptured"]:
        raise SystemExit("resume did not add frames")
    if second["chunks"] <= first["chunks"]:
        raise SystemExit("resume did not add chunks")

    code, log = run(args.mesen, args.home, common + ["--discover-restart"])
    third = session(pack)
    if third["requestedFrames"] != 600 or third["chunks"] < 2:
        raise SystemExit("restart did not create a fresh 600-frame session")

    if "Saturation is not proof of completeness" not in log:
        raise SystemExit("discovery log does not disclose the saturation limit")

    print(json.dumps({
        "passed": True,
        "first": {k: first[k] for k in ("framesCaptured", "chunks", "stopReason")},
        "resumed": {k: second[k] for k in ("requestedFrames", "framesCaptured", "chunks", "stopReason")},
        "restarted": {k: third[k] for k in ("requestedFrames", "chunks", "stopReason")},
        "saturationDisclosed": True
    }, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
