"""Reconcile a library validation run from its per-ROM capture logs.

The smoke run is intentionally idempotent: it can be interrupted and its JSON is
rewritten from completed packs. This post-pass re-reads the actual logs and raw
artifacts so fields such as gameplay detection are not inferred from prose.

Example:
  python scripts/postprocess_library_v05.py --root D:\\MesenHighWork\\validation-v05\\smoke
"""
import argparse
import csv
import json
import os


def read_text(path):
    try:
        return open(path, encoding="utf-8", errors="replace").read()
    except Exception:
        return ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    args = ap.parse_args()
    src = os.path.join(args.root, "smoke-results.json")
    if not os.path.exists(src):
        src = os.path.join(args.root, "smoke-results.partial.json")
    rows = json.load(open(src, encoding="utf-8"))
    for row in rows:
        pack = row.get("packFolder")
        if not pack:
            continue
        log = read_text(os.path.join(pack, "capture.log"))
        if "HD Studio explorer: gameplay detected" in log:
            row["gameplayReached"] = "passed"
        elif "no gameplay signature" in log:
            row["gameplayReached"] = "blocked"
        elif row.get("captureCompleted") == "passed":
            row["gameplayReached"] = "not determined"
        report_path = os.path.join(pack, "HdStudio", "report.json")
        if os.path.exists(report_path):
            try:
                report = json.load(open(report_path, encoding="utf-8"))
                cap = report.get("capture", {})
                row["framesProcessed"] = cap.get("frames", row.get("framesProcessed"))
                row["observedPatterns"] = cap.get("chrTilesSeen", row.get("observedPatterns"))
                row["spriteTilePaletteCombos"] = cap.get("seenSpriteCombos", row.get("spriteTilePaletteCombos"))
            except Exception:
                pass
        objects_path = os.path.join(pack, "HdStudio", "objects.txt")
        if os.path.exists(objects_path):
            occurrences = 0
            with open(objects_path, encoding="utf-8", errors="replace") as f:
                for line in f:
                    if line and not line.startswith("#"):
                        first = line.split("\t", 1)[0]
                        if first.isdigit():
                            occurrences += int(first)
            row["spriteOccurrences"] = occurrences

    for row in rows:
        if row.get("captureCompleted") == "passed" and row.get("assetsExported") != "passed":
            row["limitations"] = (row.get("limitations", "") + " Asset export failed.").strip()
        if row.get("captureCompleted") == "failed":
            row["limitations"] = (row.get("limitations", "") + " ROM load/capture failed; see capture.log.").strip()

    out = os.path.join(args.root, "smoke-results.json")
    json.dump(rows, open(out, "w", encoding="utf-8"), indent=1)
    fields = [
        "romName", "sha256", "gameTitle", "mapper", "chrType", "captureCompleted",
        "gameplayReached", "framesProcessed", "observedPatterns", "spriteOccurrences",
        "reconstructedCompositions", "candidateObjects", "animationRecords",
        "backgroundCompositions", "spriteReplacementVerified", "animationReplacementVerified",
        "backgroundReplacementVerified", "contextCorrectness", "stockMesenCompatibility",
        "originalRomIntegrity", "processingSeconds", "limitations",
    ]
    with open(os.path.join(args.root, "smoke-matrix.csv"), "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)
    summary = {
        "uniqueRoms": len(rows),
        "capturePassed": sum(r.get("captureCompleted") == "passed" for r in rows),
        "assetsPassed": sum(r.get("assetsExported") == "passed" for r in rows),
        "gameplayDetected": sum(r.get("gameplayReached") == "passed" for r in rows),
        "gameplayBlocked": sum(r.get("gameplayReached") == "blocked" for r in rows),
        "romIntegrityPassed": sum(r.get("originalRomIntegrity") == "passed" for r in rows),
        "totalSeconds": round(sum(r.get("processingSeconds", 0) for r in rows), 1),
        "frames": rows[0].get("framesProcessed") if rows else None,
    }
    json.dump(summary, open(os.path.join(args.root, "smoke-summary.json"), "w", encoding="utf-8"), indent=2)
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
