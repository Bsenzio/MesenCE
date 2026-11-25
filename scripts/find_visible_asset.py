"""Match visible background blocks from dump_visible.lua to catalog asset IDs.

The diagnostic log contains lines like:
  VISIBLE BG x=28 y=28 pal=0F0B0010 keys=B20,B00,B21,B00

Blank keys (all zeros) are ignored because the catalog deliberately omits blank cells.
The matcher reports the first backgroundBlock whose non-blank keys and palette match.
It is a validation aid, not engine input.
"""
import argparse
import json
import re


def normalize(value):
    if len(value) == 32:
        return value.upper()
    try:
        return format(int(value, 16), "X")
    except ValueError:
        return value.upper()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", required=True)
    ap.add_argument("--assets", required=True)
    ap.add_argument("--limit", type=int, default=12)
    args = ap.parse_args()

    doc = json.load(open(args.assets, encoding="utf-8"))
    blocks = []
    for asset in doc.get("assets", []):
        if asset.get("kind") != "backgroundBlock" or not asset.get("tiles"):
            continue
        tiles = asset["tiles"]
        key = (tuple(sorted(normalize(t.get("pattern")) for t in tiles)),
               (tiles[0].get("palette") or "").upper())
        blocks.append((key, asset))
    index = {}
    for key, asset in blocks:
        index.setdefault(key, []).append(asset)

    pattern = re.compile(r"VISIBLE BG x=(\d+) y=(\d+) pal=([0-9A-Fa-f]+) keys=([0-9A-Fa-f,]+)")
    matches = []
    with open(args.log, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = pattern.search(line)
            if not m:
                continue
            x, y, pal, keys = int(m.group(1)), int(m.group(2)), m.group(3).upper(), m.group(4).split(",")
            nonblank = sorted({normalize(k) for k in keys if k and normalize(k) != "0" * 32 and normalize(k) != "0"})
            if not nonblank:
                continue
            key = (tuple(nonblank), pal)
            if key in index:
                for asset in index[key]:
                    matches.append({
                        "x": x, "y": y, "palette": pal,
                        "keys": nonblank,
                        "asset": asset.get("name"),
                        "assetId": asset.get("id"),
                        "provenance": asset.get("provenance"),
                    })

    seen = set()
    unique = []
    for item in matches:
        if item["assetId"] in seen:
            continue
        seen.add(item["assetId"])
        unique.append(item)

    print(json.dumps({
        "visibleBlocks": len(matches),
        "uniqueAssets": len(unique),
        "matches": unique[:args.limit]
    }, indent=2))
    return 0 if unique else 1


if __name__ == "__main__":
    raise SystemExit(main())
