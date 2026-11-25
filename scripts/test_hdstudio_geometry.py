"""Offline geometry/adaptation regression test for RetroArcDo HD Studio.

Runs the built-in self test. No ROM, emulator core or network access is used.

Example:
  python scripts/test_hdstudio_geometry.py
"""
import argparse
import json
import os
import subprocess
import sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=os.environ.get("MESEN_HOME", ""))
    args = ap.parse_args()

    env = os.environ.copy()
    if args.home:
        env["MESEN_HOME"] = args.home
    p = subprocess.run([args.mesen, "--hdstudio", "--selftest"], env=env,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    match = None
    for line in p.stdout.splitlines():
        if line.startswith("SELFTEST_JSON "):
            match = line[len("SELFTEST_JSON "):]
    if p.returncode != 0 or match is None:
        print(p.stdout)
        raise SystemExit("geometry selftest failed")
    result = json.loads(match)
    if result.get("failed", 1) != 0:
        print(p.stdout)
        raise SystemExit("geometry selftest reported failures")
    print(json.dumps({"passed": True, "checks": result.get("passed"), "failed": 0}, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
