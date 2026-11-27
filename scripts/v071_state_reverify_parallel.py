"""Parallel serial-equivalent state re-verification.

Each row still replays the exact frames with the save-RAM isolation fix. Rows
are independent; the worker count only controls how many emulator processes
run at once. Any difference is retained and can be re-run serially.
"""
import argparse
import concurrent.futures
import json
import os
import re
import subprocess
import time
from PIL import Image

MARKERS = {(255, 0, 255), (0, 255, 255), (0, 255, 0)}


def load_json(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def metrics(original_path, hd_path, bboxes, scale):
    original = Image.open(original_path).convert('RGB')
    hd = Image.open(hd_path).convert('RGB')
    inside = outside = changed_inside = changed_outside = 0

    def in_box(x, y):
        return any(b[0] <= x < b[0] + b[2] and b[1] <= y < b[1] + b[3] for b in bboxes)

    for y in range(hd.height):
        for x in range(hd.width):
            if hd.getpixel((x, y)) in MARKERS:
                if in_box(x // scale, y // scale):
                    inside += 1
                else:
                    outside += 1
    down = hd.resize(original.size, Image.Resampling.NEAREST)
    for y in range(original.height):
        for x in range(original.width):
            if down.getpixel((x, y)) != original.getpixel((x, y)):
                if in_box(x, y):
                    changed_inside += 1
                else:
                    changed_outside += 1
    return {'markerPixelsInside': inside, 'markerPixelsOutside': outside,
            'changedPixelsInside': changed_inside, 'changedPixelsOutside': changed_outside}


def process(row, rom, args):
    pack = row.get('pack')
    frames = sorted({int(f['frame']) for f in (row.get('frameResults') or [])})
    if not pack or not frames:
        return {'rom': row.get('rom'), 'status': 'skipped-no-pack-or-frames'}
    home = os.path.dirname(os.path.dirname(pack))
    env = os.environ.copy()
    env['MESEN_HOME'] = home
    started = time.time()
    p = subprocess.run([args.mesen, '--hdstudio', rom['path'], args.input_script,
                        '--verify', '--shots=1', '--frames=%d' % max(frames),
                        '--verify-at=' + ','.join(map(str, frames)), '--out=' + pack],
                       env=env, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', timeout=900)
    text = p.stdout or ''
    by_pass = {'hd': [], 'original': []}
    current = None
    for part in re.split(r"Pass '(hd|original)'", text):
        if part in ('hd', 'original'):
            current = part
        elif current:
            by_pass[current].extend(re.findall(r'gameplay state at frame (\d+): ([0-9A-F]+)', part))
    identical = (len(by_pass['hd']) == len(by_pass['original'])
                 and all(a == b for a, b in zip(by_pass['hd'], by_pass['original'])))
    verify_dir = os.path.join(pack, 'HdStudio', 'verify')
    results = []
    total_inside = total_outside = 0
    available = True
    for target in row.get('frameResults') or []:
        frame = int(target['frame'])
        bboxes = [tuple(int(v) for v in b) for b in (target.get('bboxes') or [])]
        if not bboxes:
            bboxes = [(int(target.get('x', 0)), int(target.get('y', 0)),
                       int(target.get('width', 8)), int(target.get('height', 8)))]
        original = os.path.join(verify_dir, 'frame_%05d_original.png' % frame)
        hd = os.path.join(verify_dir, 'frame_%05d_hd.png' % frame)
        if not (os.path.exists(original) and os.path.exists(hd)):
            available = False
            continue
        m = metrics(original, hd, bboxes, args.scale)
        m['frame'] = frame
        results.append(m)
        total_inside += m['markerPixelsInside']
        total_outside += m['markerPixelsOutside']
    hd_active = 'HD pack ACTIVE' in text
    all_frames_inside = bool(results) and all(r['markerPixelsInside'] > 0 for r in results)
    status = row.get('status')
    if identical and hd_active and available and all_frames_inside and total_outside == 0:
        if row.get('conditionsApplied') and (row.get('contextStatus') == 'scene-isolated'):
            status = 'verified-conditional-scene-isolated'
        elif (row.get('verificationScenes') or 0) >= 2:
            status = 'verified-scene-independent-shared-keys-multi-scene'
        else:
            status = 'verified-scene-independent-shared-keys'
    return {
        'rom': row.get('rom'), 'sha256': row.get('sha256'),
        'previousStatus': row.get('status'), 'status': status,
        'stateIdenticalSerial': identical, 'hdPackActive': hd_active,
        'markerPixelsInside': total_inside, 'markerPixelsOutside': total_outside,
        'frameResults': results, 'seconds': round(time.time() - started, 2),
        'logTail': text[-1500:] if not identical else '',
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--results', required=True)
    ap.add_argument('--inventory', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--mesen', default=r'D:\MesenHigh\bin\win-x64\Release\Mesen.exe')
    ap.add_argument('--input-script', default=r'D:\MesenHigh\scripts\deterministic_input.lua')
    ap.add_argument('--scale', type=int, default=2)
    ap.add_argument('--workers', type=int, default=6)
    args = ap.parse_args()
    rows = load_json(args.results)
    inv = load_json(args.inventory)
    by_sha = {r['sha256']: r for r in inv['roms']}
    selected = [r for r in rows if r.get('stateIdentical') is False
                or 'state' in str(r.get('status'))
                or str(r.get('status')).startswith('failed-context-rule')]
    selected = [r for r in selected if by_sha.get(r.get('sha256'))]
    out = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(process, row, by_sha[row['sha256']], args): row for row in selected}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            try:
                result = future.result()
            except Exception as ex:
                row = futures[future]
                result = {'rom': row.get('rom'), 'sha256': row.get('sha256'),
                          'status': row.get('status'), 'stateIdenticalSerial': False,
                          'error': str(ex)}
            out.append(result)
            print('[%d/%d] %s -> %s (state=%s, marker %s/%s)' % (
                index, len(selected), result.get('rom'), result.get('status'),
                result.get('stateIdenticalSerial'), result.get('markerPixelsInside'),
                result.get('markerPixelsOutside')), flush=True)
            json.dump(sorted(out, key=lambda r: r.get('rom') or ''),
                      open(args.out + '.partial.json', 'w', encoding='utf-8'), indent=1)
    out.sort(key=lambda r: r.get('rom') or '')
    json.dump(out, open(args.out, 'w', encoding='utf-8'), indent=1)
    print(json.dumps({'rows': len(out),
                      'identical': sum(1 for r in out if r.get('stateIdenticalSerial')),
                      'different': sum(1 for r in out if r.get('stateIdenticalSerial') is False)}, indent=1))


if __name__ == '__main__':
    main()
