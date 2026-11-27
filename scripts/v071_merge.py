"""Merge serial state re-verification and short-path reruns into one row set."""
import argparse
import csv
import json


def load(path, default):
    try:
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return default


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', required=True)
    ap.add_argument('--override', action='append', default=[])
    ap.add_argument('--state', default='')
    ap.add_argument('--out', required=True)
    args = ap.parse_args()
    rows = load(args.base, [])
    by_sha = {r.get('sha256'): r for r in rows}
    overrides = 0
    for path in args.override:
        for r in load(path, []) or []:
            sha = r.get('sha256')
            if sha:
                by_sha[sha] = r
                overrides += 1
    state = load(args.state, []) if args.state else []
    state_used = 0
    for s in state:
        sha = s.get('sha256')
        if not sha or sha not in by_sha:
            continue
        row = by_sha[sha]
        row['serialStateReverification'] = {
            'previousStatus': s.get('previousStatus'),
            'stateIdentical': s.get('stateIdenticalSerial'),
            'markerPixelsInside': s.get('markerPixelsInside'),
            'markerPixelsOutside': s.get('markerPixelsOutside'),
            'seconds': s.get('seconds'),
        }
        if s.get('stateIdenticalSerial') and s.get('hdPackActive') and s.get('markerPixelsInside', 0) > 0 and s.get('markerPixelsOutside') == 0:
            row['status'] = s.get('status')
            row['failure'] = ''
            row['stateIdentical'] = True
            row['markerPixelsInside'] = s.get('markerPixelsInside')
            row['markerPixelsOutside'] = s.get('markerPixelsOutside')
            state_used += 1
        else:
            row['stateIdentical'] = False
    out = sorted(by_sha.values(), key=lambda r: (r.get('rom') or '').lower())
    json.dump(out, open(args.out, 'w', encoding='utf-8'), indent=1)
    fields = ['rom', 'sha256', 'status', 'mapper', 'chrType', 'split', 'targetMode',
              'pauseFrame', 'verificationScenes', 'contextStatus', 'markerPixelsInside',
              'markerPixelsOutside', 'stateIdentical', 'originalRomIntegrity',
              'engineFingerprint', 'failure']
    with open(args.out.replace('.json', '.csv'), 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=fields, extrasaction='ignore')
        w.writeheader()
        w.writerows(out)
    print('rows', len(out), 'overrides', overrides, 'state applied', state_used)


if __name__ == '__main__':
    main()
