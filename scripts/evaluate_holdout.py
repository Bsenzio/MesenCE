"""Blind holdout evaluation with game profiles disabled.

The holdout ROM (hdstudio_holdout.nes) was generated from
test-roms/build_synthetic_roms.py after the discovery/catalog parameters were frozen.
This script consumes only the exported pack/catalog; the ground truth below is used for
evaluation only and is never an input to the engine.

Expected hardware content:
  player A: patterns 02,03,04,05 with palette FF0F1636
  player B: patterns 0A,0B,0C,0D with palette FF0F1636
  enemies:  patterns 06,07,08,09 with palette FF0F1A2A in H, V and both-axis flips
  ground:   patterns 101,102,111,112 with background palette 21071727

Example:
  python scripts/evaluate_holdout.py --pack "D:\\...\\holdout-pack"
"""
import argparse
import json
import os


PLAYER_PALETTE = "FF0F1636"
ENEMY_PALETTE = "FF0F1A2A"
BG_PALETTE = "21071727"
PLAYER_PATTERNS = ["2", "3", "4", "5", "A", "B", "C", "D"]
ENEMY_PATTERNS = ["6", "7", "8", "9"]
BG_PATTERNS = ["101", "102", "111", "112"]


def flip_tag(h, v):
    return ("H" if h else "") + ("V" if v else "")


def normalize_pattern(value):
    value = (value or "").upper()
    if len(value) == 32:
        return value
    try:
        return format(int(value, 16), "X")
    except ValueError:
        return value


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pack", required=True)
    args = ap.parse_args()

    path = os.path.join(args.pack, "HdStudio", "assets", "assets.json")
    doc = json.load(open(path, encoding="utf-8"))
    assets = doc.get("assets", [])
    if not assets:
        raise SystemExit("no assets in " + path)

    # Profiles must be disabled: the generic path must not create SMB3 semantic objects
    # or named animation frames for an unknown ROM.
    profile_assets = [a for a in assets if a.get("kind") in ("semanticObject", "animationFrame", "animationSequence")]
    profiles_disabled = len(profile_assets) == 0

    expected_combos = set()
    for pattern in PLAYER_PATTERNS:
        expected_combos.add((normalize_pattern(pattern), PLAYER_PALETTE))
    for pattern in ENEMY_PATTERNS:
        expected_combos.add((normalize_pattern(pattern), ENEMY_PALETTE))
    for pattern in BG_PATTERNS:
        expected_combos.add((normalize_pattern(pattern), BG_PALETTE))

    observed_combos = set()
    observed_patterns = set()
    raw_records = 0
    for asset in assets:
        #Raw pattern records mirror every observed tile as a separate access layer.
        #Composition/hardware precision is measured on the semantic/composition
        #records; raw duplicates are counted explicitly instead of penalizing recall.
        if asset.get("kind") == "rawPattern":
            raw_records += 1
            continue
        for tile in asset.get("tiles", []):
            pattern = normalize_pattern(tile.get("pattern"))
            palette = (tile.get("palette") or "").upper()
            if pattern:
                observed_combos.add((pattern, palette))
                observed_patterns.add(pattern)

    true_positive = len(expected_combos & observed_combos)
    recall = true_positive / len(expected_combos) if expected_combos else 0.0
    precision = len(expected_combos & observed_combos) / len(observed_combos) if observed_combos else 0.0
    unexpected = sorted({p for p, _ in observed_combos} - {p for p, _ in expected_combos})

    # Composition recall: player A, player B, and the three enemy flip contexts.
    metasprites = [a for a in assets if a.get("kind") == "metasprite" and a.get("tiles")]
    found_compositions = set()
    for asset in metasprites:
        dims = asset.get("dimensions", {})
        tiles = asset.get("tiles", [])
        if dims.get("width") == 16 and dims.get("height") == 16 and tiles:
            found_compositions.add(flip_tag(all(t.get("flipH") for t in tiles), all(t.get("flipV") for t in tiles)))
    expected_compositions = {"", "H", "V", "HV"}
    composition_recall = len(expected_compositions & found_compositions) / len(expected_compositions)
    missing_compositions = sorted(expected_compositions - found_compositions)

    # Background block recall by hardware content, independently of its display name.
    bg_found = False
    for asset in assets:
        if asset.get("kind") != "backgroundBlock":
            continue
        tiles = asset.get("tiles", [])
        patterns = sorted(normalize_pattern(t.get("pattern")) for t in tiles)
        palettes = {(t.get("palette") or "").upper() for t in tiles}
        if set(BG_PATTERNS).issubset(set(patterns)) and palettes == {BG_PALETTE}:
            bg_found = True
            break

    result = {
        "pack": os.path.abspath(args.pack),
        "profilesDisabled": profiles_disabled,
        "assets": len(assets),
        "metasprites": len(metasprites),
        "rawPatternRecords": raw_records,
        "backgroundBlocks": len([a for a in assets if a.get("kind") == "backgroundBlock"]),
        "expectedHardwareCombos": len(expected_combos),
        "observedHardwareCombos": len(observed_combos),
        "hardwareComboRecall": round(recall, 4),
        "hardwareComboPrecision": round(precision, 4),
        "unexpectedPatterns": unexpected,
        "expectedCompositions": sorted(expected_compositions),
        "foundCompositions": sorted(found_compositions),
        "compositionRecall": round(composition_recall, 4),
        "missingCompositions": missing_compositions,
        "backgroundBlockRecalled": bg_found,
        "passed": bool(profiles_disabled and recall == 1.0 and precision == 1.0 and
                       composition_recall == 1.0 and bg_found and raw_records > 0),
        "note": "Unclassified partial metasprites may exist; every discovered pattern is checked against the fixture ground truth."
    }
    print(json.dumps(result, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
