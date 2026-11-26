"""RetroArcDo HD Studio v0.6 - automated authored-replacement benchmark.

For each selected ROM the script:
  1. runs a live HD capture with a deterministic, resolution-independent input script;
  2. pauses at a gameplay frame and saves the renderer's real visible sprite list;
  3. picks a visible composition through the hardware-aware picker;
  4. imports deterministic, high-contrast test artwork and compiles a standard pack;
  5. replays the SAME deterministic input to the SAME frame with the pack on/off
     (exact-scene verification, not an approximate checkpoint);
  6. measures marker pixels, target-frame scene match, changed pixels, HD state
     hashes and ROM integrity;
  7. classifies independent / shared / target-absent / not-replaced / partial.

Evidence is local. Commercial ROM bytes and packs are never committed.

Example:
  python scripts/hdstudio_v06_autoreplace.py --limit 12 --workers 3
"""
import argparse
import concurrent.futures
import csv
import hashlib
import itertools
import json
import os
import re
import shutil
import subprocess
import time

from PIL import Image, ImageDraw

MARKER_COLORS = {(255, 0, 255), (0, 255, 255), (0, 255, 0)}


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


def atomic_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    os.replace(tmp, path)


def safe_name(value):
    value = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", value).strip()
    return value[:100] or "rom"


def run(engine, args, timeout, log_path=None):
    env = os.environ.copy()
    env["MESEN_HOME"] = engine["home"]
    started = time.time()
    out = ""
    if log_path:
        with open(log_path, "w", encoding="utf-8", errors="replace") as log:
            try:
                p = subprocess.run([engine["binary"]] + args, env=env, stdout=log,
                                   stderr=subprocess.STDOUT, timeout=timeout)
                code = p.returncode
            except subprocess.TimeoutExpired:
                code = None
        try:
            out = open(log_path, encoding="utf-8", errors="replace").read()
        except Exception:
            out = ""
    else:
        try:
            p = subprocess.run([engine["binary"]] + args, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, timeout=timeout, text=True,
                               encoding="utf-8", errors="replace")
            code, out = p.returncode, p.stdout or ""
        except subprocess.TimeoutExpired as ex:
            code = None
            out = (ex.stdout or "")
            if isinstance(out, bytes):
                out = out.decode("utf-8", "replace")
    return code, out, round(time.time() - started, 2)


def make_marker(path):
    """Opaque asymmetric marker. Magenta/cyan/lime are used for exact detection."""
    size = 64
    im = Image.new("RGBA", (size, size), (255, 0, 255, 255))
    d = ImageDraw.Draw(im)
    d.rectangle([4, 4, size - 5, size - 5], fill=(0, 255, 255, 255))
    d.rectangle([16, 12, 44, 22], fill=(0, 255, 0, 255))
    d.rectangle([12, 34, 24, 52], fill=(0, 255, 0, 255))
    d.rectangle([36, 36, 52, 48], fill=(255, 0, 255, 255))
    im.save(path)


def prepare_home(base_home, target_home, rom_stem):
    os.makedirs(os.path.join(target_home, "HdPacks", rom_stem), exist_ok=True)
    for name in ("settings.json", "MesenNesDB.txt"):
        src = os.path.join(base_home, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(target_home, name))
    os.makedirs(os.path.join(target_home, "SaveStates"), exist_ok=True)


