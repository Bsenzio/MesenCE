"""v0.7 inventory update: keep the frozen v0.6 splits, detect new ROMs.

New files (not present by SHA-256 in the frozen inventory) are assigned to the
`development` split only. The validation and holdout hashes are never moved.
This preserves the evaluation dataset while letting the library grow.

Output: validation/v0.7/library-inventory.json
"""
import argparse
import hashlib
import json
import os
import re
import time


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_header(path):
    try:
        with open(path, "rb") as f:
            data = f.read(16)
        if len(data) < 16 or data[:4] != b"NES\x1a":
            return None
        nes20 = (data[7] & 0x0C) == 0x08
        mapper = (data[6] >> 4) | (data[7] & 0xF0)
        submapper = 0
        if nes20:
            mapper |= (data[8] & 0x0F) << 8
            submapper = (data[8] >> 4) & 0x0F
        chr_ram = (data[5] & 0x0F) == 0
        return {
            "headerValid": True,
            "nes20": nes20,
            "mapper": mapper,
            "submapper": submapper,
            "chrType": "CHR-RAM" if chr_ram else "CHR-ROM",
            "prgKb": (data[4] if not nes20 else ((data[4] & 0x0F) << 8 | data[9])) * 16,
            "chrKb": (data[5] if not nes20 else ((data[5] & 0x0F) << 8 | data[10])) * 8,
            "fileBytes": os.path.getsize(path),
        }
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--library", default=r"D:\NESGames\Library")
    ap.add_argument("--frozen", default=r"D:\MesenHighWork\validation-v06\inventory\library-inventory.json")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v07\inventory\library-inventory.json")
    args = ap.parse_args()

    frozen = json.load(open(args.frozen, encoding="utf-8"))
    by_hash = {r["sha256"].lower(): r for r in frozen["roms"]}
    by_rel = {r["relativePath"].lower(): r for r in frozen["roms"]}
    rows = []
    duplicates = []
    invalid = []
    seen = set()
    files = []
    for root, _, names in os.walk(args.library):
        for name in names:
            if name.lower().endswith(".nes"):
                files.append(os.path.join(root, name))
    files.sort(key=lambda p: os.path.relpath(p, args.library).lower())

    for path in files:
        rel = os.path.relpath(path, args.library)
        digest = sha256(path)
        if digest in seen:
            duplicates.append({"path": path, "sha256": digest, "duplicate": True})
            continue
        seen.add(digest)
        base = by_hash.get(digest) or by_rel.get(rel.lower())
        if base:
            row = dict(base)
            row["path"] = os.path.abspath(path)
            row["relativePath"] = rel
            row["sha256"] = digest
            row["split"] = base.get("split", "development")
        else:
            header = parse_header(path)
            if header is None:
                invalid.append({"path": path, "sha256": digest})
                continue
            row = {
                "name": os.path.basename(path),
                "path": os.path.abspath(path),
                "relativePath": rel,
                "sha256": digest,
                "title": os.path.splitext(os.path.basename(path))[0],
                "baseTitle": os.path.splitext(os.path.basename(path))[0],
                "normalizedTitle": re.sub(r"[^a-z0-9]+", " ", os.path.splitext(os.path.basename(path))[0].lower()).strip(),
                "region": "", "languages": "", "revision": "", "flags": [],
                "isMulticart": False, "isPirate": False, "isHomebrew": False,
                "isHack": False, "isUnlicensed": False,
                "split": "development",
                "newSinceFrozen": True,
                **header,
            }
        rows.append(row)

    rows.sort(key=lambda r: (r.get("name") or "").lower())
    base_titles = {r.get("baseTitle") or r["name"] for r in rows}
    summary = {
        "library": os.path.abspath(args.library),
        "uniqueRoms": len(rows),
        "baseTitles": len(base_titles),
        "duplicateFiles": len(duplicates),
        "invalidHeaders": len(invalid),
        "newSinceFrozen": sum(1 for r in rows if r.get("newSinceFrozen")),
        "chrRom": sum(1 for r in rows if r.get("chrType") == "CHR-ROM"),
        "chrRam": sum(1 for r in rows if r.get("chrType") == "CHR-RAM"),
        "splits": {
            "development": sum(1 for r in rows if r.get("split") == "development"),
            "validation": sum(1 for r in rows if r.get("split") == "validation"),
            "holdout": sum(1 for r in rows if r.get("split") == "holdout"),
        },
        "frozenInventory": os.path.abspath(args.frozen),
        "updatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "policy": "new ROMs are development-only; frozen validation/holdout hashes are never moved",
    }
    doc = {"summary": summary, "roms": rows, "duplicates": duplicates, "invalid": invalid}
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    tmp = args.out + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    os.replace(tmp, args.out)
    print(json.dumps(summary, indent=1))


if __name__ == "__main__":
    main()
