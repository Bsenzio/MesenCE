"""RetroArcDo HD Studio v0.9 - full-library combined replacement benchmark.

This is the final-engine benchmark, not a re-label of the historical v0.8
measurements. It uses the same deterministic capture and verification engine as
HD Studio, but replaces the slow per-asset import loop with a directly authored
standard Mesen pack built from observed runtime evidence:

  1. one deterministic capture per ROM (save-state Go-Explore input),
  2. select up to N distinct observed pattern+palette assets on/near the paused
     scene (sprites first, then rendered background blocks),
  3. author one combined marker pack with standard hires.txt entries,
  4. verify it against an empty HD pack at the same scale, at the exact paused
     frame, with the same input script and state hash,
  5. count changed pixels inside every observed asset footprint and outside all
     of them. Shared keys are reported, never hidden.

The result is resumable: each ROM writes result.json and the driver reuses a
compatible verified result unless --force is given.

Example:
  python scripts/hdstudio_v09_fullbench.py --out-root D:\\...\\fullbench \
      --workers 6 --assets 20 --hashes hashes.txt
"""
import argparse
import colorsys
import concurrent.futures
import json
import os
import re
import shutil
import sys
import time
from dataclasses import dataclass, field

from PIL import Image, ImageDraw
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hdstudio_v071_autoreplace as v71  # noqa: E402
import hdstudio_v08_autoreplace as v08  # noqa: E402


VERSION = "0.9-fullbench-1"
DEFAULT_BUDGETS = [900, 1800]
DEFAULT_PROFILES = ["default", "menu"]


def read_live_rows(path):
    rows = []
    if not os.path.exists(path):
        return rows
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if not line.strip() or line.startswith("#"):
                continue
            cols = line.rstrip("\n").split("\t")
            rows.append(cols)
    return rows


def choose_observed_assets(pack, pause_frame, limit, asset_kind="auto"):
    """Select distinct pattern+palette assets with concrete on-screen boxes."""
    sprites = []
    paused_backgrounds = []
    pause_path = os.path.join(pack, "HdStudio", "live", "pause-frame.json")
    if os.path.exists(pause_path):
        pause = v71.load_json(pause_path, {}) or {}
        for item in pause.get("sprites") or []:
            try:
                x = int(item.get("x")); y = int(item.get("y"))
                pattern = str(item.get("pattern") or "").strip()
                palette = str(item.get("palette") or "").strip().upper()
                chr_type = str(item.get("chrType") or "NesChrRom")
            except (TypeError, ValueError):
                continue
            if pattern and len(palette) == 8:
                sprites.append({"frame": pause_frame, "x": x, "y": y, "pattern": pattern,
                                "palette": palette, "chrType": chr_type, "kind": "sprite"})
        for item in pause.get("background") or []:
            try:
                x = int(item.get("x")); y = int(item.get("y"))
                pattern = str(item.get("pattern") or "").strip()
                palette = str(item.get("palette") or "").strip().upper()
                chr_type = str(item.get("chrType") or "NesChrRom")
            except (TypeError, ValueError):
                continue
            if pattern and len(palette) == 8:
                paused_backgrounds.append({"frame": pause_frame, "x": x, "y": y, "pattern": pattern,
                                           "palette": palette, "chrType": chr_type, "kind": "background"})
    for cols in read_live_rows(os.path.join(pack, "HdStudio", "live", "live-sprites.tsv")):
        if len(cols) < 11:
            continue
        try:
            frame = int(cols[0]); x = int(cols[3]); y = int(cols[4])
            pattern = cols[9].strip(); palette = cols[7].strip().upper()
            chr_type = cols[10].strip()
        except ValueError:
            continue
        if not pattern or len(palette) != 8:
            continue
        sprites.append({"frame": frame, "x": x, "y": y, "pattern": pattern,
                        "palette": palette, "chrType": chr_type, "kind": "sprite"})
    backgrounds = []
    bg_path = os.path.join(pack, "HdStudio", "live", "live-background-frames.tsv")
    for cols in read_live_rows(bg_path):
        if len(cols) < 10:
            continue
        try:
            frame = int(cols[0]); x = int(cols[1]); y = int(cols[2])
            pattern = cols[9].strip(); palette = cols[8].strip().upper()
            chr_type = cols[10].strip() if len(cols) > 10 else "NesChrRom"
        except ValueError:
            continue
        if not pattern or len(palette) != 8:
            continue
        backgrounds.append({"frame": frame, "x": x, "y": y, "pattern": pattern,
                            "palette": palette, "chrType": chr_type, "kind": "background"})
    backgrounds = paused_backgrounds + backgrounds
    # Prefer the paused frame; widen to nearby frames if the scene is not dense.
    if asset_kind == "background":
        sprites = []
    elif asset_kind == "sprite":
        backgrounds = []
    chosen = []
    seen = set()
    for window in (0, 30, 90, 180, 360):
        for kind_rows in (sprites, backgrounds):
            for row in kind_rows:
                if abs(row["frame"] - pause_frame) > window:
                    continue
                key = (row["pattern"].upper(), row["palette"].upper(), row["chrType"])
                if key in seen:
                    #Add another distinct footprint for a key already selected.
                    for asset in chosen:
                        if asset["key"] == key:
                            footprint = (row["frame"], row["x"], row["y"])
                            if footprint not in asset["_footprints"]:
                                asset["_footprints"].add(footprint)
                                asset["boxes"].append([row["x"], row["y"], 8 if row["kind"] == "sprite" else 16,
                                                       8 if row["kind"] == "sprite" else 16])
                            break
                    continue
                if row["kind"] == "sprite" and row["y"] >= 0xEF:
                    continue
                if row["x"] < 0 or row["x"] >= 256 or row["y"] < 0 or row["y"] >= 240:
                    continue
                seen.add(key)
                chosen.append({
                    "key": key,
                    "pattern": row["pattern"].upper(),
                    "palette": row["palette"].upper(),
                    "chrType": row["chrType"],
                    "kind": row["kind"],
                    "firstFrame": row["frame"],
                    "boxes": [[row["x"], row["y"], 8 if row["kind"] == "sprite" else 16,
                               8 if row["kind"] == "sprite" else 16]],
                    "_footprints": {(row["frame"], row["x"], row["y"])},
                })
                if len(chosen) >= limit:
                    return chosen
        if len(chosen) >= limit:
            break
    return chosen


