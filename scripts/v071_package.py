import hashlib, json, os, shutil, zipfile

SRC = r'D:\MesenHighWork\dist\RetroArcDoHDStudio-v0.7'
DST = r'D:\MesenHighWork\dist\RetroArcDoHDStudio-v0.7.1'
ZIP = r'D:\MesenHighWork\dist\RetroArcDoHDStudio-v0.7.1-win-x64.zip'
REPO = r'D:\RetroArcDo_HDStudio'
MESEN_SCRIPTS = r'D:\MesenHigh\scripts'
WORK_SCRIPTS = r'D:\MesenHighWork\scripts'

if os.path.isdir(DST):
    shutil.rmtree(DST)
shutil.copytree(SRC, DST)
for name in ('Batman .nes', 'wamg.nes'):
    p = os.path.join(DST, name)
    if os.path.exists(p):
        os.remove(p)
# Replace validation tools with the v0.7.1 set.
tools = os.path.join(DST, 'Tools')
os.makedirs(tools, exist_ok=True)
for name in ('hdstudio_v07_animation_eval.py', 'hdstudio_v07_autoreplace.py',
             'hdstudio_v07_inventory.py', 'hdstudio_v07_metrics.py',
             'hdstudio_v07_regression.py'):
    p = os.path.join(tools, name)
    if os.path.exists(p):
        os.remove(p)
for name in ('hdstudio_v071_autoreplace.py', 'hdstudio_v071_regression.py',
             'test_hdstudio_v071_fixtures.py', 'write_v071_manifest.py'):
    src = os.path.join(MESEN_SCRIPTS, name)
    if os.path.exists(src):
        shutil.copy2(src, os.path.join(tools, name))
for name in ('v071_state_reverify.py', 'v071_build_matrix.py', 'v071_report.py',
             'v071_merge.py', 'v071_metrics.py', 'v071_integrity.py',
             'v071_anim_verify.py', 'v071_anim_fixture_verify.py'):
    src = os.path.join(WORK_SCRIPTS, name)
    if os.path.exists(src):
        shutil.copy2(src, os.path.join(tools, name))
for name, src in (
    ('FINAL_ENGINEERING_REPORT_v0.7.1.md', os.path.join(REPO, 'docs', 'FINAL_ENGINEERING_REPORT_v0.7.1.md')),
    ('VALIDATION_v0.7.1.md', os.path.join(REPO, 'docs', 'VALIDATION_v0.7.1.md')),
    ('RELEASE_v0.7.1.md', os.path.join(REPO, 'docs', 'RELEASE_v0.7.1.md')),
):
    if os.path.exists(src):
        shutil.copy2(src, os.path.join(DST, name))
if os.path.exists(p := os.path.join(DST, 'FINAL_ENGINEERING_REPORT_v0.7.md')):
    os.remove(p)
if os.path.exists(p := os.path.join(DST, 'VALIDATION_v0.7.md')):
    os.remove(p)
if os.path.exists(ZIP):
    os.remove(ZIP)
with zipfile.ZipFile(ZIP, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for root, dirs, files in os.walk(DST):
        for file in files:
            path = os.path.join(root, file)
            z.write(path, os.path.relpath(path, DST))
def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()
print('zip', os.path.getsize(ZIP), sha(ZIP))
print('exe', sha(os.path.join(DST, 'Mesen.exe')))
print('studio', sha(os.path.join(DST, 'RetroArcDoHDStudio.exe')))
