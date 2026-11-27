"""Aggregate v0.7.1 and v0.8 evidence into the final v0.8 matrix and metrics.

The v0.7.1 full-library matrix is preserved as the historical baseline. A v0.8
row replaces the terminal outcome only when the v0.8 pass produced its own
measured terminal result; unrerun rows keep their v0.7.1 outcome.

Usage:
  python scripts/hdstudio_v08_build_matrix.py \
    --base D:\\MesenHighWork\\validation-v071\\master-matrix.json \
    --results D:\\...\\adaptive-devval\\replacement-results.json \
              D:\\...\\adaptive-holdout\\replacement-results.json \
    --out D:\\MesenHighWork\\validation-v08
"""
import argparse
import csv
import json
import os


def load(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def atomic_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    os.replace(tmp, path)


def terminal_from_outcome(outcome, base_terminal=""):
    outcome = outcome or ""
    if outcome.startswith("verified-multi-asset"):
        return "Verified multiple assets in one combined pack"
    if outcome.startswith("verified-background-independent"):
        return "Verified independent"
    if outcome.startswith("verified-background-context-independent"):
        return "Verified context-independent"
    if outcome.startswith("verified-background-conditional"):
        return "Verified scene-isolated (partial coverage)"
    if outcome.startswith("verified-background-scene-independent-shared-keys"):
        return "Verified scene-independent with shared keys"
    if outcome.startswith("verified-background-scene-isolated"):
        return "Verified scene-isolated (partial coverage)"
    if outcome.startswith("verified-background-shared"):
        return "Verified explicitly shared"
    if outcome.startswith("verified-independent"):
        return "Verified independent"
    if outcome.startswith("verified-context-independent"):
        return "Verified context-independent"
    if outcome.startswith("verified-conditional-scene-isolated"):
        return "Verified scene-isolated (partial coverage)"
    if outcome.startswith("verified-scene-independent-shared-keys"):
        return "Verified scene-independent with shared keys"
    if outcome.startswith("verified-scene-isolated"):
        return "Verified scene-isolated (partial coverage)"
    if outcome.startswith("verified-shared"):
        return "Verified explicitly shared"
    if outcome.startswith("verified-title-or-menu-scene"):
        return "Verified menu or title scene only"
    if outcome.startswith("partial-"):
        return "Partial replacement"
    if outcome.startswith("failed-no-visible"):
        return "Not applicable for a specific graphical test"
    if outcome.startswith("failed"):
        return "Failed replacement"
    return base_terminal or "Unresolved"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=r"D:\MesenHighWork\validation-v071\master-matrix.json")
    ap.add_argument("--results", nargs="*", default=[])
    ap.add_argument("--background-results", default=r"D:\MesenHighWork\validation-v071\background\replacement-results.json")
    ap.add_argument("--animation-results", default=r"D:\MesenHighWork\validation-v08\animation-results.json")
    ap.add_argument("--multi-results", default="")
    ap.add_argument("--integrity", default=r"D:\MesenHighWork\validation-v071\library-integrity.json")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v08")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    base = load(args.base, [])
    rows = []
    for row in base:
        item = dict(row)
        item["baselineV071Status"] = row.get("terminalStatus")
        item["v08"] = None
        item["v08Recovered"] = False
        rows.append(item)
    by_sha = {r.get("romSha256", "").lower(): r for r in rows}

    v08_rows = []
    for path in args.results:
        if not path or not os.path.exists(path):
            continue
        data = load(path, [])
        for result in data:
            sha = (result.get("sha256") or "").lower()
            row = by_sha.get(sha)
            if row is None:
                continue
            outcome = result.get("status") or "failed-unknown"
            row["v08"] = {
                "status": outcome,
                "classification": result.get("classification"),
                "profile": result.get("profile"),
                "explorationBudget": result.get("explorationBudget"),
                "pauseFrame": result.get("pauseFrame"),
                "lateScene": bool(result.get("lateScene")),
                "backgroundFallback": bool(result.get("backgroundFallback")),
                "colorShiftedMarker": bool(result.get("colorShiftedMarker")),
                "baselineUsed": bool(result.get("baselineUsed")),
                "changedPixelsInside": result.get("changedPixelsInside"),
                "changedPixelsOutside": result.get("changedPixelsOutside"),
                "markerPixelsInside": result.get("markerPixelsInside"),
                "markerPixelsOutside": result.get("markerPixelsOutside"),
                "assetsImported": result.get("assetsImported"),
                "sharedAssetCount": result.get("sharedAssetCount"),
                "stateIdentical": result.get("stateIdentical"),
                "hdPackActive": result.get("hdPackActive"),
                "originalRomIntegrity": result.get("originalRomIntegrity"),
                "resultPath": path,
            }
            v08_rows.append(result)
            if outcome.startswith("verified") or outcome.startswith("partial") or outcome.startswith("failed"):
                # A v0.8 terminal result is the current outcome. A still-failed
                # recovery row is kept in its v0.7.1 failure class, not relabelled.
                if outcome.startswith("verified"):
                    row["v08Recovered"] = not str(row.get("terminalStatus", "")).startswith("Verified")
                    row["terminalStatus"] = terminal_from_outcome(outcome, row.get("terminalStatus", ""))
                elif not str(row.get("terminalStatus", "")).startswith("Verified"):
                    row["terminalStatus"] = terminal_from_outcome(outcome, row.get("terminalStatus", ""))

    # Background benchmark (v0.7.1 90-row validation sample) remains historical.
    background = load(args.background_results, []) or []
    # v0.8 forced-background rows are also part of the background expansion.
    background_v08 = [r for r in v08_rows if r.get("status", "").startswith("verified-background")]
    multi = load(args.multi_results, []) if args.multi_results else []
    animation = load(args.animation_results, {}) or {}
    integrity = load(args.integrity, {}) or {}

    counts = {}
    for row in rows:
        counts[row.get("terminalStatus")] = counts.get(row.get("terminalStatus"), 0) + 1
    splits = {}
    for row in rows:
        split = row.get("datasetMembership") or "unknown"
        entry = splits.setdefault(split, {"total": 0, "verified": 0})
        entry["total"] += 1
        if str(row.get("terminalStatus", "")).startswith("Verified"):
            entry["verified"] += 1
    chr_types = {}
    for row in rows:
        ct = row.get("chrType") or "unknown"
        entry = chr_types.setdefault(ct, {"total": 0, "verified": 0})
        entry["total"] += 1
        if str(row.get("terminalStatus", "")).startswith("Verified"):
            entry["verified"] += 1
    mappers = {}
    for row in rows:
        mapper = str(row.get("mapper"))
        entry = mappers.setdefault(mapper, {"total": 0, "verified": 0})
        entry["total"] += 1
        if str(row.get("terminalStatus", "")).startswith("Verified"):
            entry["verified"] += 1

    recovered = [r for r in rows if r.get("v08Recovered")]
    rerun = [r for r in rows if r.get("v08")]
    failed_rerun = [r for r in rerun
                    if not str(r.get("v08", {}).get("status", "")).startswith("verified")
                    and not str(r.get("baselineV071Status", "")).startswith("Verified")]
    classification_counts = {}
    for row in rows:
        cls = ((row.get("v08") or {}).get("classification"))
        if cls:
            classification_counts[cls] = classification_counts.get(cls, 0) + 1
    recovery_kinds = {
        "lateSceneSprite": sum(1 for r in recovered if not (r.get("v08") or {}).get("backgroundFallback")
                               and int((r.get("v08") or {}).get("pauseFrame") or 0) > 3000),
        "backgroundFallback": sum(1 for r in recovered if (r.get("v08") or {}).get("backgroundFallback")),
        "colorShiftedMarker": sum(1 for r in recovered if (r.get("v08") or {}).get("colorShiftedMarker")),
        "earlyScene": sum(1 for r in recovered if not (r.get("v08") or {}).get("backgroundFallback")
                          and int((r.get("v08") or {}).get("pauseFrame") or 0) <= 3000),
    }
    prior = {}
    for row in recovered:
        prior[row.get("baselineV071Status")] = prior.get(row.get("baselineV071Status"), 0) + 1

    summary = {
        "uniqueRoms": len(rows),
        "verifiedUniqueRoms": sum(1 for r in rows if str(r.get("terminalStatus", "")).startswith("Verified")),
        "terminalStatusCounts": counts,
        "bySplit": splits,
        "byChrType": chr_types,
        "byMapper": mappers,
        "recovery": {
            "v08Rows": len(rerun),
            "recoveredRows": len(recovered),
            "stillFailedRows": len(failed_rerun),
            "byPriorStatus": prior,
            "byKind": recovery_kinds,
            "classificationCounts": classification_counts,
        },
        "background": {
            "v071Attempted": len(background),
            "v071Verified": sum(1 for r in background if str(r.get("status", "")).startswith("verified")),
            "v08ForcedOrFallbackVerified": len(background_v08),
            "v08ForcedOrFallbackRows": sum(1 for r in v08_rows if r.get("backgroundFallback")),
        },
        "multiAsset": {
            "rows": len(multi),
            "verifiedCombined": sum(1 for r in multi if str(r.get("status", "")).startswith("verified-multi-asset")),
            "sharedCombined": sum(1 for r in multi if r.get("status") == "verified-multi-asset-shared"),
        },
        "animation": animation.get("summary") if isinstance(animation, dict) else {},
        "integrity": integrity,
    }
    atomic_json(os.path.join(args.out, "metrics-v08.json"), summary)
    atomic_json(os.path.join(args.out, "master-matrix-v08.json"), rows)
    fields = ["romSha256", "gameTitle", "datasetMembership", "mapper", "chrType",
              "baselineV071Status", "terminalStatus", "v08Recovered"]
    with open(os.path.join(args.out, "master-matrix-v08.csv"), "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)
    print(json.dumps(summary, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
