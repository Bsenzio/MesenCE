"""Offline v0.8 fixtures: adaptive budgets, baseline diff, colour-shift detection,
frameRange phase selection, animation pack syntax and multi-asset classification.

No ROM or emulator is needed.
"""
import os
import sys
import tempfile

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hdstudio_v08_autoreplace as v8  # noqa: E402
import hdstudio_v08_animation_verify as av  # noqa: E402


def check(name, condition, detail=""):
    if not condition:
        raise AssertionError(name + (": " + detail if detail else ""))
    print("PASS", name)


def test_budgets():
    check("budgets", v8.parse_budgets("900;1800;3600;6000") == [900, 1800, 3600, 6000])
    check("budgets-dedupe", v8.parse_budgets("900,900;1800") == [900, 1800])
    profiles = v8.parse_profiles("default;menu;alt")
    check("profiles", [p[0] for p in profiles] == ["default", "menu", "alt"])
    check("profiles-unknown", v8.parse_profiles("default;nope") == [("default", v8.DEFAULT_PROFILES["default"])])


def test_color_shift_and_baseline():
    with tempfile.TemporaryDirectory() as tmp:
        # Emphasis-dimmed marker: exact colours are absent, family colours remain.
        target = Image.new("RGB", (64, 64), (0, 0, 0))
        px = target.load()
        for y in range(16, 32):
            for x in range(16, 48):
                px[x, y] = (255, 0, 229) if x < 32 else (0, 229, 229)
        target_path = os.path.join(tmp, "target.png")
        target.save(target_path)
        exact, outside, family = v8.exact_and_family(target, [[8, 8, 16, 16]], 2)
        check("color-shift-exact", exact == 0, "exact should be absent")
        check("color-shift-family", sum(family) > 100, "family marker should remain")
        baseline = Image.new("RGB", (64, 64), (0, 0, 0))
        baseline_path = os.path.join(tmp, "baseline.png")
        baseline.save(baseline_path)
        diff = v8.baseline_diff(target_path, baseline_path, [[8, 8, 16, 16]], 2)
        check("baseline-inside", diff["changedInside"] > 100)
        check("baseline-outside", diff["changedOutside"] == 0)
        changed_out = Image.new("RGB", (64, 64), (0, 0, 0))
        changed_out.putpixel((1, 1), (255, 0, 0))
        changed_out_path = os.path.join(tmp, "changed.png")
        changed_out.save(changed_out_path)
        diff2 = v8.baseline_diff(changed_out_path, baseline_path, [[8, 8, 16, 16]], 2)
        check("baseline-collateral", diff2["changedOutside"] > 0)


def eval_phase_expr(expr, frame):
    for part in expr.split("&"):
        part = part.strip()
        inverted = part.startswith("!")
        if inverted:
            part = part[1:]
        if not part.startswith("frameRange:"):
            return False
        a, b = [int(v) for v in part.split(":", 1)[1].split(",")]
        value = (frame % a) >= b
        if inverted:
            value = not value
        if not value:
            return False
    return True


def test_phase_conditions():
    for frames in ([322, 384], [1000, 1040, 1120], [500, 700, 900, 1400]):
        condition, reason = av.phase_conditions(frames)
        check("phase-%s" % frames, condition is not None, reason)
        period = condition["period"]
        phases = condition["phases"]
        ordered = sorted(set(phases))
        check("phase-distinct-%s" % frames, len(set(phases)) == len(frames))
        for i, observed in enumerate(frames):
            expr = condition["expressions"][i]
            check("phase-own-%d-%d" % (frames[0], observed), eval_phase_expr(expr, observed - 1),
                  "expression %s" % expr)
            for j, other in enumerate(frames):
                if i == j:
                    continue
                check("phase-other-%d-%d-%d" % (frames[0], observed, other),
                      not eval_phase_expr(expr, other - 1), "expression %s at %d" % (expr, other))


def test_animation_pack_syntax():
    asset = {
        "dimensions": {"width": 16, "height": 8},
        "tiles": [
            {"x": 0, "y": 0, "pattern": "1101", "palette": "FF0F0F0F", "chrType": "NesChrRom"},
            {"x": 8, "y": 0, "pattern": "1102", "palette": "FF0F0F0F", "chrType": "NesChrRom"},
        ],
    }
    frames = [322, 384]
    condition, _ = av.phase_conditions(frames)
    with tempfile.TemporaryDirectory() as tmp:
        # build_pack uses sha1 of the ROM; give it a tiny fixture file.
        rom_path = os.path.join(tmp, "rom.nes")
        open(rom_path, "wb").write(b"NES\x1a")
        av.build_pack(tmp, {"path": rom_path}, frames, [asset, asset],
                      [[80, 81, 16, 8], [64, 65, 16, 8]], 2, tmp, condition)
        text = open(os.path.join(tmp, "hires.txt"), encoding="utf-8").read()
        check("anim-frameRange", ",frameRange," in text)
        check("anim-inverted", "!" in text and "<condition>" in text)
        check("anim-sheets", os.path.exists(os.path.join(tmp, "AnimFrame0.png")))
        check("anim-tiles", text.count("<tile>") == 4)


def test_classification():
    probes = [{"activeFrames": 0, "spriteParts": 0, "backgroundCells": 0, "screenHash": "A"}]
    check("class-title", v8.classify_no_target(probes, "default", []) == "title-screen-stagnation")
    probes = [{"activeFrames": 200, "spriteParts": 0, "backgroundCells": 100, "screenHash": "A"}]
    check("class-background", v8.classify_no_target(probes, "default", []) == "background-only-graphics")
    check("class-multi-shared", v8.classify_verified("sprite", "", True, False, 0, True, 2)
          == "verified-multi-asset-shared")
    check("class-multi", v8.classify_verified("sprite", "", True, False, 0, False, 2)
          == "verified-multi-asset-combined")


def test_overlap_and_marker():
    check("overlap-true", v8.overlaps([0, 0, 16, 16], [8, 8, 16, 16]))
    check("overlap-false", not v8.overlaps([0, 0, 16, 16], [32, 32, 16, 16]))


def main():
    test_budgets()
    test_color_shift_and_baseline()
    test_phase_conditions()
    test_animation_pack_syntax()
    test_classification()
    test_overlap_and_marker()
    print("V08_FIXTURES_OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
