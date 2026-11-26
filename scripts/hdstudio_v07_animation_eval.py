"""Runtime animation-frame verification for RetroArcDo HD Studio v0.7.

Scans existing live-capture packs, selects observed animation sequences with at
least two frames, proposes standard context rules for one frame, imports test
artwork and replays the captured frames. An animation row only counts when the
marker is present inside every expected occurrence footprint in at least two
distinct captured frames and the gameplay state hashes remain identical.

This is a validation harness, not a game-specific profile. It does not alter the
ROM, animation order, timing or logical tile dimensions.
"""
import argparse
import concurrent.futures
import json
import os
import re
import shutil
import subprocess
import tempfile
import time

from PIL import Image

MARKERS = {(255, 0, 255), (0, 255, 255), (0, 255, 0)}


def load_json(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def atomic_json(path, data):
    tmp = path + ".tmp"
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    os.replace(tmp, path)


def run(engine, args, timeout, home):
    env = os.environ.copy()
    env["MESEN_HOME"] = home
    try:
        p = subprocess.run([engine] + args, env=env, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                           errors="replace", timeout=timeout)
        return p.returncode, p.stdout or ""
    except subprocess.TimeoutExpired as ex:
        out = ex.stdout or ""
        if isinstance(out, bytes):
            out = out.decode("utf-8", "replace")
        return None, out


def make_marker(path):
    im = Image.new("RGBA", (64, 64), (255, 0, 255, 255))
    from PIL import ImageDraw
    d = ImageDraw.Draw(im)
    d.rectangle([4, 4, 59, 59], fill=(0, 255, 255, 255))
    d.rectangle([16, 12, 44, 22], fill=(0, 255, 0, 255))
    im.save(path)


def prepare_home(base, temp):
    os.makedirs(temp, exist_ok=True)
    for name in ("settings.json", "MesenNesDB.txt", "MesenCore.dll",
                 "libSkiaSharp.dll", "libHarfBuzzSharp.dll"):
        src = os.path.join(base, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(temp, name))


def marker_metrics(original, hd, boxes):
    if not (os.path.exists(original) and os.path.exists(hd)):
        return None
    o = Image.open(original).convert("RGB")
    h = Image.open(hd).convert("RGB")
    scale = h.width // o.width if o.width else 4
    inside = outside = 0
    changed_inside = changed_outside = 0
    down = h.resize(o.size, Image.Resampling.NEAREST)
    def inside_box(x, y):
        return any(bx <= x < bx + bw and by <= y < by + bh for bx, by, bw, bh in boxes)
    for y in range(h.height):
        for x in range(h.width):
            if h.getpixel((x, y)) in MARKERS:
                if inside_box(x // scale, y // scale):
                    inside += 1
                else:
                    outside += 1
    for y in range(o.height):
        for x in range(o.width):
            if down.getpixel((x, y)) != o.getpixel((x, y)):
                if inside_box(x, y):
                    changed_inside += 1
                else:
                    changed_outside += 1
    return {"inside": inside, "outside": outside,
            "changedInside": changed_inside, "changedOutside": changed_outside}


def rom_lookup(inventory):
    doc = load_json(inventory, {}) or {}
    result = {}
    for rom in doc.get("roms", []):
        stem = os.path.splitext(rom.get("name", ""))[0]
        result.setdefault(stem.lower(), rom)
    return result


def process_pack(pack, rom, args, marker, engine_home):
    assets_path = os.path.join(pack, "HdStudio", "assets", "assets.json")
    assets_doc = load_json(assets_path, {}) or {}
    assets = assets_doc.get("assets") or []
    sequences = [a for a in assets if a.get("kind") == "animationSequence"]
    best = None
    for sequence in sequences:
        frames = [a for a in assets if a.get("kind") == "animationFrame"
                  and a.get("animationId") == sequence.get("id")
                  and (a.get("tiles") or [])
                  and (a.get("dimensions", {}).get("width") or 0) <= 128
                  and (a.get("dimensions", {}).get("height") or 0) <= 128]
        if len(frames) < 2:
            continue
        #Prefer sequences with more frames and a large visible frame.
        score = len(frames) * 1000 + max((a.get("dimensions", {}).get("width", 0) *
                                         a.get("dimensions", {}).get("height", 0)) for a in frames)
        if best is None or score > best[0]:
            best = (score, sequence, frames)
    if not best:
        return {"status": "skipped-no-multiframe-sequence", "pack": pack, "rom": rom.get("name")}
    _, sequence, frames = best
    chosen = max(frames, key=lambda a: (a.get("dimensions", {}).get("width", 0) *
                                       a.get("dimensions", {}).get("height", 0)))
    stem = os.path.splitext(rom["name"])[0]
    work = tempfile.mkdtemp(prefix="hdstudio-anim-")
    home = os.path.join(work, "home")
    prepare_home(engine_home, home)
    out_pack = os.path.join(home, "HdPacks", stem)
    os.makedirs(out_pack, exist_ok=True)
    shutil.copytree(os.path.join(pack, "HdStudio"), os.path.join(out_pack, "HdStudio"), dirs_exist_ok=True)
    plan_path = os.path.join(work, "plan.json")
    code, plan_out = run(args.mesen, ["--hdstudio", "--asset=" + chosen["id"],
                                     "--propose-context=" + plan_path,
                                     "--out=" + out_pack], 120, home)
    plan = load_json(plan_path, {}) or {}
    #Animation verification primarily measures runtime frame presence. The plan is
    #still reported, but an unresolved/shared condition must not hide the observed
    #animation. The import is unconditional and the row stays explicitly shared.
    if args.use_conditions and plan.get("status") in ("unresolved-context", "shared-context") and not args.allow_shared:
        shutil.rmtree(work, ignore_errors=True)
        return {"status": "failed-context-requires-approval", "pack": pack, "rom": rom.get("name"),
                "sequence": sequence.get("name"), "sequenceFrames": len(frames)}
    for name in os.listdir(out_pack):
        p = os.path.join(out_pack, name)
        if os.path.isfile(p):
            try:
                os.remove(p)
            except OSError:
                pass
    import_args = ["--hdstudio", rom["path"], "--import", "--asset=" + chosen["id"],
                   "--image=" + marker, "--fit=stretch", "--all-palettes",
                   "--minimal", "--import-name=AnimTest", "--out=" + out_pack]
    if args.use_conditions and plan.get("cellConditions"):
        import_args.append("--condition-map=" + plan_path)
    code, import_out = run(args.mesen, import_args, 300, home)
    if code != 0:
        shutil.rmtree(work, ignore_errors=True)
        return {"status": "failed-import", "pack": pack, "rom": rom.get("name"),
                "sequence": sequence.get("name"), "log": import_out[-1000:]}
    targets = plan.get("targetFrames") or []
    chosen_targets = [t for t in targets if t.get("pausedFrame")][:1]
    others = [t for t in targets if not t.get("pausedFrame")]
    if others:
        #Spread additional sampled frames; the target frame asset need not be
        #visible in every sampled frame of the sequence.
        for index in range(min(len(others), max(0, args.max_frames - len(chosen_targets)))):
            target = others[round(index * (len(others) - 1) / max(1, min(len(others), args.max_frames) - 1))] if len(others) > 1 else others[0]
            if all(t["frame"] != target["frame"] for t in chosen_targets):
                chosen_targets.append(target)
    if not chosen_targets:
        chosen_targets = [{"frame": 1200, "x": 0, "y": 0, "width": 8, "height": 8, "bboxes": []}]
    verify_frames = sorted({int(t["frame"]) for t in chosen_targets})
    code, verify_out = run(args.mesen, ["--hdstudio", rom["path"],
                                        args.input_script, "--verify", "--shots=1",
                                        "--frames=" + str(max(verify_frames)),
                                        "--verify-at=" + ",".join(map(str, verify_frames)),
                                        "--out=" + out_pack], 600, home)
    verify_dir = os.path.join(out_pack, "HdStudio", "verify")
    results = []
    for target in chosen_targets:
        frame = int(target["frame"])
        boxes = [tuple(int(v) for v in b) for b in (target.get("bboxes") or [])]
        if not boxes:
            boxes = [(int(target.get("x", 0)), int(target.get("y", 0)),
                      int(target.get("width", 8)), int(target.get("height", 8)))]
        metrics = marker_metrics(os.path.join(verify_dir, "frame_%05d_original.png" % frame),
                                 os.path.join(verify_dir, "frame_%05d_hd.png" % frame), boxes)
        if metrics:
            metrics["frame"] = frame
            results.append(metrics)
    hd_active = code == 0 and "HD pack ACTIVE" in verify_out
    state_identical = "Gameplay state comparison: IDENTICAL" in verify_out
    good = [r for r in results if r["inside"] > 0 and r["outside"] == 0]
    outside = sum(r["outside"] for r in results)
    distinct_frames = len({r["frame"] for r in good})
    if not hd_active:
        status = "failed-verify"
    elif not state_identical:
        status = "partial-state-mismatch"
    elif not good:
        status = "failed-frame-not-replaced"
    elif outside > 0:
        status = "verified-animation-shared"
    elif distinct_frames >= 2:
        status = "verified-animation-multi-frame"
    else:
        status = "verified-animation-single-frame"
    result = {
        "status": status, "rom": rom.get("name"), "sha256": rom.get("sha256"),
        "pack": pack, "sequence": sequence.get("name"),
        "sequenceId": sequence.get("id"), "sequenceFrames": len(frames),
        "testedAsset": chosen.get("id"), "testedFrameIndex": chosen.get("frameIndex"),
        "verifiedDistinctFrames": distinct_frames, "verifiedTargetFrames": [t["frame"] for t in chosen_targets],
        "planStatus": plan.get("status"), "planScenes": plan.get("scenes"),
        "contextRule": plan.get("contextRule"), "frameResults": results,
        "hdPackActive": hd_active, "stateIdentical": state_identical,
    }
    shutil.rmtree(work, ignore_errors=True)
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--root", default=r"D:\MesenHighWork\validation-v06\deep-1800")
    ap.add_argument("--inventory", default=r"D:\MesenHighWork\validation-v07\inventory\library-inventory.json")
    ap.add_argument("--input-script", default=r"D:\MesenHigh\scripts\deterministic_input.lua")
    ap.add_argument("--out", default=r"D:\MesenHighWork\validation-v07\animation-results.json")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--max-frames", type=int, default=2)
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--allow-shared", action="store_true")
    ap.add_argument("--use-conditions", action="store_true",
                    help="apply proposed per-cell conditions; default is the runtime animation baseline")
    args = ap.parse_args()
    lookup = rom_lookup(args.inventory)
    marker = os.path.join(os.path.dirname(args.out), "animation-marker.png")
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    make_marker(marker)
    packs = []
    for root, _, files in os.walk(args.root):
        if "assets.json" in files and os.path.basename(root) == "assets":
            pack = os.path.dirname(os.path.dirname(root))
            folder = os.path.basename(pack)
            #v0.6 deep folders are <hash12>_<stem>; v0.7 attempt packs are
            #already named after the ROM stem under HdPacks/.
            stem = folder.split("_", 1)[-1] if re.match(r"^[0-9a-fA-F]{12}_", folder) else folder
            rom = lookup.get(stem.lower())
            if rom:
                packs.append((pack, rom))
    packs.sort(key=lambda p: p[1].get("name", ""))
    if args.limit:
        packs = packs[:args.limit]
    print(json.dumps({"packs": len(packs), "workers": args.workers}, indent=1), flush=True)
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {pool.submit(process_pack, pack, rom, args, marker, args.home): (pack, rom) for pack, rom in packs}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            pack, rom = futures[future]
            try:
                result = future.result()
            except Exception as ex:
                result = {"status": "failed-exception", "rom": rom.get("name"), "pack": pack, "error": str(ex)}
            results.append(result)
            print("[%d/%d] %s -> %s" % (index, len(packs), rom.get("name"), result.get("status")), flush=True)
            atomic_json(args.out + ".partial.json", sorted(results, key=lambda r: r.get("rom", "")))
    results.sort(key=lambda r: (r.get("rom") or "").lower())
    counts = {}
    for r in results:
        counts[r.get("status")] = counts.get(r.get("status"), 0) + 1
    summary = {
        "packs": len(packs),
        "statusCounts": counts,
        "verifiedMultiFrame": sum(1 for r in results if r.get("status") == "verified-animation-multi-frame"),
        "verifiedAny": sum(1 for r in results if str(r.get("status", "")).startswith("verified")),
        "testedSequences": sum(1 for r in results if r.get("sequence")),
        "wallSeconds": None,
    }
    atomic_json(args.out, {"summary": summary, "rows": results})
    print(json.dumps(summary, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
