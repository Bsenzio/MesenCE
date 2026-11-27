import hashlib, json, os, time

inv = json.load(open(r'D:\MesenHighWork\validation-v07\inventory\library-inventory.json', encoding='utf-8'))
rows = []
for rom in inv['roms']:
    h = hashlib.sha256()
    try:
        with open(rom['path'], 'rb') as f:
            for chunk in iter(lambda: f.read(1024 * 1024), b''):
                h.update(chunk)
        actual = h.hexdigest()
        ok = actual == rom['sha256']
    except Exception as ex:
        actual = 'error:' + str(ex)
        ok = False
    rows.append({'sha256': rom['sha256'], 'path': rom['path'], 'actualSha256': actual, 'passed': ok})
out = {
    'generatedAtUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
    'uniqueRoms': len(rows),
    'passed': sum(1 for r in rows if r['passed']),
    'failed': sum(1 for r in rows if not r['passed']),
    'rows': rows,
}
json.dump(out, open(r'D:\MesenHighWork\validation-v071\library-integrity.json', 'w', encoding='utf-8'), indent=1)
print(out['uniqueRoms'], out['passed'], out['failed'])
