"""Offline v0.9 archaeology fixtures.

Runs the native archaeology self-test and an end-to-end static+observed catalog
on the synthetic fixture. No commercial ROM, emulator session or network access
is used. The tests cover: evidence graph persistence/determinism, CHR/RAM
correlation, catalog schema, approved-artwork preservation, gap honesty and
batch resume.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile

import hdstudio_v09_inventory as inv  # noqa: E402


def check(name, condition, detail=""):
    if not condition:
        raise AssertionError(name + (": " + detail if detail else ""))
    print("PASS", name)


def run(mesen, args, env, timeout=300):
    proc = subprocess.run([mesen, "--hdstudio"] + args, env=env, stdout=subprocess.PIPE,
                          stderr=subprocess.STDOUT, text=True, timeout=timeout, encoding="utf-8", errors="replace")
    return proc.returncode, proc.stdout or ""


def read_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def make_capture(pack, rom):
    studio = os.path.join(pack, "HdStudio")
    os.makedirs(os.path.join(studio, "assets"), exist_ok=True)
    open(os.path.join(studio, "tiles.txt"), "w", encoding="utf-8").write(
        "#tile\tpalette\tpixelCount\n01\t0F001020\t1234\n02\t0F001020\t99\n1F\t0F1A2A3A\t44\n")
    open(os.path.join(studio, "capture.txt"), "w", encoding="utf-8").write(
        "frames=120\nscale=2\nchrRam=0\nrom=selftest\n")
    open(os.path.join(studio, "palettes.txt"), "w", encoding="utf-8").write(
        "#palette\tuses\tfirstOffset\n0F001020\t3\t10\n")
    with open(os.path.join(studio, "assets", "assets.json"), "w", encoding="utf-8") as f:
        json.dump({
            "schemaVersion": "1.0.0",
            "generatedBy": "v0.9 fixture",
            "game": {"romSha1": "FIXTURE", "romSha256": "FIXTURE", "mapper": 0, "hasChrRom": True},
            "assets": [{"id": "approved:fixture-sentinel", "kind": "rawPattern", "name": "Approved sentinel"}],
            "replacementAssets": [],
            "replacementConditions": [],
        }, f)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home-v09")
    ap.add_argument("--rom", default=r"D:\MesenHighWork\test-roms\hdstudio_test.nes")
    args = ap.parse_args()
    env = os.environ.copy()
    env["MESEN_HOME"] = args.home
    if not os.path.exists(args.rom):
        raise SystemExit("fixture ROM not found: " + args.rom)

    code, out = run(args.mesen, ["--arch-selftest"], env)
    line = next((l for l in out.splitlines() if l.startswith("ARCH_SELFTEST_JSON")), "")
    check("selftest-exit", code == 0, out[-2000:])
    check("selftest-json", bool(line), "missing ARCH_SELFTEST_JSON")
    report = json.loads(line.split(" ", 1)[1])
    check("selftest-pass", report.get("failed") == 0 and report.get("passed", 0) >= 20,
          "passed=%s failed=%s" % (report.get("passed"), report.get("failed")))

    with tempfile.TemporaryDirectory(prefix="hdstudio-v09-fixture-") as tmp:
        pack = os.path.join(tmp, "pack")
        os.makedirs(pack)
        make_capture(pack, args.rom)
        catalog = os.path.join(pack, "HdStudio", "archaeology", "catalog.json")
        graph = os.path.join(pack, "HdStudio", "archaeology", "evidence-graph.json")

        code, out = run(args.mesen, [args.rom, "--archaeology", "--arch-mode=deep",
                                     "--arch-evidence=" + pack, "--out=" + pack], env)
        check("archaeology-exit", code == 0, out[-3000:])
        check("catalog-exists", os.path.exists(catalog), catalog)
        check("graph-exists", os.path.exists(graph), graph)
        cat = read_json(catalog)
        summary = cat["summary"]
        check("catalog-schema", cat.get("schemaVersion") == "1.0.0" and "assets" in cat and "gaps" in cat)
        check("chr-inventory", summary["uniquePatterns"] > 0 and summary["chrPages"] > 0,
              json.dumps(summary))
        check("observed", summary["observedChrRomPatterns"] > 0 and summary["assetsObserved"] > 0,
              json.dumps(summary))
        check("source-provenance", all("physical" in a["provenance"].lower() or "runtime" in a["provenance"].lower()
                                       for a in cat["assets"][:10]))
        check("honest-gaps", any(g["kind"] == "map-not-reconstructed" for g in cat["gaps"]) and
              any(g["kind"] == "animation-not-reconstructed" for g in cat["gaps"]))
        assets_path = os.path.join(pack, "HdStudio", "assets", "assets.json")
        assets = read_json(assets_path)
        ids = {a["id"] for a in assets["assets"]}
        check("approved-preserved", "approved:fixture-sentinel" in ids)
        check("assets-merged", any(a["id"].startswith("rawPattern:") for a in assets["assets"]))
        first_ids = [a["id"] for a in cat["assets"]]
        graph_doc = read_json(graph)
        check("graph-nodes", len(graph_doc["nodes"]) > 10 and len(graph_doc["edges"]) > 0)
        check("graph-runtime-status", any(n["status"] == "runtime-observed" for n in graph_doc["nodes"]))

        code, out = run(args.mesen, [args.rom, "--archaeology", "--arch-mode=deep",
                                     "--arch-evidence=" + pack, "--out=" + pack], env)
        check("archaeology-rerun", code == 0, out[-2000:])
        cat2 = read_json(catalog)
        second_ids = [a["id"] for a in cat2["assets"]]
        check("asset-id-determinism", first_ids == second_ids,
              "%d vs %d" % (len(first_ids), len(second_ids)))

        batch_root = os.path.join(tmp, "batch")
        batch_out = os.path.join(tmp, "batch-out")
        os.makedirs(batch_root)
        shutil.copyfile(args.rom, os.path.join(batch_root, "a.nes"))
        data = bytearray(open(args.rom, "rb").read())
        data[-1] ^= 0x5A
        open(os.path.join(batch_root, "b.nes"), "wb").write(data)
        code, out = run(args.mesen, ["--arch-batch=" + batch_root, "--arch-out-root=" + batch_out,
                                     "--arch-mode=deep", "--arch-parallel=2"], env, timeout=600)
        check("batch-exit", code == 0, out[-2000:])
        batch = read_json(os.path.join(batch_out, "archaeology-batch.json"))
        check("batch-complete", batch["completed"] == 2 and batch["errors"] == 0,
              json.dumps({k: batch[k] for k in ("attempted", "completed", "errors")}))
        code, out = run(args.mesen, ["--arch-batch=" + batch_root, "--arch-out-root=" + batch_out,
                                     "--arch-mode=deep", "--arch-parallel=2"], env, timeout=600)
        batch2 = read_json(os.path.join(batch_out, "archaeology-batch.json"))
        check("batch-resume", batch2["skipped"] == 2 and batch2["completed"] == 0,
              json.dumps({k: batch2[k] for k in ("completed", "skipped")}))

        #Inventory fixtures: iNES/NES 2.0 header, duplicate and quarantine logic.
        inv_root = os.path.join(tmp, "inventory-root")
        os.makedirs(inv_root)
        shutil.copyfile(args.rom, os.path.join(inv_root, "one.nes"))
        shutil.copyfile(args.rom, os.path.join(inv_root, "two.nes"))
        summary2, items, new_unique, _ = inv.scan(argparse.Namespace(root=inv_root, frozen="", workers=2))
        check("inventory-duplicate", summary2["scannedFiles"] == 2 and summary2["uniqueHashes"] == 1
              and summary2["duplicateFiles"] == 1, json.dumps(summary2))
        check("inventory-header", items[0]["headerValid"] and items[0]["prgBytes"] > 0)

    print("V09_FIXTURES_OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
