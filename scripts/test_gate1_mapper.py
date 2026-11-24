"""Gate 1 regression test: mapper-aware object capture.

Runs the SMB3 forced-object capture for a small regression set and verifies that
every recorded sprite points at the physical CHR bytes the mapper actually used.

Checks:
  * the CSV uses the mapper-aware header;
  * every row has an absolute CHR address and 16 pattern bytes;
  * every CHR-ROM pattern matches the ROM at that absolute address;
  * 8x16 sprite metadata is present;
  * the real rendered screenshot exists;
  * the Bowser capture contains the real high-bank patterns (0xEC2..0xEC7),
    not only the old blank pattern 0x131.

Example:
  python scripts/test_gate1_mapper.py --rom "D:\\ROMs\\Super Mario Bros. 3 (USA).nes"
"""
import argparse
import json
import os
import struct
import subprocess
import sys
import tempfile


def read_ines(path):
    data = open(path, "rb").read()
    if data[:4] != b"NES\x1a":
        raise SystemExit("not an iNES ROM: " + path)
    trainer = 512 if data[6] & 0x04 else 0
    prg = data[4] * 16384
    chr_size = data[5] * 8192
    offset = 16 + trainer + prg
    return data[offset:offset + chr_size], data


def run_capture(mesen, rom, home, object_id, out_dir, timeout):
    env = os.environ.copy()
    env["MESEN_HOME"] = home
    cmd = [mesen, "--hdstudio", rom, f"--spriteid={object_id}",
           f"--out={out_dir}", "--timeout=40"]
    subprocess.run(cmd, env=env, stdout=subprocess.DEVNULL,
                   stderr=subprocess.DEVNULL, timeout=timeout)
    return os.path.join(out_dir, "HdStudio", "sprites", f"{object_id:03d}.csv")


def read_rows(csv_path):
    lines = open(csv_path, encoding="utf-8", errors="replace").read().splitlines()
    if not lines or "chrAddr" not in lines[0]:
        raise AssertionError("legacy/non-mapper-aware CSV header: " + (lines[0] if lines else "<empty>"))
    rows = []
    for line in lines[1:]:
        if not line.strip():
            continue
        p = line.split(",")
        if len(p) < 16:
            raise AssertionError("short CSV row: " + line)
        row = {
            "objectId": int(p[0]),
            "oamIndex": int(p[1], 16),
            "part": int(p[2]),
            "ppuAddr": int(p[3], 16),
            "chrAddr": int(p[4], 16),
            "chrType": p[5],
            "tileData": p[6],
            "pattern": int(p[7], 16),
            "palette": p[8],
            "x": int(p[9]),
            "y": int(p[10]),
            "attr": int(p[13], 16),
            "frame": int(p[14]),
            "mapperId": int(p[15]),
            "largeSprites": len(p) > 16 and p[16].strip() == "1",
        }
        rows.append(row)
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--rom", default=r"D:\NesFlasher\ROMs\Super Mario Bros. 3 (USA).nes")
    ap.add_argument("--home", default=r"D:\MesenHighWork\mesen-home")
    ap.add_argument("--ids", default="114,24,45,13")
    ap.add_argument("--timeout", type=int, default=60)
    ap.add_argument("--out", default="")
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    out_root = args.out or tempfile.mkdtemp(prefix="hdstudio_gate1_")
    chr_data, _ = read_ines(args.rom)
    ids = [int(x) for x in args.ids.split(",") if x.strip()]
    failures = []
    summary = []

    for object_id in ids:
        out_dir = os.path.join(out_root, f"{object_id:03d}")
        os.makedirs(out_dir, exist_ok=True)
        csv_path = run_capture(args.mesen, args.rom, args.home, object_id, out_dir, args.timeout)
        if not os.path.exists(csv_path):
            failures.append(f"{object_id}: no CSV was produced")
            continue
        rows = read_rows(csv_path)
        if not rows:
            failures.append(f"{object_id}: CSV is empty")
            continue

        verified = 0
        for row in rows:
            if row["chrType"] != "NesChrRom":
                failures.append(f"{object_id}: expected NesChrRom, got {row['chrType']}")
                continue
            addr = row["chrAddr"]
            if addr < 0 or addr + 16 > len(chr_data) or addr % 16 != 0:
                failures.append(f"{object_id}: invalid CHR address {addr:X}")
                continue
            expected = chr_data[addr:addr + 16].hex().upper()
            if expected != row["tileData"]:
                failures.append(f"{object_id}: tile bytes do not match ROM at {addr:X}")
                continue
            if row["pattern"] != addr // 16:
                failures.append(f"{object_id}: pattern {row['pattern']:X} != CHR address/16 {addr//16:X}")
                continue
            verified += 1

        if rows and not all(r["largeSprites"] for r in rows):
            failures.append(f"{object_id}: SMB3 regression expects 8x16 sprites")

        screenshot = csv_path + ".png"
        if not os.path.exists(screenshot) or os.path.getsize(screenshot) < 100:
            failures.append(f"{object_id}: missing real rendered screenshot")

        patterns = sorted({r["pattern"] for r in rows})
        summary.append({
            "objectId": object_id,
            "rows": len(rows),
            "verifiedRows": verified,
            "patterns": [f"{p:X}" for p in patterns],
            "screenshotBytes": os.path.getsize(screenshot) if os.path.exists(screenshot) else 0,
        })

        # Bowser: the old bank approximation only produced blank 0x131 plus a fragment.
        # The real 8x16 sprites resolve to the 0xEC2..0xEC7 range.
        if object_id == 24:
            real = [p for p in patterns if 0xEC2 <= p <= 0xEC7]
            if len(real) < 4:
                failures.append(f"24: expected at least four real Bowser patterns in EC2..EC7, got {patterns}")

    print(json.dumps({"passed": not failures, "captures": summary, "failures": failures}, indent=2))
    if not args.keep and not args.out:
        import shutil
        shutil.rmtree(out_root, ignore_errors=True)
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
