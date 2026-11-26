"""RetroArcDo HD Studio v0.6 - resumable full-library Tier-1 smoke validation.

Runs the real headless Mesen capture (game-specific profiles disabled), exports
the generic catalog and records only measured fields. ROM bytes are hashed before
and after; the script never writes to the ROM.

The queue is resumable and cache-keyed by:
  ROM SHA-256 + engine binary SHA-256 + capture configuration
so a crashed or stopped run continues without repeating completed work.

Example:
  python scripts/hdstudio_v06_smoke.py --frames 300 --workers 6
  python scripts/hdstudio_v06_smoke.py --extend-zero --workers 5
"""
import argparse
import concurrent.futures
import csv
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time

FAIL_LOAD = "load-failed"
FAIL_CAPTURE = "capture-failed"
FAIL_TIMEOUT = "timeout"
FAIL_ZERO = "no-observed-graphics"
FAIL_EXCEPTION = "worker-exception"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def load_json(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def safe_name(value):
    value = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", value).strip()
    return value[:110] or "rom"


def atomic_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    os.replace(tmp, path)


class Engine:
    def __init__(self, binary, home):
        self.binary = binary
        self.home = home
        h = hashlib.sha256()
        for path in (binary, os.path.join(os.path.dirname(binary), "MesenCore.dll")):
            if os.path.exists(path):
                h.update(sha256(path).encode())
        self.fingerprint = h.hexdigest()[:16]
        self.version = "unknown"
        try:
            info = subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 f"(Get-Item '{binary}').VersionInfo.FileVersion"],
                capture_output=True, text=True, timeout=20)
            self.version = (info.stdout or "").strip() or "unknown"
        except Exception:
            pass


def config_key(frames, seed, scale, filter_name, bgscan, timeout):
    raw = f"frames={frames};seed={seed};scale={scale};filter={filter_name};bgscan={int(bgscan)};timeout={timeout}"
    return hashlib.sha256(raw.encode()).hexdigest()[:10], raw


def run_process(engine, args, timeout):
    env = os.environ.copy()
    env["MESEN_HOME"] = engine.home
    started = time.time()
    try:
        p = subprocess.run([engine.binary] + args, env=env, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, text=True, timeout=timeout,
                           encoding="utf-8", errors="replace")
        return p.returncode, p.stdout or "", round(time.time() - started, 2)
    except subprocess.TimeoutExpired as ex:
        out = ex.stdout or ""
        if isinstance(out, bytes):
            out = out.decode("utf-8", "replace")
        return None, out, round(time.time() - started, 2)


def parse_counts(folder, rom):
    report = load_json(os.path.join(folder, "HdStudio", "report.json"), {}) or {}
    cap = report.get("capture") or {}
    assets = []
    doc = load_json(os.path.join(folder, "HdStudio", "assets", "assets.json"), {}) or {}
    assets = doc.get("assets") or []
    counts = {}
    for asset in assets:
        kind = asset.get("kind") or "?"
        counts[kind] = counts.get(kind, 0) + 1
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
    observed = cap.get("chrTilesSeen")
    if observed is None and rom["chrType"] == "CHR-RAM":
        observed = len({t.get("tileData") for a in assets for t in a.get("tiles", [])
                        if t.get("tileData")})
    return report, cap, counts, occurrences, observed


