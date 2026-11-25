"""Full-library NES smoke validation for RetroArcDo HD Studio v0.5.

Reads the canonical library tree, de-duplicates by SHA-256, runs a real headless
Mesen capture with game-specific profiles disabled, exports the generic asset
catalog, and records only measured fields. It never writes to a ROM.

This is a Tier-1 compatibility pass: 300 frames by default. It establishes ROM
loading, mapper/CHR handling, capture completion and broad graphical observation.
It is not a semantic bestiary and does not claim authored replacement success.

Example:
  python scripts/validate_library_v05.py --library "D:\\NESGames\\Library" \
    --out-root "D:\\MesenHighWork\\validation-v05\\smoke" --frames 300 --workers 4
"""
import argparse
import concurrent.futures
import csv
import hashlib
import json
import os
import re
import struct
import subprocess
import time


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_ines(path):
    with open(path, "rb") as f:
        header = f.read(16)
    if len(header) < 16 or header[0:4] != b"NES\x1a":
        return {
            "mapper": None, "chrType": "unknown", "prgKb": None, "chrKb": None,
            "ines": False, "nes20": False,
        }
    prg = header[4] * 16
    chr_ = header[5] * 8
    mapper = (header[6] >> 4) | (header[7] & 0xF0)
    nes20 = (header[7] & 0x0C) == 0x08
    if nes20:
        mapper |= (header[8] & 0x0F) << 8
        # NES 2.0 exponent-multiplier sizes are rare in this library; keep the
        # classic size fields when they are non-zero and mark them as classic.
    return {
        "mapper": mapper,
        "chrType": "CHR-RAM" if chr_ == 0 else "CHR-ROM",
        "prgKb": prg,
        "chrKb": chr_,
        "ines": True,
        "nes20": nes20,
    }


def safe_name(value):
    value = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", value).strip()
    return value[:120] or "rom"


