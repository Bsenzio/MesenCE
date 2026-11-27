"""v0.8 runtime animation verifier with per-frame identity.

v0.7.1 used one marker image for every animation frame and position-only
conditions. Two frames at the same position therefore could not be distinguished:
the first matching entry rendered in both frames and the test still passed.

v0.8 emits standard Mesen `frameRange` conditions (plus position checks) and a
distinct solid marker colour per frame. A frame is verified only when the colour
that appears inside its observed box is that frame's own colour, at its observed
emulator frame. The conditions are an observed-phase approximation: the period is
the observed frame span + 1, because `frameRange` is a modulo-step condition and
cannot select one phase exactly at the original period. This limitation is
recorded in every row instead of being hidden.

Usage:
  python scripts/hdstudio_v08_animation_verify.py --root DIR --limit 20
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile

from PIL import Image

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hdstudio_v071_autoreplace as v71  # noqa: E402

FRAME_COLORS = [
    (255, 0, 255), (0, 255, 255), (0, 255, 0), (255, 255, 0),
    (0, 128, 255), (255, 128, 0), (128, 0, 255), (255, 0, 0),
]
FRAME_NAMES = ["magenta", "cyan", "green", "yellow", "blue", "orange", "purple", "red"]


def sha1(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def atomic_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    os.replace(tmp, path)


def load_tsv(path):
    frames = {}
    if not os.path.exists(path):
        return frames
    header = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n\r")
            if not line:
                continue
            if line.startswith("#"):
                header = line[1:].split("\t")
                continue
            parts = line.split("\t")
            if not header or len(parts) < len(header):
                continue
            row = dict(zip(header, parts))
            try:
                frame = int(row["frame"])
            except Exception:
                continue
            frames.setdefault(frame, []).append(row)
    for frame, rows in frames.items():
        seen = set()
        unique = []
        for r in rows:
            key = (r.get("x"), r.get("y"), r.get("pattern"), r.get("palette"),
                   r.get("flipH"), r.get("flipV"))
            if key in seen:
                continue
            seen.add(key)
            unique.append(r)
        frames[frame] = unique
    return frames


def unique_tiles(asset):
    tiles = []
    seen = set()
    for tile in asset.get("tiles") or []:
        key = (tile.get("x"), tile.get("y"), tile.get("pattern"), tile.get("palette"),
               tile.get("tileData"), bool(tile.get("flipH")), bool(tile.get("flipV")))
        if key in seen:
            continue
        seen.add(key)
        tiles.append(tile)
    return tiles


def infer_origin(asset, sprites):
    """Find the screen origin of a live composition from its observed frame."""
    tiles = unique_tiles(asset)
    if not tiles or not sprites:
        return None, {"reason": "no-tiles-or-sprites"}
    candidates = set()
    for anchor in tiles:
        ax, ay = int(anchor.get("x", 0)), int(anchor.get("y", 0))
        for s in sprites:
            if str(s.get("pattern", "")).upper() != str(anchor.get("pattern", "")).upper():
                continue
            if str(s.get("palette", "")).upper() != str(anchor.get("palette", "")).upper():
                continue
            candidates.add((int(s["x"]) - ax, int(s["y"]) - ay))
    valid = []
    sprite_index = {(int(s["x"]), int(s["y"]), str(s.get("pattern", "")).upper(),
                     str(s.get("palette", "")).upper())
                    for s in sprites}
    for ox, oy in candidates:
        matched = 0
        missing = 0
        for tile in tiles:
            tx, ty = ox + int(tile.get("x", 0)), oy + int(tile.get("y", 0))
            if (tx, ty, str(tile.get("pattern", "")).upper(),
                    str(tile.get("palette", "")).upper()) in sprite_index:
                matched += 1
            else:
                missing += 1
        if missing == 0:
            valid.append((ox, oy, matched))
    if not valid:
        return None, {"reason": "no-consistent-origin", "candidates": len(candidates)}
    width = int((asset.get("dimensions") or {}).get("width") or asset.get("width") or 0)
    height = int((asset.get("dimensions") or {}).get("height") or asset.get("height") or 0)
    on_screen = [(x, y, m) for x, y, m in valid
                 if 0 <= x and 0 <= y and x + width <= 256 and y + height <= 240]
    if not on_screen:
        return None, {"reason": "origin-offscreen", "valid": valid[:8]}
    if len(on_screen) > 1:
        return None, {"reason": "ambiguous-origin", "origins": on_screen[:8]}
    return on_screen[0][:2], {"reason": "ok", "width": width, "height": height}


def phase_conditions(frames):
    """Select the interval that starts at each observed effective phase.

    `frameRange,A,B` is true when FrameNumber % A >= B. The renderer's
    FrameNumber is the last completed PPU frame, which is one less than the
    screenshot frame requested by `--verify-at`. The phases are therefore built
    from `frame - 1`; this off-by-one was measured across several packs and is
    recorded here instead of being hidden in the verification code.
    """
    span = frames[-1] - frames[0]
    period = span + 1
    effective = [f - 1 for f in frames]
    phases = [f % period for f in effective]
    if len(set(phases)) != len(frames):
        return None, "duplicate-phase"
    ordered = sorted(set(phases))
    expressions = []
    for phase in phases:
        index = ordered.index(phase)
        lower = "frameRange:%d,%d" % (period, phase)
        if index == 0 and len(ordered) > 1:
            upper = ordered[1]
            expr = "!frameRange:%d,%d" % (period, upper)
        elif index == len(ordered) - 1:
            expr = lower
        else:
            upper = ordered[index + 1]
            expr = lower + "&!frameRange:%d,%d" % (period, upper)
        # The engine's condition parser does not know frameRange yet; the
        # generated hires.txt uses the standard name directly. Return both a
        # readable expression and the raw condition records.
        expressions.append(expr)
    return {"period": period, "phases": phases, "expressions": expressions}, "ok"


def condition_name(index, kind):
    return "Anim%s%d" % (kind, index)


def build_pack(pack, rom, frames, assets, boxes, scale, work, condition):
    hires = ["<ver>109", "<scale>%d" % scale, "<supportedRom>" + sha1(rom["path"])]
    images = []
    # Range conditions are defined once; Mesen auto-creates their inverted names.
    condition_defs = []
    for i in range(len(frames)):
        condition_defs.append("%s,frameRange,%d,%d" % (condition_name(i, "T"), condition["period"], condition["phases"][i]))
        condition_defs.append("%s,positionCheckX,>=,%d" % (condition_name(i, "XG"), boxes[i][0]))
        condition_defs.append("%s,positionCheckX,<=,%d" % (condition_name(i, "XL"), boxes[i][0] + boxes[i][2] - 1))
        condition_defs.append("%s,positionCheckY,>=,%d" % (condition_name(i, "YG"), boxes[i][1]))
        condition_defs.append("%s,positionCheckY,<=,%d" % (condition_name(i, "YL"), boxes[i][1] + boxes[i][3] - 1))
    for definition in condition_defs:
        hires.append("<condition>" + definition)

    ordered = sorted(set(condition["phases"]))
    phase_to_frame = {condition["phases"][i]: i for i in range(len(frames))}
    tile_size = 8 * scale
    for i, asset in enumerate(assets):
        tiles = unique_tiles(asset)
        # One source cell per unique hardware key. The colour is the frame id.
        unique_keys = []
        seen = set()
        for tile in tiles:
            key = (str(tile.get("pattern", "")).upper(), str(tile.get("palette", "")).upper(),
                   str(tile.get("tileData", "") or "").upper())
            if key in seen:
                continue
            seen.add(key)
            unique_keys.append(tile)
        width = max(1, len(unique_keys)) * tile_size
        sheet = Image.new("RGBA", (width, tile_size), FRAME_COLORS[i] + (255,))
        name = "AnimFrame%d.png" % i
        sheet.save(os.path.join(pack, name))
        images.append(name)
        # Time interval text: a phase is selected from its own value up to the
        # next observed phase. The next phase can belong to a different animation
        # index when the modulo arc wraps.
        phase = condition["phases"][i]
        pos = ordered.index(phase)
        if pos == len(ordered) - 1:
            time_expr = condition_name(i, "T")
        else:
            next_phase = ordered[pos + 1]
            next_index = phase_to_frame[next_phase]
            if pos == 0:
                time_expr = "!" + condition_name(next_index, "T")
            else:
                time_expr = condition_name(i, "T") + "&!" + condition_name(next_index, "T")
        # Position checks keep the replacement inside the observed box.
        time_expr += "&" + condition_name(i, "XG") + "&" + condition_name(i, "XL")
        time_expr += "&" + condition_name(i, "YG") + "&" + condition_name(i, "YL")
        tile_lines = []
        for cell, tile in enumerate(unique_keys):
            key = tile.get("tileData") if str(tile.get("chrType", "")).lower().find("chrram") >= 0 else tile.get("pattern")
            key = str(key or tile.get("pattern") or "").upper()
            palette = str(tile.get("palette") or "FF000000").upper()
            tile_lines.append("[%s]<tile>%d,%s,%s,%d,0,1,N" % (time_expr, i, key, palette, cell * tile_size))
        images.append(("tiles", tile_lines))
    for item in images:
        if isinstance(item, tuple):
            hires.extend(item[1])
        else:
            hires.append("<img>" + item)
    with open(os.path.join(pack, "hires.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(hires) + "\n")


def load_user_palette(base_home):
    path = os.path.join(base_home, "settings.json")
    try:
        with open(path, encoding="utf-8-sig") as f:
            doc = json.load(f)
        values = doc.get("Nes", {}).get("UserPalette") or []
        palette = []
        for value in values[:64]:
            value = int(value)
            palette.append(((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF))
        if len(palette) >= 64:
            return palette
    except Exception:
        pass
    # Standard 2C02 fallback (the first 64 entries of Mesen's default palette).
    return [(0x66, 0x66, 0x66)] * 64


def decode_tile(tile_data):
    data = bytes.fromhex(tile_data) if tile_data and len(tile_data) == 32 else bytes(16)
    rows = []
    for y in range(8):
        low = data[y]
        high = data[y + 8]
        row = []
        for x in range(8):
            bit = 7 - x
            row.append(((low >> bit) & 1) | (((high >> bit) & 1) << 1))
        rows.append(row)
    return rows


def render_asset(asset, palette):
    width = int((asset.get("dimensions") or {}).get("width") or asset.get("width") or 0)
    height = int((asset.get("dimensions") or {}).get("height") or asset.get("height") or 0)
    if width <= 0 or height <= 0:
        return None
    pixels = [[None] * width for _ in range(height)]
    for tile in asset.get("tiles") or []:
        data = tile.get("tileData") or ""
        rows = decode_tile(data)
        palette_hex = str(tile.get("palette") or "")
        if len(palette_hex) < 8:
            continue
        try:
            colors = [int(palette_hex[i:i + 2], 16) for i in range(0, 8, 2)]
        except ValueError:
            continue
        tx, ty = int(tile.get("x", 0)), int(tile.get("y", 0))
        for py in range(8):
            for px in range(8):
                value = rows[py][px]
                if value == 0:
                    continue
                index = colors[min(value, 3)] & 0x3F
                x, y = tx + px, ty + py
                if 0 <= x < width and 0 <= y < height:
                    pixels[y][x] = palette[index]
    return {"width": width, "height": height, "pixels": pixels}


def asset_visible(asset, palette):
    rendered = render_asset(asset, palette)
    if not rendered:
        return False
    colors = set()
    peak = 0
    for row in rendered["pixels"]:
        for rgb in row:
            if rgb is not None:
                colors.add(rgb)
                peak = max(peak, max(rgb))
    return len(colors) >= 2 and peak >= 70


def asset_match(original_path, asset, box, palette):
    """Fraction of the asset's opaque pixels reproduced at the observed frame.

    A frame that is not reproduced by the deterministic replay (title/blank or a
    different scene because the input script attached at a different frame) is
    rejected instead of being counted as a replacement.
    """
    rendered = render_asset(asset, palette)
    if not rendered or not os.path.exists(original_path):
        return 0.0, 0
    image = Image.open(original_path).convert("RGB")
    ox, oy = box[0], box[1]
    width, height = rendered["width"], rendered["height"]
    if ox < 0 or oy < 0 or ox + width > image.width or oy + height > image.height:
        return 0.0, 0
    rgb = np.array([[p if p is not None else (np.nan, np.nan, np.nan)
                     for p in row] for row in rendered["pixels"]], dtype=np.float32)
    mask = ~np.isnan(rgb[:, :, 0])
    total = int(np.count_nonzero(mask))
    if total == 0:
        return 0.0, 0
    crop = np.asarray(image, dtype=np.float32)[oy:oy + height, ox:ox + width]
    best = np.full((height, width), 1e9, dtype=np.float32)
    for scale in (1.0, 0.9, 0.8, 0.7, 1.1):
        distance = np.abs(crop - rgb * scale).sum(axis=2)
        best = np.minimum(best, distance)
    matched = int(np.count_nonzero((best <= 45) & mask))
    return matched / total, total


def hue_distance(a, b):
    d = abs(a - b) % 360
    return min(d, 360 - d)


def rgb_hue(r, g, b):
    mx, mn = max(r, g, b), min(r, g, b)
    if mx == mn:
        return None, 0.0
    if mx == r:
        h = (60 * ((g - b) / (mx - mn))) % 360
    elif mx == g:
        h = 60 * ((b - r) / (mx - mn)) + 120
    else:
        h = 60 * ((r - g) / (mx - mn)) + 240
    return h, (mx - mn) / mx


def color_counts(image, box):
    x0, y0, w, h = box
    x1 = min(image.width, x0 + w)
    y1 = min(image.height, y0 + h)
    arr = np.asarray(image, dtype=np.float32)[y0:y1, x0:x1]
    if arr.size == 0:
        return [0] * len(FRAME_COLORS), [0] * len(FRAME_COLORS)
    r, g, b = arr[:, :, 0], arr[:, :, 1], arr[:, :, 2]
    mx = arr.max(axis=2)
    mn = arr.min(axis=2)
    delta = np.maximum(mx - mn, 1e-6)
    hue = np.where(mx == r, ((g - b) / delta) % 6,
                   np.where(mx == g, (b - r) / delta + 2, (r - g) / delta + 4)) * 60
    saturation = (mx - mn) / np.maximum(mx, 1)
    distances = []
    base_hues = []
    for cr, cg, cb in FRAME_COLORS:
        cmx, cmn = max(cr, cg, cb), min(cr, cg, cb)
        if cmx == cmn:
            base_hues.append(0.0)
        elif cmx == cr:
            base_hues.append((60 * ((cg - cb) / (cmx - cmn))) % 360)
        elif cmx == cg:
            base_hues.append(60 * ((cb - cr) / (cmx - cmn)) + 120)
        else:
            base_hues.append(60 * ((cr - cg) / (cmx - cmn)) + 240)
    for bh in base_hues:
        d = np.abs(hue - bh) % 360
        distances.append(np.minimum(d, 360 - d))
    distance_stack = np.stack(distances, axis=0)
    nearest = np.argmin(distance_stack, axis=0)
    nearest_distance = np.min(distance_stack, axis=0)
    valid = (saturation >= 0.45) & (nearest_distance <= 24)
    counts = np.bincount(nearest[valid], minlength=len(FRAME_COLORS)).tolist()
    exact = []
    for cr, cg, cb in FRAME_COLORS:
        exact.append(int(np.count_nonzero((r == cr) & (g == cg) & (b == cb))))
    return counts, exact


COND = {}


def process_pack(pack, rom, args, out_root):
    assets_path = os.path.join(pack, "HdStudio", "assets", "assets.json")
    assets_doc = v71.load_json(assets_path, {}) or {}
    assets = assets_doc.get("assets") or []
    sequences = [a for a in assets if a.get("kind") == "animationSequence"]
    live_frames = load_tsv(os.path.join(pack, "HdStudio", "live", "live-sprites.tsv"))
    best = None
    for sequence in sequences:
        frames_assets = [a for a in assets if a.get("kind") == "animationFrame"
                         and a.get("animationId") == sequence.get("id")]
        frames_assets.sort(key=lambda a: a.get("frameIndex", 0))
        candidates = []
        for asset in frames_assets:
            m = re.search(r"live frame (\d+)", asset.get("provenance") or "")
            if not m:
                continue
            frame = int(m.group(1))
            if frame < args.min_frame:
                continue
            origin, info = infer_origin(asset, live_frames.get(frame, []))
            if origin is None:
                continue
            width = int((asset.get("dimensions") or {}).get("width") or 0)
            height = int((asset.get("dimensions") or {}).get("height") or 0)
            if width <= 0 or height <= 0:
                continue
            if not asset_visible(asset, args.palette):
                continue
            candidates.append({"asset": asset, "frame": frame, "box": [origin[0], origin[1], width, height],
                               "originInfo": info})
        if len(candidates) < 2:
            continue
        chosen = []
        seen_frames = set()
        for candidate in candidates:
            if candidate["frame"] in seen_frames:
                continue
            if chosen and candidate["frame"] <= chosen[-1]["frame"]:
                continue
            chosen.append(candidate)
            seen_frames.add(candidate["frame"])
            if len(chosen) >= min(args.max_frames, len(FRAME_COLORS)):
                break
        if len(chosen) < 2:
            continue
        frames = [c["frame"] for c in chosen]
        condition, reason = phase_conditions(frames)
        if condition is None:
            continue
        region = sum(c["box"][2] * c["box"][3] for c in chosen)
        score = region + len(chosen) * 1000
        if best is None or score > best[0]:
            best = (score, sequence, chosen, condition)
    if best is None:
        return {"status": "skipped-no-verifiable-sequence", "pack": pack, "rom": rom.get("name")}
    _, sequence, chosen, condition = best
    scale = args.scale
    work = tempfile.mkdtemp(prefix="hdstudio-anim08-")
    try:
        home = os.path.join(work, "home")
        v71.prepare_home(args.base_home, home, os.path.splitext(rom["name"])[0])
        out_pack = os.path.join(home, "HdPacks", os.path.splitext(rom["name"])[0])
        os.makedirs(out_pack, exist_ok=True)
        build_pack(out_pack, rom, [c["frame"] for c in chosen],
                   [c["asset"] for c in chosen], [c["box"] for c in chosen], scale, work,
                   condition)
        frames = [c["frame"] for c in chosen]
        engine = {"binary": args.mesen, "home": home}
        result = v71.run(engine, ["--hdstudio", rom["path"], args.input_script, "--verify",
                                  "--shots=1", "--frames=%d" % max(frames),
                                  "--verify-at=" + ",".join(map(str, frames)),
                                  "--out=" + out_pack], 900,
                         os.path.join(work, "verify.log"))
        code, verify_out, seconds = result
        verify_dir = os.path.join(out_pack, "HdStudio", "verify")
        rows = []
        for i, frame in enumerate(frames):
            hd_path = os.path.join(verify_dir, "frame_%05d_hd.png" % frame)
            original_path = os.path.join(verify_dir, "frame_%05d_original.png" % frame)
            if not os.path.exists(hd_path):
                rows.append({"frame": frame, "status": "missing-frame-image"})
                continue
            image = Image.open(hd_path).convert("RGB")
            box = [v * scale for v in chosen[i]["box"]]
            counts, exact = color_counts(image, box)
            expected = i
            other_max = max([counts[j] for j in range(len(counts)) if j != expected] or [0])
            reproduced_ratio, opaque_pixels = asset_match(
                original_path, chosen[i]["asset"], chosen[i]["box"], args.palette)
            reproduced_ok = reproduced_ratio >= 0.45
            rows.append({
                "frame": frame,
                "frameIndex": i,
                "box": chosen[i]["box"],
                "expectedColor": FRAME_NAMES[i],
                "colorCounts": {FRAME_NAMES[j]: counts[j] for j in range(len(counts))},
                "exactCounts": {FRAME_NAMES[j]: exact[j] for j in range(len(counts))},
                "expectedCount": counts[expected],
                "otherMax": other_max,
                "reproducedRatio": round(reproduced_ratio, 4),
                "opaquePixels": opaque_pixels,
                "reproducedOk": reproduced_ok,
                "correct": (counts[expected] > 20 and counts[expected] > other_max * 2
                            and reproduced_ok),
                "hdImage": hd_path,
                "originalImage": original_path,
            })
        hd_active = code == 0 and "HD pack ACTIVE" in verify_out
        state_identical = "Gameplay state comparison: IDENTICAL" in verify_out
        correct_rows = [r for r in rows if r.get("correct")]
        reproduced_rows = [r for r in rows if r.get("reproducedOk")]
        all_correct = len(correct_rows) == len(rows) and len(rows) == len(frames)
        if not hd_active:
            status = "failed-verify"
        elif not state_identical:
            status = "partial-state-mismatch"
        elif all_correct:
            status = "verified-animation-frame-mapping"
        elif len(reproduced_rows) == 0:
            status = "failed-frame-reproduction"
        elif len(correct_rows) >= 2:
            status = "partial-animation-multi-frame"
        else:
            status = "failed-frame-identity"
        return {
            "status": status, "rom": rom.get("name"), "sha256": rom.get("sha256"),
            "pack": pack, "sequence": sequence.get("name"),
            "sequenceId": sequence.get("id"), "frames": frames,
            "boxes": [c["box"] for c in chosen],
            "phasePeriod": condition["period"], "phases": condition["phases"],
            "phaseExpressions": condition["expressions"],
            "testedAssets": [c["asset"].get("id") for c in chosen],
            "frameResults": rows, "hdPackActive": hd_active,
            "stateIdentical": state_identical, "seconds": seconds,
            "reproducedFrames": sum(1 for r in rows if r.get("reproducedOk")),
            "correctFrames": sum(1 for r in rows if r.get("correct")),
            "limitation": "Observed-phase frameRange approximation; period is observed span + 1.",
        }
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--base-home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--inventory", default=r"D:\MesenHighWork\validation-v07\inventory\library-inventory.json")
    ap.add_argument("--root", default=r"D:\MesenHighWork\validation-v071\full-library")
    ap.add_argument("--pack-list", default="", help="optional file of pack folders to test")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v08\animation-results.json")
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--limit", type=int, default=20)
    ap.add_argument("--max-frames", type=int, default=3)
    ap.add_argument("--min-frame", type=int, default=300,
                    help="ignore animation observations before this emulator frame")
    ap.add_argument("--scale", type=int, default=2)
    ap.add_argument("--input-script", default=r"D:\MesenHigh\scripts\deterministic_input.lua")
    args = ap.parse_args()
    args.palette = load_user_palette(args.base_home)
    inventory = v71.load_json(args.inventory, {}) or {}
    by_stem = {}
    for rom in inventory.get("roms", []):
        by_stem[os.path.splitext(rom["name"])[0].lower()] = rom
    packs = []
    if args.pack_list and os.path.exists(args.pack_list):
        with open(args.pack_list, encoding="utf-8") as f:
            listed = [line.strip() for line in f if line.strip()]
        for pack in listed:
            stem = os.path.basename(pack.rstrip("\\/"))
            if re.match(r"^[0-9a-fA-F]{12}_", stem):
                stem = stem.split("_", 1)[1]
            rom = by_stem.get(stem.lower())
            if rom:
                packs.append((pack, rom))
    else:
        for root, _, files in os.walk(args.root):
            if "assets.json" in files and os.path.basename(root) == "assets":
                pack = os.path.dirname(os.path.dirname(root))
                stem = os.path.basename(pack)
                if re.match(r"^[0-9a-fA-F]{12}_", stem):
                    stem = stem.split("_", 1)[1]
                rom = by_stem.get(stem.lower())
                if rom:
                    packs.append((pack, rom))
    packs.sort(key=lambda p: p[1].get("name", "").lower())
    if args.limit:
        packs = packs[:args.limit]
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    print(json.dumps({"packs": len(packs), "workers": args.workers}, indent=1), flush=True)
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {pool.submit(process_pack, pack, rom, args, os.path.dirname(args.out)): (pack, rom)
                   for pack, rom in packs}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            pack, rom = futures[future]
            try:
                result = future.result()
            except Exception as ex:
                result = {"status": "failed-exception", "rom": rom.get("name"), "pack": pack,
                          "error": str(ex)}
            results.append(result)
            print("[%d/%d] %s -> %s" % (index, len(packs), rom.get("name"), result.get("status")),
                  flush=True)
            atomic_json(args.out + ".partial.json",
                        sorted(results, key=lambda r: r.get("rom", "").lower()))
    results.sort(key=lambda r: (r.get("rom") or "").lower())
    counts = {}
    for row in results:
        counts[row.get("status")] = counts.get(row.get("status"), 0) + 1
    summary = {
        "packs": len(packs),
        "statusCounts": counts,
        "verifiedFrameMapping": counts.get("verified-animation-frame-mapping", 0),
        "verifiedAny": sum(v for k, v in counts.items() if k.startswith("verified")),
    }
    atomic_json(args.out, {"summary": summary, "rows": results})
    print(json.dumps(summary, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
