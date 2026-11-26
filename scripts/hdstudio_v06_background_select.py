"""Choose a background-validation set from verified sprite-replacement rows."""
import argparse
import json


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", nargs="+", default=[
        r"D:\MesenHighWork\validation-v06\replacements-115\replacement-results.json",
        r"D:\MesenHighWork\validation-v06\replacements-homebrew\replacement-results.json",
    ])
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v06\background-selection.txt")
    ap.add_argument("--count", type=int, default=50)
    args = ap.parse_args()
    rows = []
    for path in args.results:
        try:
            rows.extend(json.load(open(path, encoding="utf-8")))
        except Exception:
            pass
    verified = [r for r in rows if str(r.get("status", "")).startswith("verified")]
    # Skip games whose pause-frame background evidence is known to be unstable.
    verified = [r for r in verified if "Super Mario Bros. 3" not in (r.get("rom") or "")]
    seen = set()
    chosen = []
    for r in sorted(verified, key=lambda x: x.get("rom", "")):
        h = r.get("sha256")
        if h and h not in seen:
            seen.add(h)
            chosen.append(r)
        if len(chosen) >= args.count:
            break
    with open(args.out, "w", encoding="utf-8") as f:
        for r in chosen:
            f.write(r["sha256"] + "\n")
    print(json.dumps({"verifiedPool": len(verified), "selected": len(chosen),
                      "games": [r.get("rom") for r in chosen[:10]]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
