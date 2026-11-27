"""Merge staged v0.8 recovery result files without losing earlier evidence.

A later stage begins from power-on with a longer budget. When a row verifies in
any stage it is kept; the earlier failed attempts remain in `attempts` so the
investigation budget is auditable.
"""
import argparse
import json
import os


def load(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return []


def rank(result):
    status = result.get("status", "")
    order = [
        "verified-independent", "verified-context-independent",
        "verified-multi-asset-combined", "verified-multi-asset-shared",
        "verified-background-independent", "verified-background-context-independent",
        "verified-scene-independent-shared-keys", "verified-background-scene-independent-shared-keys",
        "verified-shared", "verified-background-shared",
        "verified-conditional-scene-isolated", "verified-scene-isolated",
        "verified-background-scene-isolated", "verified-title-or-menu-scene",
        "partial-", "failed-no-visible", "failed-", "",
    ]
    for index, prefix in enumerate(order):
        if status.startswith(prefix):
            return len(order) - index
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--inputs", nargs="+", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    by_sha = {}
    order = []
    for path in args.inputs:
        for result in load(path):
            sha = (result.get("sha256") or "").lower()
            if not sha:
                continue
            if sha not in by_sha:
                by_sha[sha] = {"row": result, "all": []}
                order.append(sha)
            by_sha[sha]["all"].append(result)
            current = by_sha[sha]["row"]
            if rank(result) > rank(current):
                by_sha[sha]["row"] = result
            elif rank(result) == rank(current) and result.get("changedPixelsInside", 0) > current.get("changedPixelsInside", 0):
                by_sha[sha]["row"] = result
    merged = []
    for sha in order:
        row = dict(by_sha[sha]["row"])
        earlier = [r for r in by_sha[sha]["all"] if r is not row]
        row["stageEvidence"] = [{
            "status": r.get("status"), "budget": r.get("explorationBudget"),
            "profile": r.get("profile"), "classification": r.get("classification"),
            "changedInside": r.get("changedPixelsInside"),
            "markerInside": r.get("markerPixelsInside"),
        } for r in by_sha[sha]["all"]]
        merged.append(row)
    merged.sort(key=lambda r: (r.get("rom") or "").lower())
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    tmp = args.out + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(merged, f, indent=1)
    os.replace(tmp, args.out)
    counts = {}
    for row in merged:
        counts[row.get("status")] = counts.get(row.get("status"), 0) + 1
    print(json.dumps({"rows": len(merged), "statusCounts": counts}, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
