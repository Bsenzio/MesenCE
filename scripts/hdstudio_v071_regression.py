"""v0.7 regression suite runner with durable JSON evidence."""
import argparse
import json
import os
import subprocess
import time

TESTS = [
    ("project", "test_hdstudio_project.py", 900),
    ("export", "test_hdstudio_export.py", 900),
    ("safety", "test_hdstudio_safety.py", 900),
    ("discovery", "test_hdstudio_discovery.py", 1200),
    ("slicer", "test_hdstudio_slicer.py", 300),
    ("reconstruction", "test_hdstudio_reconstruction.py", 300),
    ("sampling", "test_hdstudio_sampling.py", 900),
    ("live", "test_hdstudio_live.py", 1200),
    ("geometry", "test_hdstudio_geometry.py", 300),
    ("synthetic", "test_synthetic_suite.py", 1800),
    ("context-planner", "test_hdstudio_context_planner.py", 300),
    ("render-bg-evidence", "test_render_bg_evidence.py", 300),
    ("v071-fixtures", "test_hdstudio_v071_fixtures.py", 600),
]


def tail(path, count=1600):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            data = f.read()
        return data[-count:]
    except Exception:
        return ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=r"D:\MesenHigh\scripts")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v071\\regression-results.json")
    ap.add_argument("--log-dir", default=r"D:\MesenHighWork\validation-v071\\regression-logs")
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--rom", default=r"D:\NESGames\Library\S\Super Mario Bros. 3 (USA).nes")
    ap.add_argument("--stock", default=r"D:\MesenHighWork\stock-mesen\Mesen_2.1.1\Mesen.exe")
    ap.add_argument("--stock-rom", default=r"D:\MesenHighWork\test-roms-new2\hdstudio_holdout.nes")
    ap.add_argument("--stock-pack", default=r"D:\MesenHighWork\validation-v05\synthetic-replacement")
    ap.add_argument("--importer-pack", default="")
    ap.add_argument("--importer-image", default="")
    ap.add_argument("--importer-asset", default="")
    ap.add_argument("--project-rom", default=r"D:\MesenHighWork\test-roms-new2\hdstudio_holdout.nes")
    ap.add_argument("--project-pack", default=r"D:\MesenHighWork\validation-v05\synthetic-replacement")
    ap.add_argument("--project-image", default=r"D:\MesenHighWork\test-art\v05_star.png")
    ap.add_argument("--project-asset", default="backgroundBlock:9757ac95:35928bd1a9df")
    args = ap.parse_args()
    os.makedirs(args.log_dir, exist_ok=True)
    env = os.environ.copy()
    env["MESEN_HOME"] = args.home
    env["MESEN_BIN"] = args.mesen
    results = []

    started = time.time()
    selftest_log = os.path.join(args.log_dir, "selftest.log")
    p = subprocess.run([args.mesen, "--hdstudio", "--selftest"], env=env,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                       timeout=300)
    open(selftest_log, "w", encoding="utf-8").write(p.stdout or "")
    selftest_json = {}
    for line in (p.stdout or "").splitlines():
        if line.startswith("SELFTEST_JSON"):
            try:
                selftest_json = json.loads(line.split(" ", 1)[1])
            except Exception:
                pass
    results.append({"name": "selftest", "exitCode": p.returncode,
                    "passed": p.returncode == 0 and selftest_json.get("failed", 1) == 0,
                    "details": selftest_json, "seconds": round(time.time() - started, 1),
                    "log": selftest_log})

    for name, script, timeout in TESTS:
        path = os.path.join(args.root, script)
        if not os.path.exists(path):
            results.append({"name": name, "passed": False, "skipped": True,
                            "reason": "script not found"})
            continue
        log = os.path.join(args.log_dir, name + ".log")
        started = time.time()
        command = ["python", path] if name in ("context-planner", "render-bg-evidence", "v071-fixtures") else ["python", path, "--mesen", args.mesen]
        if name == "synthetic":
            command += ["--home", args.home,
                        "--rom-dir", r"D:\RetroArcDo_HDStudio\source\examples\test-roms"]
        elif name == "live":
            command += ["--home", args.home, "--rom", args.rom]
        elif name == "project":
            command += ["--rom", args.project_rom, "--pack", args.project_pack,
                        "--image", args.project_image, "--asset", args.project_asset]
        try:
            with open(log, "w", encoding="utf-8", errors="replace") as output:
                proc = subprocess.run(command, env=env, stdout=output,
                                      stderr=subprocess.STDOUT, timeout=timeout)
            code = proc.returncode
        except subprocess.TimeoutExpired:
            code = None
        text = tail(log)
        results.append({"name": name, "exitCode": code, "passed": code == 0,
                        "seconds": round(time.time() - started, 1),
                        "log": log, "tail": text})

    # Stock Mesen compatibility, when the stock executable and a valid pack exist.
    if os.path.exists(args.stock) and os.path.exists(args.stock_rom) and os.path.exists(os.path.join(args.stock_pack, "hires.txt")):
        log = os.path.join(args.log_dir, "stock-mesen.log")
        started = time.time()
        command = ["python", os.path.join(args.root, "test_stock_mesen.py"),
                   "--stock", args.stock, "--rom", args.stock_rom,
                   "--pack", args.stock_pack,
                   "--shots", os.path.join(args.log_dir, "stock-shots"),
                   "--capture-script", r"D:\MesenHighWork\scripts\capture_window.ps1"]
        try:
            with open(log, "w", encoding="utf-8", errors="replace") as output:
                proc = subprocess.run(command, env=env, stdout=output,
                                      stderr=subprocess.STDOUT, timeout=300)
            code = proc.returncode
        except subprocess.TimeoutExpired:
            code = None
        results.append({"name": "stock-mesen-2.1.1", "exitCode": code, "passed": code == 0,
                        "seconds": round(time.time() - started, 1),
                        "log": log, "tail": tail(log)})
    else:
        results.append({"name": "stock-mesen-2.1.1", "passed": False, "skipped": True,
                        "reason": "stock Mesen, synthetic ROM or pack not available"})

    # Importer regression with explicit prerequisites.
    if not args.importer_pack:
        args.importer_pack = args.project_pack
    if not args.importer_image:
        args.importer_image = args.project_image
    if not args.importer_asset:
        args.importer_asset = args.project_asset
    if args.importer_pack and args.importer_image and args.importer_asset:
        log = os.path.join(args.log_dir, "importer.log")
        started = time.time()
        command = ["python", os.path.join(args.root, "test_importer.py"),
                   "--mesen", args.mesen, "--rom", args.project_rom,
                   "--pack", args.importer_pack, "--image", args.importer_image,
                   "--asset", args.importer_asset]
        try:
            with open(log, "w", encoding="utf-8", errors="replace") as output:
                proc = subprocess.run(command, env=env, stdout=output,
                                      stderr=subprocess.STDOUT, timeout=600)
            code = proc.returncode
        except subprocess.TimeoutExpired:
            code = None
        results.append({"name": "importer", "exitCode": code, "passed": code == 0,
                        "seconds": round(time.time() - started, 1),
                        "log": log, "tail": tail(log)})
    else:
        results.append({"name": "importer", "passed": False, "skipped": True,
                        "reason": "atlas pack image prerequisites not supplied"})

    executed = [r for r in results if not r.get("skipped")]
    report = {
        "schemaVersion": "1.0.0",
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "engine": args.mesen,
        "executed": len(executed),
        "passed": sum(1 for r in executed if r.get("passed")),
        "failed": sum(1 for r in executed if not r.get("passed")),
        "skipped": sum(1 for r in results if r.get("skipped")),
        "results": results,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=1)
    print(json.dumps({k: report[k] for k in ("executed", "passed", "failed", "skipped")}, indent=2))
    for r in results:
        print("%-20s %s %s" % (r["name"], "PASS" if r.get("passed") else ("SKIP" if r.get("skipped") else "FAIL"),
                               r.get("reason", "")))
    return 0 if report["failed"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
