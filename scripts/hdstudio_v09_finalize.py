"""Collect the final v0.9 evidence into one machine-readable metrics file.

The full-library batch summary can include cached rows whose per-row JSON was
written by an earlier process, so the catalog and gap-resolution files are the
authoritative source for final metrics. This script reads every completed ROM's
catalog and gap report and reconciles them with the batch rows.
"""
import argparse
import glob
import json
import os
import time


def load(path, fallback=None):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except Exception:
        return fallback


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=r"D:\MesenHighWork\validation-v09")
    ap.add_argument("--static", default=r"D:\MesenHighWork\validation-v09\library-static-final")
    ap.add_argument("--replacement", default=r"D:\MesenHighWork\validation-v09\fullbench-final\fullbench-summary.json")
    ap.add_argument("--background", default=r"D:\MesenHighWork\validation-v09\fullbench-background\fullbench-summary.json")
    ap.add_argument("--animation", default=r"D:\MesenHighWork\validation-v09\animation-final\animation-results.json")
    ap.add_argument("--science", default=r"D:\MesenHighWork\validation-v09\science-final\science-v09.json")
    ap.add_argument("--unresolved", default=r"D:\MesenHighWork\validation-v09\unresolved23\unresolved23.json")
    ap.add_argument("--regression", default=r"D:\MesenHighWork\validation-v09\regression-final\regression-results.json")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v09\FINAL_METRICS.json")
    args = ap.parse_args()
    batch = load(os.path.join(args.static, "archaeology-batch.json"), {}) or {}
    rows = batch.get("rows", [])
    totals = {
        "romsAttempted": 0,
        "romsComplete": 0,
        "romsSkippedFromEarlierPass": 0,
        "unsupported": 0,
        "errors": 0,
        "catalogFiles": 0,
        "uniquePatterns": 0,
        "blankPatternOccurrences": 0,
        "observedChrRomPatterns": 0,
        "observedChrRamPatterns": 0,
        "observedRoms": 0,
        "twentyObservedRoms": 0,
        "catalogAssets": 0,
        "catalogAssetsObserved": 0,
        "catalogAssetsStatic": 0,
        "catalogPages": 0,
        "codeBlocks": 0,
        "ppuWrites": 0,
        "dataReferences": 0,
        "pointerTables": 0,
        "candidateStreams": 0,
        "paletteCandidates": 0,
        "gapsResolved": 0,
        "gapsPartial": 0,
        "gapsUnresolved": 0,
        "gapsUnsupported": 0,
        "gapsResolvedGraph": 0,
        "gapsPartialGraph": 0,
        "gapsUnresolvedGraph": 0,
        "compositions": 0,
        "animationSequences": 0,
        "metatiles": 0,
        "gapReports": 0,
        "chrRom": 0,
        "chrRam": 0,
    }
    by_mapper = {}
    by_chr = {}
    seen = set()
    for row in rows:
        if row.get("status") == "unsupported":
            totals["unsupported"] += 1
            continue
        if row.get("status") == "error":
            totals["errors"] += 1
            continue
        sha = (row.get("sha256") or "").lower()
        if not sha or sha in seen:
            continue
        seen.add(sha)
        totals["romsAttempted"] += 1
        folder = row.get("outputFolder", "")
        catalog_path = os.path.join(folder, "HdStudio", "archaeology", "catalog.json")
        catalog = load(catalog_path, {}) or {}
        s = catalog.get("summary", {})
        if not s:
            totals["errors"] += 1
            continue
        totals["romsComplete"] += 1
        if row.get("status") == "skipped":
            totals["romsSkippedFromEarlierPass"] += 1
        totals["catalogFiles"] += 1
        for key, source in (("uniquePatterns", "uniquePatterns"),
                            ("blankPatternOccurrences", "blankPatternOccurrences"),
                            ("observedChrRomPatterns", "observedChrRomPatterns"),
                            ("observedChrRamPatterns", "observedChrRamPatterns"),
                            ("catalogAssets", "assetsExposed"),
                            ("catalogAssetsObserved", "assetsObserved"),
                            ("catalogAssetsStatic", "assetsStatic"),
                            ("catalogPages", "catalogPages"),
                            ("codeBlocks", "codeBlocks"),
                            ("ppuWrites", "ppuWrites"),
                            ("dataReferences", "dataReferences"),
                            ("pointerTables", "pointerTables"),
                            ("candidateStreams", "candidateStreams"),
                            ("paletteCandidates", "paletteCandidates"),
                            ("compositions", "compositions"),
                            ("animationSequences", "animationSequences"),
                            ("metatiles", "metatiles")):
            totals[key] += int(s.get(source, 0) or 0)
        gap = load(os.path.join(folder, "HdStudio", "archaeology", "gap-resolutions.json"), {}) or {}
        gs = gap.get("summary", {})
        if gs:
            totals["gapReports"] += 1
            totals["gapsResolved"] += int(gs.get("resolved", 0))
            totals["gapsPartial"] += int(gs.get("partiallyResolved", 0))
            totals["gapsUnresolved"] += int(gs.get("unresolved", 0))
            totals["gapsUnsupported"] += int(gs.get("unsupported", 0))
        totals["gapsResolvedGraph"] += int(s.get("gapsResolved", 0) or 0)
        totals["gapsPartialGraph"] += int(s.get("gapsPartiallyResolved", 0) or 0)
        totals["gapsUnresolvedGraph"] += int(s.get("gapsUnresolved", 0) or 0)
        observed = int(s.get("observedChrRomPatterns", 0)) + int(s.get("observedChrRamPatterns", 0))
        if observed > 0:
            totals["observedRoms"] += 1
        if int(s.get("assetsObserved", 0)) >= 20:
            totals["twentyObservedRoms"] += 1
        chr_mode = "CHR-ROM" if str(row.get("chrMode")) == "CHR-ROM" else "CHR-RAM"
        totals["chrRom" if chr_mode == "CHR-ROM" else "chrRam"] += 1
        mapper = str(row.get("mapper"))
        for target, key in ((by_mapper.setdefault(mapper, {"roms": 0, "observed": 0, "assets": 0, "gapsResolved": 0}),
                             "mapper"),
                            (by_chr.setdefault(chr_mode, {"roms": 0, "observed": 0, "assets": 0, "gapsResolved": 0}),
                             "chr")):
            target["roms"] += 1
            target["observed"] += observed
            target["assets"] += int(s.get("assetsExposed", 0))
            target["gapsResolved"] += int(gs.get("resolved", 0))
    evidence_files = 0
    evidence_bytes = 0
    for folder, _, files in os.walk(os.path.join(args.static, "roms")):
        for name in files:
            evidence_files += 1
            try:
                evidence_bytes += os.path.getsize(os.path.join(folder, name))
            except OSError:
                pass
    if totals["gapsResolvedGraph"] + totals["gapsPartialGraph"] + totals["gapsUnresolvedGraph"] > 0:
        totals["gapsResolved"] = totals["gapsResolvedGraph"]
        totals["gapsPartial"] = totals["gapsPartialGraph"]
        totals["gapsUnresolved"] = totals["gapsUnresolvedGraph"]
    result = {
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "batchSummary": os.path.join(args.static, "archaeology-batch.json"),
        "batchWallSeconds": batch.get("wallSeconds"),
        "totals": totals,
        "byMapper": dict(sorted(by_mapper.items(), key=lambda kv: int(kv[0]) if kv[0].isdigit() else 9999)),
        "byChr": by_chr,
        "evidence": {
            "files": evidence_files,
            "bytes": evidence_bytes,
            "gib": round(evidence_bytes / (1024 ** 3), 2),
            "root": os.path.join(args.static, "roms"),
        },
        "replacement": load(args.replacement, {}) or {},
        "background": load(args.background, {}) or {},
        "animation": (load(args.animation, {}) or {}).get("summary", {}),
        "science": (load(args.science, {}) or {}).get("variants", {}),
        "unresolved23": load(args.unresolved, {}) or {},
        "regression": load(args.regression, {}) or {},
        "notes": [
            "Counts are read from each ROM catalog/gap report; the batch summary may include cached rows.",
            "No completeness percentage is computed for an unknown whole-game graphical inventory.",
        ],
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(json.dumps({k: result[k] for k in ("generatedAtUtc", "totals", "evidence")}, indent=1)[:6000])
    print("metrics:", args.out)


if __name__ == "__main__":
    main()
