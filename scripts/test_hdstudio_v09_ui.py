"""Native RetroArchaeology workspace smoke test.

Launches the real Avalonia Studio, invokes the RetroArchaeology toolbar button
through UI Automation, checks that the native workspace opens with its catalog
and evidence sections, then closes the process. Skipped on non-Windows.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile
import time


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", default=r"D:\MesenHighWork\test-roms\hdstudio_test.nes")
    ap.add_argument("--probe", default=r"D:\MesenHigh\scripts\hdstudio_v09_ui_probe.ps1")
    args = ap.parse_args()
    if os.name != "nt":
        print("SKIP: Windows-only UI smoke test")
        return 0
    with tempfile.TemporaryDirectory(prefix="hdstudio-v09-ui-") as tmp:
        pack = os.path.join(tmp, "pack")
        os.makedirs(pack)
        env = os.environ.copy()
        env["MESEN_HOME"] = args.home
        proc = subprocess.Popen([args.mesen, "--hdstudio-ui", "--rom=" + args.rom, "--pack=" + pack],
                                env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            #Give Avalonia time to create the main window before probing.
            time.sleep(6)
            probe = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", args.probe,
                                    "-ProcessId", str(proc.pid), "-TimeoutSeconds", "25"],
                                   capture_output=True, text=True, timeout=45)
            text = (probe.stdout or "").strip()
            line = next((l for l in text.splitlines() if l.startswith("{")), "")
            if not line:
                raise AssertionError("UI probe produced no JSON: " + text + "\n" + (probe.stderr or ""))
            result = json.loads(line)
            if not result.get("ok"):
                raise AssertionError("UI probe failed: " + json.dumps(result))
            if not result.get("runButton") or not result.get("catalog") or not result.get("evidence"):
                raise AssertionError("workspace sections missing: " + json.dumps(result))
            print("PASS ui-main-window")
            print("PASS ui-archaeology-button")
            print("PASS ui-catalog-section")
            print("PASS ui-evidence-section")
        finally:
            try:
                proc.terminate()
                proc.wait(timeout=10)
            except Exception:
                try:
                    proc.kill()
                except Exception:
                    pass
    print("V09_UI_OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
