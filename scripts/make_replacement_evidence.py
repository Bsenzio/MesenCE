"""Compose evidence images from a real replacement import and emulator verify pass.

Example:
  python scripts/make_replacement_evidence.py --pack PACK --slug GoombaTest \
    --name guardian --out D:\\RetroArcDo_HDStudio\\docs\\assets\\v05
"""
import argparse
import os

from PIL import Image, ImageDraw, ImageFont


def load(path):
    return Image.open(path).convert("RGBA")


def fit(im, box, nearest=True):
    scale = max(1, min(box[0] // max(1, im.width), box[1] // max(1, im.height)))
    return im.resize((im.width * scale, im.height * scale), Image.NEAREST if nearest else Image.LANCZOS)


def label(draw, x, y, text, color=(230, 230, 235, 255)):
    try:
        font = ImageFont.truetype("segoeui.ttf", 18)
    except Exception:
        font = ImageFont.load_default()
    draw.text((x, y), text, fill=color, font=font)


def checker(size, cell=12):
    im = Image.new("RGBA", size, (34, 37, 44, 255))
    d = ImageDraw.Draw(im)
    for y in range(0, size[1], cell):
        for x in range(0, size[0], cell):
            if (x // cell + y // cell) % 2:
                d.rectangle([x, y, x + cell - 1, y + cell - 1], fill=(48, 52, 61, 255))
    return im


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pack", required=True)
    ap.add_argument("--slug", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    imports = os.path.join(args.pack, "HdStudio", "imports")
    verify = os.path.join(args.pack, "HdStudio", "verify")
    os.makedirs(args.out, exist_ok=True)

    def find(suffix):
        for name in os.listdir(imports):
            if name.startswith(args.slug) and name.endswith(suffix):
                return os.path.join(imports, name)
        raise SystemExit("missing import asset: " + suffix)

    original = fit(load(find("_original.png")), (300, 300))
    replacement = fit(load(find("_replacement.png")), (300, 300))
    sheet = None
    for name in os.listdir(args.pack):
        if name.startswith("HdStudioManaged_") and name.endswith(".png"):
            raw = load(os.path.join(args.pack, name))
            tile = raw.height
            count = max(1, raw.width // tile)
            cols = min(7, count)
            rows = (count + cols - 1) // cols
            grid = Image.new("RGBA", (cols * (tile + 3), rows * (tile + 3)), (24, 27, 33, 255))
            for i in range(count):
                piece = raw.crop((i * tile, 0, (i + 1) * tile, tile))
                grid.alpha_composite(piece, ((i % cols) * (tile + 3), (i // cols) * (tile + 3)))
            sheet = fit(grid, (300, 300))
            break

    card = Image.new("RGBA", (1000, 390), (24, 27, 33, 255))
    d = ImageDraw.Draw(card)
    card.alpha_composite(original, (20, 55))
    bg = checker((300, 300))
    bg.alpha_composite(replacement, ((300 - replacement.width) // 2, (300 - replacement.height) // 2))
    card.alpha_composite(bg, (360, 55))
    if sheet:
        bg2 = checker((300, 300))
        bg2.alpha_composite(sheet, ((300 - sheet.width) // 2, (300 - sheet.height) // 2))
        card.alpha_composite(bg2, (700, 55))
    label(d, 20, 22, "Original NES")
    label(d, 360, 22, "Adapted replacement")
    label(d, 700, 22, "HD tile sheet")
    d.text((20, 366), "Original logical geometry and tile placement preserved.", fill=(160, 170, 180, 255))
    card.save(os.path.join(args.out, args.name + "-character.png"))

    frames = sorted([f for f in os.listdir(verify) if f.endswith("_original.png")])
    if not frames:
        raise SystemExit("no verify frames")
    picks = [frames[0]] + ([frames[-1]] if len(frames) > 1 else [])
    panels = []
    for frame in picks:
        stem = frame[:-len("_original.png")]
        orig = load(os.path.join(verify, frame)).resize((320, 300), Image.NEAREST)
        hd = load(os.path.join(verify, stem + "_hd.png")).resize((320, 300), Image.LANCZOS)
        panels.append((orig, hd))
    gap = 16
    width = 320 * 2 + gap
    height = (300 + 40) * len(panels) + gap * (len(panels) - 1)
    compare = Image.new("RGBA", (width, height), (24, 27, 33, 255))
    d = ImageDraw.Draw(compare)
    y = 0
    for orig, hd in panels:
        label(d, 6, y + 6, "Original 256x240")
        label(d, 320 + gap + 6, y + 6, "HD Pack 1024x960")
        compare.alpha_composite(orig, (0, y + 34))
        compare.alpha_composite(hd, (320 + gap, y + 34))
        y += 300 + 40 + gap
    compare.save(os.path.join(args.out, args.name + "-gameplay-compare.png"))
    print(os.path.join(args.out, args.name + "-character.png"))
    print(os.path.join(args.out, args.name + "-gameplay-compare.png"))


if __name__ == "__main__":
    main()