def composition_components(sprites):
    """Same conservative geometry as the Studio picker: overlap, exact 8x16
    vertical half, or horizontal pair with adjacent tiles and equal palette."""
    n = len(sprites)
    parent = list(range(n))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[rb] = ra

    for i in range(n):
        a = sprites[i]
        for j in range(i + 1, n):
            b = sprites[j]
            overlap = a["x"] < b["x"] + 8 and b["x"] < a["x"] + 8 and a["y"] < b["y"] + 8 and b["y"] < a["y"] + 8
            vertical = a["x"] == b["x"] and abs(a["y"] - b["y"]) == 8 and a["palette"] == b["palette"] and abs(a["tile"] - b["tile"]) == 1
            tile_delta = abs(a["tile"] - b["tile"])
            horizontal = a["y"] == b["y"] and abs(a["x"] - b["x"]) == 8 and a["palette"] == b["palette"] and 0 < tile_delta <= 2
            if overlap or vertical or horizontal:
                union(i, j)
    groups = {}
    for i, sprite in enumerate(sprites):
        groups.setdefault(find(i), []).append(sprite)
    result = []
    for members in groups.values():
        x0 = min(s["x"] for s in members)
        y0 = min(s["y"] for s in members)
        x1 = max(s["x"] + 8 for s in members)
        y1 = max(s["y"] + 8 for s in members)
        nonblank = any(re.sub(r"[^0-9A-Fa-f]", "", s.get("data") or "") not in ("", "0" * len(re.sub(r"[^0-9A-Fa-f]", "", s.get("data") or ""))) for s in members)
        result.append({
            "sprites": members,
            "x": x0, "y": y0, "width": x1 - x0, "height": y1 - y0,
            "area": (x1 - x0) * (y1 - y0),
            "nonblank": nonblank,
            "point": (members[0]["x"] + 4, members[0]["y"] + 4),
        })
    return result


def choose_component(components):
    candidates = [c for c in components if c["nonblank"] and c["width"] <= 128 and c["height"] <= 128]
    if not candidates:
        return None
    # Prefer a size that behaves like a character/object, then more observed parts.
    def score(c):
        size_penalty = 0 if c["area"] <= 4096 else (c["area"] - 4096) * 4
        hud_penalty = 60 if c["y"] < 24 else 0
        return c["area"] + len(c["sprites"]) * 12 - size_penalty - hud_penalty
    return max(candidates, key=score)


def decode_tile(data):
    """Decode 32 hex digits into 8x8 NES colour indices (0-3)."""
    data = re.sub(r"[^0-9A-Fa-f]", "", data or "")
    if len(data) != 32:
        return None
    raw = bytes.fromhex(data)
    rows = []
    for y in range(8):
        p0 = raw[y]
        p1 = raw[y + 8]
        row = []
        for x in range(8):
            bit = 7 - x
            row.append(((p1 >> bit) & 1) << 1 | ((p0 >> bit) & 1))
        rows.append(row)
    return rows


def tile_visible(screenshot, x, y, data):
    """True when the 8x8 screenshot at x,y is this tile under some palette mapping.

    This uses the actual displayed pixels instead of trusting a sampled nametable
    coordinate. It therefore detects scroll/mirroring/one-frame offset errors and
    refuses to pick a block that is not really at the recorded screen position.
    """
    if screenshot is None or x < 0 or y < 0 or x + 8 > screenshot.width or y + 8 > screenshot.height:
        return False
    rows = decode_tile(data)
    if rows is None:
        return False
    colors = [screenshot.getpixel((x + sx, y + sy)) for sy in range(8) for sx in range(8)]
    unique = list(dict.fromkeys(colors))
    if len(unique) > 4:
        return False
    used = sorted({v for row in rows for v in row})
    if len(used) > len(unique):
        return False
    for mapping in itertools.permutations(unique, len(used)):
        table = dict(zip(used, mapping))
        ok = True
        for sy in range(8):
            for sx in range(8):
                if table[rows[sy][sx]] != colors[sy * 8 + sx]:
                    ok = False
                    break
            if not ok:
                break
        if ok:
            return True
    return False


