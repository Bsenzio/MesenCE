"""Consolidate v0.7 validation evidence into machine-readable metrics."""
import argparse
import csv
import json
import os
from collections import Counter, defaultdict


def load(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def scan_animation_catalogs(roots):
    games = 0
    sequences = 0
    frames = 0
    seen = set()
    for root in roots:
        if not os.path.isdir(root):
            continue
        for current, _, files in os.walk(root):
            if "assets.json" not in files or os.path.basename(current) != "assets":
                continue
            path = os.path.join(current, "assets.json")
            if path in seen:
                continue
            seen.add(path)
            doc = load(path, {}) or {}
            assets = doc.get("assets") or []
            seq = [a for a in assets if a.get("kind") == "animationSequence"]
            if seq:
                games += 1
                sequences += len(seq)
                frames += sum(1 for a in assets if a.get("kind") == "animationFrame")
    return {"gamesWithObservedSequences": games, "observedSequences": sequences,
            "observedAnimationFrames": frames, "catalogsScanned": len(seen)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=r"D:\MesenHighWork\validation-v07")
    ap.add_argument("--out", default=r"D:\RetroArcDo_HDStudio\validation\v0.7\metrics.json")
    ap.add_argument("--batch", default=r"batch300d\replacement-results.json")
    ap.add_argument("--priority", default=r"batch10\replacement-results.json")
    ap.add_argument("--background", default=r"background-v07\replacement-results.json")
    ap.add_argument("--animation", default=r"animation-v07\animation-results.json")
    ap.add_argument("--holdout", default=r"holdout-v07\replacement-results.json")
    ap.add_argument("--homebrew", default=r"homebrew-v07\replacement-results.json")
    ap.add_argument("--regression", default=r"regression-results.json")
    ap.add_argument("--performance", default=r"performance-v07.json")
    ap.add_argument("--inventory", default=r"inventory\library-inventory.json")
    args = ap.parse_args()

    inventory = load(os.path.join(args.root, args.inventory), {}) or {}
    batch_rows = load(os.path.join(args.root, args.batch), []) or []
    priority_rows = load(os.path.join(args.root, args.priority), []) or []
    background_rows = load(os.path.join(args.root, args.background), []) or []
    animation = load(os.path.join(args.root, args.animation), {}) or {}
    holdout_rows = load(os.path.join(args.root, args.holdout), []) or []
    homebrew_rows = load(os.path.join(args.root, args.homebrew), []) or []
    regression = load(os.path.join(args.root, args.regression), {}) or {}
    performance = load(os.path.join(args.root, args.performance), {}) or {}

    all_rows = batch_rows + priority_rows + background_rows + holdout_rows + homebrew_rows
    #Deduplicate rows for the unique-game counts; row-level metrics keep duplicates
    #when the same game was part of a separate evidence set.
    verified_rows = [r for r in all_rows if str(r.get("status", "")).startswith("verified")]
    verified_games = {r.get("sha256") or r.get("rom") for r in verified_rows if r.get("rom")}
    attempted_games = {r.get("sha256") or r.get("rom") for r in all_rows if r.get("rom")}
    sha_to_base = {}
    for rom in inventory.get("roms", []):
        sha = (rom.get("sha256") or "").lower()
        if sha:
            sha_to_base[sha] = rom.get("baseTitle") or rom.get("name") or sha
    verified_base_titles = {
        sha_to_base.get((r.get("sha256") or "").lower(), r.get("rom"))
        for r in verified_rows
    }

    status_counts = Counter(r.get("status") for r in all_rows)
    verified_statuses = [s for s in status_counts if s.startswith("verified")]
    context_independent = [r for r in verified_rows if "context-independent" in str(r.get("status"))]
    independent = [r for r in verified_rows if "independent" in str(r.get("status")) and "shared" not in str(r.get("status"))]
    scene_isolated = [r for r in verified_rows if "scene-isolated" in str(r.get("status"))]
    multi_scene = [r for r in verified_rows if (r.get("verificationScenes") or 0) >= 2]
    shared_rows = [r for r in verified_rows if "shared" in str(r.get("status")) or (r.get("markerPixelsOutside") or 0) > 0]
    sprite_rows = [r for r in verified_rows if "background" not in str(r.get("status"))]
    background_verified = [r for r in verified_rows if "background" in str(r.get("status"))]
    assets_attempted = sum(1 for r in all_rows if r.get("assetId"))
    assets_verified = len(verified_rows)
    rows_with_integrity = [r for r in all_rows if r.get("originalRomIntegrity")]
    integrity_passed = sum(1 for r in rows_with_integrity if r.get("originalRomIntegrity") == "passed")
    rows_with_state = [r for r in all_rows if r.get("stateIdentical") is not None]
    state_passed = sum(1 for r in rows_with_state if r.get("stateIdentical"))
    tile_occurrences = sum((r.get("assetTiles") or 0) for r in verified_rows)
    verified_intended = sum((r.get("contextIntendedMatched") or 0) for r in all_rows)
    verified_intended_total = sum((r.get("contextIntendedCells") or 0) for r in all_rows)
    verified_unrelated = sum((r.get("contextUnrelatedMatched") or 0) for r in all_rows)

    def rule_count(fragment):
        return sum(1 for r in all_rows if fragment in str(r.get("contextRule") or ""))
    rule_usage = {
        "rowsUsingNearbyRules": sum(1 for r in all_rows if any(t in str(r.get("contextRule") or "") for t in ("spriteNearby", "tileNearby"))),
        "rowsUsingMirrorOrPriorityRules": sum(1 for r in all_rows if any(t in str(r.get("contextRule") or "") for t in ("hmirror", "vmirror", "bgpriority"))),
        "rowsUsingPositionRules": rule_count("positionCheck"),
        "rowsUsingNoCondition": sum(1 for r in all_rows if str(r.get("contextRule") or "").startswith("unconditional")),
    }

    animation_summary = (animation.get("summary") or {}) if isinstance(animation, dict) else {}
    animation_rows = animation.get("rows", []) if isinstance(animation, dict) else []

    metrics = {
        "schemaVersion": "1.0.0",
        "algorithmVersion": "7.0",
        "library": inventory.get("summary", {}),
        "replacement": {
            "rowsAttempted": len(all_rows),
            "rowsVerified": len(verified_rows),
            "uniqueGamesAttempted": len(attempted_games),
            "uniqueGamesVerified": len(verified_games),
            "uniqueNormalizedBaseTitlesVerified": len(verified_base_titles),
            "uniqueSpriteGames": len({r.get("sha256") or r.get("rom") for r in sprite_rows}),
            "uniqueBackgroundGames": len({r.get("sha256") or r.get("rom") for r in background_verified}),
            "statusCounts": dict(status_counts),
            "verifiedStatuses": verified_statuses,
            "strictOrContextIndependentRows": len(independent),
            "contextIndependentRows": len(context_independent),
            "uniqueGamesContextIndependent": len({r.get("sha256") or r.get("rom") for r in context_independent}),
            "sceneIsolatedRows": len(scene_isolated),
            "sharedRows": len(shared_rows),
            "uniqueGamesSharedOrScene": len({r.get("sha256") or r.get("rom") for r in (shared_rows + scene_isolated)}),
            "multiSceneRows": len(multi_scene),
            "spriteRows": len(sprite_rows),
            "backgroundRows": len(background_verified),
            "assetRowsAttempted": assets_attempted,
            "assetRowsVerified": assets_verified,
            "tileOccurrencesInVerifiedAssets": tile_occurrences,
            "intendedCellsMatched": verified_intended,
            "intendedCellsTotal": verified_intended_total,
            "unrelatedCellsMatched": verified_unrelated,
            "romIntegrityRows": len(rows_with_integrity),
            "romIntegrityPassed": integrity_passed,
            "stateRows": len(rows_with_state),
            "stateIdenticalRows": state_passed,
            "ruleUsage": rule_usage,
        },
        "priorityCases": [
            {"rom": r.get("rom"), "status": r.get("status"), "pauseFrame": r.get("pauseFrame"),
             "verificationScenes": r.get("verificationScenes"),
             "markerInside": r.get("markerPixelsInside"), "markerOutside": r.get("markerPixelsOutside"),
             "contextStatus": r.get("contextStatus"), "contextRule": r.get("contextRule")}
            for r in priority_rows
        ],
        "background": {
            "rowsAttempted": len(background_rows),
            "rowsVerified": len([r for r in background_rows if str(r.get("status", "")).startswith("verified")]),
            "uniqueGamesVerified": len({r.get("sha256") or r.get("rom") for r in background_rows if str(r.get("status", "")).startswith("verified")}),
        },
        "animation": {
            "testedSequences": animation_summary.get("testedSequences", 0),
            "verifiedAny": animation_summary.get("verifiedAny", 0),
            "verifiedMultiFrame": animation_summary.get("verifiedMultiFrame", 0),
            "rows": len(animation_rows),
            "statusCounts": animation_summary.get("statusCounts", {}),
            "observedCatalogSummary": scan_animation_catalogs([
                os.path.join(args.root, "batch300d"),
                os.path.join(args.root, "homebrew-v07"),
                os.path.dirname(os.path.join(args.root, args.priority))
            ]),
        },
        "holdout": {
            "rowsAttempted": len(holdout_rows),
            "rowsVerified": len([r for r in holdout_rows if str(r.get("status", "")).startswith("verified")]),
            "uniqueGamesVerified": len({r.get("sha256") or r.get("rom") for r in holdout_rows if str(r.get("status", "")).startswith("verified")}),
        },
        "homebrew": {
            "rowsAttempted": len(homebrew_rows),
            "rowsVerified": len([r for r in homebrew_rows if str(r.get("status", "")).startswith("verified")]),
            "uniqueTitles": len({r.get("rom") for r in homebrew_rows}),
        },
        "regression": regression,
        "performance": performance,
        "provenance": {
            "batch": os.path.join(args.root, args.batch),
            "priority": os.path.join(args.root, args.priority),
            "background": os.path.join(args.root, args.background),
            "animation": os.path.join(args.root, args.animation),
            "holdout": os.path.join(args.root, args.holdout),
            "homebrew": os.path.join(args.root, args.homebrew),
        },
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out + ".tmp", "w", encoding="utf-8") as f:
        json.dump(metrics, f, indent=1)
    os.replace(args.out + ".tmp", args.out)
    #Also emit a flat CSV for publication.
    csv_path = os.path.splitext(args.out)[0] + "-summary.csv"
    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["section", "metric", "value"])
        def flat(prefix, obj):
            if isinstance(obj, dict):
                for k, v in obj.items():
                    flat(prefix + "." + k if prefix else k, v)
            else:
                w.writerow([prefix.split(".")[0], prefix, obj])
        flat("", metrics)
    print(json.dumps(metrics["replacement"], indent=1))
    print("metrics:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