def analyze(folder, rom, rom_before_hash, code, output, seconds, args, engine,
            config_id, config_text, frames):
    capture_ok = code == 0
    log_path = os.path.join(folder, "capture.log")
    log_text = ""
    if os.path.exists(log_path):
        log_text = open(log_path, encoding="utf-8", errors="replace").read()
    combined = (output or "") + "\n" + log_text

    assets_code, assets_output, assets_seconds = 0, "", 0.0
    if capture_ok:
        assets_code, assets_output, assets_seconds = run_process(
            engine,
            ["--hdstudio", rom["path"], "--assets", "--out=" + folder,
             "--log=" + os.path.join(folder, "assets.log")], 420)
    report, cap, counts, occurrences, observed = parse_counts(folder, rom)

    gameplay = "not determined"
    if "HD Studio explorer: gameplay detected" in combined:
        gameplay = "passed"
    elif "no gameplay signature" in combined:
        gameplay = "blocked"

    failure = ""
    if code is None:
        failure = FAIL_TIMEOUT
    elif not capture_ok:
        low = combined.lower()
        if "could not be loaded" in low or "unsupported" in low or "mapper" in low and "not supported" in low:
            failure = FAIL_LOAD
        else:
            failure = FAIL_CAPTURE
    elif not observed and not counts.get("metasprite") and not counts.get("backgroundBlock"):
        failure = FAIL_ZERO

    integrity = "passed" if code is None or sha256(rom["path"]) == rom_before_hash else "failed"
    if code is None:
        integrity = "passed" if sha256(rom["path"]) == rom_before_hash else "failed"

    result = {
        "romName": rom["name"],
        "romPath": os.path.abspath(rom["path"]),
        "sha256": rom["sha256"],
        "gameTitle": rom["title"],
        "baseTitle": rom.get("baseTitle"),
        "split": rom.get("split", "unassigned"),
        "mapper": rom["mapper"],
        "submapper": rom.get("submapper"),
        "chrType": rom["chrType"],
        "prgKb": rom["prgKb"],
        "chrKb": rom["chrKb"],
        "framesRequested": frames,
        "framesProcessed": cap.get("frames"),
        "captureCompleted": "passed" if capture_ok else "failed",
        "assetsExported": "passed" if assets_code == 0 else "failed",
        "gameplayReached": gameplay,
        "failureCategory": failure,
        "observedPatterns": observed,
        "chrCoveragePercent": cap.get("chrCoveragePercent"),
        "spriteOccurrences": occurrences,
        "reconstructedCompositions": counts.get("metasprite", 0),
        "candidateObjects": counts.get("semanticObject", 0) + counts.get("pickedObject", 0) + counts.get("manualComposition", 0),
        "animationRecords": counts.get("animationSequence", 0) + counts.get("animationFrame", 0),
        "backgroundCompositions": counts.get("backgroundBlock", 0) + counts.get("backgroundTile", 0) + counts.get("pickedBackground", 0),
        "rawPatterns": counts.get("rawPattern", 0),
        "spriteReplacementVerified": "not tested",
        "animationReplacementVerified": "not tested",
        "backgroundReplacementVerified": "not tested",
        "contextCorrectness": "not tested",
        "stockMesenCompatibility": "not tested",
        "originalRomIntegrity": integrity,
        "processingSeconds": round(seconds + assets_seconds, 2),
        "captureSeconds": seconds,
        "assetsSeconds": assets_seconds,
        "engineFingerprint": engine.fingerprint,
        "engineVersion": engine.version,
        "configKey": config_id,
        "config": config_text,
        "cacheHit": False,
        "packFolder": os.path.abspath(folder),
        "captureExitCode": code,
        "assetsExitCode": assets_code,
        "limitations": "Tier-1 smoke capture; gameplay detection depends on the generic explorer; no authored replacement was attempted.",
        "error": "" if capture_ok and assets_code == 0 else (combined or assets_output or "")[-2000:],
    }
    return result


def process(rom, args, engine, config_id, config_text, out_root, cache_root, attempt):
    stem = safe_name(os.path.splitext(rom["name"])[0])
    folder = os.path.join(out_root, rom["sha256"][:12] + "_" + stem)
    os.makedirs(folder, exist_ok=True)
    log_path = os.path.join(folder, "capture.log")
    before = sha256(rom["path"])
    capture_args = [
        "--hdstudio", rom["path"], "--generate", "--bgscan",
        "--frames=" + str(args.frames), "--timeout=" + str(args.timeout),
        "--seed=" + str(args.seed), "--scale=" + str(args.scale),
        "--filter=" + args.filter, "--out=" + folder, "--log=" + log_path,
    ]
    if args.no_bgscan:
        capture_args.remove("--bgscan")
    code, output, seconds = run_process(engine, capture_args, args.timeout + 90)
    result = analyze(folder, rom, before, code, output, seconds, args, engine,
                     config_id, config_text, args.frames)
    result["attempt"] = attempt
    result["cacheKey"] = f"{rom['sha256']}-{engine.fingerprint}-{config_id}"
    cache_path = os.path.join(cache_root, result["cacheKey"] + ".json")
    atomic_json(cache_path, result)
    return result


