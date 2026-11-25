"""Sprite-sheet slicer regression test (no emulator/ROM required).

Example:
  python scripts/test_hdstudio_slicer.py
"""
import argparse
import json
import os
import shutil
import subprocess
import tempfile

try:
    from PIL import Image, ImageDraw
except Exception:
    raise SystemExit("Pillow is required for this test")


def make_sheet(path):
    image = Image.new("RGBA", (200, 100), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    colors = [(255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255), (255, 255, 0, 255)]
    for i, color in enumerate(colors):
        x = 4 + i * 48
        draw.rectangle([x, 10, x + 39, 49], fill=color)
    image.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mesen", default=r"D:\MesenHigh\bin\win-x64\Release\Mesen.exe")
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()

    work = tempfile.mkdtemp(prefix="hdstudio_slice_")
    sheet = os.path.join(work, "sheet.png")
    out = os.path.join(work, "frames")
    make_sheet(sheet)

    p = subprocess.run([args.mesen, "--hdstudio", "--slice-sheet=" + sheet,
                        "--grid=4x1", "--slice-frame=40x40", "--slice-margin=4x10",
                        "--slice-spacing=8x0", "--slice-out=" + out],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode != 0:
        print(p.stdout)
        raise SystemExit("slice command failed")
    files = sorted(os.listdir(out))
    if files != [f"frame_{i:03d}.png" for i in range(4)]:
        raise SystemExit("unexpected sliced files: " + repr(files))
    for name in files:
        image = Image.open(os.path.join(out, name))
        if image.size != (40, 40):
            raise SystemExit(f"{name} has size {image.size}, expected 40x40")
    # The four source colors must remain in order.
    expected = [(255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255), (255, 255, 0, 255)]
    for name, color in zip(files, expected):
        image = Image.open(os.path.join(out, name)).convert("RGBA")
        if image.getpixel((0, 0)) != color:
            raise SystemExit(f"{name} has the wrong source color")

    # Frame size can be derived from the grid, and trim removes transparent borders.
    derived = os.path.join(work, "derived")
    p = subprocess.run([args.mesen, "--hdstudio", "--slice-sheet=" + sheet,
                        "--grid=4x1", "--slice-margin=4x10",
                        "--slice-spacing=8x0", "--slice-out=" + derived],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode != 0:
        raise SystemExit("derived grid slice failed")

    trimmed = os.path.join(work, "trimmed")
    p = subprocess.run([args.mesen, "--hdstudio", "--slice-sheet=" + sheet,
                        "--grid=4x1", "--slice-frame=40x40", "--slice-margin=4x10",
                        "--slice-spacing=8x0", "--slice-trim", "--slice-out=" + trimmed],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode != 0:
        raise SystemExit("trim slice failed")
    size = Image.open(os.path.join(trimmed, "frame_000.png")).size
    if size != (40, 40):
        raise SystemExit("trim should keep a fully occupied 40x40 frame")

    # An impossible grid must fail cleanly.
    bad = os.path.join(work, "bad")
    p = subprocess.run([args.mesen, "--hdstudio", "--slice-sheet=" + sheet,
                        "--grid=20x20", "--slice-frame=40x40", "--slice-out=" + bad],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode == 0:
        raise SystemExit("an oversized grid did not fail")

    print(json.dumps({
        "passed": True,
        "frames": len(files),
        "frameSize": [40, 40],
        "derivedGrid": True,
        "trim": True,
        "invalidGridRejected": True
    }, indent=2))
    if not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
