"""Run staged v0.8 adaptive recovery: first pass, then longer budgets only for
unresolved rows, then merge all evidence.

The live recorder is slower than a throughput batch. Paying the longest budget for
every row would not be a good use of local compute, so the stages implement the
directive's "longer exploration only where justified" rule explicitly.
"""
import argparse
import json
import os
import subprocess
import sys


def load(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def write_failures(results, path):
    hashes = []
    for row in results:
        status = str(row.get("status", ""))
        # Title/menu rows are real replacements but not gameplay evidence; a
        # longer stage tries to reach gameplay before the row is left in that class.
        if (not status.startswith("verified")) or status == "verified-title-or-menu-scene":
            if row.get("sha256"):
                hashes.append(row["sha256"].lower())
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(hashes) + ("\n" if hashes else ""))
    return hashes


def run_stage(script, hashes, out_root, budgets, profiles, workers, extra):
    cmd = [sys.executable, script,
           "--hash-file", hashes,
           "--budgets", budgets,
           "--profiles", profiles,
           "--target-mode", "auto",
           "--workers", str(workers),
           "--scale", "2",
           "--out-root", out_root,
           "--live-timeout", "300",
           "--short-work",
           "--force"]
    print("STAGE", out_root, "hashes", os.path.getsize(hashes), "budgets", budgets, "profiles", profiles, flush=True)
    subprocess.run(cmd, check=True, cwd=os.path.dirname(os.path.dirname(script)))
    return load(os.path.join(out_root, "replacement-results.json"), []) or []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stage1", required=True)
    ap.add_argument("--hashes", required=True, help="the initial 213-row hash file")
    ap.add_argument("--work", default=r"D:\MesenHighWork\validation-v08")
    ap.add_argument("--workers", type=int, default=7)
    args = ap.parse_args()
    os.makedirs(args.work, exist_ok=True)
    script = os.path.join(r"D:\MesenHigh", "scripts", "hdstudio_v08_autoreplace.py")
    merge = os.path.join(r"D:\MesenHigh", "scripts", "hdstudio_v08_merge_results.py")
    stage1 = load(args.stage1, []) or []
    print("stage1 rows", len(stage1), flush=True)
    inputs = [args.stage1]

    fail2 = os.path.join(args.work, "stage2-hashes.txt")
    hashes = write_failures(stage1, fail2)
    stage2_path = os.path.join(args.work, "adaptive-stage2", "replacement-results.json")
    if hashes:
        stage2 = run_stage(script, fail2, os.path.join(args.work, "adaptive-stage2"),
                           "2700", "default;menu", args.workers, [])
        inputs.append(stage2_path)
    else:
        stage2 = []

    combined = load(args.stage1, []) or []
    fail3 = os.path.join(args.work, "stage3-hashes.txt")
    hashes3 = write_failures(combined if not stage2 else stage2, fail3)
    # A stage-3 row must also keep any stage-2 verified result; the merge handles
    # that, so the stage-3 candidate set is the still-unverified rows from stage 2.
    stage3_path = os.path.join(args.work, "adaptive-stage3", "replacement-results.json")
    if hashes3:
        stage3 = run_stage(script, fail3, os.path.join(args.work, "adaptive-stage3"),
                           "4500", "default;menu;alt", args.workers, [])
        inputs.append(stage3_path)

    merged = os.path.join(args.work, "adaptive-merged.json")
    subprocess.run([sys.executable, merge, "--inputs"] + inputs + ["--out", merged], check=True)
    print("merged", merged, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