def write_csv(path, rows):
    fields = [
        "romName", "baseTitle", "split", "sha256", "mapper", "chrType", "prgKb", "chrKb",
        "framesRequested", "framesProcessed", "captureCompleted", "assetsExported",
        "gameplayReached", "failureCategory", "observedPatterns", "chrCoveragePercent",
        "spriteOccurrences", "reconstructedCompositions", "candidateObjects",
        "animationRecords", "backgroundCompositions", "rawPatterns",
        "spriteReplacementVerified", "animationReplacementVerified", "backgroundReplacementVerified",
        "originalRomIntegrity", "processingSeconds", "engineVersion", "limitations",
    ]
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def summary(rows, args, engine, config_id):
    return {
        "schemaVersion": "1.0.0",
        "engine": engine.binary,
        "engineVersion": engine.version,
        "engineFingerprint": engine.fingerprint,
        "configKey": config_id,
        "frames": args.frames,
        "workers": args.workers,
        "uniqueRomsAttempted": len(rows),
        "capturePassed": sum(r.get("captureCompleted") == "passed" for r in rows),
        "assetsPassed": sum(r.get("assetsExported") == "passed" for r in rows),
        "gameplayDetected": sum(r.get("gameplayReached") == "passed" for r in rows),
        "romIntegrityPassed": sum(r.get("originalRomIntegrity") == "passed" for r in rows),
        "zeroObservedGraphics": sum(r.get("failureCategory") == FAIL_ZERO for r in rows),
        "loadFailures": sum(r.get("failureCategory") == FAIL_LOAD for r in rows),
        "timeouts": sum(r.get("failureCategory") == FAIL_TIMEOUT for r in rows),
        "totalProcessingSeconds": round(sum(r.get("processingSeconds", 0) for r in rows), 1),
        "chrRom": sum(1 for r in rows if r.get("chrType") == "CHR-ROM"),
        "chrRam": sum(1 for r in rows if r.get("chrType") == "CHR-RAM"),
        "mappers": _mapper_counts(rows),
    }