def choose_background_block(pause, screenshot):
    cells = pause.get("background") or []
    sprites = pause.get("sprites") or []

    def sprite_covered(x, y):
        return any(s["x"] < x + 8 and x < s["x"] + 8 and s["y"] < y + 8 and y < s["y"] + 8 for s in sprites)

    blocks = {}
    for cell in cells:
        tx, ty = cell.get("tileX"), cell.get("tileY")
        if tx is None or ty is None:
            continue
        bx, by = (tx // 2) * 2, (ty // 2) * 2
        blocks.setdefault((bx, by), []).append(cell)
    candidates = []
    singles = []
    for (bx, by), members in blocks.items():
        if len(members) != 4:
            continue
        if len({m.get("palette") for m in members}) != 1:
            continue
        visible = []
        nonblank = 0
        for m in members:
            data = re.sub(r"[^0-9A-Fa-f]", "", m.get("data") or "")
            is_nonblank = bool(data) and data != "0" * len(data)
            if is_nonblank:
                nonblank += 1
            if is_nonblank and not sprite_covered(m["x"], m["y"]) and tile_visible(screenshot, m["x"], m["y"], data):
                visible.append(m)
        x0 = min(m["x"] for m in members)
        y0 = min(m["y"] for m in members)
        if len(visible) >= 2 and nonblank >= 2:
            candidates.append({"kind": "backgroundBlock", "x": x0, "y": y0,
                               "width": 16, "height": 16, "area": 256,
                               "nonblank": True, "nonblankCells": nonblank,
                               "visibleCells": len(visible),
                               "point": ((visible[0]["x"] + 4), (visible[0]["y"] + 4)),
                               "cells": members,
                               "score": len(visible) * 100 + nonblank * 20 + (0 if y0 < 24 else 40)})
        for m in visible:
            singles.append({"kind": "backgroundTile", "x": m["x"], "y": m["y"],
                            "width": 8, "height": 8, "area": 64,
                            "nonblank": True, "visibleCells": 1,
                            "point": (m["x"] + 4, m["y"] + 4),
                            "cells": [m],
                            "score": 100 + (0 if m["y"] < 24 else 20)})
    if candidates:
        return max(candidates, key=lambda c: c["score"])
    return max(singles, key=lambda c: c["score"]) if singles else None


def parse_pick_candidates(text):
    result = []
    pattern = re.compile(r"^\s*\[(\d+)\]\s+([A-Za-z]+):\s+.*?(\d+)x(\d+),\s+(\d+)\s+sprite")
    for line in text.splitlines():
        m = pattern.search(line)
        if m:
            result.append({"index": int(m.group(1)), "kind": m.group(2),
                           "width": int(m.group(3)), "height": int(m.group(4)),
                           "sprites": int(m.group(5)), "line": line.strip()})
    return result


def downsample_hd(hd, size):
    # Each HD cell is a 4x4 block. Use the block center to undo any filter ringing.
    return hd.resize(size, Image.Resampling.NEAREST)


def compare_images(pause_path, original_path, hd_path, bbox, scale):
    result = {
        "pauseFrameAvailable": bool(pause_path and os.path.exists(pause_path)),
        "sceneMatchRatio": None, "sceneExact": None,
        "markerPixelsInside": 0, "markerPixelsOutside": 0,
        "changedPixelsInside": 0, "changedPixelsOutside": 0,
        "targetBBox": list(bbox) if bbox else None,
    }
    if not original_path or not hd_path or not os.path.exists(original_path) or not os.path.exists(hd_path):
        return result
    original = Image.open(original_path).convert("RGBA")
    hd = Image.open(hd_path).convert("RGBA")
    if pause_path and os.path.exists(pause_path):
        pause = Image.open(pause_path).convert("RGBA")
        if pause.size == original.size:
            a = pause.load()
            b = original.load()
            same = 0
            total = pause.width * pause.height
            for y in range(pause.height):
                for x in range(pause.width):
                    if a[x, y][:3] == b[x, y][:3]:
                        same += 1
            result["sceneMatchRatio"] = round(same / total, 6) if total else 0
            result["sceneExact"] = same == total
    down = downsample_hd(hd, original.size)
    hp = hd.load()
    dp = down.load()
    op = original.load()
    ox0, oy0, ox1, oy1 = bbox if bbox else (0, 0, original.width, original.height)
    sx, sy = scale, scale
    for y in range(hd.height):
        for x in range(hd.width):
            r, g, b, a = hp[x, y]
            if (r, g, b) in MARKER_COLORS:
                inside = ox0 * sx <= x < ox1 * sx and oy0 * sy <= y < oy1 * sy
                if inside:
                    result["markerPixelsInside"] += 1
                else:
                    result["markerPixelsOutside"] += 1
    for y in range(original.height):
        for x in range(original.width):
            if dp[x, y][:3] != op[x, y][:3]:
                if ox0 <= x < ox1 and oy0 <= y < oy1:
                    result["changedPixelsInside"] += 1
                else:
                    result["changedPixelsOutside"] += 1
    return result


def analyze_live_frame(pause, components):
    return {
        "frame": pause.get("frame"),
        "spriteParts": len(pause.get("sprites") or []),
        "backgroundCells": len(pause.get("background") or []),
        "components": len(components),
    }


def process_rom(rom, args, engine, marker_path, engine_fingerprint):
    stem = safe_name(os.path.splitext(rom["name"])[0])
    work = os.path.join(args.out_root, rom["sha256"][:12] + "_" + stem)
    os.makedirs(work, exist_ok=True)
    result_path = os.path.join(work, "result.json")
    previous = load_json(result_path, {})
    if previous and previous.get("status", "").startswith("verified") and not args.force:
        previous["cacheHit"] = True
        return previous

    rom_before = sha256(rom["path"])
    attempts = []
    target_frames = []
    for value in args.target_frames.split(";"):
        value = value.strip().lower()
        if value == "auto":
            target_frames.append(0)  # adaptive: wait for active gameplay frames
        elif value:
            target_frames.append(int(value))
    if not target_frames:
        target_frames = [1200]
    final = None
    for attempt_index, target_frame in enumerate(target_frames, 1):
        attempt_root = os.path.join(work, "attempt%d" % attempt_index)
        if os.path.isdir(attempt_root):
            shutil.rmtree(attempt_root, ignore_errors=True)
        home = os.path.join(attempt_root, "home")
        pack = os.path.join(home, "HdPacks", os.path.splitext(rom["name"])[0])
        os.makedirs(os.path.join(pack, "HdStudio", "live"), exist_ok=True)
        prepare_home(args.base_home, home, os.path.splitext(rom["name"])[0])
        engine_attempt = dict(engine)
        engine_attempt["home"] = home
        control = os.path.join(pack, "HdStudio", "live", "control.txt")
        open(control, "w", encoding="utf-8").write("0|resume\n")
        live_log = os.path.join(attempt_root, "live.log")
        args_live = [
            "--hdstudio", rom["path"], args.input_script, "--live", "--live-mode=balanced",
            "--generate", "--frames=100000", "--timeout=" + str(args.live_timeout),
            "--seed=20261124", "--out=" + pack, "--log=" + live_log,
        ]
        started = time.time()
        env = os.environ.copy()
        env["MESEN_HOME"] = home
        with open(os.path.join(attempt_root, "live-stdout.log"), "w", encoding="utf-8") as stdout:
            proc = subprocess.Popen([args.mesen] + args_live, env=env, stdout=stdout,
                                    stderr=subprocess.STDOUT)
            status_path = os.path.join(pack, "HdStudio", "live", "live-status.json")
            pause_path = os.path.join(pack, "HdStudio", "live", "pause-frame.json")
            paused = False
            stop_written = False
            command_id = 1
            deadline = time.time() + args.live_timeout + 30
            while time.time() < deadline and proc.poll() is None:
                status = load_json(status_path, {}) or {}
                frame = status.get("frame", 0)
                ready = False
                if target_frame <= 0:
                    #Adaptive: wait until the live evidence has active sprite frames
                    #rather than assuming that frame 1200 is gameplay for every game.
                    ready = (frame >= 900 and status.get("samples", 0) >= 20
                             and status.get("gameplayFrames", 0) >= 30) or frame >= 3600
                else:
                    ready = frame >= target_frame and status.get("samples", 0) > 0
                if not paused and ready:
                    open(control, "w", encoding="utf-8").write("%d|pause\n" % command_id)
                    paused = True
                if paused:
                    status = load_json(status_path, {}) or {}
                    if status.get("paused") and os.path.exists(pause_path):
                        open(control, "w", encoding="utf-8").write("%d|stop\n" % (command_id + 1))
                        stop_written = True
                        break
                time.sleep(0.2)
            if not stop_written and proc.poll() is None:
                open(control, "w", encoding="utf-8").write("%d|stop\n" % (command_id + 2))
            if proc.poll() is None:
                # The paused live frame and the TSV evidence are already flushed. The
                # recorder's wildcard pack is not used for replacement validation: we
                # compile an isolated minimal pack below, so stop the process instead
                # of waiting for a long capture timeout.
                time.sleep(1.0)
                proc.terminate()
            try:
                proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=10)
        live_seconds = round(time.time() - started, 2)
        pause = load_json(pause_path, {}) or {}
        live_status = load_json(os.path.join(pack, "HdStudio", "live", "live-status.json"), {}) or {}
        components = composition_components(pause.get("sprites") or []) if pause else []
        pause_shot = os.path.join(pack, "HdStudio", "live", pause.get("screenshot") or "") if pause else ""
        shot_img = None
        if pause_shot and os.path.exists(pause_shot):
            try:
                shot_img = Image.open(pause_shot).convert("RGB")
            except Exception:
                shot_img = None
        chosen = choose_component(components) if args.target_mode in ("sprite", "auto") else None
        if chosen is None and args.target_mode in ("background", "auto"):
            chosen = choose_background_block(pause, shot_img) if pause else None
        attempt = {
            "attempt": attempt_index,
            "targetFrame": target_frame,
            "pauseFrame": pause.get("frame"),
            "pauseInfo": analyze_live_frame(pause, components),
            "liveSeconds": live_seconds,
            "liveExitCode": proc.returncode,
            "liveActiveFrames": live_status.get("gameplayFrames"),
            "liveSamples": live_status.get("samples"),
            "chosenComponent": None,
        }
        attempts.append(attempt)
        if not chosen:
            attempt["failure"] = "no-visible-nonblank-sprite"
            final = attempt
            continue

        point = chosen["point"]
        code, candidates_text, seconds = run(
            engine_attempt, ["--hdstudio", "--pick=%d,%d" % point, "--out=" + pack],
            120, os.path.join(attempt_root, "pick.log"))
        candidates = parse_pick_candidates(candidates_text)
        if code != 0 or not candidates:
            attempt["failure"] = "picker-no-candidates"
            attempt["pickLog"] = candidates_text[-1000:]
            final = attempt
            continue
        # Prefer the reconstructed composition/block that covers the chosen component.
        if chosen.get("kind") == "backgroundBlock":
            choice = next((c for c in candidates if c["kind"] == "backgroundBlock"), None)
            if choice is None:
                choice = next((c for c in candidates if c["kind"] == "backgroundTile"), None)
        elif chosen.get("kind") == "backgroundTile":
            choice = next((c for c in candidates if c["kind"] == "backgroundTile"), None)
        else:
            choice = next((c for c in candidates if c["kind"] == "metasprite" and
                           abs(c["width"] - chosen["width"]) <= 8 and abs(c["height"] - chosen["height"]) <= 8), None)
            if choice is None:
                choice = next((c for c in candidates if c["kind"] == "metasprite"), None)
        if choice is None:
            choice = candidates[0]
        code, pick_out, pick_seconds = run(
            engine_attempt, ["--hdstudio", "--pick=%d,%d" % point,
                             "--pick-save=%d" % choice["index"],
                             "--pick-name=AutoTest", "--out=" + pack],
            120, os.path.join(attempt_root, "pick-save.log"))
        if code != 0:
            attempt["failure"] = "pick-save-failed"
            attempt["pickLog"] = pick_out[-1000:]
            final = attempt
            continue

        code, assets_out, assets_seconds = run(
            engine_attempt, ["--hdstudio", rom["path"], "--assets", "--out=" + pack,
                             "--log=" + os.path.join(attempt_root, "assets.log")], 420)
        if code != 0:
            attempt["failure"] = "assets-export-failed"
            attempt["assetsLog"] = assets_out[-1000:]
            final = attempt
            continue
        assets_doc = load_json(os.path.join(pack, "HdStudio", "assets", "assets.json"), {}) or {}
        picked = [a for a in assets_doc.get("assets", []) if (a.get("id") or "").startswith("picked:")]
        picked = [a for a in picked if (a.get("provenance") or "").find("AutoTest") >= 0 or a.get("name") == "AutoTest"]
        if not picked:
            picked = [a for a in assets_doc.get("assets", []) if (a.get("id") or "").startswith("picked:")]
        if not picked:
            attempt["failure"] = "picked-asset-missing"
            final = attempt
            continue
        asset = picked[-1]
        if not asset.get("tiles"):
            attempt["failure"] = "picked-asset-has-no-tiles"
            final = attempt
            continue
        dims = asset.get("dimensions") or {}
        width = dims.get("width") or asset.get("width") or 8
        height = dims.get("height") or asset.get("height") or 8
        origin_x = dims.get("originX", asset.get("originX", 0))
        origin_y = dims.get("originY", asset.get("originY", 0))
        if width > 128 or height > 128:
            attempt["failure"] = "picked-asset-too-large"
            final = attempt
            continue
        bbox = (origin_x, origin_y, origin_x + width, origin_y + height)
        attempt.update({
            "targetMode": args.target_mode,
            "assetId": asset.get("id"), "assetKind": asset.get("kind"),
            "assetWidth": width, "assetHeight": height, "assetTiles": len(asset.get("tiles") or []),
            "originX": bbox[0], "originY": bbox[1], "point": point,
            "candidate": choice,
            "chosenComponent": {k: chosen[k] for k in ("x", "y", "width", "height", "area")},
        })

        # Isolated authored pack: remove any partially recorded wildcard pack, keep
        # the HdStudio evidence folder, and let --minimal write only the test cells.
        for name in os.listdir(pack):
            path = os.path.join(pack, name)
            if os.path.isfile(path):
                try:
                    os.remove(path)
                except OSError:
                    pass
        marker_name = "AutoTest_" + rom["sha256"][:8]
        code, import_out, import_seconds = run(
            engine_attempt, ["--hdstudio", rom["path"], "--import",
                             "--asset=" + asset["id"], "--image=" + marker_path,
                             "--fit=stretch", "--all-palettes", "--mirror-conditions",
                             "--minimal", "--import-name=" + marker_name, "--out=" + pack],
            600, os.path.join(attempt_root, "import.log"))
        shared = re.search(r"also used by (\d+) other captured asset", import_out)
        attempt["sharedAssetCount"] = int(shared.group(1)) if shared else 0
        attempt["importSeconds"] = import_seconds
        if code != 0:
            attempt["failure"] = "import-compile-failed"
            attempt["importLog"] = import_out[-1500:]
            final = attempt
            continue

        exact_frame = int(pause.get("frame") or target_frame)
        code, verify_out, verify_seconds = run(
            engine_attempt, ["--hdstudio", rom["path"], args.input_script, "--verify",
                             "--shots=1", "--frames=%d" % max(exact_frame, 1),
                             "--verify-at=%d" % exact_frame, "--out=" + pack],
            600, os.path.join(attempt_root, "verify.log"))
        attempt["verifySeconds"] = verify_seconds
        attempt["verifyExitCode"] = code
        verify_ok = code == 0 and "HD pack ACTIVE" in verify_out
        state_identical = "Gameplay state comparison: IDENTICAL" in verify_out
        verify_dir = os.path.join(pack, "HdStudio", "verify")
        original_path = os.path.join(verify_dir, "frame_%05d_original.png" % exact_frame)
        hd_path = os.path.join(verify_dir, "frame_%05d_hd.png" % exact_frame)
        pause_shot = os.path.join(pack, "HdStudio", "live", pause.get("screenshot") or "")
        compare = compare_images(pause_shot, original_path, hd_path, bbox, args.scale)
        attempt.update(compare)
        attempt["stateIdentical"] = state_identical
        attempt["hdPackActive"] = verify_ok
        attempt["originalRomIntegrity"] = "passed" if sha256(rom["path"]) == rom_before else "failed"

        if not verify_ok:
            status = "failed-verify"
        elif attempt["originalRomIntegrity"] != "passed":
            status = "failed-integrity"
        elif attempt["markerPixelsInside"] <= 0:
            if compare.get("sceneExact") is False and (compare.get("sceneMatchRatio") or 0) < 0.98:
                status = "failed-target-absent"
            else:
                status = "failed-not-replaced"
        elif attempt["markerPixelsOutside"] > 0:
            status = ("verified-background-shared" if (asset.get("kind") or "") == "pickedBackground"
                      else "verified-shared")
        elif not state_identical:
            status = "partial-visual-state-mismatch"
        elif attempt["markerPixelsInside"] < 8:
            status = "partial-marker-too-small"
        elif (attempt.get("liveActiveFrames") or 0) < 30:
            #The replacement is real, but the pause scene looks like a title/menu
            #(few active sampled frames). It is not counted as gameplay evidence.
            status = "verified-title-or-menu-scene"
        else:
            prefix = "verified-background" if (asset.get("kind") or "") == "pickedBackground" else "verified"
            if attempt["sharedAssetCount"] > 0:
                #The exact scene is isolated (no marker outside the target), but the hardware
                #keys are also referenced by other captured assets. Standard Mesen cannot
                #distinguish them; another scene may change too. Report the distinction.
                status = prefix + "-scene-independent-shared-keys"
            else:
                status = prefix + "-independent"

        attempt["status"] = status
        attempt["elapsedSeconds"] = round(live_seconds + pick_seconds + assets_seconds + import_seconds + verify_seconds, 2)
        attempt["failure"] = "" if status.startswith("verified") else status
        attempt["rom"] = rom["name"]
        attempt["sha256"] = rom["sha256"]
        attempt["marker"] = marker_path
        attempt["pack"] = pack
        attempt["verifyFolder"] = verify_dir
        attempt["evidence"] = {
            "pauseScreenshot": pause_shot,
            "original": original_path,
            "hd": hd_path,
        }
        if status.startswith("verified"):
            final = attempt
            break
        # A target that was visible but did not change can be retried at another frame.
        final = attempt

    if final is None:
        final = {"failure": "no-attempt", "status": "failed-no-attempt"}
    final = dict(final)
    final.setdefault("status", "failed-" + (final.get("failure") or "unknown"))
    final["rom"] = rom["name"]
    final["sha256"] = rom["sha256"]
    final["mapper"] = rom.get("mapper")
    final["chrType"] = rom.get("chrType")
    final["split"] = rom.get("split")
    # Avoid a self-referential JSON document when the final attempt is also the
    # item stored in the attempts list.
    final["attempts"] = [{k: v for k, v in attempt.items() if k != "attempts"}
                         for attempt in attempts]
    final["engineFingerprint"] = engine_fingerprint
    final["frozenInputScript"] = args.input_script
    final["cacheHit"] = False
    final["processedAtUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    # Copy the small evidence set to a predictable folder, not the whole pack.
    ev = os.path.join(work, "evidence")
    os.makedirs(ev, exist_ok=True)
    for key, src in (final.get("evidence") or {}).items():
        if src and os.path.exists(src):
            shutil.copy2(src, os.path.join(ev, key + ".png"))
    atomic_json(result_path, final)
    return final


def write_csv(path, rows):
    fields = ["rom", "sha256", "status", "mapper", "chrType", "split", "targetMode",
              "pauseFrame", "liveActiveFrames", "assetId", "assetKind", "assetWidth",
              "assetHeight", "assetTiles", "sharedAssetCount", "markerPixelsInside",
              "markerPixelsOutside", "changedPixelsInside", "changedPixelsOutside",
              "sceneExact", "sceneMatchRatio", "stateIdentical", "hdPackActive",
              "originalRomIntegrity", "elapsedSeconds", "failure"]
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--base-home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--inventory", default=r"D:\MesenHighWork\validation-v06\inventory\library-inventory.json")
    ap.add_argument("--out-root", default=r"D:\MesenHighWork\validation-v06\replacements")
    ap.add_argument("--input-script", default=r"D:\MesenHigh\scripts\deterministic_input.lua")
    ap.add_argument("--target-frames", default="1200;2600")
    ap.add_argument("--target-mode", choices=("sprite", "background", "auto"), default="sprite")
    ap.add_argument("--live-timeout", type=int, default=120)
    ap.add_argument("--scale", type=int, default=4)
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--selected", default="")
    ap.add_argument("--split", default="")
    ap.add_argument("--hash-file", default="")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--start", type=int, default=0)
    args = ap.parse_args()

    doc = load_json(args.inventory)
    if not doc:
        raise SystemExit("inventory missing")
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
    if args.start:
        roms = roms[args.start:]
    if args.limit:
        roms = roms[:args.limit]
    os.makedirs(args.out_root, exist_ok=True)
    marker_path = os.path.join(args.out_root, "marker.png")
    make_marker(marker_path)
    engine = {"binary": args.mesen, "home": args.base_home}
    fingerprint = (sha256(args.mesen)[:16] if os.path.exists(args.mesen) else "missing")

    print(json.dumps({"roms": len(roms), "workers": args.workers,
                      "out": os.path.abspath(args.out_root),
                      "engine": fingerprint, "input": args.input_script}, indent=2), flush=True)
    results = []
    started = time.time()
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {pool.submit(process_rom, rom, args, engine, marker_path, fingerprint): rom
                   for rom in roms}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            rom = futures[future]
            try:
                result = future.result()
            except Exception as ex:
                result = {"rom": rom["name"], "sha256": rom["sha256"], "status": "failed-exception",
                          "failure": str(ex), "attempts": []}
            results.append(result)
            print("[%d/%d] %s -> %s (frame %s, marker %s/%s)" % (
                index, len(roms), rom["name"], result.get("status"),
                result.get("pauseFrame"), result.get("markerPixelsInside"),
                result.get("markerPixelsOutside")), flush=True)
            atomic_json(os.path.join(args.out_root, "replacement-results.partial.json"),
                        sorted(results, key=lambda r: r.get("rom", "").lower()))
    results.sort(key=lambda r: (r.get("rom") or "").lower())
    atomic_json(os.path.join(args.out_root, "replacement-results.json"), results)
    write_csv(os.path.join(args.out_root, "replacement-matrix.csv"), results)
    counts = {}
    for r in results:
        counts[r.get("status")] = counts.get(r.get("status"), 0) + 1
    summary = {
        "attempted": len(results),
        "statusCounts": counts,
        "verifiedIndependent": sum(1 for r in results if r.get("status") == "verified-independent"),
        "verifiedSceneIndependentSharedKeys": sum(1 for r in results if r.get("status") == "verified-scene-independent-shared-keys"),
        "verifiedShared": sum(1 for r in results if r.get("status") == "verified-shared"),
        "verifiedTitleOrMenu": sum(1 for r in results if r.get("status") == "verified-title-or-menu-scene"),
        "verifiedBackground": sum(1 for r in results if "background" in str(r.get("status", "")) and str(r.get("status", "")).startswith("verified")),
        "verifiedTotal": sum(1 for r in results if str(r.get("status", "")).startswith("verified")),
        "wallSeconds": round(time.time() - started, 1),
        "workers": args.workers,
        "inputScript": args.input_script,
        "engineFingerprint": fingerprint,
    }
    atomic_json(os.path.join(args.out_root, "replacement-summary.json"), summary)
    print(json.dumps(summary, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
