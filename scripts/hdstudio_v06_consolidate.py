"""Consolidate per-ROM result.json files after interrupted/resumed batches."""
import argparse
import csv
import glob
import json
import os


FIELDS = ["rom", "sha256", "status", "mapper", "chrType", "split", "targetMode",
          "pauseFrame", "liveActiveFrames", "assetId", "assetKind", "assetWidth",
          "assetHeight", "assetTiles", "sharedAssetCount", "markerPixelsInside",
          "markerPixelsOutside", "changedPixelsInside", "changedPixelsOutside",
          "sceneExact", "sceneMatchRatio", "stateIdentical", "hdPackActive",
          "originalRomIntegrity", "elapsedSeconds", "failure"]


def compact(row):
    result = {k: row.get(k) for k in FIELDS if k in row}
    if row.get("attempts"):
        result["attempts"] = [{k: a.get(k) for k in
                               ("attempt", "targetFrame", "pauseFrame", "failure",
                                "status", "liveActiveFrames", "assetKind",
                                "sharedAssetCount", "markerPixelsInside",
                                "markerPixelsOutside")}
                              for a in row["attempts"]]
    if row.get("reverification"):
        result["reverification"] = row["reverification"]
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--work", default=r"D:\MesenHighWork\validation-v06")
    args = ap.parse_args()
    sets = {
        "replacements-115": "replacement-batch.txt",
        "replacements-background": "background-30.txt",
        "replacements-homebrew": "homebrew.txt",
        "holdout-replacement": "holdout-15.txt",
        "replacement-recovery2": "recovery2.txt",
        "release-priority": None,
    }
    all_counts = {}
    for root_name, hash_file in sets.items():
        root = os.path.join(args.work, root_name)
        if not os.path.isdir(root):
            continue
        allowed = None
        if hash_file and os.path.exists(os.path.join(args.work, hash_file)):
            allowed = {line.strip() for line in open(os.path.join(args.work, hash_file), encoding="utf-8") if line.strip()}
        rows = []
        seen = set()
        for path in glob.glob(os.path.join(root, "*", "result.json")):
            row = json.load(open(path, encoding="utf-8"))
            h = row.get("sha256")
            if allowed is not None and h not in allowed:
                continue
            if h in seen:
                continue
            seen.add(h)
            rows.append(compact(row))
        rows.sort(key=lambda r: (r.get("rom") or "").lower())
        out_json = os.path.join(root, "replacement-results-consolidated.json")
        out_csv = os.path.join(root, "replacement-matrix-consolidated.csv")
        with open(out_json, "w", encoding="utf-8") as f:
            json.dump(rows, f, indent=1)
        with open(out_csv, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=FIELDS, extrasaction="ignore")
            writer.writeheader()
            writer.writerows(rows)
        counts = {}
        for r in rows:
            counts[r.get("status")] = counts.get(r.get("status"), 0) + 1
        all_counts[root_name] = {"rows": len(rows), "statuses": counts}
        print(root_name, len(rows), counts)
    # Merge the two deep runs.
    deep = []
    for name in ("deep-1800", "deep-expansion"):
        path = os.path.join(args.work, name, "smoke-results.json")
        if os.path.exists(path):
            deep.extend(json.load(open(path, encoding="utf-8")))
    if deep:
        seen = set()
        unique = []
        for row in deep:
            if row.get("sha256") in seen:
                continue
            seen.add(row.get("sha256"))
            unique.append(row)
        unique.sort(key=lambda r: (r.get("romName") or "").lower())
        with open(os.path.join(args.work, "deep-combined.json"), "w", encoding="utf-8") as f:
            json.dump(unique, f, indent=1)
        print("deep-combined", len(unique), "gameplay",
              sum(1 for r in unique if r.get("gameplayReached") == "passed"))
    with open(os.path.join(args.work, "consolidated-summary.json"), "w", encoding="utf-8") as f:
        json.dump(all_counts, f, indent=2)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
