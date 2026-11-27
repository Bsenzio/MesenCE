import json, glob, os, collections

files = glob.glob(r'D:\MesenHighWork\validation-v07\**\*results*.json', recursive=True)
for f in files:
    if os.path.getsize(f) < 1000:
        continue
    try:
        d = json.load(open(f, encoding='utf-8'))
    except Exception:
        continue
    if isinstance(d, list):
        rows = d
    elif isinstance(d, dict):
        rows = d.get('rows') if isinstance(d.get('rows'), list) else d.get('results') if isinstance(d.get('results'), list) else []
    else:
        rows = []
    roms = [r for r in rows if isinstance(r, dict) and r.get('sha256')]
    if roms:
        print(os.path.relpath(f, r'D:\MesenHighWork\validation-v07'), 'rows=', len(roms), 'uniq=', len({r['sha256'] for r in roms}),
              'engine=', collections.Counter(r.get('engineFingerprint') for r in roms).most_common(3))
        print('   ', collections.Counter(r.get('status') for r in roms).most_common(10))
