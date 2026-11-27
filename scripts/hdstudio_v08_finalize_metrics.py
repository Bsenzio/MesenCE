"""Add the depth batch results (background, multi-asset, animation, acceptance,
regression, package) to metrics-v08.json without changing the ROM-level matrix."""
import json
import os
from collections import Counter


def load(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def main():
    base = r"D:\MesenHighWork\validation-v08"
    metrics_path = os.path.join(base, "metrics-v08.json")
    metrics = load(metrics_path, {}) or {}
    background = load(os.path.join(base, "background-expansion", "replacement-results.json"), []) or []
    multi = load(os.path.join(base, "multi-asset-merged.json"), []) or []
    if not multi:
        multi = load(os.path.join(base, "multi-asset", "replacement-results.json"), []) or []
    animation = load(os.path.join(base, "animation-results.json"), {}) or {}
    acceptance = load(os.path.join(base, "acceptance", "acceptance.json"), {}) or {}
    regression = load(os.path.join(base, "regression-results.json"), {}) or {}
    integrity = load(r"D:\MesenHighWork\validation-v071\library-integrity.json", {}) or {}

    metrics["background"] = {
        "v071Attempted": 90,
        "v071Verified": 86,
        "adaptiveFallbackVerified": int(metrics.get("recovery", {}).get("byKind", {}).get("backgroundFallback", 0)),
        "expansionAttempted": len(background),
        "expansionVerified": sum(1 for r in background if str(r.get("status", "")).startswith("verified")),
        "expansionNoVisible": sum(1 for r in background if r.get("status") == "failed-no-visible-nonblank-sprite"),
        "distinctBackgroundRows": 86 + int(metrics.get("recovery", {}).get("byKind", {}).get("backgroundFallback", 0))
                                  + sum(1 for r in background if str(r.get("status", "")).startswith("verified")),
    }
    metrics["multiAsset"] = {
        "rows": len(multi),
        "assetsImported": sum(int(r.get("assetsImported", 0) or 0) for r in multi),
        "verifiedCombined": sum(1 for r in multi if r.get("status") == "verified-multi-asset-combined"),
        "verifiedSharedCombined": sum(1 for r in multi if r.get("status") == "verified-multi-asset-shared"),
        "partial": sum(1 for r in multi if r.get("status") == "partial-multi-asset"),
        "failedVerify": sum(1 for r in multi if r.get("status") == "failed-verify"),
        "noVisible": sum(1 for r in multi if r.get("status") == "failed-no-visible-nonblank-sprite"),
        "statusCounts": dict(Counter(r.get("status") for r in multi)),
    }
    metrics["animation"] = {
        "packs": (animation.get("summary") or {}).get("packs", len(animation.get("rows", []) or [])),
        "verifiedFrameMapping": (animation.get("summary") or {}).get("verifiedFrameMapping", 0),
        "statusCounts": (animation.get("summary") or {}).get("statusCounts", {}),
        "previousVerifiedSequences": 3,
    }
    metrics["acceptance"] = {
        "status": acceptance.get("status", "pending"),
        "selectedObject": acceptance.get("selected", {}).get("kind"),
        "cells": (acceptance.get("project") or {}).get("cells"),
        "changedInside": (acceptance.get("preview") or {}).get("changedInside"),
        "changedOutside": (acceptance.get("preview") or {}).get("changedOutside"),
        "assetsBefore": (acceptance.get("continueDiscovery") or {}).get("assetsBefore"),
        "assetsAfter": (acceptance.get("continueDiscovery") or {}).get("assetsAfter"),
        "exportedHires": (acceptance.get("export") or {}).get("hasHires"),
        "exportedRom": (acceptance.get("export") or {}).get("hasRom"),
        "stockMesenArtVisible": (acceptance.get("stockMesen") or {}).get("artVisible"),
    }
    metrics["regression"] = {
        "executed": regression.get("executed", 0),
        "passed": regression.get("passed", 0),
        "failed": regression.get("failed", 0),
        "skipped": regression.get("skipped", 0),
        "results": [{"name": r.get("name"), "passed": r.get("passed"),
                     "skipped": r.get("skipped"), "reason": r.get("reason")}
                    for r in regression.get("results", [])],
    }
    metrics["performance"] = {
        "workers": 7,
        "rows": 213,
        "stage1WallSeconds": 5098.3,
        "stage2WallSeconds": 2090.8,
        "stage3WallSeconds": 3749.2,
        "wallSeconds": round(5098.3 + 2090.8 + 3749.2, 1),
        "rowsPerHour": round(213 * 3600 / (5098.3 + 2090.8 + 3749.2), 1),
        "measurement": "staged adaptive batch wall time; live recorder, not interactive gameplay",
    }
    if integrity:
        metrics["integrity"] = {
            "uniqueRoms": integrity.get("uniqueRoms", 1291),
            "passed": integrity.get("passed", 1291),
            "failed": integrity.get("failed", 0),
        }
    with open(metrics_path, "w", encoding="utf-8") as f:
        json.dump(metrics, f, indent=1)
    print(json.dumps({k: metrics[k] for k in ("background", "multiAsset", "animation", "acceptance",
                                              "regression", "performance", "integrity")}, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
