"""Controlled v0.9 discovery comparison.

Runs four engine variants on one stratified sample and records measured
coverage, terminal gap outcomes, runtime frames used and processing time. It
does not compute a completeness percentage for an unknown textbook inventory;
counts are exact observed/structural counts only.

Variants:
  static-only       no runtime evidence, structural walk, no gap resolution
  dynamic-only      existing runtime evidence, no structural walk, no gap resolution
  hybrid            existing runtime evidence + structural walk, no gap resolution
  hybrid+resolve    same as hybrid + automatic evidence-gap resolution
"""
import argparse
import csv
import json
import os
import shutil
import subprocess
import time
from collections import defaultdict


def load(path, fallback=None):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except Exception:
        return fallback


def run_variant(mesen, rom, evidence, out_root, variant, mode="deep", no_structural=False,
                no_resolve=False, timeout=600):
    out = os.path.join(out_root, variant, rom["sha256"][:12])
    if os.path.isdir(out):
        shutil.rmtree(out, ignore_errors=True)
    os.makedirs(out, exist_ok=True)
    log = os.path.join(out, "run.log")
    args = [mesen, "--hdstudio", rom["path"], "--archaeology", "--arch-mode=" + mode,
            "--arch-assets=0", "--out=" + out, "--log=" + log]
    if evidence and os.path.isdir(evidence):
        args.append("--arch-evidence=" + evidence)
    if no_structural:
        args.append("--arch-no-structural")
    if no_resolve:
        args.append("--arch-no-resolve")
    started = time.time()
    proc = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    seconds = time.time() - started
    catalog = load(os.path.join(out, "HdStudio", "archaeology", "catalog.json"), {}) or {}
    graph = load(os.path.join(out, "HdStudio", "archaeology", "evidence-graph.json"), {}) or {}
    gaps = load(os.path.join(out, "HdStudio", "archaeology", "gap-resolutions.json"), {}) or {}
    summary = catalog.get("summary", {})
    gs = gaps.get("summary", {})
    frames = 0
    if evidence:
        capture = os.path.join(evidence, "HdStudio", "capture.txt")
        if os.path.exists(capture):
            for line in open(capture, encoding="utf-8", errors="replace"):
                if line.startswith("frames="):
                    try:
                        frames = int(line.split("=", 1)[1].strip())
                    except ValueError:
                        pass
    return {
        "variant": variant,
        "rom": rom["name"],
        "sha256": rom["sha256"],
        "split": rom.get("frozenSplit") or rom.get("split") or "",
        "mapper": rom.get("mapper"),
        "chrMode": "CHR-ROM" if rom.get("chrBytes", 0) else "CHR-RAM",
        "exitCode": proc.returncode,
        "seconds": round(seconds, 2),
        "frames": frames,
        "uniquePatterns": summary.get("uniquePatterns", 0),
        "observedChrRomPatterns": summary.get("observedChrRomPatterns", 0),
        "observedChrRamPatterns": summary.get("observedChrRamPatterns", 0),
        "assetsExposed": summary.get("assetsExposed", 0),
        "assetsObserved": summary.get("assetsObserved", 0),
        "assetsStatic": summary.get("assetsStatic", 0),
        "compositions": summary.get("compositions", 0),
        "animationSequences": summary.get("animationSequences", 0),
        "metatiles": summary.get("metatiles", 0),
        "codeBlocks": summary.get("codeBlocks", 0),
        "ppuWrites": summary.get("ppuWrites", 0),
        "pointerTables": summary.get("pointerTables", 0),
        "gapsResolved": gs.get("resolved", summary.get("gapsResolved", 0)),
        "gapsPartial": gs.get("partiallyResolved", summary.get("gapsPartiallyResolved", 0)),
        "gapsUnresolved": gs.get("unresolved", summary.get("gapsUnresolved", 0)),
        "noResolve": no_resolve,
        "noStructural": no_structural,
        "logTail": proc.stdout[-500:] if proc.returncode != 0 else "",
    }


