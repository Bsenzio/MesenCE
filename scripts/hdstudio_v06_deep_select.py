"""Select a stratified Tier-2 deep-exploration set without touching the frozen holdout."""
import argparse
import hashlib
import json
import os

PRIORITY = [
    "Don Doko Don 2 (Japan).nes",
    "Toki (USA).nes",
    "Little Mermaid - Ningyo Hime (Japan).nes",
    "Guardian Legend, The (USA).nes",
    "Super Mario Bros. 3 (USA).nes",
    "Batman - Return of the Joker (USA).nes",
    "croom__croom.nes",
    "irritating-ship__irritating-ship-NESdev-Compo-2022.nes",
    "rhde__rhde.nes",
    "thwaite__thwaite128.nes",
    "files__Color Match Combat (compo edition).nes",
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--inventory", default=r"D:\MesenHighWork\validation-v06\inventory\library-inventory.json")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v06\deep-selection.json")
    ap.add_argument("--count", type=int, default=120)
    args = ap.parse_args()
    doc = json.load(open(args.inventory, encoding="utf-8"))
    roms = [r for r in doc["roms"] if r.get("split") != "holdout"]
    by_name = {r["name"]: r for r in roms}
    chosen = {}
    for name in PRIORITY:
        if name in by_name:
            chosen[by_name[name]["sha256"]] = (by_name[name], "priority-regression")
    # One base title per boss/character family; stratify by mapper band, CHR and size.
    groups = {}
    for rom in roms:
        if rom["sha256"] in chosen:
            continue
        groups.setdefault(rom["normalizedTitle"], []).append(rom)
    strata = {}
    for title, members in groups.items():
        lead = members[0]
        key = (lead["mapperBand"], lead["chrType"], lead["chrSizeBucket"])
        strata.setdefault(key, []).append(lead)
    for key, members in strata.items():
        members.sort(key=lambda r: hashlib.sha256(
            ("v06-deep|" + r["sha256"]).encode()).hexdigest())
    order = sorted(strata.keys())
    index = 0
    while len(chosen) < args.count and order:
        key = order[index % len(order)]
        bucket = strata[key]
        if bucket:
            rom = bucket.pop(0)
            chosen[rom["sha256"]] = (rom, "stratified:" + "/".join(key))
        else:
            order.remove(key)
            continue
        index += 1
    result = {
        "count": len(chosen),
        "policy": "development+validation only; holdout excluded; one base title per family; stratified mapper/CHR/size",
        "items": [
            {"sha256": h, "name": r["name"], "mapper": r["mapper"], "chrType": r["chrType"],
             "split": r["split"], "reason": reason}
            for h, (r, reason) in sorted(chosen.items(), key=lambda kv: kv[1][0]["name"].lower())
        ],
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    with open(args.out.replace(".json", ".txt"), "w", encoding="utf-8") as f:
        for item in result["items"]:
            f.write(item["sha256"] + "\n")
    print(json.dumps({"selected": len(result["items"]), "out": os.path.abspath(args.out)}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
