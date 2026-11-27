import json, subprocess, os, sys, glob
root = r'D:\MesenHighWork\validation-v07\batch300d'
rows = json.load(open(os.path.join(root, 'replacement-results.json'), encoding='utf-8'))
targets = [r for r in rows if r.get('stateIdentical') is False and r.get('status') == 'partial-visual-state-mismatch']
for r in targets[:3]:
    pack = r['pack']
    home = os.path.dirname(os.path.dirname(pack))
    frames = sorted({int(f['frame']) for f in r.get('frameResults', [])})
    print('===', r['rom'], 'frames', frames)
    env = os.environ.copy()
    env['MESEN_HOME'] = home
    cmd = [r'D:\MesenHigh\bin\win-x64\Release\Mesen.exe', '--hdstudio', r['rom'] if False else '', '', '--verify',
           '--shots=1', '--frames=%d' % max(frames), '--verify-at=' + ','.join(map(str, frames)), '--out=' + pack]
    # need ROM path from inventory
    inv = json.load(open(r'D:\MesenHighWork\validation-v07\inventory\library-inventory.json', encoding='utf-8'))
    rom = next(x for x in inv['roms'] if x['sha256'] == r['sha256'])
    cmd[2] = rom['path']
    cmd[3] = r'D:\MesenHigh\scripts\deterministic_input.lua'
    p = subprocess.run(cmd, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
    for line in p.stdout.splitlines():
        if 'gameplay state' in line or 'comparison' in line or 'HD pack' in line:
            print(line)
