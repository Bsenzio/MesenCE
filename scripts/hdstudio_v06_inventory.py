"""RetroArcDo HD Studio v0.6 - library inventory and frozen dataset splits.

Deterministic, local-only. Never opens a ROM for writing.

Outputs (default: D:\\MesenHighWork\\validation-v06\\inventory):
  library-inventory.json   one machine-readable record per unique ROM hash
  library-index-v06.csv    compact review matrix
  dataset-splits.json      development/validation/holdout assignment (frozen)
  inventory-summary.json   aggregate counts

The splitter keeps every regional revision of the same base title in one split,
stratifies by mapper band, CHR type and CHR/PRG size bucket, and freezes the
holdout. New files added after the freeze go to development, never to holdout.
"""
import argparse
import csv
import hashlib
import json
import os
import re
import struct

REGIONS = {
    "usa": "USA", "europe": "Europe", "japan": "Japan", "world": "World",
    "asia": "Asia", "korea": "Korea", "china": "China", "taiwan": "Taiwan",
    "brazil": "Brazil", "australia": "Australia", "germany": "Germany",
    "france": "France", "spain": "Spain", "italy": "Italy", "sweden": "Sweden",
    "netherlands": "Netherlands", "canada": "Canada", "uk": "UK",
    "united kingdom": "UK", "russia": "Russia", "hong kong": "Hong Kong",
    "mexico": "Mexico", "finland": "Finland", "denmark": "Denmark",
    "norway": "Norway", "poland": "Poland", "portugal": "Portugal",
}
LANGUAGES = {
    "en": "En", "ja": "Ja", "fr": "Fr", "de": "De", "es": "Es", "it": "It",
    "nl": "Nl", "pt": "Pt", "sv": "Sv", "ko": "Ko", "zh": "Zh", "ru": "Ru",
    "da": "Da", "fi": "Fi", "no": "No", "pl": "Pl",
}
FLAG_TAGS = {
    "beta": "Beta", "proto": "Proto", "prototype": "Proto", "demo": "Demo",
    "sample": "Sample", "unl": "Unl", "unlicensed": "Unl", "pirate": "Pirate",
    "hack": "Hack", "homebrew": "Homebrew", "aftermarket": "Aftermarket",
    "program": "Program", "test": "Test", "sound": "Sound", "music": "Music",
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_rom(path):
    """Parse iNES / NES 2.0 without trusting the extension or the database."""
    with open(path, "rb") as f:
        header = f.read(16)
        size = os.path.getsize(path)
    info = {
        "headerValid": False, "nes20": False, "mapper": None, "submapper": None,
        "prgKb": None, "chrKb": None, "chrType": "unknown", "prgRamKb": None,
        "chrRamKb": None, "battery": False, "fourScreen": False,
        "mirroring": "unknown", "consoleType": "unknown", "fileBytes": size,
        "trailingBytes": 0,
    }
    if len(header) < 16 or header[:4] != b"NES\x1a":
        info["error"] = "not an iNES/NES 2.0 header"
        return info
    info["headerValid"] = True
    prg_units, chr_units, flags6, flags7 = header[4], header[5], header[6], header[7]
    info["nes20"] = (flags7 & 0x0C) == 0x08
    info["fourScreen"] = bool(flags6 & 0x08)
    info["battery"] = bool(flags6 & 0x02)
    info["mirroring"] = "Four-screen" if info["fourScreen"] else ("Vertical" if flags6 & 1 else "Horizontal")
    mapper = (flags6 >> 4) | (flags7 & 0xF0)
    prg_size = prg_units * 16
    chr_size = chr_units * 8
    if info["nes20"]:
        mapper |= (header[8] & 0x0F) << 8
        info["submapper"] = (header[8] >> 4) & 0x0F
        # Only use exponent-multiplier form when the classic field is zero.
        if prg_units == 0x0F:
            exp = header[4] & 0x0F
            prg_size = (1 << exp) * (header[10] * 256 + header[9] or 1) // 1024
        if chr_units == 0x0F:
            exp = header[5] & 0x0F
            chr_size = (1 << exp) * (header[12] * 256 + header[11] or 1) // 1024
        ram_sizes = header[10] or 0
        info["prgRamKb"] = (64 << (ram_sizes & 0x0F)) // 1024 if (ram_sizes & 0x0F) else 0
        eeprom = (ram_sizes >> 4) & 0x0F
        info["eepromKb"] = (64 << eeprom) // 1024 if eeprom else 0
        info["chrRamKb"] = 0 if chr_units else (64 << (header[11] & 0x0F)) // 1024
    info["mapper"] = mapper
    info["prgKb"] = prg_size
    info["chrKb"] = chr_size
    info["chrType"] = "CHR-ROM" if chr_units else "CHR-RAM"
    expected = 16 + prg_units * 16384 + chr_units * 8192
    info["trailingBytes"] = max(0, size - expected)
    if size < expected:
        info["truncatedBytes"] = expected - size
    return info


def split_tags(title):
    groups = re.findall(r"[\(\[]([^\)\]]*)[\)\]]", title)
    return groups


def classify_title(name):
    stem = os.path.splitext(name)[0]
    tags = split_tags(stem)
    regions, languages, flags, revisions = [], [], [], []
    for tag in tags:
        low = tag.lower().strip()
        if low in REGIONS:
            regions.append(REGIONS[low])
            continue
        if "," in low and all(part.strip() in REGIONS for part in low.split(",")):
            regions.extend(REGIONS[part.strip()] for part in low.split(","))
            continue
        if low in LANGUAGES:
            languages.append(LANGUAGES[low])
            continue
        m = re.match(r"rev(?:ision)?\s*([a-z0-9]+)", low)
        if m:
            revisions.append("Rev " + m.group(1).upper())
            continue
        if re.match(r"v\d+(\.\d+)*$", low):
            revisions.append(low.upper())
            continue
        if low in FLAG_TAGS:
            flags.append(FLAG_TAGS[low])
            continue
        if low.startswith("t+") or low.startswith("t-"):
            languages.append(low.upper())
    # Base title: remove every parenthetical/bracket group that was recognized as a
    # qualifier. Unknown parenthetical text stays (it can be part of the title).
    known = set(REGIONS) | set(LANGUAGES) | set(FLAG_TAGS)
    base = stem
    for m in re.finditer(r"\s*[\(\[]([^\)\]]*)[\)\]]", stem):
        low = m.group(1).lower().strip()
        recognized = (
            low in known
            or low in {"rev", "revision"}
            or re.match(r"rev(?:ision)?\s*[a-z0-9]+$", low)
            or re.match(r"v\d+(\.\d+)*$", low)
            or ("," in low and all(p.strip() in REGIONS for p in low.split(",")))
            or low.startswith("t+") or low.startswith("t-")
        )
        if recognized:
            base = base.replace(m.group(0), "")
    base = re.sub(r"\s+", " ", base).strip(" -_.")
    if not base:
        base = stem
    normalized = re.sub(r"[^a-z0-9]+", " ", base.lower()).strip()
    multicart = bool(re.search(r"\b\d+\s*[- ]?in\s*[- ]?1\b", normalized))
    return {
        "title": stem,
        "baseTitle": base,
        "normalizedTitle": normalized,
        "region": ", ".join(dict.fromkeys(regions)) or "Unknown",
        "languages": ", ".join(dict.fromkeys(languages)),
        "revision": ", ".join(dict.fromkeys(revisions)),
        "flags": sorted(set(flags)),
        "isMulticart": multicart,
        "isPirate": "Pirate" in flags,
        "isHomebrew": "Homebrew" in flags or "Aftermarket" in flags,
        "isHack": "Hack" in flags,
        "isUnlicensed": "Unl" in flags,
    }


def size_bucket(value):
    if value is None:
        return "unknown"
    if value <= 32:
        return "<=32K"
    if value <= 128:
        return "33-128K"
    if value <= 256:
        return "129-256K"
    if value <= 512:
        return "257-512K"
    return ">512K"


def mapper_band(mapper):
    if mapper is None:
        return "unknown"
    if mapper <= 4:
        return str(mapper)
    if mapper <= 9:
        return "5-9"
    if mapper <= 19:
        return "10-19"
    if mapper <= 49:
        return "20-49"
    if mapper <= 99:
        return "50-99"
    if mapper <= 199:
        return "100-199"
    if mapper <= 399:
        return "200-399"
    return "400+"


def inventory(library):
    files = []
    for root, _, names in os.walk(library):
        for name in sorted(names):
            if not name.lower().endswith(".nes"):
                continue
            path = os.path.join(root, name)
            files.append((path, name))
    by_hash = {}
    duplicates = []
    invalid = []
    for path, name in files:
        digest = sha256(path)
        if digest in by_hash:
            duplicates.append({"name": name, "duplicateOf": by_hash[digest]["name"], "sha256": digest})
            continue
        rom = parse_rom(path)
        title = classify_title(name)
        rel = os.path.relpath(path, library)
        record = {
            "name": name,
            "path": path,
            "relativePath": rel,
            "sha256": digest,
            **title,
            **rom,
        }
        record["mapperBand"] = mapper_band(record["mapper"])
        record["chrSizeBucket"] = size_bucket(record["chrKb"])
        record["prgSizeBucket"] = size_bucket(record["prgKb"])
        if not record["headerValid"]:
            invalid.append({"name": name, "sha256": digest, "error": rom.get("error")})
        by_hash[digest] = record
    records = sorted(by_hash.values(), key=lambda r: r["name"].lower())
    return files, records, duplicates, invalid


def make_splits(records, seed):
    """Freeze a 60/25/15 split. Related revisions share a split; strata keep the
    holdout representative of mapper/CHR/size complexity."""
    groups = {}
    for record in records:
        groups.setdefault(record["normalizedTitle"], []).append(record)
    strata = {}
    for key, members in groups.items():
        lead = members[0]
        stratum = (lead["mapperBand"], lead["chrType"], lead["chrSizeBucket"])
        strata.setdefault(stratum, []).append(key)
    assignment = {}
    for stratum, keys in sorted(strata.items()):
        keys.sort(key=lambda k: hashlib.sha256((str(seed) + "|" + k).encode()).hexdigest())
        n = len(keys)
        n_hold = max(1, round(n * 0.15)) if n >= 3 else 0
        n_val = max(1, round(n * 0.25)) if n >= 3 else (1 if n == 2 else 0)
        for i, key in enumerate(keys):
            if i < n_hold:
                split = "holdout"
            elif i < n_hold + n_val:
                split = "validation"
            else:
                split = "development"
            for member in groups[key]:
                assignment[member["sha256"]] = split
    return {
        "schemaVersion": "1.0.0",
        "seed": seed,
        "frozenAtUtc": __import__("datetime").datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
        "policy": "60/25/15; related revisions share a split; new post-freeze ROMs are development only",
        "assignment": assignment,
        "counts": {
            s: sum(1 for v in assignment.values() if v == s)
            for s in ("development", "validation", "holdout")
        },
        "groups": {k: [m["sha256"] for m in v] for k, v in sorted(groups.items())},
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--library", default=r"D:\NESGames\Library")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v06\inventory")
    ap.add_argument("--split-seed", type=int, default=20261126)
    ap.add_argument("--force-splits", action="store_true")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    files, records, duplicates, invalid = inventory(args.library)

    splits_path = os.path.join(args.out, "dataset-splits.json")
    frozen = None
    if os.path.exists(splits_path) and not args.force_splits:
        with open(splits_path, encoding="utf-8") as f:
            frozen = json.load(f)
        existing = frozen.get("assignment", {})
        for record in records:
            if record["sha256"] not in existing:
                existing[record["sha256"]] = "development"
                record["splitNewAfterFreeze"] = True
        frozen["assignment"] = existing
        frozen["counts"] = {s: sum(1 for v in existing.values() if v == s)
                            for s in ("development", "validation", "holdout")}
        frozen["newAfterFreeze"] = sum(1 for r in records if r.get("splitNewAfterFreeze"))
    else:
        frozen = make_splits(records, args.split_seed)
        with open(splits_path, "w", encoding="utf-8") as f:
            json.dump(frozen, f, indent=1)

    for record in records:
        record["split"] = frozen["assignment"].get(record["sha256"], "development")

    summary = {
        "library": os.path.abspath(args.library),
        "filesWithNesExtension": len(files),
        "uniqueRoms": len(records),
        "duplicateFiles": len(duplicates),
        "invalidHeaders": len(invalid),
        "baseTitles": len({r["normalizedTitle"] for r in records}),
        "regionalRevisions": len(records) - len({r["normalizedTitle"] for r in records}),
        "multicarts": sum(1 for r in records if r["isMulticart"]),
        "homebrewTagged": sum(1 for r in records if r["isHomebrew"]),
        "pirates": sum(1 for r in records if r["isPirate"]),
        "hacks": sum(1 for r in records if r["isHack"]),
        "chrRom": sum(1 for r in records if r["chrType"] == "CHR-ROM"),
        "chrRam": sum(1 for r in records if r["chrType"] == "CHR-RAM"),
        "mappers": {str(k): v for k, v in sorted(_counts(r["mapper"] for r in records).items(),
                                                  key=lambda kv: (-kv[1], str(kv[0])))},
        "splits": frozen["counts"],
        "seed": args.split_seed,
        "frozenAtUtc": frozen.get("frozenAtUtc"),
    }
    with open(os.path.join(args.out, "library-inventory.json"), "w", encoding="utf-8") as f:
        json.dump({"summary": summary, "roms": records, "duplicates": duplicates,
                   "invalid": invalid}, f, indent=1)
    with open(os.path.join(args.out, "dataset-splits.json"), "w", encoding="utf-8") as f:
        json.dump(frozen, f, indent=1)
    fields = ["name", "baseTitle", "normalizedTitle", "region", "revision", "flags",
              "sha256", "mapper", "submapper", "mapperBand", "chrType", "prgKb", "chrKb",
              "chrSizeBucket", "headerValid", "nes20", "isMulticart", "isHomebrew",
              "isPirate", "isHack", "split", "path"]
    with open(os.path.join(args.out, "library-index-v06.csv"), "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(records)
    with open(os.path.join(args.out, "inventory-summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2)
    print(json.dumps(summary, indent=2))
    return 0


def _counts(values):
    result = {}
    for value in values:
        result[value] = result.get(value, 0) + 1
    return result


if __name__ == "__main__":
    raise SystemExit(main())
