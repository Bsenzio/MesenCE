"""v0.7.1 regression fixtures: runtime animation and CHR-RAM conditions.

The heavy live captures are retained as evidence. This test re-checks the
machine-readable results, exercises the CHR-RAM condition rewrite directly,
and (with --run-runtime) replays the synthetic animation fixture.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile

ROOT = r'D:\MesenHighWork\validation-v071'


def load(path):
    try:
        return json.load(open(path, encoding='utf-8'))
    except Exception:
        return None


def check_animation(path):
    doc = load(path)
    if not doc:
        return False, 'missing ' + path
    rows = doc.get('rows') or []
    if doc.get('status') != 'verified-animation-multi-frame':
        return False, 'status=' + str(doc.get('status'))
    if len(rows) < 2:
        return False, 'less than two frames'
    if any(r.get('inside', 0) <= 0 or r.get('outside', 0) != 0 for r in rows):
        return False, 'marker measurement failed'
    return True, '%d frames' % len(rows)


def check_chrram():
    result = load(os.path.join(ROOT, 'chrram-fixture', 'replacement-results.json'))
    if not result:
        return False, 'missing chrram fixture result'
    row = result[0]
    if row.get('status') != 'verified-conditional-scene-isolated':
        return False, 'positionCheck status=' + str(row.get('status'))
    if row.get('chrramConditionMode') != 'positionCheck':
        return False, 'positionCheck mode not recorded'
    content = load(os.path.join(ROOT, 'chrram-content-fixture', 'replacement-results.json'))
    if not content:
        return False, 'missing content-key fixture result'
    if content[0].get('status') != 'failed-not-replaced':
        return False, 'content-key fixture unexpectedly passed'
    return True, 'positionCheck passes; raw content keys fail on the same fixture'


def check_rewrite():
    sys.path.insert(0, r'D:\MesenHigh\scripts')
    from hdstudio_v071_autoreplace import apply_chrram_position_conditions
    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, 'plan.json')
        plan = {
            'status': 'contextually-isolated-multi-scene',
            'cellConditions': {
                '00' * 16 + '@0,0': 'tileNearby:-8,-1,' + '11' * 16 + ',FF000000,false',
                'AA@0,0': 'hmirror',
            },
        }
        json.dump(plan, open(path, 'w'))
        changed = apply_chrram_position_conditions(path, plan, (40, 50, 56, 58))
        updated = json.load(open(path))
        if not changed:
            return False, 'rewrite did not trigger'
        if updated.get('status') != 'scene-isolated':
            return False, 'status not downgraded to scene-isolated'
        if 'positionCheckX' not in updated['cellConditions']['00' * 16 + '@0,0']:
            return False, 'content key not replaced'
        if updated['cellConditions']['AA@0,0'] != 'hmirror':
            return False, 'stable condition was overwritten'
        return True, 'content key -> positionCheck; hmirror preserved'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--run-runtime', action='store_true')
    args = ap.parse_args()
    checks = [
        ('animation-synthetic', check_animation(os.path.join(ROOT, 'anim-runtime', 'animation-fixture-result.json'))),
        ('animation-commercial-1942', check_animation(os.path.join(ROOT, 'anim-1942', 'animation-result.json'))),
        ('animation-commercial-1942-moving', check_animation(os.path.join(ROOT, 'anim-1942-seq41', 'animation-result.json'))),
        ('chrram-conditions', check_chrram()),
        ('chrram-rewrite', check_rewrite()),
    ]
    failed = 0
    for name, (ok, detail) in checks:
        print('%-32s %s %s' % (name, 'PASS' if ok else 'FAIL', detail))
        failed += 0 if ok else 1
    if args.run_runtime:
        cmd = [sys.executable, r'D:\MesenHigh\scripts\v071_anim_verify.py',
               '--source-home', r'D:\MesenHighWork\validation-v071\anim-fixture3\897e8ec5377e_hdstudio_anim\attempt1\home',
               '--rom', r'D:\MesenHighWork\test-roms-new2\hdstudio_anim.nes',
               '--out', r'D:\MesenHighWork\validation-v071\anim-runtime-regression',
               '--frames', '6,14', '--bbox', '40,160,56,168',
               '--assets', 'liveSequence:1eeb26ff:5:0,liveSequence:1eeb26ff:5:1']
        p = subprocess.run(cmd)
        if p.returncode:
            failed += 1
    print('v0.7.1 fixture regression: %s' % ('FAIL' if failed else 'PASS'))
    return 1 if failed else 0


if __name__ == '__main__':
    raise SystemExit(main())