def _mapper_counts(rows):
    counts = {}
    for row in rows:
        key = str(row.get("mapper"))
        counts[key] = counts.get(key, 0) + 1
    return dict(sorted(counts.items(), key=lambda kv: (-kv[1], kv[0])))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--library", default=r"D:\NESGames\Library")
    ap.add_argument("--inventory", default=r"D:\MesenHighWork\validation-v06\inventory\library-inventory.json")
    ap.add_argument("--out-root", default=r"D:\MesenHighWork\validation-v06\smoke")
    ap.add_argument("--frames", type=int, default=300)
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--seed", type=int, default=20261124)
    ap.add_argument("--scale", type=int, default=4)
    ap.add_argument("--filter", default="prescale")
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--selected", default="")
    ap.add_argument("--split", default="")
    ap.add_argument("--hash-file", default="", help="newline-separated SHA-256 values to include")
    ap.add_argument("--retry-failed", action="store_true")
    ap.add_argument("--retry-timeouts", action="store_true")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--no-bgscan", action="store_true")
    args = ap.parse_args()

    doc = load_json(args.inventory)
    if doc is None:
        raise SystemExit("inventory not found: run hdstudio_v06_inventory.py first")
    roms = doc["roms"]
    if args.selected:
        wanted = {v.strip().lower() for v in args.selected.split(";") if v.strip()}
        roms = [r for r in roms if r["name"].lower() in wanted]
    if args.split:
        roms = [r for r in roms if r.get("split") == args.split]
    if args.hash_file:
        with open(args.hash_file, encoding="utf-8") as f:
            wanted = {line.strip().lower() for line in f if line.strip()}
        roms = [r for r in roms if r["sha256"].lower() in wanted]
    if args.limit:
        roms = roms[:args.limit]

    os.makedirs(args.out_root, exist_ok=True)
    cache_root = os.path.join(args.out_root, "cache")
    os.makedirs(cache_root, exist_ok=True)
    engine = Engine(args.mesen, args.home)
    config_id, config_text = config_key(args.frames, args.seed, args.scale, args.filter,
                                        not args.no_bgscan, args.timeout)

    # Load cache and select the work queue.
    cached = {}
    for name in os.listdir(cache_root):
        if not name.endswith(".json"):
            continue
        item = load_json(os.path.join(cache_root, name))
        if (isinstance(item, dict) and item.get("cacheKey") == name[:-5]
                and item.get("engineFingerprint") == engine.fingerprint
                and item.get("configKey") == config_id):
            cached[item.get("sha256")] = item
    queue = []
    skipped = 0
    for rom in roms:
        item = cached.get(rom["sha256"])
        if item and not args.force:
            ok = item.get("captureCompleted") == "passed" and item.get("assetsExported") == "passed"
            if ok or (not args.retry_failed and item.get("failureCategory") != FAIL_TIMEOUT):
                skipped += 1
                continue
            if item.get("failureCategory") == FAIL_TIMEOUT and not args.retry_timeouts:
                skipped += 1
                continue
        queue.append(rom)

    print(json.dumps({
        "library": os.path.abspath(args.library),
        "uniqueRomsInFilter": len(roms),
        "queued": len(queue),
        "cachedSkipped": skipped,
        "frames": args.frames,
        "workers": args.workers,
        "engine": engine.version,
        "engineFingerprint": engine.fingerprint,
        "configKey": config_id,
        "output": os.path.abspath(args.out_root),
    }, indent=2), flush=True)

    results = [cached[r["sha256"]] for r in roms if r["sha256"] in cached]
    for item in results:
        item["cacheHit"] = True
    completed = 0
    queue_started = time.time()

    def work(rom):
        attempts = 0
        while True:
            attempts += 1
            result = process(rom, args, engine, config_id, config_text, args.out_root,
                             cache_root, attempts)
            retryable = result.get("failureCategory") in (FAIL_CAPTURE, FAIL_TIMEOUT) or result.get("assetsExported") != "passed"
            if not retryable or attempts >= 2 or (result.get("failureCategory") == FAIL_TIMEOUT and not args.retry_timeouts):
                return result
            time.sleep(2)

    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {pool.submit(work, rom): rom for rom in queue}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            rom = futures[future]
            try:
                result = future.result()
            except Exception as ex:
                result = {
                    "romName": rom["name"], "sha256": rom["sha256"],
                    "gameTitle": rom["title"], "mapper": rom.get("mapper"),
                    "chrType": rom.get("chrType"), "captureCompleted": "failed",
                    "assetsExported": "failed", "gameplayReached": "not determined",
                    "failureCategory": FAIL_EXCEPTION, "originalRomIntegrity": "not tested",
                    "processingSeconds": 0, "cacheHit": False,
                    "limitations": "worker exception", "error": str(ex),
                }
            results = [r for r in results if r.get("sha256") != result.get("sha256")]
            results.append(result)
            completed += 1
            elapsed = time.time() - queue_started
            rate = completed / elapsed if elapsed > 0 else 0
            remaining = len(queue) - completed
            eta = remaining / rate if rate > 0 else 0
            print("[%d/%d] %s -> %s %s (%ss, %.1f/s, ETA %.0f min)" % (
                completed, len(queue), rom["name"], result.get("captureCompleted"),
                result.get("failureCategory") or "", result.get("processingSeconds", 0),
                rate, eta / 60), flush=True)
            if completed % 5 == 0 or completed == len(queue):
                results.sort(key=lambda r: (r.get("romName") or "").lower())
                atomic_json(os.path.join(args.out_root, "smoke-results.partial.json"), results)
                atomic_json(os.path.join(args.out_root, "smoke-summary.partial.json"),
                            summary(results, args, engine, config_id))

    results.sort(key=lambda r: (r.get("romName") or "").lower())
    atomic_json(os.path.join(args.out_root, "smoke-results.json"), results)
    write_csv(os.path.join(args.out_root, "smoke-matrix.csv"), results)
    final = summary(results, args, engine, config_id)
    final["skippedFromCache"] = skipped
    final["cacheHitRows"] = sum(1 for r in results if r.get("cacheHit"))
    final["wallSecondsThisRun"] = round(time.time() - queue_started, 1)
    atomic_json(os.path.join(args.out_root, "smoke-summary.json"), final)
    print(json.dumps(final, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
