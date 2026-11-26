"""Print the v0.7 report replacement tables from metrics.json."""
import json
import argparse

ap = argparse.ArgumentParser()
ap.add_argument("--metrics", default=r"D:\RetroArcDo_HDStudio\validation\v0.7\metrics.json")
args = ap.parse_args()
m = json.load(open(args.metrics, encoding="utf-8"))
r = m["replacement"]
print("### Combined evidence")
print("| Class | Rows | Unique games |")
print("|---|---:|---:|")
for k, label in [
    ("verified-scene-independent-shared-keys-multi-scene", "Scene-independent shared-key, multi-scene"),
    ("verified-scene-independent-shared-keys", "Scene-independent shared-key, single scene"),
    ("verified-context-independent-multi-scene", "Context-independent multi-scene"),
    ("verified-independent-multi-scene", "Strict independent multi-scene"),
    ("verified-scene-isolated", "Scene-isolated"),
    ("verified-shared", "Explicit shared"),
    ("verified-title-or-menu-scene", "Title/menu scene"),
    ("failed-no-visible-nonblank-sprite", "No visible nonblank sprite"),
    ("failed-verify", "Verification failure"),
    ("failed-context-unresolved", "Unresolved context"),
    ("failed-context-rule", "Context rule runtime failure"),
    ("partial-visual-state-mismatch", "Partial visual/state mismatch"),
]:
    count = r["statusCounts"].get(k, 0)
    print(f"| {label} | {count} | ? |")
print()
print("| Metric | Value |")
print("|---|---:|")
for k in ("rowsAttempted", "rowsVerified", "uniqueGamesAttempted", "uniqueGamesVerified",
          "uniqueNormalizedBaseTitlesVerified", "uniqueSpriteGames", "uniqueBackgroundGames",
          "contextIndependentRows", "uniqueGamesContextIndependent", "strictOrContextIndependentRows",
          "sceneIsolatedRows", "sharedRows", "multiSceneRows", "assetRowsVerified",
          "tileOccurrencesInVerifiedAssets", "intendedCellsMatched", "intendedCellsTotal",
          "unrelatedCellsMatched", "romIntegrityRows", "romIntegrityPassed",
          "stateRows", "stateIdenticalRows"):
    print(f"| `{k}` | {r.get(k)} |")
print()
print(json.dumps({"scaleCounts": r.get("scaleCounts"), "ruleUsage": r.get("ruleUsage")}, indent=1))