def summarize(rows):
    grouped = defaultdict(list)
    for row in rows:
        grouped[row["variant"]].append(row)
    out = {}
    for variant, items in grouped.items():
        out[variant] = {
            "rows": len(items),
            "failed": sum(1 for r in items if r["exitCode"] != 0),
            "uniquePatterns": sum(r["uniquePatterns"] for r in items),
            "observedChrRomPatterns": sum(r["observedChrRomPatterns"] for r in items),
            "observedChrRamPatterns": sum(r["observedChrRamPatterns"] for r in items),
            "assetsExposed": sum(r["assetsExposed"] for r in items),
            "assetsObserved": sum(r["assetsObserved"] for r in items),
            "assetsStatic": sum(r["assetsStatic"] for r in items),
            "compositions": sum(r["compositions"] for r in items),
            "animationSequences": sum(r["animationSequences"] for r in items),
            "metatiles": sum(r["metatiles"] for r in items),
            "codeBlocks": sum(r["codeBlocks"] for r in items),
            "ppuWrites": sum(r["ppuWrites"] for r in items),
            "pointerTables": sum(r["pointerTables"] for r in items),
            "gapsResolved": sum(r["gapsResolved"] for r in items),
            "gapsPartial": sum(r["gapsPartial"] for r in items),
            "gapsUnresolved": sum(r["gapsUnresolved"] for r in items),
            "runtimeFrames": sum(r["frames"] for r in items),
            "seconds": round(sum(r["seconds"] for r in items), 2),
        }
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--inventory", default=r"D:\MesenHighWork\validation-v09\inventory\library-inventory-v09.json")
    ap.add_argument("--evidence-map", default=r"D:\MesenHighWork\validation-v09\evidence-map-v06.json")
    ap.add_argument("--out-root", default=r"D:\MesenHighWork\validation-v09\science")
    ap.add_argument("--sample", type=int, default=30)
    args = ap.parse_args()
    inv = load(args.inventory, {}) or {}
    evidence_map = load(args.evidence_map, {}) or {}
    roms = [r for r in inv.get("roms", []) if r.get("headerValid", True)]
    by_split = defaultdict(list)
    for rom in roms:
        if rom.get("root") != "Library":
            continue
        by_split[rom.get("frozenSplit") or "development"].append(rom)
    sample = []
    per_split = max(3, args.sample // max(1, len(by_split)))
    for split, items in by_split.items():
        items = sorted(items, key=lambda r: r["name"].lower())
        step = max(1, len(items) // per_split)
        sample.extend(items[::step][:per_split])
    sample = sample[:args.sample]
    os.makedirs(args.out_root, exist_ok=True)
    rows = []
    variants = [
        ("static-only", dict(mode="deep", no_structural=False, no_resolve=True, use_evidence=False)),
        ("dynamic-only", dict(mode="deep", no_structural=True, no_resolve=True, use_evidence=True)),
        ("hybrid", dict(mode="deep", no_structural=False, no_resolve=True, use_evidence=True)),
        ("hybrid+resolve", dict(mode="deep", no_structural=False, no_resolve=False, use_evidence=True)),
    ]
    for rom in sample:
        evidence = evidence_map.get((rom.get("sha256") or "")[:12], "")
        for name, spec in variants:
            ev = evidence if spec["use_evidence"] else ""
            row = run_variant(args.mesen, rom, ev, args.out_root, name,
                              mode=spec["mode"], no_structural=spec["no_structural"],
                              no_resolve=spec["no_resolve"])
            rows.append(row)
            print(json.dumps({"rom": rom["name"], "variant": name,
                              "seconds": row["seconds"], "assets": row["assetsExposed"],
                              "observed": row["observedChrRomPatterns"] + row["observedChrRamPatterns"]},
                             ensure_ascii=False), flush=True)
    summary = summarize(rows)
    result = {
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "engine": os.path.abspath(args.mesen),
        "sample": [r["name"] for r in sample],
        "variants": summary,
        "rows": rows,
        "notes": [
            "Counts are exact observed/structural counts on this sample; they are not a completeness percentage for an unknown full graphical inventory.",
            "static-only uses no runtime evidence; dynamic-only uses existing runtime evidence without the structural walk; hybrid adds the structural walk; hybrid+resolve executes the automatic gap resolver.",
            "Runtime frames are read from each existing capture.txt; the resolver itself runs locally with no additional emulated frames.",
            "Replacement accuracy is reported separately by hdstudio_v09_fullbench.py.",
        ],
    }
    out_json = os.path.join(args.out_root, "science-v09.json")
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1, ensure_ascii=False)
    with open(os.path.join(args.out_root, "science-v09-rows.csv"), "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()) if rows else [])
        writer.writeheader()
        writer.writerows(rows)
    print(json.dumps(summary, indent=1))
    print("science:", out_json)


if __name__ == "__main__":
    main()
