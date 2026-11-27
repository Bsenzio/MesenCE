import json, os, shutil, subprocess, sys
from PIL import Image, ImageDraw

ROOT = r'D:\MesenHighWork\validation-v071\anim-fixture3'
WORK = os.path.join(ROOT, os.listdir(ROOT)[0])
SRC_PACK = os.path.join(WORK, 'attempt1', 'home', 'HdPacks', 'hdstudio_anim')
OUT = r'D:\MesenHighWork\validation-v071\anim-runtime'
if os.path.isdir(OUT):
    shutil.rmtree(OUT)
os.makedirs(OUT)
HOME = os.path.join(OUT, 'home')
shutil.copytree(os.path.join(WORK, 'attempt1', 'home'), HOME)
# Start from the capture evidence only. The previous attempt's project/imports
# must not leak a second unconditional replacement into the animation fixture.
for name in ('project.hdstudio.json', 'hires.txt',
             'HdStudio/project.hdstudio.json', 'HdStudio/base-hires.txt'):
    try:
        os.remove(os.path.join(HOME, 'HdPacks', 'hdstudio_anim', name))
    except OSError:
        pass
PACK = os.path.join(HOME, 'HdPacks', 'hdstudio_anim')
for sub in ('artwork', 'imports', 'templates', 'verify'):
    shutil.rmtree(os.path.join(PACK, 'HdStudio', sub), ignore_errors=True)
for name in ('picked-assets.json',):
    try:
        os.remove(os.path.join(PACK, 'HdStudio', 'live', name))
    except OSError:
        pass
assets_path = os.path.join(PACK, 'HdStudio', 'assets', 'assets.json')
assets_doc = json.load(open(assets_path, encoding='utf-8'))
assets_doc['assets'] = [a for a in assets_doc.get('assets', []) if not str(a.get('id', '')).startswith('picked:')]
json.dump(assets_doc, open(assets_path, 'w', encoding='utf-8'), indent=1)

MARKER = os.path.join(OUT, 'marker.png')
im = Image.new('RGBA', (64, 64), (255, 0, 255, 255))
d = ImageDraw.Draw(im)
d.rectangle([4, 4, 59, 59], fill=(0, 255, 255, 255))
d.rectangle([16, 12, 44, 22], fill=(0, 255, 0, 255))
im.save(MARKER)

cond = 'positionCheckX:>=,40&positionCheckX:<=,55&positionCheckY:>=,160&positionCheckY:<=,167'
plan = {
    'status': 'scene-isolated', 'contextRule': cond,
    'cellConditions': {
        '1@0,0': cond, '3@8,0': cond, '2@8,0': cond,
    }
}
PLAN = os.path.join(OUT, 'plan.json')
json.dump(plan, open(PLAN, 'w'), indent=1)

ROM = r'D:\MesenHighWork\test-roms-new2\hdstudio_anim.nes'
ENV = os.environ.copy()
ENV['MESEN_HOME'] = HOME
EXE = r'D:\MesenHigh\bin\win-x64\Release\Mesen.exe'
assets = json.load(open(os.path.join(PACK, 'HdStudio', 'assets', 'assets.json'), encoding='utf-8'))['assets']
seq = [a for a in assets if a.get('kind') == 'animationSequence'][0]
frames = sorted([a for a in assets if a.get('kind') == 'animationFrame' and a.get('animationId') == seq['id']],
                key=lambda a: a['id'])

def run(args):
    p = subprocess.run([EXE] + args, env=ENV, capture_output=True, text=True,
                       encoding='utf-8', errors='replace')
    print(p.stdout[-3000:])
    if p.returncode:
        print('EXIT', p.returncode, p.stderr[-1000:])
        sys.exit(p.returncode)

for i, asset in enumerate(frames):
    args = ['--hdstudio', ROM, '--import', '--asset=' + asset['id'], '--image=' + MARKER,
            '--fit=stretch', '--all-palettes', '--mirror-conditions',
            '--import-scale=4', '--import-name=AnimFrame%d' % (i + 1),
            '--condition-map=' + PLAN, '--out=' + PACK]
    if i == 0:
        args.append('--minimal')
    run(args)

run(['--hdstudio', ROM, r'D:\MesenHigh\scripts\deterministic_input.lua', '--verify',
     '--shots=1', '--frames=14', '--verify-at=6,14', '--out=' + PACK])

verify = os.path.join(PACK, 'HdStudio', 'verify')
result = {'sequence': seq['id'], 'frames': [f['id'] for f in frames], 'rows': []}

for frame in (6, 14):
    o = Image.open(os.path.join(verify, 'frame_%05d_original.png' % frame)).convert('RGB')
    h = Image.open(os.path.join(verify, 'frame_%05d_hd.png' % frame)).convert('RGB')
    scale = h.width // o.width
    inside = outside = changed_inside = changed_outside = 0
    box = (40, 160, 56, 168)
    for y in range(h.height):
        for x in range(h.width):
            if h.getpixel((x, y)) in {(255, 0, 255), (0, 255, 255), (0, 255, 0)}:
                if box[0] <= x / scale < box[2] and box[1] <= y / scale < box[3]:
                    inside += 1
                else:
                    outside += 1
    down = h.resize(o.size, Image.Resampling.NEAREST)
    for y in range(o.height):
        for x in range(o.width):
            if down.getpixel((x, y)) != o.getpixel((x, y)):
                if box[0] <= x < box[2] and box[1] <= y < box[3]:
                    changed_inside += 1
                else:
                    changed_outside += 1
    result['rows'].append({'frame': frame, 'inside': inside, 'outside': outside,
                           'changedInside': changed_inside, 'changedOutside': changed_outside})
    print(result['rows'][-1])
result['status'] = ('verified-animation-multi-frame'
                    if len(result['rows']) >= 2 and all(r['inside'] > 0 and r['outside'] == 0 for r in result['rows'])
                    else 'failed-animation')
json.dump(result, open(os.path.join(OUT, 'animation-fixture-result.json'), 'w'), indent=1)
