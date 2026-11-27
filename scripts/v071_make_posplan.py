import json, os, shutil
src = r'D:\MesenHighWork\validation-v071\probe3\8a506e04ecee_100 Man Dollar Kid - Maboroshi no Teiou Hen (Japan)\attempt1'
dst = r'D:\MesenHighWork\validation-v071\probe3c\attempt1'
if os.path.isdir(dst):
    shutil.rmtree(dst)
shutil.copytree(src, dst)
pack = os.path.join(dst, 'home', 'HdPacks', '100 Man Dollar Kid - Maboroshi no Teiou Hen (Japan)')
for name in os.listdir(pack):
    p = os.path.join(pack, name)
    if os.path.isfile(p):
        os.remove(p)
plan = json.load(open(os.path.join(src, 'context-plan.json'), encoding='utf-8'))
ox, oy = 176, 185
w, h = 32, 8
cond = '&'.join([
    'positionCheckX:>=,%d' % ox, 'positionCheckX:<=,%d' % (ox + w - 1),
    'positionCheckY:>=,%d' % oy, 'positionCheckY:<=,%d' % (oy + h - 1)])
for k in list(plan['cellConditions'].keys()):
    plan['cellConditions'][k] = cond
plan['contextRule'] = 'positionCheck'
json.dump(plan, open(os.path.join(dst, 'context-plan-pos.json'), 'w'), indent=1)
print(pack)
print(cond)
