"""RetroArcDo HD Studio v0.8 - adaptive discovery and comprehensive replacement benchmark.

This is the v0.8 measurement engine. It keeps the v0.7.1 evidence format and
adds the generalized corrections required by the v0.8 directive:

  1. Adaptive frame budgets inside one deterministic capture process
     (900 / 1800 / 3600 / 6000 frames, configurable). The runner pauses at each
     budget, inspects the real visible frame and continues only when no suitable
     graphical object is present. A late sprite therefore costs one capture, not
     four power-on runs.
  2. Alternative deterministic input profiles (default, menu escape, alternate)
     used only after the default profile fails.
  3. Automatic background fallback when no visible nonblank hardware sprite is
     present in any probe. The fallback is a real rendered background block, not
     a blank-tile recolour.
  4. Baseline-HD verification. A minimal authored pack and an empty pack at the
     same scale are replayed with the same input. Authored changes are the
     per-pixel difference between the two HD frames, independent of the game's
     emphasis/dim/grayscale effects. This corrects false "not replaced" rows
     where the marker colours were shifted by the emulator's emphasis bits.
  5. Optional multi-asset combined-pack verification on one paused scene.

Evidence stays local. ROM bytes and commercial packs are never committed.

Example:
  python scripts/hdstudio_v08_autoreplace.py --selected "Game.nes" --budgets 900;1800;3600;6000
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import time
from dataclasses import dataclass, field
from typing import Any

from PIL import Image

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hdstudio_v071_autoreplace as v71  # noqa: E402


DEFAULT_BUDGETS = [900, 1800, 3600, 6000]
DEFAULT_PROFILES = {
    "default": r"D:\MesenHigh\scripts\deterministic_input.lua",
    "menu": r"D:\MesenHigh\scripts\deterministic_input_menu.lua",
    "alt": r"D:\MesenHigh\scripts\deterministic_input_alt.lua",
}
MARKER_EXACT = {(255, 0, 255), (0, 255, 255), (0, 255, 0)}


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

def sha1(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def family(r, g, b):
    """Marker colour family after Mesen emphasis/brightness transforms.

    The exact marker colours are checked separately. This classification is only
    used INSIDE the target footprint, where the authored-only pack makes a
    saturated marker-coloured cluster strong evidence. It is deliberately not
    used for collateral counts because ordinary game art contains saturated
    colours too.
    """
    mx, mn = max(r, g, b), min(r, g, b)
    if mx < 60:
        return None
    if (mx - mn) / mx < 0.72:
        return None
    if abs(r - b) <= 0.28 * mx and g <= 0.45 * mx and r >= 0.75 * mx and b >= 0.70 * mx:
        return "M"
    if abs(g - b) <= 0.30 * mx and r <= 0.45 * mx and g >= 0.75 * mx and b >= 0.70 * mx:
        return "C"
    if g >= 0.75 * mx and r <= 0.55 * g and b <= 0.55 * g:
        return "G"
    return None


def parse_profiles(value):
    names = [x.strip().lower() for x in value.split(";") if x.strip()]
    return [(name, DEFAULT_PROFILES[name]) for name in names if name in DEFAULT_PROFILES]


def parse_budgets(value):
    budgets = []
    for part in value.replace(",", ";").split(";"):
        part = part.strip()
        if part.isdigit() and int(part) > 0:
            budgets.append(int(part))
    return sorted(set(budgets)) or list(DEFAULT_BUDGETS)


def bounds(component):
    return (int(component["x"]), int(component["y"]),
            int(component["x"]) + int(component["width"]),
            int(component["y"]) + int(component["height"]))


def box_list(component):
    x0, y0, x1, y1 = bounds(component)
    return [x0, y0, x1 - x0, y1 - y0]


def overlaps(a, b, margin=2):
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    return not (ax + aw + margin <= bx or bx + bw + margin <= ax or
                ay + ah + margin <= by or by + bh + margin <= ay)


# ---------------------------------------------------------------------------
# Adaptive capture
# ---------------------------------------------------------------------------

def capture_adaptive(rom, args, engine, attempt_root, profile_name, input_script):
    """Run one deterministic capture and probe several frame budgets in-process.

    Pausing stops live sampling, not emulation. The frame stays at the probe
    position until the runner resumes, so the next probe is a later frame of the
    same deterministic session. The final pause is the replacement scene.
    """
    home = os.path.join(attempt_root, "home")
    pack = os.path.join(home, "HdPacks", os.path.splitext(rom["name"])[0])
    os.makedirs(os.path.join(pack, "HdStudio", "live"), exist_ok=True)
    v71.prepare_home(args.base_home, home, os.path.splitext(rom["name"])[0])
    attempt_engine = {"binary": engine["binary"], "home": home}
    control = os.path.join(pack, "HdStudio", "live", "control.txt")
    status_path = os.path.join(pack, "HdStudio", "live", "live-status.json")
    pause_path = os.path.join(pack, "HdStudio", "live", "pause-frame.json")
    if os.path.exists(pause_path):
        try:
            os.remove(pause_path)
        except OSError:
            pass
    with open(control, "w", encoding="utf-8") as f:
        f.write("0|resume\n")

    live_log = os.path.join(attempt_root, "live-%s.log" % profile_name)
    live_args = [
        "--hdstudio", rom["path"], input_script, "--live", "--live-mode=minimal",
        "--generate", "--frames=100000", "--timeout=" + str(args.live_timeout),
        "--seed=20261124", "--scale=" + str(args.scale),
        "--out=" + pack, "--log=" + live_log,
    ]
    started = time.time()
    env = os.environ.copy()
    env["MESEN_HOME"] = home
    stdout_path = os.path.join(attempt_root, "live-%s-stdout.log" % profile_name)
    with open(stdout_path, "w", encoding="utf-8") as stdout:
        proc = __import__("subprocess").Popen(
            [engine["binary"]] + live_args, env=env, stdout=stdout,
            stderr=__import__("subprocess").STDOUT)

    probes = []
    chosen = None
    command_id = 0
    last_pause_frame = None
    deadline = time.time() + args.live_timeout + 45
    reasons = []
    try:
        for budget_index, budget in enumerate(args.budgets):
            # Wait for this budget. The live recorder writes status every 10 frames.
            while time.time() < deadline and proc.poll() is None:
                status = v71.load_json(status_path, {}) or {}
                if int(status.get("frame", 0)) >= budget and status.get("samples", 0) > 0:
                    break
                time.sleep(0.05)
            status = v71.load_json(status_path, {}) or {}
            if proc.poll() is not None:
                reasons.append("capture-exited-before-budget-%d" % budget)
                break
            if int(status.get("frame", 0)) < budget:
                reasons.append("capture-timeout-before-budget-%d" % budget)
                break

            command_id += 1
            with open(control, "w", encoding="utf-8") as f:
                f.write("%d|pause\n" % command_id)
            # Wait for a NEW paused frame at or after this budget.
            pause = None
            pause_wait = time.time() + 30
            while time.time() < pause_wait and proc.poll() is None:
                st = v71.load_json(status_path, {}) or {}
                if st.get("paused"):
                    candidate = v71.load_json(pause_path, {}) or {}
                    frame = int(candidate.get("frame") or 0)
                    if frame >= budget and frame != last_pause_frame:
                        pause = candidate
                        break
                time.sleep(0.05)
            if not pause:
                reasons.append("pause-timeout-at-budget-%d" % budget)
                break

            last_pause_frame = int(pause.get("frame") or 0)
            components = v71.composition_components(pause.get("sprites") or [])
            shot_name = pause.get("screenshot") or ""
            shot_path = os.path.join(pack, "HdStudio", "live", shot_name) if shot_name else ""
            shot_img = None
            if shot_path and os.path.exists(shot_path):
                try:
                    shot_img = Image.open(shot_path).convert("RGB")
                except Exception:
                    shot_img = None
            sprite = v71.choose_component(components) if args.target_mode in ("sprite", "auto") else None
            background = None
            if not sprite and args.target_mode in ("background", "auto"):
                background = v71.choose_background_block(pause, shot_img) if pause else None

            probe = {
                "profile": profile_name,
                "budget": budget,
                "pauseFrame": last_pause_frame,
                "spriteParts": len(pause.get("sprites") or []),
                "backgroundCells": len(pause.get("background") or []),
                "components": len(components),
                "activeFrames": int(status.get("gameplayFrames") or 0),
                "samples": int(status.get("samples") or 0),
                "screenHash": status.get("screenHash"),
                "spriteChosen": bool(sprite),
                "backgroundChosen": bool(background),
                "chosenKind": "sprite" if sprite else ("background" if background else None),
            }
            probes.append(probe)

            # Preserve the probe evidence before a resume overwrites it.
            probe_dir = os.path.join(attempt_root, "probe-%02d" % len(probes))
            os.makedirs(probe_dir, exist_ok=True)
            if os.path.exists(pause_path):
                shutil.copy2(pause_path, os.path.join(probe_dir, "pause-frame.json"))
            if shot_path and os.path.exists(shot_path):
                shutil.copy2(shot_path, os.path.join(probe_dir, "pause.png"))

            if sprite:
                chosen = {
                    "kind": "sprite", "component": sprite, "pause": pause,
                    "pausePath": pause_path, "pauseScreenshot": shot_path,
                    "pack": pack, "home": home, "engine": attempt_engine,
                    "budget": budget, "profile": profile_name,
                }
                break
            # Longer exploration is only justified while the screen is still
            # producing new evidence. A stagnant title/menu probe that already has
            # a rendered background does not need the 3,600/6,000-frame budgets.
            screen_hashes = {p.get("screenHash") for p in probes if p.get("screenHash")}
            static_scene = (int(status.get("gameplayFrames") or 0) < 30
                            and len(screen_hashes) <= 1)
            if background and (budget == args.budgets[-1] or args.target_mode == "background"
                               or (budget >= 1800 and static_scene)
                               or budget >= 3600):
                chosen = {
                    "kind": "background", "component": background, "pause": pause,
                    "pausePath": pause_path, "pauseScreenshot": shot_path,
                    "pack": pack, "home": home, "engine": attempt_engine,
                    "budget": budget, "profile": profile_name,
                }
                break
            if static_scene and budget >= 1800:
                # No sprite, no rendered background and no visible progression:
                # the next deterministic profile is more useful than more frames.
                reasons.append("static-scene-stopped-at-%d" % budget)
                break
            if budget_index < len(args.budgets) - 1:
                command_id += 1
                with open(control, "w", encoding="utf-8") as f:
                    f.write("%d|resume\n" % command_id)
                # Wait for the recorder to acknowledge the resume before asking again.
                resume_wait = time.time() + 5
                while time.time() < resume_wait:
                    st = v71.load_json(status_path, {}) or {}
                    if not st.get("paused"):
                        break
                    time.sleep(0.05)
        # Stop the capture cleanly.
        if proc.poll() is None:
            command_id += 1
            try:
                with open(control, "w", encoding="utf-8") as f:
                    f.write("%d|stop\n" % command_id)
            except OSError:
                pass
            time.sleep(0.5)
            if proc.poll() is None:
                proc.terminate()
        try:
            proc.wait(timeout=20)
        except Exception:
            proc.kill()
            try:
                proc.wait(timeout=10)
            except Exception:
                pass
    finally:
        if proc.poll() is None:
            proc.kill()

    live_status = v71.load_json(status_path, {}) or {}
    result = {
        "profile": profile_name,
        "inputScript": input_script,
        "budgets": list(args.budgets),
        "probes": probes,
        "seconds": round(time.time() - started, 2),
        "exitCode": proc.returncode,
        "reasons": reasons,
        "pack": pack,
        "home": home,
        "engine": attempt_engine,
        "liveStatus": {
            "frame": live_status.get("frame"),
            "samples": live_status.get("samples"),
            "gameplayFrames": live_status.get("gameplayFrames"),
            "spriteOccurrences": live_status.get("spriteOccurrences"),
            "backgroundBlocks": live_status.get("backgroundBlocks"),
            "screenHash": live_status.get("screenHash"),
            "patternLimitReached": live_status.get("patternLimitReached"),
        },
    }
    if chosen:
        result["chosen"] = chosen
    return result


# ---------------------------------------------------------------------------
# Picking, planning and importing
# ---------------------------------------------------------------------------

def pick_and_save(engine, rom, pack, attempt_root, component, name, tag):
    point = component["point"]
    code, candidates_text, seconds = v71.run(
        engine, ["--hdstudio", "--pick=%d,%d" % point, "--out=" + pack],
        120, os.path.join(attempt_root, "pick-%s.log" % tag))
    candidates = v71.parse_pick_candidates(candidates_text)
    if code != 0 or not candidates:
        return None, {"failure": "picker-no-candidates", "log": candidates_text[-1000:],
                      "seconds": seconds}
    if component.get("kind") == "backgroundBlock":
        choice = next((c for c in candidates if c["kind"] == "backgroundBlock"), None)
        if choice is None:
            choice = next((c for c in candidates if c["kind"] == "backgroundTile"), None)
    elif component.get("kind") == "backgroundTile":
        choice = next((c for c in candidates if c["kind"] == "backgroundTile"), None)
    else:
        choice = next((c for c in candidates if c["kind"] == "metasprite" and
                       abs(c["width"] - component["width"]) <= 8 and
                       abs(c["height"] - component["height"]) <= 8), None)
        if choice is None:
            choice = next((c for c in candidates if c["kind"] == "metasprite"), None)
    if choice is None:
        choice = candidates[0]
    code, pick_out, pick_seconds = v71.run(
        engine, ["--hdstudio", "--pick=%d,%d" % point,
                 "--pick-save=%d" % choice["index"],
                 "--pick-name=" + name, "--out=" + pack],
        120, os.path.join(attempt_root, "pick-save-%s.log" % tag))
    if code != 0:
        return None, {"failure": "pick-save-failed", "log": pick_out[-1200:],
                      "seconds": pick_seconds}
    return {"candidate": choice, "seconds": pick_seconds + seconds}, None


def export_assets(engine, rom, pack, attempt_root, tag):
    code, out, seconds = v71.run(
        engine, ["--hdstudio", rom["path"], "--assets", "--out=" + pack,
                 "--log=" + os.path.join(attempt_root, "assets-%s.log" % tag)], 420)
    if code != 0:
        return None, {"failure": "assets-export-failed", "log": out[-1200:], "seconds": seconds}
    doc = v71.load_json(os.path.join(pack, "HdStudio", "assets", "assets.json"), {}) or {}
    return doc, None


def find_picked_asset(doc, name):
    picked = [a for a in doc.get("assets", []) if (a.get("id") or "").startswith("picked:")]
    exact = [a for a in picked if a.get("name") == name]
    if exact:
        return exact[-1]
    by_prov = [a for a in picked if (a.get("provenance") or "").find(name) >= 0]
    if by_prov:
        return by_prov[-1]
    return picked[-1] if picked else None


def plan_for_asset(engine, rom, pack, attempt_root, asset, tag, args):
    plan_path = os.path.join(attempt_root, "context-plan-%s.json" % tag)
    code, out, seconds = v71.run(
        engine, ["--hdstudio", "--asset=" + asset["id"],
                 "--propose-context=" + plan_path, "--out=" + pack],
        180, os.path.join(attempt_root, "context-plan-%s.log" % tag))
    plan = v71.load_json(plan_path, {}) or {}
    use_plan_conditions = bool(plan.get("cellConditions"))
    info = {
        "assetId": asset.get("id"),
        "assetKind": asset.get("kind"),
        "planSeconds": seconds,
        "contextStatus": plan.get("status"),
        "contextRule": plan.get("contextRule"),
        "contextScenes": plan.get("scenes"),
        "contextIntendedMatched": plan.get("intendedMatched"),
        "contextIntendedCells": plan.get("intendedCells"),
        "contextUnrelatedMatched": plan.get("unrelatedMatched"),
        "contextUnrelatedCandidates": plan.get("unrelatedCandidates"),
        "contextSharedAssetCount": plan.get("sharedAssetCount"),
        "conditionsApplied": use_plan_conditions,
    }
    if rom.get("chrType") == "CHR-RAM":
        if args.chrram_conditions == "off":
            use_plan_conditions = False
            info["chrramConditionMode"] = "off"
        elif args.chrram_conditions == "position":
            dims = asset.get("dimensions") or {}
            width = dims.get("width") or asset.get("width") or 8
            height = dims.get("height") or asset.get("height") or 8
            ox = dims.get("originX", asset.get("originX", 0))
            oy = dims.get("originY", asset.get("originY", 0))
            bbox = (ox, oy, ox + width, oy + height)
            if v71.apply_chrram_position_conditions(plan_path, plan, bbox):
                use_plan_conditions = True
                info["contextStatusOriginal"] = plan.get("contextStatusOriginal")
                info["contextStatus"] = plan.get("status")
                info["contextRule"] = plan.get("contextRule")
                info["chrramConditionMode"] = "positionCheck"
            else:
                info["chrramConditionMode"] = "existing-plan"
        else:
            info["chrramConditionMode"] = "content-keys"
    info["conditionsApplied"] = use_plan_conditions
    info["planPath"] = plan_path
    info["plan"] = plan
    return info


def import_asset(engine, rom, pack, attempt_root, asset, plan_info, marker_path, args,
                 marker_name):
    import_args = ["--hdstudio", rom["path"], "--import",
                   "--asset=" + asset["id"], "--image=" + marker_path,
                   "--fit=stretch", "--all-palettes", "--mirror-conditions",
                   "--minimal", "--import-scale=" + str(args.scale),
                   "--import-name=" + marker_name, "--out=" + pack]
    if plan_info.get("conditionsApplied"):
        import_args.append("--condition-map=" + plan_info["planPath"])
    code, out, seconds = v71.run(
        engine, import_args, 600, os.path.join(attempt_root, "import-%s.log" % marker_name))
    shared = re.search(r"also used by (\d+) other captured asset", out)
    return {
        "exitCode": code,
        "log": out[-1600:] if code != 0 else "",
        "seconds": seconds,
        "sharedAssetCount": int(shared.group(1)) if shared else 0,
        "conditionsApplied": bool(plan_info.get("conditionsApplied")),
    }


# ---------------------------------------------------------------------------
# Baseline-HD verification
# ---------------------------------------------------------------------------

def prepare_baseline_home(args, attempt_root, rom, target_pack):
    home = os.path.join(attempt_root, "baseline", "home")
    pack = os.path.join(home, "HdPacks", os.path.splitext(rom["name"])[0])
    os.makedirs(pack, exist_ok=True)
    v71.prepare_home(args.base_home, home, os.path.splitext(rom["name"])[0])
    # Reuse the exact supportedRom key from the authored pack when available.
    sha1_value = sha1(rom["path"])
    hires = os.path.join(target_pack, "hires.txt")
    if os.path.exists(hires):
        try:
            for line in open(hires, encoding="utf-8", errors="replace"):
                if line.startswith("<supportedRom>"):
                    sha1_value = line.strip().split(">", 1)[1]
                    break
        except Exception:
            pass
    with open(os.path.join(pack, "hires.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("<ver>109\n<scale>%d\n<supportedRom>%s\n" % (args.scale, sha1_value))
    return {"home": home, "pack": pack, "engine": {"binary": args.mesen, "home": home}}


def run_verify(engine, rom, pack, args, frames, tag, attempt_root, input_script):
    verify_args = [
        "--hdstudio", rom["path"], input_script, "--verify", "--shots=1",
        "--frames=%d" % max(max(frames), 1),
        "--verify-at=" + ",".join(str(f) for f in sorted(set(frames))),
        "--out=" + pack,
    ]
    code, out, seconds = v71.run(
        engine, verify_args, 900, os.path.join(attempt_root, "verify-%s.log" % tag))
    return {
        "exitCode": code,
        "output": out,
        "seconds": seconds,
        "hdActive": code == 0 and "HD pack ACTIVE" in out,
        "stateIdentical": "Gameplay state comparison: IDENTICAL" in out,
        "verifyDir": os.path.join(pack, "HdStudio", "verify"),
    }


def exact_and_family(image, boxes, scale):
    """Marker evidence inside target boxes and exact marker outside (vectorized)."""
    arr = np.asarray(image.convert("RGB"), dtype=np.int16)
    height, width = arr.shape[:2]
    r, g, b = arr[:, :, 0], arr[:, :, 1], arr[:, :, 2]
    mx = arr.max(axis=2)
    mn = arr.min(axis=2)
    saturation = (mx - mn) / np.maximum(mx, 1)
    valid = (mx >= 60) & (saturation >= 0.72)
    magenta = valid & (np.abs(r - b) <= 0.28 * mx) & (g <= 0.45 * mx) & (r >= 0.75 * mx) & (b >= 0.70 * mx)
    cyan = valid & (np.abs(g - b) <= 0.30 * mx) & (r <= 0.45 * mx) & (g >= 0.75 * mx) & (b >= 0.70 * mx)
    green = valid & (g >= 0.75 * mx) & (r <= 0.55 * g) & (b <= 0.55 * g)
    family_mask = magenta | cyan | green
    exact_mask = (((r == 255) & (g == 0) & (b == 255)) |
                  ((r == 0) & (g == 255) & (b == 255)) |
                  ((r == 0) & (g == 255) & (b == 0)))
    ys, xs = np.indices((height, width))
    lx = xs // scale
    ly = ys // scale
    any_box = np.zeros((height, width), dtype=bool)
    family_in = []
    for bx, by, bw, bh in boxes:
        mask = (lx >= bx) & (lx < bx + bw) & (ly >= by) & (ly < by + bh)
        any_box |= mask
        family_in.append(int(np.count_nonzero(family_mask & mask)))
    exact_in = int(np.count_nonzero(exact_mask & any_box))
    exact_out = int(np.count_nonzero(exact_mask & ~any_box))
    return exact_in, exact_out, family_in


def baseline_diff(target_path, baseline_path, boxes, scale):
    """Authored HD changes measured against an empty HD pack at the same scale."""
    result = {
        "available": False,
        "changedInside": 0,
        "changedOutside": 0,
        "perBox": [0] * len(boxes),
    }
    if not (os.path.exists(target_path) and os.path.exists(baseline_path)):
        return result
    target = Image.open(target_path).convert("RGB")
    baseline = Image.open(baseline_path).convert("RGB")
    if target.size != baseline.size:
        result["sizeMismatch"] = [list(target.size), list(baseline.size)]
        return result
    result["available"] = True
    a = np.asarray(target, dtype=np.int16)
    b = np.asarray(baseline, dtype=np.int16)
    changed = np.any(a != b, axis=2)
    height, width = changed.shape
    ys, xs = np.indices((height, width))
    lx = xs // scale
    ly = ys // scale
    any_box = np.zeros((height, width), dtype=bool)
    for index, (bx, by, bw, bh) in enumerate(boxes):
        mask = (lx >= bx) & (lx < bx + bw) & (ly >= by) & (ly < by + bh)
        any_box |= mask
        result["perBox"][index] = int(np.count_nonzero(changed & mask))
    result["changedInside"] = int(np.count_nonzero(changed & any_box))
    result["changedOutside"] = int(np.count_nonzero(changed & ~any_box))
    return result


def scene_match(original_path, pause_path):
    if not (original_path and pause_path and os.path.exists(original_path) and os.path.exists(pause_path)):
        return None
    original = Image.open(original_path).convert("RGB")
    pause = Image.open(pause_path).convert("RGB")
    if original.size != pause.size:
        return None
    a = np.asarray(original, dtype=np.int16)
    b = np.asarray(pause, dtype=np.int16)
    same = int(np.count_nonzero(np.all(a == b, axis=2)))
    return round(same / (original.width * original.height), 6)


def measure_frames(args, frames_targets, target_verify, baseline_verify, pause_path):
    measurements = []
    for item in frames_targets:
        frame = int(item["frame"])
        boxes = item["boxes"]
        original_path = os.path.join(target_verify["verifyDir"], "frame_%05d_original.png" % frame)
        target_path = os.path.join(target_verify["verifyDir"], "frame_%05d_hd.png" % frame)
        baseline_path = os.path.join(baseline_verify["verifyDir"], "frame_%05d_hd.png" % frame) \
            if baseline_verify else ""
        marker_exact_in = marker_exact_out = 0
        family_in = [0] * len(boxes)
        if os.path.exists(target_path):
            try:
                image = Image.open(target_path).convert("RGB")
                marker_exact_in, marker_exact_out, family_in = exact_and_family(image, boxes, args.scale)
            except Exception:
                pass
        diff = baseline_diff(target_path, baseline_path, boxes, args.scale) if baseline_verify else {
            "available": False, "changedInside": 0, "changedOutside": 0,
            "perBox": [0] * len(boxes)}
        match = scene_match(original_path, pause_path) if item.get("pausedFrame") else None
        measurements.append({
            "frame": frame,
            "pausedFrame": bool(item.get("pausedFrame")),
            "boxes": boxes,
            "assetBoxes": item.get("assetBoxes") or [],
            "markerExactInside": marker_exact_in,
            "markerExactOutside": marker_exact_out,
            "markerFamilyInside": family_in,
            "baselineAvailable": diff["available"],
            "changedInside": diff["changedInside"],
            "changedOutside": diff["changedOutside"],
            "changedPerBox": diff["perBox"],
            "sceneMatchRatio": match,
            "sceneExact": (match == 1.0) if match is not None else None,
            "markerExactAvailable": marker_exact_in > 0,
        })
    return measurements


# ---------------------------------------------------------------------------
# Result classification
# ---------------------------------------------------------------------------

def classify_no_target(probes, profile_name, reasons):
    if not probes:
        return "emulator-capture-failure" if reasons else "no-visible-sprites"
    max_active = max(p.get("activeFrames") or 0 for p in probes)
    max_background = max(p.get("backgroundCells") or 0 for p in probes)
    sprite_probes = sum(1 for p in probes if (p.get("spriteParts") or 0) > 0)
    hashes = {p.get("screenHash") for p in probes if p.get("screenHash")}
    if max_active < 30:
        return "title-screen-stagnation"
    if sprite_probes == 0 and max_background > 0:
        return "background-only-graphics"
    if len(hashes) <= 1 and profile_name != "default":
        return "menu-navigation-failure"
    if sprite_probes > 0:
        return "no-selectable-composition"
    return "no-visible-sprites"


def classify_verified(asset_kind, plan_status, multi_scene, use_plan_conditions,
                      shared_count, collateral, multi_assets):
    prefix = "verified-background" if asset_kind == "background" else "verified"
    if multi_assets >= 2:
        return "verified-multi-asset-shared" if collateral else "verified-multi-asset-combined"
    if collateral:
        return prefix + "-shared"
    if plan_status == "strict-independent" and multi_scene:
        return prefix + "-independent-multi-scene"
    if plan_status == "contextually-isolated-multi-scene" and multi_scene and use_plan_conditions:
        return prefix + "-context-independent-multi-scene"
    if plan_status == "scene-isolated" and use_plan_conditions:
        return prefix + "-conditional-scene-isolated"
    if plan_status == "shared-context" or shared_count > 0:
        return prefix + ("-scene-independent-shared-keys-multi-scene" if multi_scene
                         else "-scene-independent-shared-keys")
    if multi_scene and use_plan_conditions:
        return prefix + "-context-independent-multi-scene"
    if multi_scene:
        return prefix + "-multi-scene-shared-keys"
    return prefix + "-scene-isolated"


# ---------------------------------------------------------------------------
# One ROM
# ---------------------------------------------------------------------------

def process_rom(rom, args, engine, marker_path, engine_fingerprint):
    stem = v71.safe_name(os.path.splitext(rom["name"])[0])
    work_name = rom["sha256"][:12] if args.short_work else rom["sha256"][:12] + "_" + stem
    work = os.path.join(args.out_root, work_name)
    os.makedirs(work, exist_ok=True)
    result_path = os.path.join(work, "result.json")
    previous = v71.load_json(result_path, {})
    if previous and previous.get("status", "").startswith("verified") and not args.force:
        config = previous.get("testConfig") or {}
        if (previous.get("engineFingerprint") == engine_fingerprint and
                config.get("scale", args.scale) == args.scale and
                config.get("budgets") == args.budgets and
                config.get("profiles") == [p[0] for p in args.profiles] and
                config.get("multiAssets", 1) == args.multi_assets and
                config.get("baselineVerification", "auto") == args.baseline):
            previous["cacheHit"] = True
            return previous

    rom_before = v71.sha256(rom["path"])
    attempts = []
    final = None
    for attempt_index, (profile_name, input_script) in enumerate(args.profiles, 1):
        attempt_root = os.path.join(work, "attempt-%s" % profile_name)
        if os.path.isdir(attempt_root):
            shutil.rmtree(attempt_root, ignore_errors=True)
        os.makedirs(attempt_root, exist_ok=True)
        capture = capture_adaptive(rom, args, engine, attempt_root, profile_name, input_script)
        attempt = {
            "attempt": attempt_index,
            "profile": profile_name,
            "inputScript": input_script,
            "budgets": args.budgets,
            "probes": capture.get("probes"),
            "liveSeconds": capture.get("seconds"),
            "liveExitCode": capture.get("exitCode"),
            "liveStatus": capture.get("liveStatus"),
            "captureReasons": capture.get("reasons"),
            "pauseFrame": (capture.get("chosen") or {}).get("pause", {}).get("frame"),
            "chosenKind": (capture.get("chosen") or {}).get("kind"),
            "chosenComponent": None,
        }
        attempts.append(attempt)
        chosen = capture.get("chosen")
        if not chosen:
            attempt["failure"] = "no-visible-nonblank-sprite"
            attempt["classification"] = classify_no_target(
                capture.get("probes") or [], profile_name, capture.get("reasons"))
            final = attempt
            # A title/menu failure is exactly what the next deterministic profile is for.
            continue

        engine_attempt = chosen["engine"]
        pack = chosen["pack"]
        pause = chosen["pause"]
        pause_path = chosen["pausePath"]
        pause_shot_path = chosen["pauseScreenshot"]
        component = chosen["component"]
        attempt["chosenComponent"] = {k: component[k] for k in ("x", "y", "width", "height", "area")}
        attempt["explorationBudget"] = chosen["budget"]
        attempt["lateScene"] = bool(chosen["budget"] > args.budgets[0])
        attempt["backgroundFallback"] = chosen["kind"] == "background"

        # Select one or more non-overlapping components from the paused scene.
        components = v71.composition_components(pause.get("sprites") or [])
        selected = []
        if args.multi_assets > 1 and chosen["kind"] == "sprite":
            ordered = sorted(components, key=lambda c: -int(c.get("area") or 0))
            selected.append(component)
            for candidate in ordered:
                if len(selected) >= args.multi_assets:
                    break
                box = box_list(candidate)
                if any(overlaps(box, box_list(s)) for s in selected):
                    continue
                if candidate["width"] > 128 or candidate["height"] > 128:
                    continue
                selected.append(candidate)
        else:
            selected = [component]

        imported = []
        selected_boxes = []
        shared_total = 0
        pick_seconds = 0.0
        import_seconds = 0.0
        plan_seconds = 0.0
        conflict_skipped = 0
        known_keys = set()
        for asset_index, component_item in enumerate(selected, 1):
            name = "AutoTest" if asset_index == 1 else "AutoTest%d" % asset_index
            pick, pick_error = pick_and_save(
                engine_attempt, rom, pack, attempt_root, component_item, name,
                "%s-a%d" % (profile_name, asset_index))
            if pick_error:
                attempt["failure"] = pick_error["failure"]
                attempt["pickLog"] = pick_error.get("log")
                final = attempt
                break
            pick_seconds += pick.get("seconds") or 0
            assets_doc, assets_error = export_assets(
                engine_attempt, rom, pack, attempt_root, "%s-a%d" % (profile_name, asset_index))
            if assets_error:
                attempt["failure"] = assets_error["failure"]
                attempt["assetsLog"] = assets_error.get("log")
                final = attempt
                break
            asset = find_picked_asset(assets_doc, name)
            if not asset or not asset.get("tiles"):
                attempt["failure"] = "picked-asset-missing"
                final = attempt
                break
            dims = asset.get("dimensions") or {}
            width = dims.get("width") or asset.get("width") or 8
            height = dims.get("height") or asset.get("height") or 8
            origin_x = dims.get("originX", asset.get("originX", 0))
            origin_y = dims.get("originY", asset.get("originY", 0))
            if width > 128 or height > 128:
                attempt["failure"] = "picked-asset-too-large"
                final = attempt
                break
            asset_box = [origin_x, origin_y, width, height]
            keys = {(t.get("pattern", "").upper(), t.get("palette", "").upper())
                    for t in asset.get("tiles") or []}
            if asset_index > 1 and keys & known_keys and not args.allow_key_conflict:
                conflict_skipped += 1
                continue
            known_keys |= keys
            plan_info = plan_for_asset(engine_attempt, rom, pack, attempt_root, asset, name, args)
            plan_seconds += plan_info.get("planSeconds") or 0
            if plan_info.get("plan", {}).get("status") == "unresolved-context" and not args.allow_shared:
                attempt["failure"] = "context-unresolved"
                final = attempt
                break
            marker_name = ("AutoBg_" if asset.get("kind") == "pickedBackground" else "AutoTest_") + \
                          rom["sha256"][:8] + ("_%d" % asset_index if asset_index > 1 else "")
            imported_info = import_asset(
                engine_attempt, rom, pack, attempt_root, asset, plan_info, marker_path,
                args, marker_name)
            import_seconds += imported_info.get("seconds") or 0
            shared_total += imported_info.get("sharedAssetCount") or 0
            imported.append({
                "assetId": asset.get("id"),
                "assetKind": asset.get("kind"),
                "name": name,
                "box": asset_box,
                "plan": {k: v for k, v in plan_info.items() if k != "plan"},
                "import": imported_info,
            })
            selected_boxes.append(asset_box)
            if imported_info["exitCode"] != 0:
                attempt["failure"] = "import-compile-failed"
                attempt["importLog"] = imported_info.get("log")
                final = attempt
                break
        if final is attempt and attempt.get("failure"):
            continue

        # Minimal imports write only the current asset's cells. A combined pack
        # must be rebuilt from the project so every approved replacement is in the
        # same standard hires.txt.
        combine_compile = {"attempted": False, "exitCode": None, "seconds": 0.0, "log": ""}
        if len(imported) >= 2 and args.combine_compile:
            combine_compile["attempted"] = True
            code, compile_out, compile_seconds = v71.run(
                engine_attempt,
                ["--hdstudio", rom["path"], "--compile", "--out=" + pack],
                600, os.path.join(attempt_root, "combine-compile.log"))
            combine_compile.update({
                "exitCode": code, "seconds": compile_seconds,
                "log": compile_out[-2000:] if code != 0 else "",
            })
            if code != 0:
                attempt["failure"] = "combined-compile-failed"
                attempt["combineCompile"] = combine_compile
                final = attempt
                continue
        attempt["combineCompile"] = combine_compile

        attempt["assetsImported"] = len(imported)
        attempt["assetsSelected"] = len(selected)
        attempt["conflictsSkipped"] = conflict_skipped
        attempt["imports"] = imported
        attempt["sharedAssetCount"] = shared_total
        attempt["pickSeconds"] = round(pick_seconds, 2)
        attempt["importSeconds"] = round(import_seconds, 2)
        attempt["planSeconds"] = round(plan_seconds, 2)

        # Verification targets. For a single asset use the planner's multi-scene
        # occurrences. For a combined pack use the exact paused scene with every
        # imported asset's footprint, because each asset's other occurrences are
        # not interchangeable.
        primary_plan = (imported[0].get("plan") or {}) if imported else {}
        exact_frame = int(pause.get("frame") or (capture.get("probes") or [{}])[-1].get("budget") or 0)
        frames_targets = []
        if len(imported) == 1:
            plan_doc = v71.load_json(primary_plan.get("planPath"), {}) or {}
            verify_targets = v71.choose_verification_frames(plan_doc, max(1, args.verify_scenes))
            for target in verify_targets:
                boxes = target.get("bboxes") or []
                if not boxes:
                    boxes = [[int(target["x"]), int(target["y"]),
                              int(target.get("width") or 8), int(target.get("height") or 8)]]
                frames_targets.append({"frame": int(target["frame"]), "boxes": boxes,
                                       "pausedFrame": bool(target.get("pausedFrame")),
                                       "assetBoxes": [box_list(component_item)]})
            if all(t["frame"] != exact_frame for t in frames_targets):
                frames_targets.append({"frame": exact_frame, "boxes": [imported[0]["box"]],
                                       "pausedFrame": True,
                                       "assetBoxes": [imported[0]["box"]]})
            for target in frames_targets:
                if target["frame"] == exact_frame:
                    target["boxes"] = [imported[0]["box"]]
                    target["pausedFrame"] = True
                    target["assetBoxes"] = [imported[0]["box"]]
        else:
            frames_targets = [{"frame": exact_frame, "boxes": selected_boxes,
                               "pausedFrame": True, "assetBoxes": selected_boxes}]
        frames_targets.sort(key=lambda t: t["frame"])
        frames = [t["frame"] for t in frames_targets]

        target_verify = run_verify(
            engine_attempt, rom, pack, args, frames, profile_name, attempt_root,
            input_script)
        attempt["verifySeconds"] = target_verify["seconds"]
        attempt["verifyExitCode"] = target_verify["exitCode"]
        attempt["hdPackActive"] = target_verify["hdActive"]
        attempt["stateIdentical"] = target_verify["stateIdentical"]

        baseline_verify = None
        baseline_seconds = 0.0
        need_baseline = False
        if target_verify["hdActive"] and args.baseline != "never":
            if args.baseline == "always" or len(imported) >= 2:
                need_baseline = True
            else:
                # Auto: exact marker colours are sufficient when they are present
                # everywhere and no exact marker leaked outside. Emphasis/dim
                # frames have no exact marker inside and are sent to baseline.
                for item in frames_targets:
                    frame = int(item["frame"])
                    image_path = os.path.join(target_verify["verifyDir"], "frame_%05d_hd.png" % frame)
                    if not os.path.exists(image_path):
                        need_baseline = True
                        break
                    try:
                        image = Image.open(image_path).convert("RGB")
                        exact_in, exact_out, family_in = exact_and_family(
                            image, item["boxes"], args.scale)
                    except Exception:
                        need_baseline = True
                        break
                    if exact_in <= 0 or exact_out > 0:
                        need_baseline = True
                        break
        if need_baseline:
            baseline = prepare_baseline_home(args, attempt_root, rom, pack)
            baseline_verify = run_verify(
                baseline["engine"], rom, baseline["pack"], args, frames,
                profile_name + "-baseline", attempt_root,
                input_script)
            baseline_seconds = baseline_verify["seconds"]
            if not baseline_verify["hdActive"]:
                baseline_verify = None
        attempt["baselineSeconds"] = baseline_seconds
        attempt["baselineUsed"] = bool(baseline_verify)

        measurements = measure_frames(
            args, frames_targets, target_verify, baseline_verify,
            pause_shot_path)
        for m in measurements:
            per_box = m.get("changedPerBox") or []
            m["perBoxPresent"] = [value > 0 for value in per_box]
            m["present"] = bool((m["changedInside"] > 0) or
                                (m["markerExactInside"] > 0) or
                                any(v >= 4 for v in m["markerFamilyInside"]))

        attempt["verificationScenes"] = len(measurements)
        attempt["frameResults"] = measurements
        present = all(m["present"] for m in measurements) if measurements else False
        multi_all_present = True
        if len(imported) >= 2:
            multi_all_present = all(all(m.get("perBoxPresent") or []) for m in measurements)
        changed_inside = sum(m["changedInside"] for m in measurements)
        changed_outside = sum(m["changedOutside"] for m in measurements)
        marker_inside = sum(m["markerExactInside"] for m in measurements)
        marker_outside = sum(m["markerExactOutside"] for m in measurements)
        family_inside = sum(sum(m["markerFamilyInside"]) for m in measurements)
        attempt["changedPixelsInside"] = changed_inside
        attempt["changedPixelsOutside"] = changed_outside
        attempt["markerPixelsInside"] = marker_inside
        attempt["markerPixelsOutside"] = marker_outside
        attempt["markerFamilyPixelsInside"] = family_inside
        attempt["colorShiftedMarker"] = bool(marker_inside == 0 and family_inside > 0)
        attempt["sceneExact"] = next((m["sceneExact"] for m in measurements
                                      if m["pausedFrame"]), None)
        attempt["sceneMatchRatio"] = next((m["sceneMatchRatio"] for m in measurements
                                          if m["pausedFrame"]), None)
        attempt["originalRomIntegrity"] = "passed" if v71.sha256(rom["path"]) == rom_before else "failed"
        multi_scene = len(measurements) >= 2 and len({m["frame"] for m in measurements}) >= 2
        collateral = (changed_outside > 0) or (marker_outside > 0)
        use_plan_conditions = any((a.get("plan") or {}).get("conditionsApplied") for a in imported)
        primary_plan_status = (imported[0].get("plan") or {}).get("contextStatus") if imported else ""
        asset_kind = "background" if chosen["kind"] == "background" else "sprite"

        if not target_verify["hdActive"]:
            status = "failed-verify"
        elif attempt["originalRomIntegrity"] != "passed":
            status = "failed-integrity"
        elif not present:
            status = "failed-not-replaced"
        elif len(imported) >= 2 and not multi_all_present:
            status = "partial-multi-asset"
        elif attempt["sceneExact"] is False and attempt["sceneMatchRatio"] is not None and attempt["sceneMatchRatio"] < 0.98:
            status = "partial-scene-mismatch"
        elif not target_verify["stateIdentical"]:
            status = "partial-visual-state-mismatch"
        else:
            live = attempt.get("liveStatus") or {}
            active = int(live.get("gameplayFrames") or 0)
            samples = int(live.get("samples") or 0)
            active_ratio = (active / samples) if samples else 0.0
            if active_ratio >= 0.15:
                scene_class = "gameplay"
            elif attempt.get("backgroundFallback"):
                scene_class = "background-content"
            else:
                scene_class = "title-or-menu"
            attempt["sceneActivityClass"] = scene_class
            attempt["sceneActiveRatio"] = round(active_ratio, 4)
            if scene_class == "title-or-menu":
                # A real replacement on a title/menu screen remains a title/menu
                # result; it is never presented as gameplay evidence. The ratio is
                # sampling-normalised because the fast/minimal recorder samples
                # fewer frames than the balanced mode.
                status = "verified-title-or-menu-scene"
            else:
                status = classify_verified(
                    asset_kind, primary_plan_status, multi_scene, use_plan_conditions,
                    shared_total, collateral, len(imported))
        attempt["status"] = status
        attempt["failure"] = "" if status.startswith("verified") else status
        attempt["rom"] = rom["name"]
        attempt["sha256"] = rom["sha256"]
        attempt["pack"] = pack
        attempt["verifyFolder"] = target_verify["verifyDir"]
        attempt["evidence"] = {
            "pauseScreenshot": chosen.get("pauseScreenshot"),
            "hd": os.path.join(target_verify["verifyDir"], "frame_%05d_hd.png" % exact_frame)
            if os.path.exists(os.path.join(target_verify["verifyDir"], "frame_%05d_hd.png" % exact_frame)) else "",
        }
        if status.startswith("verified"):
            final = attempt
            break
        final = attempt

    if final is None:
        final = {"failure": "no-attempt", "status": "failed-no-attempt"}
    final = dict(final)
    if str(final.get("status", "")).startswith("failed") and attempts:
        all_probes = [p for a in attempts for p in (a.get("probes") or [])]
        max_active = max([p.get("activeFrames") or 0 for p in all_probes] or [0])
        max_sprites = max([p.get("spriteParts") or 0 for p in all_probes] or [0])
        max_background = max([p.get("backgroundCells") or 0 for p in all_probes] or [0])
        hashes = {p.get("screenHash") for p in all_probes if p.get("screenHash")}
        if max_active < 30 and max_sprites == 0:
            classification = "title-screen-stagnation"
        elif max_sprites == 0 and max_background > 0:
            classification = "background-only-graphics"
        elif max_sprites > 0:
            classification = "no-selectable-composition"
        elif len(hashes) <= 1:
            classification = "menu-navigation-failure"
        else:
            classification = "no-visible-sprites"
        final["classification"] = classification
        final["profilesAttempted"] = [a.get("profile") for a in attempts]
    final.setdefault("status", "failed-" + (final.get("failure") or "unknown"))
    final["rom"] = rom["name"]
    final["sha256"] = rom["sha256"]
    final["mapper"] = rom.get("mapper")
    final["chrType"] = rom.get("chrType")
    final["split"] = rom.get("split")
    final["attempts"] = [{k: v for k, v in attempt.items() if k != "attempts"}
                         for attempt in attempts]
    final["engineFingerprint"] = engine_fingerprint
    final["testConfig"] = {
        "budgets": args.budgets,
        "profiles": [p[0] for p in args.profiles],
        "scale": args.scale,
        "verifyScenes": args.verify_scenes,
        "targetMode": args.target_mode,
        "chrramConditions": args.chrram_conditions,
        "multiAssets": args.multi_assets,
        "baselineVerification": args.baseline,
        "seed": 20261124,
    }
    final["cacheHit"] = False
    final["processedAtUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    ev = os.path.join(work, "evidence")
    os.makedirs(ev, exist_ok=True)
    for key, src in (final.get("evidence") or {}).items():
        if src and os.path.exists(src):
            try:
                shutil.copy2(src, os.path.join(ev, key + ".png"))
            except OSError:
                pass
    v71.atomic_json(result_path, final)
    return final


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--base-home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--inventory", default=r"D:\MesenHighWork\validation-v07\inventory\library-inventory.json")
    ap.add_argument("--out-root", default=r"D:\MesenHighWork\validation-v08\replacements")
    ap.add_argument("--input-script", default=DEFAULT_PROFILES["default"],
                    help="default profile script (kept for compatibility)")
    ap.add_argument("--profiles", default="default;menu;alt",
                    help="deterministic input profiles in order (default;menu;alt)")
    ap.add_argument("--budgets", default="900;1800;3600;6000",
                    help="adaptive frame budgets probed in one capture")
    ap.add_argument("--target-mode", choices=("sprite", "background", "auto"), default="auto")
    ap.add_argument("--live-timeout", type=int, default=300)
    ap.add_argument("--scale", type=int, default=2)
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--selected", default="")
    ap.add_argument("--split", default="")
    ap.add_argument("--hash-file", default="")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--start", type=int, default=0)
    ap.add_argument("--verify-scenes", type=int, default=3)
    ap.add_argument("--multi-assets", type=int, default=1)
    ap.add_argument("--allow-shared", action="store_true", default=True)
    ap.add_argument("--no-allow-shared", dest="allow_shared", action="store_false")
    ap.add_argument("--allow-key-conflict", action="store_true")
    ap.add_argument("--no-combine-compile", dest="combine_compile", action="store_false", default=True,
                    help="do not rebuild a multi-asset pack from the project before verification")
    ap.add_argument("--baseline", choices=("auto", "always", "never"), default="auto",
                    help="baseline-HD verification: auto runs it only when exact marker "
                         "evidence is absent/ambiguous; always/never force it")
    ap.add_argument("--chrram-conditions", choices=("position", "content", "off"), default="position")
    ap.add_argument("--short-work", action="store_true")
    args = ap.parse_args()

    args.budgets = parse_budgets(args.budgets)
    args.profiles = parse_profiles(args.profiles)
    if not args.profiles:
        args.profiles = parse_profiles("default;menu;alt")
    if args.multi_assets < 1:
        args.multi_assets = 1

    doc = v71.load_json(args.inventory)
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
    v71.make_marker(marker_path)
    engine = {"binary": args.mesen, "home": args.base_home}
    parts = []
    for path in (args.mesen,
                 os.path.join(os.path.dirname(args.mesen), "Mesen.dll"),
                 os.path.join(os.path.dirname(args.mesen), "MesenCore.dll"),
                 os.path.abspath(__file__)):
        parts.append(v71.sha256(path)[:8] if os.path.exists(path) else "missing")
    fingerprint = "-".join(parts)

    print(json.dumps({"roms": len(roms), "workers": args.workers,
                      "out": os.path.abspath(args.out_root),
                      "engine": fingerprint, "budgets": args.budgets,
                      "profiles": [p[0] for p in args.profiles],
                      "multiAssets": args.multi_assets,
                      "baseline": args.baseline}, indent=2), flush=True)
    results = []
    started = time.time()
    import concurrent.futures
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {pool.submit(process_rom, rom, args, engine, marker_path, fingerprint): rom
                   for rom in roms}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            rom = futures[future]
            try:
                result = future.result()
            except Exception as ex:
                result = {"rom": rom["name"], "sha256": rom["sha256"],
                          "status": "failed-exception", "failure": str(ex), "attempts": []}
            results.append(result)
            print("[%d/%d] %s -> %s (budget %s, frame %s, changed %s/%s, marker %s/%s)" % (
                index, len(roms), rom["name"], result.get("status"),
                result.get("explorationBudget"), result.get("pauseFrame"),
                result.get("changedPixelsInside"), result.get("changedPixelsOutside"),
                result.get("markerPixelsInside"), result.get("markerPixelsOutside")), flush=True)
            v71.atomic_json(os.path.join(args.out_root, "replacement-results.partial.json"),
                            sorted(results, key=lambda r: r.get("rom", "").lower()))
    results.sort(key=lambda r: (r.get("rom") or "").lower())
    v71.atomic_json(os.path.join(args.out_root, "replacement-results.json"), results)
    counts = {}
    classifications = {}
    for r in results:
        counts[r.get("status")] = counts.get(r.get("status"), 0) + 1
        if r.get("classification"):
            classifications[r["classification"]] = classifications.get(r["classification"], 0) + 1
    summary = {
        "attempted": len(results),
        "statusCounts": counts,
        "classificationCounts": classifications,
        "verifiedTotal": sum(1 for r in results if str(r.get("status", "")).startswith("verified")),
        "verifiedBackground": sum(1 for r in results if str(r.get("status", "")).startswith("verified-background")),
        "lateScenes": sum(1 for r in results if r.get("lateScene")),
        "backgroundFallbacks": sum(1 for r in results if r.get("backgroundFallback")),
        "colorShiftedMarkers": sum(1 for r in results if r.get("colorShiftedMarker")),
        "wallSeconds": round(time.time() - started, 1),
        "workers": args.workers,
        "engineFingerprint": fingerprint,
    }
    v71.atomic_json(os.path.join(args.out_root, "replacement-summary.json"), summary)
    print(json.dumps(summary, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
