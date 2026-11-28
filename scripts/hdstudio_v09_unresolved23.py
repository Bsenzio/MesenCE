"""Investigate the 23 historical v0.8 unresolved ROMs with final-engine evidence.

This joins the historical non-verified rows to the final v0.9 archaeology
catalogs, records which now have observed graphical evidence, and classifies
each remaining case as unsupported mapper, observation gap, emulator limit or a
real replacement failure. It does not relabel a discovery as a replacement.
"""
import argparse
import json
import os


def load(path, fallback=None):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except Exception:
        return fallback


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--matrix", default=r"D:\MesenHighWork\validation-v08\master-matrix-v08.json")
    ap.add_argument("--batch", default=r"D:\MesenHighWork\validation-v09\library-static-v09b\archaeology-batch.json")
    ap.add_argument("--replacement", default=r"D:\MesenHighWork\validation-v09\unresolved23\replacement-results.json")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v09\unresolved23")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    matrix = load(args.matrix, []) or []
    batch = load(args.batch, {}) or {}
    rows_by_sha = {(r.get("sha256") or "").lower(): r for r in batch.get("rows", [])}
    repl = load(args.replacement, []) or []
    repl_by_sha = {(r.get("sha256") or "").lower(): r for r in repl}
    unresolved = [r for r in matrix if not str(r.get("terminalStatus", "")).startswith("Verified")]
    result = {
        "generatedAtUtc": __import__("time").strftime("%Y-%m-%dT%H:%M:%SZ", __import__("time").gmtime()),
        "historicalSource": args.matrix,
        "historicalUnresolved": len(unresolved),
        "rows": [],
        "notes": [
            "A v0.9 observed pattern is discovery evidence, not a replacement claim.",
            "The classification distinguishes unsupported mappers, observation gaps, emulator limits and real replacement failures.",
        ],
    }
    counts = {}
    for row in unresolved:
        sha = (row.get("romSha256") or "").lower()
        batch_row = rows_by_sha.get(sha, {})
        catalog_path = os.path.join(batch_row.get("outputFolder", ""), "HdStudio", "archaeology", "catalog.json")
        catalog = load(catalog_path, {}) or {}
        summary = catalog.get("summary", {})
        observed = int(summary.get("observedChrRomPatterns", 0)) + int(summary.get("observedChrRamPatterns", 0))
        replacement = repl_by_sha.get(sha, {})
        replacement_status = replacement.get("status", "")
        if row.get("captureStatus") == "failed" and row.get("mapper") in (576, 396, 547):
            classification = "emulator-mapper-limitation"
            reason = "Mesen rejects or cannot expose the mapper/board; no legal ROM patch is applied"
        elif row.get("captureStatus") == "failed":
            classification = "capture-limitation"
            reason = "the emulator did not reach a capturable frame"
        elif observed >= 20 and replacement_status.startswith("verified"):
            classification = "resolved-observed-and-replaced"
            reason = "final v0.9 catalog has >=20 observed assets and the targeted replacement verified"
        elif observed > 0 and replacement_status.startswith("verified"):
            classification = "resolved-observed-and-replaced-partial-coverage"
            reason = "observed evidence exists; replacement verified on the available observed assets"
        elif observed > 0:
            classification = "observed-but-replacement-not-yet-verified"
            reason = "runtime evidence now exists; targeted replacement did not verify in the final run"
        elif replacement_status:
            classification = "replacement-failure"
            reason = "targeted replacement was attempted and failed: " + replacement_status
        else:
            classification = "observation-gap"
            reason = "no observed graphical evidence and no final targeted replacement result; queued for deeper deterministic exploration"
        counts[classification] = counts.get(classification, 0) + 1
        result["rows"].append({
            "rom": row.get("gameTitle"),
            "sha256": row.get("romSha256"),
            "mapper": row.get("mapper"),
            "chrType": row.get("chrType"),
            "historicalStatus": row.get("terminalStatus"),
            "historicalOutcome": row.get("spriteReplacementOutcome"),
            "v09ObservedChrRom": summary.get("observedChrRomPatterns", 0),
            "v09ObservedChrRam": summary.get("observedChrRamPatterns", 0),
            "v09Assets": summary.get("assetsExposed", 0),
            "v09Compositions": summary.get("compositions", 0),
            "v09Catalog": catalog_path if os.path.exists(catalog_path) else "",
            "replacementStatus": replacement_status,
            "classification": classification,
            "reason": reason,
        })
    result["classificationCounts"] = counts
    result["observedNow"] = sum(1 for r in result["rows"] if r["v09ObservedChrRom"] + r["v09ObservedChrRam"] > 0)
    result["withAtLeast20Observed"] = sum(1 for r in result["rows"] if r["v09ObservedChrRom"] + r["v09ObservedChrRam"] >= 20)
    with open(os.path.join(args.out, "unresolved23.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1, ensure_ascii=False)
    print(json.dumps({k: result[k] for k in ("historicalUnresolved", "observedNow", "withAtLeast20Observed", "classificationCounts")}, indent=1, ensure_ascii=False))
    print("out:", os.path.join(args.out, "unresolved23.json"))


if __name__ == "__main__":
    main()
