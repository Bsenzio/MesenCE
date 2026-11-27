import json, os, subprocess, re, glob, time

root = r'D:\MesenHighWork\validation-v07'
files = [os.path.join(root, 'batch300d', 'replacement-results.json'),
         os.path.join(root, 'holdout-v07', 'replacement-results.json')]
inv = json.load(open(os.path.join(root, 'inventory', 'library-inventory.json'), encoding='utf-8'))
by_sha = {r['sha256']: r for r in inv['roms']}
rows = []
for path in files:
    try:
        rows.extend(json.load(open(path, encoding='utf-8')))
    except Exception:
        pass
targets = [r for r in rows if r.get('stateIdentical') is False and r.get('status') == 'partial-visual-state-mismatch']
out = []
for r in targets:
    pack = r.get('pack')
    if not pack or not os.path.isdir(pack):
        out.append({'rom': r.get('rom'), 'status': 'missing-pack'})
        continue
    home = os.path.dirname(os.path.dirname(pack))
    rom = by_sha.get(r.get('sha256'))
    frames = sorted({int(f['frame']) for f in (r.get('frameResults') or [])})
    if not rom or not frames:
        out.append({'rom': r.get('rom'), 'status': 'missing-rom-or-frames'})
        continue
    env = os.environ.copy()
    env['MESEN_HOME'] = home
    p = subprocess.run([r'D:\MesenHigh\bin\win-x64\Release\Mesen.exe', '--hdstudio', rom['path'],
                        r'D:\MesenHigh\scripts\deterministic_input.lua', '--verify', '--shots=1',
                        '--frames=%d' % max(frames), '--verify-at=' + ','.join(map(str, frames)),
                        '--out=' + pack], env=env, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', timeout=900)
    text = p.stdout or ''
    identical = 'Gameplay state comparison: IDENTICAL' in text
    out.append({'rom': r.get('rom'), 'sha256': r.get('sha256'), 'previousStatus': r.get('status'),
                'frames': frames, 'stateIdenticalSerial': identical,
                'hdPackActive': 'HD pack ACTIVE' in text,
                'logTail': '' if identical else text[-800:]})
    print(r.get('rom'), 'IDENTICAL' if identical else 'DIFFERENT', flush=True)
json.dump(out, open(r'D:\MesenHighWork\validation-v071\v07-state-retest.json', 'w', encoding='utf-8'), indent=1)
print('rows', len(out), 'identical', sum(1 for x in out if x.get('stateIdenticalSerial')))
