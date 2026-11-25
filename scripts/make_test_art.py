"""Generates original, legally distributable test artwork for HD Studio validation.

The images are deliberately geometric so dimensions, orientation, transparency and
tile seams are easy to measure. No external image service is used.

Usage:
  python scripts/make_test_art.py --out "D:\\MesenHighWork\\test-art"
"""
import argparse
import os

from PIL import Image, ImageDraw, ImageFont


def font(size):
    for name in ("arial.ttf", "segoeui.ttf", "DejaVuSans-Bold.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except Exception:
            continue
    return ImageFont.load_default()


def quadrants(path):
    image = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rectangle([16, 16, 127, 127], fill=(220, 30, 40, 255))
    draw.rectangle([128, 16, 239, 127], fill=(20, 190, 70, 255))
    draw.rectangle([16, 128, 127, 239], fill=(40, 80, 230, 255))
    draw.rectangle([128, 128, 239, 239], fill=(250, 190, 20, 255))
    draw.rectangle([12, 12, 243, 243], outline=(10, 10, 10, 255), width=6)
    draw.line([128, 16, 128, 239], fill=(10, 10, 10, 255), width=6)
    draw.line([16, 128, 239, 128], fill=(10, 10, 10, 255), width=6)
    image.save(path)


def asymmetric(path):
    image = Image.new("RGBA", (256, 128), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    f = font(92)
    draw.rectangle([6, 6, 249, 121], outline=(0, 0, 0, 255), width=5)
    draw.text((30, 18), "L", font=f, fill=(235, 40, 40, 255), stroke_width=3, stroke_fill=(0, 0, 0, 255))
    draw.text((155, 18), "R", font=f, fill=(40, 100, 235, 255), stroke_width=3, stroke_fill=(0, 0, 0, 255))
    image.save(path)


def frame_sheet(path):
    image = Image.new("RGBA", (256, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    colors = [(235, 60, 60, 255), (60, 210, 90, 255), (70, 130, 245, 255), (250, 200, 30, 255)]
    f = font(34)
    for i, color in enumerate(colors):
        x = i * 64
        draw.ellipse([x + 4, 4, x + 60, 60], fill=color, outline=(0, 0, 0, 255), width=3)
        draw.text((x + 24, 12), str(i + 1), font=f, fill=(0, 0, 0, 255))
    image.save(path)


def oversized(path):
    image = Image.new("RGBA", (800, 200), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    for i in range(0, 800, 40):
        color = (30, 170, 210, 255) if (i // 40) % 2 == 0 else (240, 240, 240, 255)
        draw.rectangle([i, 0, i + 39, 199], fill=color)
    draw.rectangle([0, 0, 799, 199], outline=(0, 0, 0, 255), width=8)
    image.save(path)


def transparent_star(path):
    image = Image.new("RGBA", (128, 128), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.polygon([(64, 6), (80, 46), (124, 46), (88, 72), (102, 118), (64, 90),
                  (26, 118), (40, 72), (4, 46), (48, 46)], fill=(255, 215, 40, 255),
                 outline=(0, 0, 0, 255))
    image.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    quadrants(os.path.join(args.out, "quadrants.png"))
    asymmetric(os.path.join(args.out, "asymmetric_lr.png"))
    frame_sheet(os.path.join(args.out, "frames_sheet.png"))
    oversized(os.path.join(args.out, "oversized_wide.png"))
    transparent_star(os.path.join(args.out, "transparent_star.png"))
    print("wrote test art to " + args.out)


if __name__ == "__main__":
    main()