def marker_color(index, total):
    hue = (index + 0.5) / max(1, total)
    r, g, b = colorsys.hsv_to_rgb(hue, 1.0, 1.0)
    #Keep colours away from pure black/white and from the NES grey ramp.
    return (int(40 + 200 * r), int(40 + 200 * g), int(40 + 200 * b), 255)


def author_pack(pack, rom_path, assets, scale):
    os.makedirs(pack, exist_ok=True)
    tile = 8 * scale
    width = max(1, len(assets)) * tile
    image = Image.new("RGBA", (width, tile), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    for index, asset in enumerate(assets):
        color = marker_color(index, len(assets))
        x0 = index * tile
        draw.rectangle([x0 + 1, 1, x0 + tile - 2, tile - 2], fill=color)
        #A small white notch makes each tile identifiable even if an emphasis
        #transform scales RGB uniformly.
        draw.rectangle([x0 + 3, 3, x0 + tile // 2, 6], fill=(255, 255, 255, 255))
        asset["markerColor"] = list(color[:3])
        asset["sheetX"] = index * tile
    image.save(os.path.join(pack, "benchmark-markers.png"))
    sha1_value = v08.sha1(rom_path)
    lines = ["<ver>109", "<scale>%d" % scale, "<supportedRom>%s" % sha1_value,
             "<img>benchmark-markers.png"]
    for asset in assets:
        lines.append("<tile>0,%s,%s,%d,0,1,N" % (
            asset["pattern"], asset["palette"], asset["sheetX"]))
    with open(os.path.join(pack, "hires.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def author_baseline(pack, rom_path, scale):
    os.makedirs(pack, exist_ok=True)
    sha1_value = v08.sha1(rom_path)
    with open(os.path.join(pack, "hires.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("<ver>109\n<scale>%d\n<supportedRom>%s\n" % (scale, sha1_value))


def load_hd(path):
    if not os.path.exists(path):
        return None
    return Image.open(path).convert("RGB")


def count_changed(target, original, boxes, scale):
    if target is None:
        return {"inside": 0, "outside": 0, "perBox": [0] * len(boxes), "available": False}
    if original is not None and original.size != target.size:
        original = original.resize(target.size, Image.Resampling.NEAREST)
    if original is not None and target.size == original.size:
        a = np.asarray(target, dtype=np.int16)
        b = np.asarray(original, dtype=np.int16)
        changed = np.any(a != b, axis=2)
    else:
        #Fallback: saturation-selected marker pixels. The original pass is
        #normally present in the same verification output.
        a = np.asarray(target, dtype=np.int16)
        mx = a.max(axis=2); mn = a.min(axis=2)
        changed = (mx >= 100) & ((mx - mn) >= 90)
    height, width = changed.shape
    ys, xs = np.indices((height, width))
    lx = xs // scale
    ly = ys // scale
    any_box = np.zeros((height, width), dtype=bool)
    per_box = []
    for bx, by, bw, bh in boxes:
        mask = (lx >= bx) & (lx < bx + bw) & (ly >= by) & (ly < by + bh)
        any_box |= mask
        per_box.append(int(np.count_nonzero(changed & mask)))
    return {
        "inside": int(np.count_nonzero(changed & any_box)),
        "outside": int(np.count_nonzero(changed & ~any_box)),
        "perBox": per_box,
        "available": True,
    }


def marker_pixels(target, asset, scale):
    if target is None or not asset.get("markerColor"):
        return 0
    arr = np.asarray(target, dtype=np.int16)
    r, g, b = asset["markerColor"]
    #Allow the emulator's emphasis/brightness transform: compare direction,
    #not one exact RGB triple.
    vr, vg, vb = max(r, 1), max(g, 1), max(b, 1)
    nr = arr[:, :, 0] / vr; ng = arr[:, :, 1] / vg; nb = arr[:, :, 2] / vb
    spread = np.maximum.reduce([nr, ng, nb]) - np.minimum.reduce([nr, ng, nb])
    mask = (spread < 0.22) & (np.maximum.reduce([nr, ng, nb]) > 0.55)
    count = 0
    for bx, by, bw, bh in asset["boxes"]:
        x0, y0 = bx * scale, by * scale
        x1, y1 = min(arr.shape[1], (bx + bw) * scale), min(arr.shape[0], (by + bh) * scale)
        if x1 <= x0 or y1 <= y0:
            continue
        count += int(np.count_nonzero(mask[y0:y1, x0:x1]))
    return count


def classify(assets, frame_result, hd_active, state_identical):
    if not hd_active:
        return "failed-hd-pack-inactive"
    if not state_identical:
        return "partial-visual-state-mismatch"
    present = [r for r in frame_result.get("perBox", []) if r >= 16]
    if not present:
        return "failed-not-replaced"
    if len(present) < len(assets):
        return "partial-assets-not-visible"
    if frame_result.get("outside", 0) > 0:
        return "verified-combined-shared-keys"
    return "verified-combined-independent"


@dataclass
class BenchConfig:
    mesen: str
    base_home: str
    out_root: str
    budgets: list
    profiles: list
    assets: int
    scale: int
    workers: int
    isolated_per_rom: int = 0
    force: bool = False
    short_work: bool = False
    live_timeout: int = 300
    verify_scenes: int = 1
    asset_kind: str = "auto"


def process_rom(rom, cfg, engine_fingerprint):
    stem = v71.safe_name(os.path.splitext(rom["name"])[0])
    work_name = rom["sha256"][:12] if cfg.short_work else rom["sha256"][:12] + "_" + stem
    work = os.path.join(cfg.out_root, work_name)
    os.makedirs(work, exist_ok=True)
    result_path = os.path.join(work, "result.json")
    previous = v71.load_json(result_path, {})
    if previous and not cfg.force and str(previous.get("status", "")).startswith("verified"):
        tc = previous.get("testConfig") or {}
        if (previous.get("engineFingerprint") == engine_fingerprint and
                tc.get("assets") == cfg.assets and tc.get("scale") == cfg.scale and
                tc.get("budgets") == cfg.budgets and tc.get("profiles") == [p[0] for p in cfg.profiles] and
                tc.get("assetKind", "auto") == cfg.asset_kind):
            previous["cacheHit"] = True
            return previous
    attempt_root = os.path.join(work, "attempt")
    if os.path.isdir(attempt_root) and cfg.force:
        shutil.rmtree(attempt_root, ignore_errors=True)
    os.makedirs(attempt_root, exist_ok=True)
    started = time.time()
    result = {
        "rom": rom["name"],
        "sha256": rom["sha256"],
        "path": rom["path"],
        "mapper": rom.get("mapper"),
        "chrType": "CHR-ROM" if rom.get("chrBytes", 0) else "CHR-RAM",
        "testConfig": {
            "assets": cfg.assets,
            "scale": cfg.scale,
            "budgets": cfg.budgets,
            "profiles": [p[0] for p in cfg.profiles],
            "isolatedPerRom": cfg.isolated_per_rom,
            "verificationScenes": cfg.verify_scenes,
            "assetKind": cfg.asset_kind,
        },
        "engineFingerprint": engine_fingerprint,
        "benchmarkVersion": VERSION,
    }
    #Capture one deterministic scene. v08 keeps the probe and profile logic.
    chosen = None
    capture = None
    used_profile = None
    used_script = None
    for profile_name, input_script in cfg.profiles:
        profile_root = os.path.join(attempt_root, "capture-" + profile_name)
        os.makedirs(profile_root, exist_ok=True)
        args = argparse.Namespace(
            budgets=cfg.budgets, scale=cfg.scale, live_timeout=cfg.live_timeout,
            base_home=cfg.base_home, mesen=cfg.mesen, profile=profile_name,
            input_script=input_script, target_mode="auto",
        )
        capture = v08.capture_adaptive(rom, args, {"binary": cfg.mesen, "home": cfg.base_home},
                                       profile_root, profile_name, input_script)
        chosen = capture.get("chosen")
        if chosen:
            used_profile = profile_name
            used_script = input_script
            break
    if not chosen:
        result["status"] = "failed-no-visible-scene"
        result["classification"] = v08.classify_no_target(capture.get("probes") or [], used_profile or "default",
                                                          capture.get("reasons")) if capture else "capture-failure"
        result["probes"] = capture.get("probes") if capture else []
        result["seconds"] = round(time.time() - started, 2)
        v71.atomic_json(result_path, result)
        return result
    pack = chosen["pack"]
    pause_frame = int(chosen["pause"]["frame"])
    assets = choose_observed_assets(pack, pause_frame, cfg.assets, cfg.asset_kind)
    result["observedScene"] = {
        "profile": used_profile,
        "pauseFrame": pause_frame,
        "chosenKind": chosen.get("kind"),
        "liveStatus": capture.get("liveStatus"),
        "assetsSelected": len(assets),
    }
    if not assets:
        result["status"] = "failed-no-observed-assets"
        result["seconds"] = round(time.time() - started, 2)
        v71.atomic_json(result_path, result)
        return result
    #One combined pack is verified at the exact paused frame with the same input
    #script. The verifier's own original-resolution pass is the no-art baseline;
    #it is the same emulation state, so no second empty-pack run is needed.
    bench_home = os.path.join(attempt_root, "benchmark-home")
    bench_pack = os.path.join(bench_home, "HdPacks", os.path.splitext(rom["name"])[0])
    v71.prepare_home(cfg.base_home, bench_home, os.path.splitext(rom["name"])[0])
    author_pack(bench_pack, rom["path"], assets, cfg.scale)
    engine_bench = {"binary": cfg.mesen, "home": bench_home}
    frames = [pause_frame]
    target_verify = v08.run_verify(engine_bench, rom, bench_pack, argparse.Namespace(scale=cfg.scale),
                                   frames, "combined", attempt_root, used_script)
    result["verify"] = {
        "hdActive": target_verify["hdActive"],
        "stateIdentical": target_verify["stateIdentical"],
        "targetSeconds": target_verify["seconds"],
        "exitCode": target_verify["exitCode"],
        "log": target_verify["output"][-1200:] if target_verify["exitCode"] != 0 else "",
    }
    target_path = os.path.join(target_verify["verifyDir"], "frame_%05d_hd.png" % pause_frame)
    original_path = os.path.join(target_verify["verifyDir"], "frame_%05d_original.png" % pause_frame)
    target_image = load_hd(target_path)
    original_image = load_hd(original_path)
    all_boxes = [box for asset in assets for box in asset["boxes"]]
    changed = count_changed(target_image, original_image, all_boxes, cfg.scale)
    per_asset = []
    for asset in assets:
        asset_changed = count_changed(target_image, original_image, asset["boxes"], cfg.scale)
        marker = marker_pixels(target_image, asset, cfg.scale)
        per_asset.append({
            "pattern": asset["pattern"],
            "palette": asset["palette"],
            "chrType": asset["chrType"],
            "kind": asset["kind"],
            "boxes": asset["boxes"],
            "changed": asset_changed["perBox"],
            "markerPixels": marker,
            "present": any(v >= 16 for v in asset_changed["perBox"]) or marker >= 16,
        })
    shared = sum(1 for a in assets if len(a["boxes"]) > 1)
    status = classify(assets, changed, target_verify["hdActive"], target_verify["stateIdentical"])
    result.update({
        "status": status,
        "assets": per_asset,
        "assetCount": len(assets),
        "multiOccurrenceAssets": shared,
        "changedInside": changed["inside"],
        "changedOutside": changed["outside"],
        "markerPixels": sum(a["markerPixels"] for a in per_asset),
        "presentAssets": sum(1 for a in per_asset if a["present"]),
        "originalRomIntegrity": "passed" if v71.sha256(rom["path"]) == rom["sha256"] else "failed",
        "evidence": {"targetFrame": target_path, "originalFrame": original_path},
    })
    if result["originalRomIntegrity"] != "passed":
        result["status"] = "failed-integrity"
    #Optional isolated packs for a bounded number of assets. This is the
    #isolated-vs-combined comparison; the full library uses the combined pack.
    if cfg.isolated_per_rom > 0:
        isolated = []
        for index, asset in enumerate(assets[:cfg.isolated_per_rom]):
            iso_home = os.path.join(attempt_root, "isolated-home-%d" % index)
            iso_pack = os.path.join(iso_home, "HdPacks", os.path.splitext(rom["name"])[0])
            v71.prepare_home(cfg.base_home, iso_home, os.path.splitext(rom["name"])[0])
            author_pack(iso_pack, rom["path"], [asset], cfg.scale)
            iso_verify = v08.run_verify({"binary": cfg.mesen, "home": iso_home}, rom, iso_pack,
                                        argparse.Namespace(scale=cfg.scale), frames,
                                        "isolated-%d" % index, attempt_root, used_script)
            iso_path = os.path.join(iso_verify["verifyDir"], "frame_%05d_hd.png" % pause_frame)
            iso_image = load_hd(iso_path)
            iso_changed = count_changed(iso_image, original_image, asset["boxes"], cfg.scale)
            isolated.append({
                "pattern": asset["pattern"],
                "palette": asset["palette"],
                "hdActive": iso_verify["hdActive"],
                "stateIdentical": iso_verify["stateIdentical"],
                "changed": iso_changed["perBox"],
                "present": any(v >= 16 for v in iso_changed["perBox"]),
            })
        result["isolated"] = isolated
    result["seconds"] = round(time.time() - started, 2)
    result["processedAtUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    v71.atomic_json(result_path, result)
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--base-home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--inventory", default=r"D:\MesenHighWork\validation-v09\inventory\library-inventory-v09.json")
    ap.add_argument("--out-root", default=r"D:\MesenHighWork\validation-v09\fullbench")
    ap.add_argument("--assets", type=int, default=20)
    ap.add_argument("--budgets", default="900;1800")
    ap.add_argument("--profiles", default="default")
    ap.add_argument("--scale", type=int, default=2)
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--hashes", default="")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--start", type=int, default=0)
    ap.add_argument("--isolated-per-rom", type=int, default=0)
    ap.add_argument("--asset-kind", choices=("auto", "sprite", "background"), default="auto")
    ap.add_argument("--live-timeout", type=int, default=300)
    ap.add_argument("--verify-scenes", type=int, default=1)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--short-work", action="store_true")
    args = ap.parse_args()
    os.makedirs(args.out_root, exist_ok=True)
    inv = v71.load_json(args.inventory, {}) or {}
    roms = [r for r in inv.get("roms", []) if r.get("headerValid", True)]
    seen = set()
    unique = []
    for rom in roms:
        sha = (rom.get("sha256") or "").lower()
        if not sha or sha in seen:
            continue
        seen.add(sha)
        unique.append(rom)
    if args.hashes:
        wanted = {x.strip().lower() for x in open(args.hashes, encoding="utf-8") if x.strip()}
        unique = [r for r in unique if r["sha256"].lower() in wanted]
    unique.sort(key=lambda r: r["name"].lower())
    if args.start:
        unique = unique[args.start:]
    if args.limit:
        unique = unique[:args.limit]
    budgets = sorted({int(x) for x in re.split(r"[;,]", args.budgets) if x.strip().isdigit()})
    profiles = [(p.strip().lower(), v08.DEFAULT_PROFILES[p.strip().lower()])
                for p in args.profiles.split(";") if p.strip().lower() in v08.DEFAULT_PROFILES]
    if not profiles:
        profiles = [("default", v08.DEFAULT_PROFILES["default"])]
    cfg = BenchConfig(args.mesen, args.base_home, args.out_root, budgets, profiles,
                      max(1, args.assets), args.scale, args.workers,
                      args.isolated_per_rom, args.force, args.short_work,
                      args.live_timeout, args.verify_scenes, args.asset_kind)
    engine_fingerprint = v71.sha256(args.mesen)[:16]
    print(json.dumps({"roms": len(unique), "workers": cfg.workers, "assets": cfg.assets,
                      "budgets": budgets, "profiles": [p[0] for p in profiles],
                      "engineFingerprint": engine_fingerprint}), flush=True)
    results = []
    lock = __import__("threading").Lock()
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, cfg.workers)) as pool:
        futures = {pool.submit(process_rom, rom, cfg, engine_fingerprint): rom for rom in unique}
        for future in concurrent.futures.as_completed(futures):
            rom = futures[future]
            try:
                result = future.result()
            except Exception as exc:
                result = {"rom": rom["name"], "sha256": rom["sha256"], "status": "failed-exception",
                          "failure": str(exc)}
            with lock:
                results.append(result)
                #Atomic, resumable master file after every completed ROM.
                tmp = os.path.join(args.out_root, "fullbench-results.json.tmp")
                v71.atomic_json(tmp, results)
                os.replace(tmp, os.path.join(args.out_root, "fullbench-results.json"))
                summary = summarize(results)
                v71.atomic_json(os.path.join(args.out_root, "fullbench-summary.json"), summary)
                print(json.dumps({"done": len(results), "total": len(unique),
                                  "rom": result.get("rom"), "status": result.get("status"),
                                  "verified": summary.get("verified")}), flush=True)
    summary = summarize(results)
    v71.atomic_json(os.path.join(args.out_root, "fullbench-summary.json"), summary)
    print(json.dumps(summary, indent=1))


def summarize(results):
    counts = {}
    for result in results:
        status = result.get("status", "unknown")
        counts[status] = counts.get(status, 0) + 1
    verified = sum(1 for r in results if str(r.get("status", "")).startswith("verified"))
    return {
        "attempted": len(results),
        "verified": verified,
        "statusCounts": counts,
        "assetsAttempted": sum(int(r.get("assetCount") or 0) for r in results),
        "assetsPresent": sum(int(r.get("presentAssets") or 0) for r in results),
        "changedInside": sum(int(r.get("changedInside") or 0) for r in results),
        "changedOutside": sum(int(r.get("changedOutside") or 0) for r in results),
        "multiOccurrenceAssets": sum(int(r.get("multiOccurrenceAssets") or 0) for r in results),
        "rowsWithCollateral": sum(1 for r in results if int(r.get("changedOutside") or 0) > 0),
        "stateIdenticalRows": sum(1 for r in results if (r.get("verify") or {}).get("stateIdentical")),
        "integrityPassed": sum(1 for r in results if r.get("originalRomIntegrity") == "passed"),
        "updatedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }


if __name__ == "__main__":
    main()

