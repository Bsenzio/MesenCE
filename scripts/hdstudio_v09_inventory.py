"""v0.9 inventory and quarantine.

Read-only recursive inventory of every .nes below a root. Compares SHA-256
against the frozen 1,291-ROM v0.7/v0.8 inventory, separates additions into a
quarantined list, parses iNES/NES 2.0 headers and writes machine-readable JSON
plus CSV. No ROM is modified, moved or copied.

Usage:
  python hdstudio_v09_inventory.py --root D:\\NESGames \
      --frozen D:\\MesenHighWork\\validation-v07\\inventory\\library-inventory.json \
      --out D:\\MesenHighWork\\validation-v09\\inventory
"""
import argparse
import concurrent.futures
import csv
import hashlib
import json
import os
import re
import statistics
import time
from collections import Counter, defaultdict

FAMILY_SUFFIX = re.compile(
    r"\s*\((?:USA|Europe|Japan|Asia|China|Korea|Australia|Brazil|Canada|France|Germany|Italy|Spain|Sweden|World|En|Ja|Fr|De|Es|It|Nl|Pt|Sv|Da|No|Fi|Zh|Ko|Ru|Pl|Unl|Rev [A-Z0-9]+|Beta|Proto|Sample|Demo|Pirate|Hack|Translation|Aftermarket|Homebrew)[^)]*\)\s*$",
    re.IGNORECASE,
)


def parse_header(path, file_bytes):
    item = {
        "headerValid": False,
        "nes20": False,
        "mapper": -1,
        "submapper": 0,
        "prgBytes": 0,
        "chrBytes": 0,
        "chrType": "unknown",
        "prgRamBytes": 0,
        "chrRamBytes": 0,
        "battery": False,
        "fourScreen": False,
        "mirroring": "",
        "consoleType": 0,
        "hasTrainer": False,
        "error": "",
    }
    if len(file_bytes) < 16 or file_bytes[0:4] != b"NES\x1a":
        item["error"] = "not iNES/NES 2.0"
        return item
    data = file_bytes
    item["headerValid"] = True
    item["nes20"] = (data[7] & 0x0C) == 0x08
    item["hasTrainer"] = (data[6] & 0x04) != 0
    item["battery"] = (data[6] & 0x02) != 0
    item["fourScreen"] = (data[6] & 0x08) != 0
    item["mirroring"] = "Four-screen" if item["fourScreen"] else ("Vertical" if data[6] & 1 else "Horizontal")
    item["consoleType"] = data[7] & 3
    prg_units = data[4]
    chr_units = data[5]
    item["mapper"] = (data[6] >> 4) | (data[7] & 0xF0)
    if item["nes20"]:
        prg_units |= (data[9] & 0x0F) << 8
        chr_units |= (data[9] & 0xF0) << 4
        item["mapper"] |= (data[8] & 0x0F) << 8
        item["submapper"] = (data[8] >> 4) & 0x0F
        item["prgRamBytes"] = (data[10] & 0x0F) * 8192
        item["chrRamBytes"] = ((data[10] >> 4) & 0x0F) * 8192
    item["prgBytes"] = prg_units * 16384
    item["chrBytes"] = chr_units * 8192
    item["chrType"] = "CHR-ROM" if item["chrBytes"] > 0 else "CHR-RAM"
    header = 16 + (512 if item["hasTrainer"] else 0)
    expected = header + item["prgBytes"] + item["chrBytes"]
    if len(data) < header + item["prgBytes"]:
        item["error"] = "truncated PRG"
    elif len(data) > expected:
        item["trailingBytes"] = len(data) - expected
    return item


def normalize_title(title):
    value = FAMILY_SUFFIX.sub("", title).strip()
    value = re.sub(r"\s+", " ", value)
    return value


def key_title(value):
    return "".join(ch.lower() for ch in value if ch.isalnum())


