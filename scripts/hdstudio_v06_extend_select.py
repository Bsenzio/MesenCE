"""Create extension hash lists from a completed Tier-1 smoke result."""
import argparse
import json


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--smoke", default=r"D:\MesenHighWork\validation-v06\smoke-300\smoke-results.json")
    ap.add_argument("--out-dir", default=r"D:\MesenHighWork\validation-v06")
    args = ap.parse_args()
    rows = json.load(open(args.smoke, encoding="utf-8"))
    zero = [r for r in rows if r.get("failureCategory") == "no-observed-graphics"]
    timeouts = [r for r in rows if r.get("failureCategory") == "timeout"]
    short = [r for r in rows if r.get("failureCategory") == "no-observed-graphics"
             or (r.get("captureCompleted") == "passed" and (r.get("observedPatterns") or 0) < 20)]
    with open(args.out_dir + r"\extend-1800.txt", "w", encoding="utf-8") as f:
        for r in short:
            f.write(r["sha256"] + "\n")
    with open(args.out_dir + r"\extend-6000.txt", "w", encoding="utf-8") as f:
        for r in zero + timeouts:
            f.write(r["sha256"] + "\n")
    print(json.dumps({"zero": len(zero), "timeouts": len(timeouts),
                      "extend1800": len(short), "extend6000": len(zero) + len(timeouts)},
                     indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
