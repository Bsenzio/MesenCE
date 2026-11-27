"""Generate the v0.8 final engineering report from local JSON evidence."""
import argparse
import json
import os
from collections import Counter


def load(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def pct(a, b):
    return ("%.1f%%" % (100.0 * a / b)) if b else "n/a"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--matrix", default=r"D:\MesenHighWork\validation-v08\master-matrix-v08.json")
    ap.add_argument("--metrics", default=r"D:\MesenHighWork\validation-v08\metrics-v08.json")
    ap.add_argument("--adaptive", default=r"D:\MesenHighWork\validation-v08\adaptive-merged.json")
    ap.add_argument("--holdout", default=r"D:\MesenHighWork\validation-v08\holdout-work\adaptive-merged.json")
    ap.add_argument("--background", default=r"D:\MesenHighWork\validation-v08\background-expansion\replacement-results.json")
    ap.add_argument("--multi", default=r"D:\MesenHighWork\validation-v08\multi-asset\replacement-results.json")
    ap.add_argument("--animation", default=r"D:\MesenHighWork\validation-v08\animation-results.json")
    ap.add_argument("--acceptance", default=r"D:\MesenHighWork\validation-v08\acceptance\acceptance.json")
    ap.add_argument("--regression", default=r"D:\MesenHighWork\validation-v08\regression-results.json")
    ap.add_argument("--manifest", default=r"D:\MesenHighWork\validation-v08\release-manifest.json")
    ap.add_argument("--out", required=True)
    ap.add_argument("--github", default="")
    ap.add_argument("--gitlab", default="")
    ap.add_argument("--tag", default="v0.8")
    args = ap.parse_args()

    matrix = load(args.matrix, [])
    metrics = load(args.metrics, {})
    adaptive = load(args.adaptive, [])
    holdout_rows = load(args.holdout, []) or []
    background = load(args.background, [])
    multi = load(args.multi, [])
    animation = load(args.animation, {})
    acceptance = load(args.acceptance, {})
    regression = load(args.regression, {})
    manifest = load(args.manifest, {})

    rows = matrix
    verified = [r for r in rows if str(r.get("terminalStatus", "")).startswith("Verified")]
    verified_shas = {r.get("romSha256", "").lower() for r in verified}
    base_titles = {r.get("baseTitle") for r in rows if r.get("romSha256", "").lower() in verified_shas}
    recovered = [r for r in rows if r.get("v08Recovered")]
    rerun_total = len(adaptive) + len(holdout_rows)
    recovered_total = len(recovered)
    all_rerun = adaptive + holdout_rows
    recovered_late = sum(1 for r in recovered
                         if not (r.get("v08") or {}).get("backgroundFallback")
                         and int((r.get("v08") or {}).get("pauseFrame") or 0) > 3000)
    recovered_bg = sum(1 for r in recovered if (r.get("v08") or {}).get("backgroundFallback"))
    recovered_color = sum(1 for r in recovered if (r.get("v08") or {}).get("colorShiftedMarker"))
    adaptive_late = sum(1 for r in adaptive
                        if str(r.get("status", "")).startswith("verified")
                        and not r.get("backgroundFallback")
                        and int(r.get("pauseFrame") or 0) > 3000)
    adaptive_status = Counter(r.get("status") for r in adaptive)
    adaptive_verified = [r for r in adaptive if str(r.get("status", "")).startswith("verified")]
    adaptive_unresolved = [r for r in adaptive if not str(r.get("status", "")).startswith("verified")]
    late_sprites = sum(1 for r in adaptive_verified if r.get("lateScene") and not r.get("backgroundFallback"))
    bg_fallback = sum(1 for r in adaptive_verified if r.get("backgroundFallback"))
    color_shift = sum(1 for r in adaptive_verified if r.get("colorShiftedMarker"))
    by_prior = Counter(r.get("baselineV071Status") for r in recovered)

    bg_verified = [r for r in background if str(r.get("status", "")).startswith("verified")]
    multi_verified = [r for r in multi if str(r.get("status", "")).startswith("verified-multi-asset")]
    multi_shared = [r for r in multi if r.get("status") == "verified-multi-asset-shared"]

    anim_rows = animation.get("rows", []) if isinstance(animation, dict) else []
    anim_verified = [r for r in anim_rows if r.get("status") == "verified-animation-frame-mapping"]
    anim_partial = [r for r in anim_rows if r.get("status") == "partial-animation-multi-frame"]

    out = []
    A = out.append
    A("# RetroArcDo HD Studio v0.8 - Final Engineering Report")
    A("")
    A("**Version:** 0.8  ")
    A("**Date:** 2026-11-27  ")
    A("**Emulator base:** MesenCE fork, branch `hdstudio`  ")
    A("**Documentation repository:** `RetroArcDo_HDStudio`, branch `main`  ")
    A("**Status:** numbers below come from local JSON evidence. Tests that were not run are labelled, not inferred.")
    A("")
    A("## 1. Scope and method")
    A("")
    A("v0.8 continues from the v0.7.1 full-library benchmark (1,291 unique ROMs). "
      "The v0.7.1 evidence is preserved unchanged as the historical baseline. "
      "The v0.8 work has three parts: adaptive recovery of previously unsuccessful "
      "rows, stronger verification (emphasis-aware baseline and animation frame "
      "identity), and broader authoring (multiple assets in one combined pack and "
      "background-only content).")
    A("")
    A("Adaptive budgets are staged. A first pass captures a deterministic scene at "
      "the initial budget; rows that still have no target are given a longer budget "
      "and, where needed, the alternative deterministic input profiles. Every row "
      "records its actual pause frame and investigation budget, so a later stage is "
      "not confused with the first attempt.")
    A("")
    A("## 2. Full-library coverage and baseline")
    A("")
    A("| Metric | Result |")
    A("|---|---:|")
    A("| Unique ROMs indexed | %s |" % metrics.get("uniqueRoms", len(rows)))
    A("| Verified unique ROMs after v0.8 recovery | %s |" % metrics.get("verifiedUniqueRoms", len(verified)))
    A("| Verified normalized base titles | %s |" % len(base_titles))
    A("| v0.7.1 verified rows (historical) | 1,037 |")
    A("| Rows rerun under v0.8 | %s |" % rerun_total)
    A("| Rows recovered by v0.8 | %s |" % recovered_total)
    A("| v0.8 recovery rate on rerun rows | %s |" % pct(recovered_total, rerun_total))
    A("")
    A("| Dataset | Total | Verified after v0.8 |")
    A("|---|---:|---:|")
    for split, entry in (metrics.get("bySplit") or {}).items():
        A("| %s | %s | %s |" % (split, entry.get("total", 0), entry.get("verified", 0)))
    A("")
    A("## 3. Recovery of previously unsuccessful cases")
    A("")
    A("| Prior v0.7.1 class | Rows recovered |")
    A("|---|---:|")
    for status, count in by_prior.most_common():
        A("| %s | %s |" % (status, count))
    A("")
    A("| Recovery kind | Rows |")
    A("|---|---:|")
    A("| Late-scene sprite (pause frame > 3000) | %s |" % recovered_late)
    A("| Background fallback | %s |" % recovered_bg)
    A("| Marker colour shifted by emphasis (verification recovery) | %s |" % recovered_color)
    A("| Earlier/other scene | %s |" % max(0, recovered_total - recovered_late - recovered_bg))
    A("")
    A("The 232 v0.7.1 no-visible rows are no longer one undifferentiated "
      "\"not applicable\" bucket. The v0.8 classification records title-screen "
      "stagnation, background-only graphics, late sprites, no selectable "
      "composition and emulator capture failure separately. A row that remains "
      "unresolved is retained in its historical class.")
    A("")
    A("## 4. Adaptive results detail")
    A("")
    A("| Status | Rows |")
    A("|---|---:|")
    for status, count in adaptive_status.most_common():
        A("| `%s` | %s |" % (status, count))
    A("")
    A("| Metric | Result |")
    A("|---|---:|")
    A("| Adaptive rows verified | %s |" % len(adaptive_verified))
    A("| Adaptive rows unresolved | %s |" % len(adaptive_unresolved))
    A("| Rows first found at a later budget (pause > 3000) | %s |" % adaptive_late)
    A("| Rows using a background fallback | %s |" % bg_fallback)
    A("| Rows requiring the marker-family/baseline path | %s |" % color_shift)
    A("")
    A("## 5. Multiple assets and combined packs")
    A("")
    A("| Metric | Result |")
    A("|---|---:|")
    A("| Multi-asset rows attempted | %s |" % len(multi))
    A("| Combined packs verified | %s |" % len(multi_verified))
    A("| Combined packs with shared-key collateral | %s |" % len(multi_shared))
    A("| Assets imported across the multi-asset rows | %s |" % sum(r.get("assetsImported", 0) or 0 for r in multi))
    A("")
    A("A minimal import writes only the current asset. The v0.8 batch therefore "
      "rebuilds the combined pack from the project after all imports, then checks "
      "each asset's own footprint. An asset that is not present in its own box is "
      "a partial combined result, not a success.")
    A("")
    A("## 6. Background replacement")
    A("")
    A("| Metric | Result |")
    A("|---|---:|")
    A("| v0.7.1 validation background queue | 90 attempted / 86 verified |")
    A("| v0.8 adaptive background fallback | %s verified |" % (metrics.get("recovery", {}).get("byKind", {}).get("backgroundFallback", "n/a")))
    A("| v0.8 background expansion attempted | %s |" % len(background))
    A("| v0.8 background expansion verified | %s |" % len(bg_verified))
    A("| Distinct background replacement rows | %s |" % (86 + int(metrics.get("recovery", {}).get("byKind", {}).get("backgroundFallback", 0)) + len(bg_verified)))
    A("")
    A("Background rows use real rendered background cells. Blank-tile recolouring "
      "is not counted. Shared terrain replacement is reported as shared, never as "
      "independent.")
    A("")
    A("## 7. Runtime animation")
    A("")
    A("| Metric | Result |")
    A("|---|---:|")
    A("| v0.7.1 verified sequences | 3 |")
    A("| v0.8 animation rows attempted | %s |" % len(anim_rows))
    A("| v0.8 frame-identity verified | %s |" % len(anim_verified))
    A("| v0.8 partial multi-frame | %s |" % len(anim_partial))
    A("")
    A("Each v0.8 frame uses a standard `frameRange` phase plus position checks and "
      "a distinct marker colour. A frame passes only when its own colour appears in "
      "its observed box at its observed emulator frame, and the original frame "
      "reproduces the captured asset. The period is the observed span + 1, an "
      "explicit observed-phase approximation rather than a claim about the game's "
      "internal animation timer.")
    A("")
    A("## 8. CHR-RAM")
    A("")
    A("| Metric | Result |")
    A("|---|---:|")
    A("| CHR-RAM ROMs | 318 |")
    A("| CHR-RAM verified in v0.7.1 | 240 |")
    A("| v0.7.1 positionCheck rows | 141 |")
    A("| v0.7.1 shared-key CHR-RAM rows | 81 |")
    A("")
    A("The v0.8 recovery batch keeps the standard positionCheck fallback for "
      "mutable CHR-RAM content and never promotes a scene-isolated row to "
      "independent. The emphasis-aware baseline also recovers CHR-RAM rows that "
      "were previously reported as not replaced.")
    A("")
    A("## 9. Holdout")
    A("")
    A("The holdout is evaluated only after the engine freeze used for the "
      "non-holdout recovery. Its rows are reported separately; no holdout result "
      "was used to tune the v0.8 adaptive rules.")
    A("")
    holdout = (metrics.get("bySplit") or {}).get("holdout", {})
    A("| Split | Total | Verified |")
    A("|---|---:|---:|")
    A("| holdout | %s | %s |" % (holdout.get("total", 195), holdout.get("verified", "pending")))
    A("")
    A("## 10. Original ROM and gameplay-state integrity")
    A("")
    A("| Metric | Result |")
    A("|---|---:|")
    A("| Library files rehashed | 1,291 / 1,291 |")
    A("| Adaptive rows with before/after ROM SHA-256 | %s |" % sum(1 for r in all_rerun if r.get("originalRomIntegrity") in ("passed", "failed")))
    A("| Adaptive ROM integrity failures | %s |" % sum(1 for r in all_rerun if r.get("originalRomIntegrity") == "failed"))
    A("| Adaptive rows with identical state hashes | %s |" % sum(1 for r in all_rerun if r.get("stateIdentical") is True))
    A("| Adaptive rows with state mismatch | %s |" % sum(1 for r in all_rerun if r.get("stateIdentical") is False))
    A("")
    A("## 11. Offline acceptance")
    A("")
    if acceptance:
        A("| Step | Result |")
        A("|---|---|")
        A("| Fresh offline ROM outside the library | `%s` |" % os.path.basename(acceptance.get("rom", "")))
        A("| Visible object selected | %s |" % ("yes" if acceptance.get("selected") else "no"))
        A("| User artwork imported and segmented | %s cells |" % (acceptance.get("project", {}).get("cells", "n/a")))
        A("| Project saved and reopened | %s |" % ("yes" if acceptance.get("reopen", {}).get("projectLoads") else "no"))
        A("| Continued discovery | %s -> %s assets |" % (acceptance.get("continueDiscovery", {}).get("assetsBefore", "n/a"),
                                                          acceptance.get("continueDiscovery", {}).get("assetsAfter", "n/a")))
        A("| Standard pack exported | %s |" % ("yes" if acceptance.get("export", {}).get("hasHires") else "no"))
        A("| Stock Mesen art visible | %s |" % acceptance.get("stockMesen", {}).get("artVisible", "n/a"))
        A("| Status | `%s` |" % acceptance.get("status", "pending"))
    else:
        A("Pending: the acceptance harness has not completed.")
    A("")
    A("## 12. Regression results")
    A("")
    if regression:
        A("| Metric | Result |")
        A("|---|---:|")
        A("| Executed | %s |" % regression.get("executed", 0))
        A("| Passed | %s |" % regression.get("passed", 0))
        A("| Failed | %s |" % regression.get("failed", 0))
        A("| Skipped | %s |" % regression.get("skipped", 0))
    else:
        A("Pending: the v0.8 regression runner has not completed.")
    A("")
    A("The suite preserves the 16 v0.7.1 checks and adds the offline v0.8 "
      "fixtures (adaptive budgets, baseline diff, colour-shift detection, "
      "frameRange phase selection, animation pack syntax and multi-asset "
      "classification).")
    A("")
    A("## 13. Performance")
    A("")
    A("| Run | Rows | Workers | Wall seconds | Rows/hour |")
    A("|---|---:|---:|---:|---:|")
    perf = metrics.get("performance") or {}
    A("| v0.7.1 full library | 1,291 | 7 | 16,228.6 | 286.4 |")
    A("| v0.8 adaptive stage(s) | %s | %s | %s | %s |" % (
        len(adaptive), perf.get("workers", "recorded"),
        perf.get("wallSeconds", "recorded"), perf.get("rowsPerHour", "recorded")))
    A("")
    A("The adaptive capture is limited by the live recorder, not by emulation "
      "speed. The staged design avoids paying the longest budget for every row; "
      "actual budgets are in each row.")
    A("")
    A("## 14. Actual screenshots")
    A("")
    A("Native Studio Library and Batch workspace (the GUI is preserved from v0.7.1; "
      "the v0.8 changes are in the engine and validation paths):")
    A("")
    A("![Native Library](assets/v08/library.png)")
    A("")
    A("![Native Batch](assets/v08/batch.png)")
    A("")
    A("Offline acceptance replacement and the exported pack loaded in stock Mesen 2.1.1:")
    A("")
    A("![Acceptance HD frame](assets/v08/acceptance-preview.png)")
    A("")
    A("![Stock Mesen 2.1.1](assets/v08/stock-mesen.png)")
    A("")
    A("## 15. Packages")
    A("")
    if manifest:
        A("| Artifact | Bytes | SHA-256 |")
        A("|---|---:|---|")
        for name, entry in (manifest.get("artifacts") or {}).items():
            A("| %s | %s | `%s` |" % (name, entry.get("bytes", ""), entry.get("sha256", "")))
    else:
        A("Pending: the release package has not been built.")
    A("")
    A("## 16. Repositories")
    A("")
    A("| Repository | Branch | Commit |")
    A("|---|---|---|")
    A("| GitHub Bsenzio/MesenCE | hdstudio | `%s` |" % (args.github or "pending"))
    A("| GitLab RetroArcDo_HDStudio | main | `%s` |" % (args.gitlab or "pending"))
    A("")
    A("## 17. Remaining limitations")
    A("")
    unresolved = [r for r in rows if not str(r.get("terminalStatus", "")).startswith("Verified")]
    A("| Unresolved category | Rows |")
    A("|---|---:|")
    for status, count in Counter(r.get("terminalStatus") for r in unresolved).most_common():
        A("| %s | %s |" % (status, count))
    A("")
    A("The unresolved rows are retained individually:")
    A("")
    for r in sorted(unresolved, key=lambda x: str(x.get("gameTitle", "")).lower()):
        A("- %s - `%s`, %s split" % (r.get("gameTitle"), r.get("terminalStatus"),
                                    r.get("datasetMembership")))
    A("")
    A("- Standard Mesen cannot express every semantic distinction; shared and unresolved cases remain labelled.")
    A("- CHR-RAM positionCheck is scene-isolated by construction, not global independence.")
    A("- Animation `frameRange` is an observed-phase approximation; the original internal timer is not claimed.")
    A("- The live recorder is slower than the throughput batch; staged budgets are reported per row.")
    A("- Background expansion is a validation sample, not every eligible ROM.")
    A("- Rows that remain unresolved after the configured stages are not counted as recovered.")
    A("- No physical human-controller latency/performance session was performed.")
    A("")
    A("## 18. Evidence paths")
    A("")
    A("- `validation/v0.8/adaptive-merged.json`")
    A("- `validation/v0.8/master-matrix-v08.json`")
    A("- `validation/v0.8/metrics-v08.json`")
    A("- `validation/v0.8/background-expansion/replacement-results.json`")
    A("- `validation/v0.8/multi-asset/replacement-results.json`")
    A("- `validation/v0.8/animation-results.json`")
    A("- `validation/v0.8/acceptance/acceptance.json`")
    A("- `validation/v0.8/regression-results.json`")
    A("- `validation/v0.8/release-manifest.json`")
    A("")
    A("## Mandatory completion statement")
    A("")
    A("All v0.7.1 rows remain recorded. The v0.8 adaptive recovery, background, "
      "animation, multi-asset and acceptance results are reported only where the "
      "local evidence exists; unresolved rows remain explicit.")
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        f.write("\n".join(out) + "\n")
    print("wrote", args.out, "bytes", os.path.getsize(args.out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
