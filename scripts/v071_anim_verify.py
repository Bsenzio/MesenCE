"""Verify a runtime multi-frame animation with standard Mesen conditions.

Usage:
  python v071_anim_verify.py --source-pack PACK --rom ROM --out OUT \
      --frames 194,404 --bbox 128,73,144,81 --assets id1,id2 --scale 4

The source pack is an existing live-capture pack. The script starts from its
capture evidence only (no previous project/imports), imports each animation
frame with a standard positionCheck condition, replays the exact frames and
counts marker pixels inside/outside the intended bbox.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys

from PIL import Image, ImageDraw

MARKERS = {(255, 0, 255), (0, 255, 255), (0, 255, 0)}


def run(exe, home, args):
    env = os.environ.copy()
    env['MESEN_HOME'] = home
    p = subprocess.run([exe] + args, env=env, capture_output=True, text=True,
                       encoding='utf-8', errors='replace')
    print(p.stdout[-2500:])
    if p.returncode:
        print('EXIT', p.returncode, p.stderr[-1000:])
        sys.exit(p.returncode)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--mesen', default=r'D:\MesenHigh\bin\win-x64\Release\Mesen.exe')
    ap.add_argument('--source-home', required=True, help='attempt1/home containing HdPacks/<stem>')
    ap.add_argument('--rom', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--frames', required=True)
    ap.add_argument('--bbox', help='x0,y0,x1,y1 in original 256x240 pixels (single box)')
    ap.add_argument('--bboxes', help='semicolon-separated boxes, one per frame')
    ap.add_argument('--assets', required=True, help='comma-separated animationFrame asset ids')
    ap.add_argument('--scale', type=int, default=4)
    args = ap.parse_args()

    frames = [int(v) for v in args.frames.split(',') if v.strip()]
    if args.bboxes:
        boxes = [tuple(int(v) for v in b.split(',')) for b in args.bboxes.split(';') if b.strip()]
    else:
        boxes = [tuple(int(v) for v in args.bbox.split(','))] * len(frames)
    if len(boxes) != len(frames):
        raise SystemExit('bbox count must match frame count')
    bbox = boxes[0]
    asset_ids = [v.strip() for v in args.assets.split(',') if v.strip()]
    if os.path.isdir(args.out):
        shutil.rmtree(args.out)
    os.makedirs(args.out)
    home = os.path.join(args.out, 'home')
    shutil.copytree(args.source_home, home)
    stem = os.path.splitext(os.path.basename(args.rom))[0]
    pack = os.path.join(home, 'HdPacks', stem)
    for name in ('hires.txt', 'HdStudio/project.hdstudio.json', 'HdStudio/base-hires.txt'):
        try:
            os.remove(os.path.join(pack, name))
        except OSError:
            pass
    for sub in ('artwork', 'imports', 'templates', 'verify'):
        shutil.rmtree(os.path.join(pack, 'HdStudio', sub), ignore_errors=True)
    try:
        os.remove(os.path.join(pack, 'HdStudio', 'live', 'picked-assets.json'))
    except OSError:
        pass
    assets_path = os.path.join(pack, 'HdStudio', 'assets', 'assets.json')
    doc = json.load(open(assets_path, encoding='utf-8'))
    doc['assets'] = [a for a in doc.get('assets', []) if not str(a.get('id', '')).startswith('picked:')]
    json.dump(doc, open(assets_path, 'w', encoding='utf-8'), indent=1)

    marker = os.path.join(args.out, 'marker.png')
    im = Image.new('RGBA', (64, 64), (255, 0, 255, 255))
    d = ImageDraw.Draw(im)
    d.rectangle([4, 4, 59, 59], fill=(0, 255, 255, 255))
    d.rectangle([16, 12, 44, 22], fill=(0, 255, 0, 255))
    im.save(marker)

    for i, asset_id in enumerate(asset_ids):
        box = boxes[i]
        condition = ('positionCheckX:>=,%d&positionCheckX:<=,%d&'
                     'positionCheckY:>=,%d&positionCheckY:<=,%d' %
                     (box[0], box[2] - 1, box[1], box[3] - 1))
        plan = {'status': 'scene-isolated', 'contextRule': condition, 'cellConditions': {}}
        for asset in doc['assets']:
            if asset.get('id') == asset_id:
                for tile in asset.get('tiles', []):
                    plan['cellConditions']['%s@%d,%d' % (tile['pattern'], tile['x'], tile['y'])] = condition
        plan_path = os.path.join(args.out, 'plan-%d.json' % (i + 1))
        json.dump(plan, open(plan_path, 'w'), indent=1)
        cmd = [args.mesen, '--hdstudio', args.rom, '--import', '--asset=' + asset_id,
               '--image=' + marker, '--fit=stretch', '--all-palettes', '--mirror-conditions',
               '--import-scale=%d' % args.scale, '--import-name=AnimFrame%d' % (i + 1),
               '--condition-map=' + plan_path, '--out=' + pack]
        if i == 0:
            cmd.append('--minimal')
        run(args.mesen, home, cmd)

    run(args.mesen, home, ['--hdstudio', args.rom,
                            r'D:\MesenHigh\scripts\deterministic_input.lua', '--verify',
                            '--shots=1', '--frames=%d' % max(frames),
                            '--verify-at=' + ','.join(map(str, frames)), '--out=' + pack])

    verify = os.path.join(pack, 'HdStudio', 'verify')
    rows = []
    for index, frame in enumerate(frames):
        box = boxes[index]
        original = Image.open(os.path.join(verify, 'frame_%05d_original.png' % frame)).convert('RGB')
        hd = Image.open(os.path.join(verify, 'frame_%05d_hd.png' % frame)).convert('RGB')
        scale = hd.width // original.width
        inside = outside = 0
        changed_inside = changed_outside = 0
        for y in range(hd.height):
            for x in range(hd.width):
                if hd.getpixel((x, y)) in MARKERS:
                    if box[0] <= x / scale < box[2] and box[1] <= y / scale < box[3]:
                        inside += 1
                    else:
                        outside += 1
        down = hd.resize(original.size, Image.Resampling.NEAREST)
        for y in range(original.height):
            for x in range(original.width):
                if down.getpixel((x, y)) != original.getpixel((x, y)):
                    if box[0] <= x < box[2] and box[1] <= y < box[3]:
                        changed_inside += 1
                    else:
                        changed_outside += 1
        rows.append({'frame': frame, 'inside': inside, 'outside': outside,
                     'changedInside': changed_inside, 'changedOutside': changed_outside})
        print(rows[-1])
    good = [r for r in rows if r['inside'] > 0 and r['outside'] == 0]
    result = {
        'assets': asset_ids, 'frames': frames, 'bboxes': [list(b) for b in boxes], 'rows': rows,
        'status': ('verified-animation-multi-frame' if len(good) == len(rows) and len(rows) >= 2
                   else 'failed-animation'),
    }
    json.dump(result, open(os.path.join(args.out, 'animation-result.json'), 'w'), indent=1)
    print(json.dumps(result, indent=1))


if __name__ == '__main__':
    main()
