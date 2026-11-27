import json, glob, os, shutil

OUT = r'D:\MesenHighWork\validation-v071\full-library'
os.makedirs(OUT, exist_ok=True)
inv = json.load(open(r'D:\MesenHighWork\validation-v07\inventory\library-inventory.json', encoding='utf-8'))
by_sha = {r['sha256']: r for r in inv['roms']}
files = glob.glob(r'D:\MesenHighWork\validation-v07\**\replacement-results.json', recursive=True)
seen = {}
for f in files:
    try:
        d = json.load(open(f, encoding='utf-8'))
    except Exception:
        continue
    if not isinstance(d, list):
        continue
    for r in d:
        sha = r.get('sha256')
        if not sha or not str(r.get('status', '')).startswith('verified'):
            continue
        if r.get('engineFingerprint') != 'c57f2fc119c00ec7':
            continue
        p = r.get('pack') or ''
        scale = None
        try:
            for line in open(os.path.join(p, 'hires.txt'), encoding='utf-8', errors='ignore'):
                if line.startswith('<scale>'):
                    scale = int(line.strip().split('>')[1])
                    break
        except Exception:
            pass
        if scale != 2:
            continue
        if sha in seen:
            #Prefer the row with the most verification scenes.
            if (r.get('verificationScenes') or 0) <= (seen[sha].get('verificationScenes') or 0):
                continue
        r = dict(r)
        r['testConfig'] = {
            'targetFrame': r.get('targetFrame') or 1200,
            'scale': 2,
            'verifyScenes': r.get('verificationScenes') or 1,
            'targetMode': r.get('targetMode') or 'sprite',
            'chrramConditions': 'legacy-unconditional',
            'contextPlan': True,
            'allowShared': True,
            'seed': 20261124,
            'cachedFrom': f,
        }
        seen[sha] = r

copied = 0
for sha, r in seen.items():
    rom = by_sha.get(sha)
    if not rom:
        continue
    stem = os.path.splitext(rom['name'])[0].replace('<', '_').replace('>', '_').replace(':', '_').replace('"', '_').replace('/', '_').replace('\\', '_').replace('|', '_').replace('?', '_').replace('*', '_')[:100]
    work = os.path.join(OUT, sha[:12] + '_' + stem)
    os.makedirs(work, exist_ok=True)
    json.dump(r, open(os.path.join(work, 'result.json'), 'w', encoding='utf-8'), indent=1)
    copied += 1
print('seeded', copied, 'of', len(seen))
all_hashes = [r['sha256'] for r in inv['roms']]
open(r'D:\MesenHighWork\validation-v071\all-hashes.txt', 'w').write('\n'.join(all_hashes))