def scan(args):
    frozen = {}
    if args.frozen and os.path.exists(args.frozen):
        with open(args.frozen, encoding="utf-8-sig") as f:
            frozen_doc = json.load(f)
        for row in frozen_doc.get("roms", []):
            frozen[row["sha256"].lower()] = row
    files = []
    for directory, _, names in os.walk(args.root):
        for name in names:
            if name.lower().endswith(".nes"):
                files.append(os.path.join(directory, name))
    files.sort(key=str.lower)
    started = time.time()

    def hash_one(path):
        with open(path, "rb") as f:
            data = f.read()
        return path, data

    items = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        for path, data in pool.map(hash_one, files):
            digest = hashlib.sha256(data).hexdigest()
            title = os.path.splitext(os.path.basename(path))[0]
            rel = os.path.relpath(path, args.root)
            root_name = rel.split(os.sep, 1)[0]
            header = parse_header(path, data)
            item = {
                "name": os.path.basename(path),
                "path": os.path.abspath(path),
                "relativePath": rel,
                "root": root_name,
                "sha256": digest,
                "title": title,
                "baseTitle": normalize_title(title),
                "normalizedTitle": key_title(title),
                "isHomebrew": "homebrew" in rel.lower() or "homebrew" in title.lower(),
                "isMulticart": bool(re.search(r"\b\d+-in-1\b|multi", title, re.IGNORECASE)),
                "isPirate": "pirate" in title.lower(),
                "isTranslation": "translation" in title.lower() or "[t" in title.lower(),
                "isHack": "hack" in title.lower(),
                "fileBytes": len(data),
                "frozen": digest in frozen,
                "frozenSplit": frozen.get(digest, {}).get("split", ""),
            }
            item.update(header)
            items.append(item)

    by_hash = defaultdict(list)
    for item in items:
        by_hash[item["sha256"]].append(item)
    for digest, group in by_hash.items():
        for index, item in enumerate(group):
            item["duplicateOf"] = "" if index == 0 else group[0]["path"]
    new_unique = [item for item in items if not item["frozen"] and not item["duplicateOf"]]
    for item in new_unique:
        item["quarantined"] = True

    summary = {
        "generatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "root": os.path.abspath(args.root),
        "frozenInventory": os.path.abspath(args.frozen) if args.frozen else "",
        "scannedFiles": len(items),
        "uniqueHashes": len(by_hash),
        "frozenMatchedFiles": sum(1 for item in items if item["frozen"]),
        "frozenMissingFromScan": len(set(frozen) - set(by_hash)),
        "newUniqueHashes": len(new_unique),
        "duplicateFiles": sum(1 for item in items if item.get("duplicateOf")),
        "invalidHeaders": sum(1 for item in items if not item.get("headerValid")),
        "homebrewFiles": sum(1 for item in items if item.get("isHomebrew")),
        "byRoot": dict(Counter(item["root"] for item in items)),
        "byMapper": dict(Counter(str(item["mapper"]) for item in items)),
        "byChrType": dict(Counter(item["chrType"] for item in items)),
        "bySplit": dict(Counter(item["frozenSplit"] or ("quarantine" if item.get("quarantined") else "unmatched") for item in items)),
        "scanSeconds": round(time.time() - started, 2),
        "policy": "new unique hashes are quarantined and excluded from v0.9 tuning and final-engine claims",
    }
    return summary, items, new_unique, frozen


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=r"D:\NESGames")
    parser.add_argument("--frozen", default=r"D:\MesenHighWork\validation-v07\inventory\library-inventory.json")
    parser.add_argument("--out", default=r"D:\MesenHighWork\validation-v09\inventory")
    parser.add_argument("--workers", type=int, default=6)
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    summary, items, new_unique, frozen = scan(args)
    doc = {
        "summary": summary,
        "roms": items,
        "quarantine": new_unique,
        "invalid": [item for item in items if not item.get("headerValid")],
        "duplicates": [item for item in items if item.get("duplicateOf")],
    }
    json_path = os.path.join(args.out, "library-inventory-v09.json")
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    csv_path = os.path.join(args.out, "library-inventory-v09.csv")
    fields = ["root", "relativePath", "sha256", "title", "baseTitle", "mapper", "submapper", "chrType", "prgBytes", "chrBytes",
              "isHomebrew", "frozen", "frozenSplit", "quarantined", "duplicateOf", "headerValid", "error"]
    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        for item in items:
            writer.writerow(item)
    with open(os.path.join(args.out, "quarantine-sha256.txt"), "w", encoding="utf-8") as f:
        for item in new_unique:
            f.write(item["sha256"] + "\t" + item["relativePath"] + "\n")
    with open(os.path.join(args.out, "inventory-summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=1)
    print(json.dumps(summary, indent=2))
    print("inventory:", json_path)
    print("quarantine:", len(new_unique), "new unique hashes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
