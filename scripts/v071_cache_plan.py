import json, glob, os, collections
files = glob.glob(r'D:\MesenHighWork\validation-v07\**\replacement-results.json', recursive=True)
inv = json.load(open(r'D:\MesenHighWork\validation-v07\inventory\library-inventory.json', encoding='utf-8'))
by_sha = {r['sha256']: r for r in inv['roms']}
best = {}
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
        p = r.get('pack') or ''
        scale = None
        try:
            for line in open(os.path.join(p, 'hires.txt'), encoding='utf-8', errors='ignore'):
                if line.startswith('<scale>'):
                    scale = int(line.strip().split('>')[1])
                    break
        except Exception:
            pass
        r['_scale'] = scale
        r['_file'] = f
        if scale == 2 and r.get('targetFrame') == 1200 and (r.get('verificationScenes') or 0) >= 2:
            best.setdefault(sha, r)
print('cache3? verified scale2 target1200 scenes>=2:', len(best))
print('by file:', collections.Counter(os.path.basename(os.path.dirname(v['_file'])) for v in best.values()))
rem = [r for r in inv['roms'] if r['sha256'] not in best]
print('remaining:', len(rem))
print('by split:', collections.Counter(r['split'] for r in rem))
print('by chr:', collections.Counter(r['chrType'] for r in rem))
print('by mapper top:', collections.Counter(r['mapper'] for r in rem).most_common(12))
json.dump([{'sha256': v['sha256'], 'name': v['rom'], 'file': v['_file'], 'status': v['status']} for v in best.values()],
          open(r'D:\MesenHighWork\validation-v071\cache2x.json', 'w'), indent=1)
json.dump([r['sha256'] for r in rem], open(r'D:\MesenHighWork\validation-v071\remaining.txt', 'w'), indent=0)