def load_json(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def run(mesen, home, args, timeout):
    env = os.environ.copy()
    env["MESEN_HOME"] = home
    started = time.time()
    try:
        p = subprocess.run([mesen] + args, env=env, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, text=True, timeout=timeout)
        return p.returncode, p.stdout, round(time.time() - started, 2)
    except subprocess.TimeoutExpired as ex:
        out = ex.stdout or ""
        if isinstance(out, bytes):
            out = out.decode("utf-8", "replace")
        return None, out, round(time.time() - started, 2)


def inventory(library):
    entries = []
    seen = {}
    for root, _, files in os.walk(library):
        for name in sorted(files):
            if not name.lower().endswith(".nes"):
                continue
            path = os.path.join(root, name)
            digest = sha256(path)
            if digest in seen:
                continue
            seen[digest] = path
            header = parse_ines(path)
            entries.append({
                "path": path,
                "name": name,
                "title": os.path.splitext(name)[0],
                "sha256": digest,
                **header,
            })
    entries.sort(key=lambda e: e["name"].lower())
    return entries


def process(rom, args):
    stem = safe_name(os.path.splitext(rom["name"])[0])
    folder = os.path.join(args.out_root, rom["sha256"][:12] + "_" + stem)
    os.makedirs(folder, exist_ok=True)
    log_path = os.path.join(folder, "capture.log")
    capture_args = [
        "--hdstudio", rom["path"], "--generate", "--bgscan",
        "--frames=" + str(args.frames), "--timeout=" + str(args.timeout),
        "--seed=" + str(args.seed), "--out=" + folder, "--log=" + log_path,
    ]
    code, output, seconds = run(args.mesen, args.home, capture_args, args.timeout + 90)
    capture_ok = code == 0
    log_text = ""
    if os.path.exists(log_path):
        try:
            log_text = open(log_path, encoding="utf-8", errors="replace").read()
        except Exception:
            pass
    combined = output + "\n" + log_text
    gameplay = "not determined"
    if "HD Studio explorer: gameplay detected" in combined:
        gameplay = "passed"
    elif "no gameplay signature" in combined:
        gameplay = "blocked"

    assets_code, assets_output, assets_seconds = run(
        args.mesen, args.home,
        ["--hdstudio", rom["path"], "--assets", "--out=" + folder,
         "--log=" + os.path.join(folder, "assets.log")], 420)
    report = load_json(os.path.join(folder, "HdStudio", "report.json")) or {}
    assets_doc = load_json(os.path.join(folder, "HdStudio", "assets", "assets.json")) or {}
    cap = report.get("capture") or {}
    assets = assets_doc.get("assets") or []
    counts = {}
    for asset in assets:
        kind = asset.get("kind") or "?"
        counts[kind] = counts.get(kind, 0) + 1

    #Composition occurrence count from the raw objects table; this is a count of
    #observed composition instances per sampled frame, not raw OAM rows.
    occurrences = 0
    objects_path = os.path.join(folder, "HdStudio", "objects.txt")
    if os.path.exists(objects_path):
        with open(objects_path, encoding="utf-8", errors="replace") as f:
            for line in f:
                if not line or line.startswith("#"):
                    continue
                first = line.split("\t", 1)[0]
                if first.isdigit():
                    occurrences += int(first)

    candidate_objects = sum(counts.get(k, 0) for k in ("semanticObject", "pickedObject", "manualComposition"))
    animations = sum(counts.get(k, 0) for k in ("animationSequence", "animationFrame"))
    backgrounds = sum(counts.get(k, 0) for k in ("backgroundBlock", "backgroundTile", "pickedBackground"))
    observed_patterns = cap.get("chrTilesSeen")
    if observed_patterns is None and rom["chrType"] == "CHR-RAM":
        observed_patterns = len({t.get("tileData") for a in assets for t in a.get("tiles", [])
                                 if t.get("tileData")})
    result = {
        "romName": rom["name"],
        "romPath": os.path.abspath(rom["path"]),
        "sha256": rom["sha256"],
        "gameTitle": rom["title"],
        "mapper": rom["mapper"] if rom["mapper"] is not None else (report.get("rom") or {}).get("mapper"),
        "chrType": rom["chrType"],
        "prgKb": rom["prgKb"],
        "chrKb": rom["chrKb"],
        "nes20": rom["nes20"],
        "captureCompleted": "passed" if capture_ok else "failed",
        "assetsExported": "passed" if assets_code == 0 else "failed",
        "gameplayReached": gameplay,
        "framesProcessed": cap.get("frames"),
        "observedPatterns": observed_patterns,
        "spriteTilePaletteCombos": cap.get("seenSpriteCombos"),
        "spriteOccurrences": occurrences,
        "reconstructedCompositions": counts.get("metasprite", 0),
        "candidateObjects": candidate_objects,
        "animationRecords": animations,
        "backgroundCompositions": backgrounds,
        "spriteReplacementVerified": "not tested",
        "animationReplacementVerified": "not tested",
        "backgroundReplacementVerified": "not tested",
        "contextCorrectness": "not tested",
        "stockMesenCompatibility": "not tested",
        "originalRomIntegrity": "passed" if sha256(rom["path"]) == rom["sha256"] else "failed",
        "processingSeconds": round(seconds + assets_seconds, 2),
        "captureSeconds": seconds,
        "assetsSeconds": assets_seconds,
        "limitations": (
            "Tier-1 smoke capture only; gameplay detection depends on the generic explorer; "
            "no authored replacement was attempted."
        ),
        "packFolder": os.path.abspath(folder),
        "captureExitCode": code,
        "assetsExitCode": assets_code,
        "error": "" if capture_ok and assets_code == 0 else (combined or assets_output)[-2000:],
    }
    return result


def write_csv(path, rows):
    fields = [
        "romName", "sha256", "gameTitle", "mapper", "chrType", "captureCompleted",
        "gameplayReached", "framesProcessed", "observedPatterns", "spriteOccurrences",
        "reconstructedCompositions", "candidateObjects", "animationRecords",
        "backgroundCompositions", "spriteReplacementVerified", "animationReplacementVerified",
        "backgroundReplacementVerified", "contextCorrectness", "stockMesenCompatibility",
        "originalRomIntegrity", "processingSeconds", "limitations",
    ]
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--library", default=r"D:\NESGames\Library")
    ap.add_argument("--out-root", default=r"D:\MesenHighWork\validation-v05\smoke")
    ap.add_argument("--frames", type=int, default=300)
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--seed", type=int, default=20261124)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--selected", default="", help="semicolon-separated ROM filename exact matches")
    args = ap.parse_args()

    os.makedirs(args.out_root, exist_ok=True)
    roms = inventory(args.library)
    if args.selected:
        wanted = {v.strip().lower() for v in args.selected.split(";") if v.strip()}
        roms = [r for r in roms if r["name"].lower() in wanted]
    if args.limit:
        roms = roms[:args.limit]
    print(json.dumps({
        "library": os.path.abspath(args.library),
        "uniqueRoms": len(roms),
        "frames": args.frames,
        "workers": args.workers,
    }, indent=2), flush=True)

    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {pool.submit(process, rom, args): rom for rom in roms}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            rom = futures[future]
            try:
                result = future.result()
            except Exception as ex:
                result = {
                    "romName": rom["name"], "sha256": rom["sha256"],
                    "gameTitle": rom["title"], "mapper": rom.get("mapper"),
                    "chrType": rom.get("chrType"), "captureCompleted": "failed",
                    "gameplayReached": "not determined", "originalRomIntegrity": "not tested",
                    "processingSeconds": 0, "limitations": "validation worker exception",
                    "error": str(ex),
                }
            results.append(result)
            print("[%d/%d] %s -> %s (%ss)" % (
                index, len(roms), rom["name"], result.get("captureCompleted"),
                result.get("processingSeconds")), flush=True)
            with open(os.path.join(args.out_root, "smoke-results.partial.json"), "w", encoding="utf-8") as f:
                json.dump(sorted(results, key=lambda r: r.get("romName", "").lower()), f, indent=1)

    results.sort(key=lambda r: r.get("romName", "").lower())
    out_json = os.path.join(args.out_root, "smoke-results.json")
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    write_csv(os.path.join(args.out_root, "smoke-matrix.csv"), results)
    summary = {
        "uniqueRoms": len(roms),
        "capturePassed": sum(r.get("captureCompleted") == "passed" for r in results),
        "assetsPassed": sum(r.get("assetsExported") == "passed" for r in results),
        "gameplayDetected": sum(r.get("gameplayReached") == "passed" for r in results),
        "romIntegrityPassed": sum(r.get("originalRomIntegrity") == "passed" for r in results),
        "totalSeconds": round(sum(r.get("processingSeconds", 0) for r in results), 1),
        "frames": args.frames,
    }
    with open(os.path.join(args.out_root, "smoke-summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2)
    print(json.dumps(summary, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
