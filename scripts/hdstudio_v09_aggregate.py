"""Aggregate the v0.9 full-library archaeology batch from per-ROM catalogs.

Reads the batch summary and each catalog.json; writes aggregate JSON/CSV and a
small reconciliation against the v0.8 master matrix. This reports discovery
coverage, not replacement verification.
"""
import argparse
import csv
import json
import os
from collections import Counter, defaultdict


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--batch", default=r"D:\MesenHighWork\validation-v09\library-static-v09b\archaeology-batch.json")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v09\library-static-v09b")
    ap.add_argument("--v08", default=r"D:\MesenHighWork\validation-v08\master-matrix-v08.json")
    args = ap.parse_args()
    batch = load(args.batch)
    rows = batch["rows"]
    aggregate = {
        "generatedAtUtc": batch["generatedAtUtc"],
        "engineVersion": batch["engineVersion"],
        "mode": batch["mode"],
        "romsAttempted": batch["attempted"],
        "romsComplete": batch["completed"],
        "romsUnsupported": batch["unsupported"],
        "romsErrors": batch["errors"],
        "chrRom": 0,
        "chrRam": 0,
        "uniquePatterns": 0,
        "blankPatternOccurrences": 0,
        "observedChrRomPatterns": 0,
        "observedChrRamPatterns": 0,
        "observedRoms": 0,
        "twentyObservedRoms": 0,
        "assetsExposed": 0,
        "assetsObserved": 0,
        "assetsStatic": 0,
        "candidateStreams": 0,
        "paletteCandidates": 0,
        "codeBlocks": 0,
        "ppuWrites": 0,
        "dataReferences": 0,
        "pointerTables": 0,
        "openGaps": 0,
        "gapsResolved": 0,
        "gapsPartial": 0,
        "gapsUnresolved": 0,
        "compositions": 0,
        "animationSequences": 0,
        "metatiles": 0,
        "catalogPages": 0,
        "chrRamRoms": 0,
        "chrRamObservedRoms": 0,
        "staticCandidateRoms": 0,
        "pointerTableRoms": 0,
        "noObservedRoms": 0,
        "byMapper": defaultdict(lambda: {"roms": 0, "observedRoms": 0, "assets": 0, "observedPatterns": 0, "gaps": 0}),
        "byChr": defaultdict(lambda: {"roms": 0, "observedRoms": 0, "assets": 0, "observedPatterns": 0, "gaps": 0}),
        "errors": [],
        "unsupported": [],
        "catalogMissing": 0,
    }
    for row in rows:
        if row["status"] == "unsupported":
            aggregate["unsupported"].append({"fileName": row["fileName"], "error": row["error"]})
            continue
        if row["status"] == "error":
            aggregate["errors"].append({"fileName": row["fileName"], "error": row["error"]})
            continue
        if row["status"] not in ("complete", "skipped"):
            continue
        folder = row["outputFolder"]
        catalog_path = os.path.join(folder, "HdStudio", "archaeology", "catalog.json")
        if not os.path.exists(catalog_path):
            aggregate["catalogMissing"] += 1
            continue
        catalog = load(catalog_path)
        s = catalog["summary"]
        mapper = str(row.get("mapper", ""))
        chr_mode = row.get("chrMode", "")
        agg_mapper = aggregate["byMapper"][mapper]
        agg_chr = aggregate["byChr"][chr_mode]
        for target in (aggregate, agg_mapper, agg_chr):
            target["assets"] = target.get("assets", 0) + s.get("assetsExposed", 0)
            target["observedPatterns"] = target.get("observedPatterns", 0) + s.get("observedChrRomPatterns", 0)
            target["gaps"] = target.get("gaps", 0) + s.get("openGaps", 0)
        agg_mapper["roms"] += 1
        agg_chr["roms"] += 1
        aggregate["uniquePatterns"] += s.get("uniquePatterns", 0)
        aggregate["blankPatternOccurrences"] += s.get("blankPatternOccurrences", 0)
        aggregate["observedChrRomPatterns"] += s.get("observedChrRomPatterns", 0)
        aggregate["observedChrRamPatterns"] += s.get("observedChrRamPatterns", 0)
        aggregate["assetsExposed"] += s.get("assetsExposed", 0)
        aggregate["assetsObserved"] += s.get("assetsObserved", 0)
        aggregate["assetsStatic"] += s.get("assetsStatic", 0)
        aggregate["candidateStreams"] += s.get("candidateStreams", 0)
        aggregate["paletteCandidates"] += s.get("paletteCandidates", 0)
        aggregate["codeBlocks"] += s.get("codeBlocks", 0)
        aggregate["ppuWrites"] += s.get("ppuWrites", 0)
        aggregate["dataReferences"] += s.get("dataReferences", 0)
        aggregate["pointerTables"] += s.get("pointerTables", 0)
        aggregate["openGaps"] += s.get("openGaps", 0)
        aggregate["gapsResolved"] += s.get("gapsResolved", 0)
        aggregate["gapsPartial"] += s.get("gapsPartiallyResolved", 0)
        aggregate["gapsUnresolved"] += s.get("gapsUnresolved", 0)
        aggregate["compositions"] += s.get("compositions", 0)
        aggregate["animationSequences"] += s.get("animationSequences", 0)
        aggregate["metatiles"] += s.get("metatiles", 0)
        aggregate["catalogPages"] += s.get("catalogPages", 1)
        observed = s.get("observedChrRomPatterns", 0) + s.get("observedChrRamPatterns", 0)
        if observed > 0:
            aggregate["observedRoms"] += 1
            agg_mapper["observedRoms"] += 1
            agg_chr["observedRoms"] += 1
        agg_mapper["assetsObserved"] = agg_mapper.get("assetsObserved", 0) + s.get("assetsObserved", 0)
        agg_mapper["assetsStatic"] = agg_mapper.get("assetsStatic", 0) + s.get("assetsStatic", 0)
        agg_chr["assetsObserved"] = agg_chr.get("assetsObserved", 0) + s.get("assetsObserved", 0)
        agg_chr["assetsStatic"] = agg_chr.get("assetsStatic", 0) + s.get("assetsStatic", 0)
        agg_chr["observedChrRomPatterns"] = agg_chr.get("observedChrRomPatterns", 0) + s.get("observedChrRomPatterns", 0)
        agg_chr["observedChrRamPatterns"] = agg_chr.get("observedChrRamPatterns", 0) + s.get("observedChrRamPatterns", 0)
        if observed >= 20:
            aggregate["twentyObservedRoms"] += 1
        if chr_mode == "CHR-RAM":
            aggregate["chrRamRoms"] += 1
            if s.get("observedChrRamPatterns", 0) > 0:
                aggregate["chrRamObservedRoms"] += 1
        if s.get("candidateStreams", 0) > 0:
            aggregate["staticCandidateRoms"] += 1
        if s.get("pointerTables", 0) > 0:
            aggregate["pointerTableRoms"] += 1
        if observed == 0:
            aggregate["noObservedRoms"] += 1

    #Reconcile with v0.8. This is not a new replacement result.
    v08_rows = load(args.v08) if os.path.exists(args.v08) else []
    v08_verified = sum(1 for r in v08_rows if r.get("spriteReplacementOutcome", "").startswith("verified"))
    aggregate["v08Reconciliation"] = {
        "historicalVerifiedRows": v08_verified,
        "v08Source": args.v08,
        "note": "v0.8 replacement verification is historical evidence; v0.9 discovery counts are separate and do not re-verify those replacements.",
    }
    aggregate["byMapper"] = dict(sorted(aggregate["byMapper"].items(), key=lambda kv: int(kv[0]) if kv[0].isdigit() else 9999))
    aggregate["byChr"] = dict(aggregate["byChr"])

    os.makedirs(args.out, exist_ok=True)
    out_json = os.path.join(args.out, "aggregate-v09.json")
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(aggregate, f, indent=1)
    out_csv = os.path.join(args.out, "aggregate-v09-by-mapper.csv")
    with open(out_csv, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["mapper", "roms", "observedRoms", "assets", "observedPatterns", "gaps"])
        for mapper, values in aggregate["byMapper"].items():
            writer.writerow([mapper, values["roms"], values["observedRoms"], values["assets"], values["observedPatterns"], values["gaps"]])
    print(json.dumps({k: v for k, v in aggregate.items() if not isinstance(v, dict)}, indent=2))
    print("aggregate:", out_json)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
