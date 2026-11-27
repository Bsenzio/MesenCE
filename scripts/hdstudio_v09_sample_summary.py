"""Summarize the final-engine replacement sample without inventing results."""
import argparse
import json
import os
from collections import Counter


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", default=r"D:\MesenHighWork\validation-v09\replacement-sample\replacement-results.json")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v09\replacement-sample\replacement-sample-summary.json")
    args = ap.parse_args()
    with open(args.results, encoding="utf-8") as f:
        rows = json.load(f)
    status = Counter(r.get("status", "") for r in rows)
    summary = {
        "attempted": len(rows),
        "statusCounts": dict(status),
        "verifiedTotal": sum(1 for r in rows if str(r.get("status", "")).startswith("verified")),
        "verifiedSpriteOrBackground": sum(1 for r in rows if str(r.get("status", "")).startswith("verified")),
        "sharedRows": sum(1 for r in rows if "shared" in str(r.get("status", ""))),
        "collateralRows": sum(1 for r in rows if (r.get("changedPixelsOutside") or 0) > 0),
        "stateIdenticalRows": sum(1 for r in rows if r.get("stateIdentical") is True),
        "changedInside": sum((r.get("changedPixelsInside") or 0) for r in rows),
        "changedOutside": sum((r.get("changedPixelsOutside") or 0) for r in rows),
        "markerInside": sum((r.get("markerPixelsInside") or 0) for r in rows),
        "markerOutside": sum((r.get("markerPixelsOutside") or 0) for r in rows),
        "roms": [{"rom": r.get("rom"), "status": r.get("status"),
                  "budget": r.get("explorationBudget"), "frame": r.get("pauseFrame"),
                  "changedInside": r.get("changedPixelsInside"), "changedOutside": r.get("changedPixelsOutside"),
                  "markerInside": r.get("markerPixelsInside"), "markerOutside": r.get("markerPixelsOutside"),
                  "state": "identical" if r.get("stateIdentical") is True else "different-or-unknown"} for r in rows],
        "note": "Final-engine sample only; this is not the full 1,291-ROM replacement gate and does not relabel historical v0.8 evidence.",
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=1)
    print(json.dumps({k: v for k, v in summary.items() if k != "roms"}, indent=2))
    print("summary:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
