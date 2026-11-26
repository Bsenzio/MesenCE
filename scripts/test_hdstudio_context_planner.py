"""Offline regression for the v0.7 context planner.

Builds a tiny synthetic HD Studio evidence folder and invokes the real
`--propose-context` code path. It checks that:
  * a vertical two-tile composition is found in more than one captured scene;
  * the generated expression uses only parser-supported standard syntax;
  * render-order-impossible below/right neighbours are never emitted;
  * the plan distinguishes intended cells from an unrelated same-key sprite.

No commercial ROM or emulator run is required.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

MESEN = os.environ.get("MESEN_BIN", r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
HOME = os.environ.get("MESEN_HOME", r"D:\MesenHighWork\mesen-home")
PALETTE = "0F362737"
A, B = "41", "42"


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if isinstance(data, str):
        open(path, "w", encoding="utf-8").write(data)
    else:
        open(path, "w", encoding="utf-8").write(json.dumps(data, indent=1))


def sprite(frame, slot, x, y, key):
    return (f"{frame}\t{slot}\t0\t{x}\t{y}\t{int(key, 16)}\t{PALETTE}\t0\t0\t0\t"
            f"{key}\tNesChrRom\t{int(key, 16) * 16 - 0x100}\n")


def main():
    root = tempfile.mkdtemp(prefix="hdstudio-context-")
    pack = os.path.join(root, "pack")
    live = os.path.join(pack, "HdStudio", "live")
    assets = os.path.join(pack, "HdStudio", "assets")
    os.makedirs(live, exist_ok=True)
    os.makedirs(assets, exist_ok=True)

    write(os.path.join(assets, "assets.json"), {
        "schemaVersion": "1.2.0",
        "game": {"romSha1": "0" * 40, "mapper": 0, "hasChrRom": True},
        "assets": [{
            "id": "picked:testcomposition",
            "kind": "pickedObject",
            "name": "Vertical test composition",
            "source": "liveCapture",
            "provenance": "synthetic regression",
            "confidence": 1.0,
            "dimensions": {"width": 8, "height": 16, "originX": 10, "originY": 20},
            "paletteVariants": [PALETTE],
            "tiles": [
                {"pattern": A, "chrType": "NesChrRom", "x": 0, "y": 0,
                 "flipH": False, "flipV": False, "backgroundPriority": False, "palette": PALETTE},
                {"pattern": B, "chrType": "NesChrRom", "x": 0, "y": 8,
                 "flipH": False, "flipV": False, "backgroundPriority": False, "palette": PALETTE}
            ]
        }]
    })

    pause = {
        "schemaVersion": "1.0.0", "frame": 100, "screenshot": "",
        "scrollX": 0, "scrollY": 0, "mirroring": "Horizontal", "largeSprites": False,
        "spritePatternAddr": 0, "backgroundPatternAddr": 4096, "mapperId": 0,
        "backgroundEvidence": "render-time",
        "sprites": [
            {"renderSlot": 0, "part": 0, "x": 10, "y": 20, "tile": int(A, 16), "palette": PALETTE,
             "flipH": False, "flipV": False, "backgroundPriority": False,
             "pattern": A, "chrType": "NesChrRom", "chrAddress": int(A, 16) * 16, "data": "FF" * 16,
             "evidence": "render-time"},
            {"renderSlot": 1, "part": 0, "x": 10, "y": 28, "tile": int(B, 16), "palette": PALETTE,
             "flipH": False, "flipV": False, "backgroundPriority": False,
             "pattern": B, "chrType": "NesChrRom", "chrAddress": int(B, 16) * 16, "data": "FF" * 16,
             "evidence": "render-time"}
        ],
        "background": []
    }
    write(os.path.join(live, "pause-frame.json"), pause)

    rows = ["#frame\toam\tpart\tx\ty\ttile\tpalette\tflipH\tflipV\tpriority\tpattern\tchrType\tchrAddr\n"]
    for frame, ox, oy in ((100, 10, 20), (200, 50, 30)):
        rows.append(sprite(frame, 0, ox, oy, A))
        rows.append(sprite(frame, 1, ox, oy + 8, B))
    #An unrelated B with no A above it, in the second scene.
    rows.append(sprite(200, 20, 100, 100, B))
    write(os.path.join(live, "live-sprites.tsv"), "".join(rows))
    write(os.path.join(live, "live-background-frames.tsv"),
          "#frame\tx\ty\ttileX\ttileY\tmapX\tmapY\ttile\tpalette\tpattern\tchrType\tchrAddr\tdata\tscrollX\tscrollY\tnametable\tnametableAddress\n")

    plan_path = os.path.join(pack, "HdStudio", "context-plan.json")
    env = os.environ.copy()
    env["MESEN_HOME"] = HOME
    proc = subprocess.run([MESEN, "--hdstudio", "--asset=picked:testcomposition",
                           "--propose-context=" + plan_path, "--out=" + pack],
                          env=env, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", timeout=120)
    if not os.path.exists(plan_path):
        print(proc.stdout)
        print(proc.stderr)
        raise SystemExit("context plan was not written")
    plan = json.load(open(plan_path, encoding="utf-8"))

    def check(condition, message):
        if not condition:
            raise AssertionError(message)

    check(plan.get("status") in ("contextually-isolated-multi-scene", "strict-independent"),
          "expected a multi-scene isolated result, got " + str(plan.get("status")))
    check(plan.get("scenes") >= 2, "expected at least two scenes")
    check(plan.get("intendedMissed") == 0, "intended occurrences were missed")
    check(plan.get("unrelatedMatched") == 0, "unrelated cells matched the rule")
    expressions = " & ".join((plan.get("cellConditions") or {}).values())
    check("spriteNearby:" in expressions, "a composition neighbour rule was expected")
    for expression in (plan.get("cellConditions") or {}).values():
        for token in expression.split("&"):
            if token.startswith("spriteNearby:") or token.startswith("tileNearby:"):
                _, rest = token.split(":", 1)
                args = rest.split(",")
                dx, dy = int(args[0]), int(args[1])
                check(not (dy > 0 or (dy == 0 and dx > 0)),
                      "render-order-impossible below/right neighbour was emitted: " + token)
                check(args[2].upper() == A or args[2].upper() == B,
                      "unexpected condition key: " + args[2])
    print(json.dumps({"status": plan.get("status"), "scenes": plan.get("scenes"),
                      "rules": len(plan.get("cellConditions") or {}),
                      "intended": plan.get("intendedMatched"),
                      "unrelated": plan.get("unrelatedMatched")}, indent=1))
    print("context planner regression: PASS")
    shutil.rmtree(root, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
