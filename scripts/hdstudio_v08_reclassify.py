"""Recompute v0.8 scene-activity classes from stored evidence.

The first adaptive pass used the old absolute gameplay-frame threshold. The
fast/minimal recorder takes fewer samples, so that threshold over-labelled
title/menu scenes. This script applies the sampling-normalised rule to the same
stored evidence; it does not change any pixel, state-hash or ROM-integrity result.
"""
import argparse
import json
import os
import sys

sys.path.insert(0, r"D:\MesenHigh\scripts")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hdstudio_v08_autoreplace as v08  # noqa: E402


def classify(row):
    live = row.get("liveStatus") or {}
    samples = int(live.get("samples") or 0)
    active = int(live.get("gameplayFrames") or 0)
    ratio = (active / samples) if samples else 0.0
    row["sceneActiveRatio"] = round(ratio, 4)
    if ratio >= 0.15:
        row["sceneActivityClass"] = "gameplay"
    elif row.get("backgroundFallback"):
        row["sceneActivityClass"] = "background-content"
    else:
        row["sceneActivityClass"] = "title-or-menu"
    status = str(row.get("status", ""))
    if status != "verified-title-or-menu-scene":
        return row
    if row["sceneActivityClass"] == "title-or-menu":
        return row
    imports = row.get("imports") or []
    plan = (imports[0].get("plan") if imports else {}) or {}
    plan_status = plan.get("contextStatus") or ""
    use_conditions = any((a.get("plan") or {}).get("conditionsApplied") for a in imports)
    frames = row.get("frameResults") or []
    multi_scene = len(frames) >= 2 and len({f.get("frame") for f in frames}) >= 2
    collateral = (row.get("changedPixelsOutside") or 0) > 0 or (row.get("markerPixelsOutside") or 0) > 0
    row["status"] = v08.classify_verified(
        "background" if row.get("backgroundFallback") else "sprite",
        plan_status, multi_scene, use_conditions,
        row.get("sharedAssetCount") or 0, collateral, row.get("assetsImported") or 1)
    row["reclassifiedFrom"] = "verified-title-or-menu-scene"
    return row


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    rows = json.load(open(args.input, encoding="utf-8"))
    changed = 0
    for row in rows:
        before = row.get("status")
        classify(row)
        if row.get("status") != before:
            changed += 1
    tmp = args.out + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(rows, f, indent=1)
    os.replace(tmp, args.out)
    from collections import Counter
    print(json.dumps({
        "rows": len(rows), "reclassified": changed,
        "statusCounts": dict(Counter(r.get("status") for r in rows)),
        "sceneClasses": dict(Counter(r.get("sceneActivityClass") for r in rows)),
    }, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
