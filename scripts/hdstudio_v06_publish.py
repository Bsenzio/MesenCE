"""Collect the v0.6 validation artifacts and publish compact copies to the docs repository.

Only JSON/CSV and documentation are copied. Commercial ROMs, packs and screenshots
stay in the local validation tree.
"""
import argparse
import csv
import glob
import hashlib
import json
import os
import shutil


def load(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def dump(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def compact_result(row):
    keep = [
        "rom", "sha256", "status", "mapper", "chrType", "split", "targetMode",
        "pauseFrame", "liveActiveFrames", "assetId", "assetKind", "assetWidth",
        "assetHeight", "assetTiles", "sharedAssetCount", "markerPixelsInside",
        "markerPixelsOutside", "changedPixelsInside", "changedPixelsOutside",
        "sceneExact", "sceneMatchRatio", "stateIdentical", "hdPackActive",
        "originalRomIntegrity", "elapsedSeconds", "failure", "attempts",
    ]
    result = {k: row.get(k) for k in keep if k in row}
    if result.get("attempts"):
        result["attempts"] = [
            {k: a.get(k) for k in ("attempt", "targetFrame", "pauseFrame", "failure",
                                   "status", "liveActiveFrames", "assetKind",
                                   "sharedAssetCount", "markerPixelsInside",
                                   "markerPixelsOutside")}
            for a in result["attempts"]
        ]
    return result


def unique_by_hash(rows):
    seen = set()
    result = []
    for row in rows:
        h = row.get("sha256")
        if h and h not in seen:
            seen.add(h)
            result.append(row)
    return result


def write_csv(path, rows, fields):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--work", default=r"D:\MesenHighWork\validation-v06")
    ap.add_argument("--docs", default=r"D:\RetroArcDo_HDStudio\validation\v0.6")
    ap.add_argument("--release-binary", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--release-studio", default=r"D:\MesenHigh\bin\win-x64\Release\RetroArcDoHDStudio.exe")
    ap.add_argument("--release-zip", default=r"D:\MesenHighWork\dist\RetroArcDoHDStudio-v0.6-win-x64.zip")
    args = ap.parse_args()
    os.makedirs(args.docs, exist_ok=True)

    inv = load(os.path.join(args.work, "inventory", "library-inventory.json"), {}) or {}
    summary = inv.get("summary", {})
    dump(os.path.join(args.docs, "library-inventory.json"), inv)
    shutil.copy2(os.path.join(args.work, "inventory", "dataset-splits.json"),
                 os.path.join(args.docs, "dataset-splits.json"))
    shutil.copy2(os.path.join(args.work, "inventory", "library-index-v06.csv"),
                 os.path.join(args.docs, "library-index-v06.csv"))

    smoke_root = os.path.join(args.work, "smoke-300")
    smoke = load(os.path.join(smoke_root, "smoke-results.json"), []) or []
    smoke_summary = load(os.path.join(smoke_root, "smoke-summary.json"), {}) or {}
    dump(os.path.join(args.docs, "smoke-results.json"), smoke)
    if os.path.exists(os.path.join(smoke_root, "smoke-matrix.csv")):
        shutil.copy2(os.path.join(smoke_root, "smoke-matrix.csv"),
                     os.path.join(args.docs, "smoke-matrix.csv"))
    dump(os.path.join(args.docs, "smoke-summary.json"), smoke_summary)
    holdout = [r for r in smoke if r.get("split") == "holdout"]
    dump(os.path.join(args.docs, "holdout-results.json"), {"rows": holdout,
                                                          "captured": sum(1 for r in holdout if r.get("captureCompleted") == "passed"),
                                                          "total": len(holdout)})

    ext1800 = load(os.path.join(args.work, "extend-1800", "smoke-results.json"), []) or []
    ext6000 = load(os.path.join(args.work, "extend-6000", "smoke-results.json"), []) or []
    dump(os.path.join(args.docs, "smoke-extensions.json"), {"1800": ext1800, "6000": ext6000})

    deep_root = os.path.join(args.work, "deep-1800")
    deep = load(os.path.join(args.work, "deep-combined.json"), []) or []
    if not deep:
        deep = load(os.path.join(deep_root, "smoke-results.json"), []) or []
    deep_summary = load(os.path.join(deep_root, "smoke-summary.json"), {}) or {}
    deep_expansion = load(os.path.join(args.work, "deep-expansion", "smoke-results.json"), []) or []
    if deep_expansion:
        deep_summary = dict(deep_summary)
        deep_summary["expansionRows"] = len(deep_expansion)
        deep_summary["combinedRows"] = len(deep)
    if deep:
        dump(os.path.join(args.docs, "deep-results.json"), deep)
        write_csv(os.path.join(args.docs, "deep-matrix.csv"), deep,
                  ["romName", "baseTitle", "mapper", "chrType", "framesRequested",
                   "framesProcessed", "captureCompleted", "gameplayReached",
                   "observedPatterns", "reconstructedCompositions", "backgroundCompositions",
                   "rawPatterns", "originalRomIntegrity", "processingSeconds"])
    deep6000 = load(os.path.join(args.work, "deep-6000", "smoke-results.json"), []) or []
    if deep6000:
        dump(os.path.join(args.docs, "deep-6000-results.json"), deep6000)
    release_smoke = load(os.path.join(args.work, "release-smoke", "smoke-results.json"), []) or []
    release_smoke_summary = load(os.path.join(args.work, "release-smoke", "smoke-summary.json"), {}) or {}
    if release_smoke:
        dump(os.path.join(args.docs, "release-smoke-results.json"), release_smoke)
        dump(os.path.join(args.docs, "release-smoke-summary.json"), release_smoke_summary)

    replacement_rows = []
    for pattern in ("replacements-priority", "replacements-115", "replacements-background",
                    "replacements-homebrew", "holdout-replacement", "replacement-recovery2",
                    "release-priority"):
        root = os.path.join(args.work, pattern)
        rows = (load(os.path.join(root, "replacement-results-consolidated.json"), [])
                or load(os.path.join(root, "replacement-results.json"), []) or [])
        replacement_rows.extend(rows)
    compact = [compact_result(r) for r in sorted(replacement_rows, key=lambda r: (r.get("rom") or "").lower())]
    dump(os.path.join(args.docs, "replacement-results.json"), compact)
    write_csv(os.path.join(args.docs, "replacement-matrix.csv"), compact,
              ["rom", "sha256", "status", "mapper", "chrType", "split", "targetMode",
               "pauseFrame", "liveActiveFrames", "assetId", "assetKind", "assetWidth",
               "assetHeight", "assetTiles", "sharedAssetCount", "markerPixelsInside",
               "markerPixelsOutside", "changedPixelsInside", "changedPixelsOutside",
               "sceneExact", "sceneMatchRatio", "stateIdentical", "hdPackActive",
               "originalRomIntegrity", "elapsedSeconds", "failure"])
    bg_list_path = os.path.join(args.work, "background-30.txt")
    bg_hashes = set()
    if os.path.exists(bg_list_path):
        bg_hashes = {line.strip() for line in open(bg_list_path, encoding="utf-8") if line.strip()}
    background = load(os.path.join(args.work, "replacements-background",
                                   "replacement-results-consolidated.json"), []) or []
    if not background:
        background = [r for r in compact if r.get("sha256") in bg_hashes]
    dump(os.path.join(args.docs, "background-results.json"), {
        "attempted": len(background),
        "verified": sum(1 for r in background if str(r.get("status")).startswith("verified")),
        "rows": background,
    })
    homebrew_names = ("croom", "cybercoaster", "files__", "irritat", "rhde", "thwaite")
    homebrew_repl = unique_by_hash([r for r in compact if any(n in (r.get("rom") or "").lower() for n in homebrew_names)])
    homebrew_smoke = unique_by_hash([r for r in smoke if any(n in (r.get("romName") or "").lower() for n in homebrew_names)])
    holdout_repl = unique_by_hash([r for r in compact if r.get("split") == "holdout"])
    dump(os.path.join(args.docs, "homebrew-results.json"), {
        "smoke": homebrew_smoke, "replacement": homebrew_repl,
        "smokeCaptured": sum(1 for r in homebrew_smoke if r.get("captureCompleted") == "passed"),
        "replacementVerified": sum(1 for r in homebrew_repl if str(r.get("status")).startswith("verified")),
        "candidates": len(homebrew_smoke),
    })
    dump(os.path.join(args.docs, "holdout-replacement-results.json"), {
        "attempted": len(holdout_repl),
        "verified": sum(1 for r in holdout_repl if str(r.get("status")).startswith("verified")),
        "rows": holdout_repl,
    })

    # Animation evidence from the deep rows. Observed tracks are not runtime-verified sequences.
    anim_rows = [r for r in deep if (r.get("animationRecords") or 0) > 0]
    dump(os.path.join(args.docs, "animation-results.json"), {
        "scope": "Tier-2 deep captures",
        "gamesWithObservedAnimationRecords": len(anim_rows),
        "gamesCaptured": len(deep),
        "observedAnimationRecords": sum(r.get("animationRecords") or 0 for r in deep),
        "runtimeVerifiedSequences": 0,
        "status": "observed only; no independent runtime sequence verification in this data set",
        "rows": anim_rows,
    })

    perf = load(os.path.join(args.work, "performance", "sampling-modes.json"), {}) or {}
    perf["smokeThroughput"] = {
        "uniqueRoms": smoke_summary.get("uniqueRomsAttempted"),
        "frames": smoke_summary.get("frames"),
        "workers": smoke_summary.get("workers"),
        "totalProcessingSeconds": smoke_summary.get("totalProcessingSeconds"),
        "wallSeconds": smoke_summary.get("wallSecondsThisRun"),
        "romsPerHour": round((smoke_summary.get("uniqueRomsAttempted") or 0) /
                             max(0.0001, (smoke_summary.get("wallSecondsThisRun") or 1) / 3600), 1),
    }
    dump(os.path.join(args.docs, "performance-results.json"), perf)
    regression = load(os.path.join(args.work, "regression-results.json"), {}) or {}
    dump(os.path.join(args.docs, "regression-results.json"), regression)

    release = {"designation": "v0.6 Alpha - Large-Scale Remastering Validation and Generalized Engine Optimization",
               "generatedAtUtc": __import__("datetime").datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
               "files": {}}
    for key, path in (("mesen", args.release_binary), ("studio", args.release_studio),
                      ("portableZip", args.release_zip)):
        if path and os.path.exists(path):
            release["files"][key] = {"path": path, "bytes": os.path.getsize(path), "sha256": sha(path)}
    dump(os.path.join(args.docs, "release-manifest.json"), release)

    # Consolidated counts used by the report generator.
    status_counts = {}
    for r in compact:
        status_counts[r.get("status")] = status_counts.get(r.get("status"), 0) + 1
    unique_verified = {}
    unique_attempted = set()
    for row in compact:
        if row.get("sha256"):
            unique_attempted.add(row["sha256"])
            if str(row.get("status", "")).startswith("verified"):
                unique_verified[row["sha256"]] = row
    metrics = {
        "library": summary,
        "replacementUniqueAttempted": len(unique_attempted),
        "replacementUniqueVerified": len(unique_verified),
        "replacementUniqueSpriteVerified": sum(1 for r in unique_verified.values() if r.get("assetKind") != "pickedBackground"),
        "replacementUniqueBackgroundVerified": sum(1 for r in unique_verified.values() if r.get("assetKind") == "pickedBackground"),
        "smoke": smoke_summary,
        "deep": deep_summary,
        "deepRows": len(deep),
        "deepGameplay": sum(1 for r in deep if r.get("gameplayReached") == "passed"),
        "extension1800Rows": len(ext1800),
        "extension6000Rows": len(ext6000),
        "extension1800Capture": sum(1 for r in ext1800 if r.get("captureCompleted") == "passed"),
        "extension6000Capture": sum(1 for r in ext6000 if r.get("captureCompleted") == "passed"),
        "replacementAttempted": len(compact),
        "replacementStatusCounts": status_counts,
        "replacementVerified": sum(1 for r in compact if str(r.get("status")).startswith("verified")),
        "replacementSceneIndependent": sum(1 for r in compact if "scene-independent" in str(r.get("status"))),
        "replacementShared": sum(1 for r in compact if str(r.get("status")) in ("verified-shared", "verified-background-shared")),
        "replacementIndependent": sum(1 for r in compact if str(r.get("status")) in ("verified-independent", "verified-background-independent")),
        "backgroundAttempted": len(bg_hashes) if bg_hashes else len(background),
        "backgroundVerified": sum(1 for r in background if str(r.get("status")).startswith("verified")),
        "holdoutCaptured": sum(1 for r in holdout if r.get("captureCompleted") == "passed"),
        "holdoutTotal": len(holdout),
        "holdoutReplacementAttempted": len(holdout_repl),
        "holdoutReplacementVerified": sum(1 for r in holdout_repl if str(r.get("status")).startswith("verified")),
        "homebrewCandidates": len(homebrew_smoke),
        "homebrewVerified": sum(1 for r in homebrew_repl if str(r.get("status")).startswith("verified")),
        "releaseSmoke": release_smoke_summary,
        "performance": perf,
        "release": release,
    }
    dump(os.path.join(args.docs, "metrics.json"), metrics)
    print(json.dumps(metrics, indent=2)[:6000])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
