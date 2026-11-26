"""Aggregate v0.6 failures for root-cause review (no LLM per-ROM analysis)."""
import argparse
import collections
import json


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--smoke", default=r"D:\MesenHighWork\validation-v06\smoke-300\smoke-results.json")
    ap.add_argument("--replacement", action="append", default=[])
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v06\failure-summary.json")
    args = ap.parse_args()
    smoke = json.load(open(args.smoke, encoding="utf-8"))
    by_category = collections.Counter(r.get("failureCategory") or "captured" for r in smoke)
    by_mapper = collections.Counter((str(r.get("mapper")), r.get("failureCategory") or "captured")
                                    for r in smoke if r.get("failureCategory"))
    by_chr = collections.Counter((r.get("chrType"), r.get("failureCategory") or "captured")
                                 for r in smoke if r.get("failureCategory"))
    zero = [r["romName"] for r in smoke if r.get("failureCategory") == "no-observed-graphics"]
    load = [r["romName"] for r in smoke if r.get("failureCategory") == "load-failed"]
    timeouts = [r["romName"] for r in smoke if r.get("failureCategory") == "timeout"]
    replacement = []
    for path in args.replacement:
        try:
            replacement.extend(json.load(open(path, encoding="utf-8")))
        except Exception:
            pass
    rep_status = collections.Counter(r.get("status") for r in replacement)
    result = {
        "smoke": {"categories": dict(by_category), "zero": zero, "load": load,
                  "timeouts": timeouts,
                  "byMapperCategory": {f"{m}|{c}": n for (m, c), n in sorted(by_mapper.items())},
                  "byChrCategory": {f"{c}|{s}": n for (c, s), n in sorted(by_chr.items())}},
        "replacement": {"statuses": dict(rep_status),
                        "failed": [{"rom": r.get("rom"), "status": r.get("status"),
                                    "failure": r.get("failure"), "frame": r.get("pauseFrame")}
                                   for r in replacement if not str(r.get("status", "")).startswith("verified")]},
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(json.dumps({"smokeCategories": dict(by_category), "replacementStatuses": dict(rep_status)}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
