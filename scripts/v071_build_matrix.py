"""Build the v0.7.1 master ROM matrix and aggregate counts.

Inputs are machine-readable local evidence only. One row per unique indexed ROM.
Missing evidence is explicit (`not-tested` / `unattempted`), never inferred.
"""
import argparse
import csv
import json
import os
from collections import Counter, defaultdict


def load(path, default):
    try:
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return default


def terminal_status(row):
    s = str(row.get('status') or '')
    if row.get('mapper') in (576, 396):
        return 'Unsupported mapper'
    if row.get('originalRomIntegrity') == 'failed':
        return 'Invalid or unsupported ROM'
    if s.startswith('verified-independent'):
        return 'Verified independent'
    if s.startswith('verified-context-independent'):
        return 'Verified context-independent'
    if s.startswith('verified-scene-independent-shared-keys'):
        return 'Verified scene-independent with shared keys'
    if s.startswith('verified-shared') or s.startswith('verified-background-shared'):
        return 'Verified explicitly shared'
    if s.startswith('verified-title-or-menu'):
        return 'Verified menu or title scene only'
    if s.startswith('verified-scene-isolated') or s.startswith('verified-conditional-scene-isolated'):
        return 'Verified scene-isolated (partial coverage)'
    if s.startswith('verified-background'):
        return 'Verified background replacement'
    if 'context-unresolved' in s:
        return 'Unresolved contextual ambiguity'
    if s.startswith('failed-context-rule') and (row.get('markerPixelsInside') or 0) > 0:
        return 'Partial replacement'
    if s.startswith('failed-no-visible') or s.startswith('failed-not-replaced') or s.startswith('failed-verify'):
        return 'Failed replacement'
    if s.startswith('partial-'):
        return 'Partial replacement'
    if s.startswith('failed-'):
        return 'Failed exploration'
    return 'Unattempted'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--inventory', required=True)
    ap.add_argument('--results', required=True)
    ap.add_argument('--background', default='')
    ap.add_argument('--animation', default='')
    ap.add_argument('--state', default='')
    ap.add_argument('--out', required=True)
    args = ap.parse_args()

    inv = load(args.inventory, {})
    rows = load(args.results, [])
    by_sha = {r.get('sha256'): r for r in rows if r.get('sha256')}
    bg = {r.get('sha256'): r for r in load(args.background, []) if r.get('sha256')}
    state = {r.get('sha256'): r for r in load(args.state, []) if r.get('sha256')}
    anim_doc = load(args.animation, {})
    anim_rows = anim_doc.get('rows', []) if isinstance(anim_doc, dict) else anim_doc
    anim_by_sha = defaultdict(list)
    for a in anim_rows:
        if a.get('sha256'):
            anim_by_sha[a['sha256']].append(a)

    matrix = []
    for rom in inv.get('roms', []):
        r = by_sha.get(rom['sha256'])
        b = bg.get(rom['sha256'])
        st = state.get(rom['sha256'])
        anim = anim_by_sha.get(rom['sha256'], [])
        status = (r or {}).get('status', 'unattempted')
        if st:
            status = st.get('status', status)
        matrix.append({
            'romSha256': rom['sha256'],
            'gameTitle': rom['name'],
            'baseTitle': rom.get('baseTitle') or rom.get('title'),
            'datasetMembership': rom.get('split'),
            'mapper': rom.get('mapper'),
            'chrType': rom.get('chrType'),
            'captureStatus': 'captured' if (r or {}).get('pauseFrame') else ('failed' if r else 'unattempted'),
            'explorationStatus': 'paused-scene' if (r or {}).get('pauseFrame') else ('failed' if r else 'unattempted'),
            'spriteReplacementAttempt': 'yes' if r and r.get('targetMode') != 'background' else 'no',
            'spriteReplacementOutcome': status,
            'backgroundReplacementAttempt': 'yes' if b else 'no',
            'backgroundReplacementOutcome': (b or {}).get('status', 'not-tested'),
            'animationApplicability': ('observed-runtime-sequences' if anim else 'not-evaluated'),
            'animationOutcome': '; '.join(a.get('status', '') for a in anim) if anim else 'not-tested',
            'verificationSceneCount': (r or {}).get('verificationScenes', 0),
            'contextClassification': (r or {}).get('contextStatus') or (b or {}).get('contextStatus') or '',
            'intendedReplacementCoverage': (r or {}).get('markerPixelsInside', 0),
            'unintendedGraphicalChanges': (r or {}).get('markerPixelsOutside', 0),
            'gameplayStateIntegrity': ('identical' if (r or {}).get('stateIdentical') else
                                       ('different' if (r or {}).get('stateIdentical') is False else 'not-recorded')),
            'originalRomIntegrity': (r or {}).get('originalRomIntegrity', 'not-recorded'),
            'testScale': (r or {}).get('testConfig', {}).get('scale', (r or {}).get('testScale')),
            'engineFingerprint': (r or {}).get('engineFingerprint', ''),
            'failureReason': (r or {}).get('failure', '') or (b or {}).get('failure', ''),
            'evidence': (r or {}).get('verifyFolder', '') or (b or {}).get('verifyFolder', ''),
            'terminalStatus': terminal_status(r or {}),
            'serialStateReverification': (st or {}).get('stateIdenticalSerial'),
            'statusBeforeSerial': (st or {}).get('previousStatus'),
        })

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    fields = list(matrix[0].keys())
    with open(args.out + '.csv', 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=fields)
        w.writeheader()
        w.writerows(matrix)
    json.dump(matrix, open(args.out + '.json', 'w', encoding='utf-8'), indent=1)

    summary = {
        'uniqueRoms': len(matrix),
        'recordedRows': len(rows),
        'unattempted': sum(1 for m in matrix if m['spriteReplacementOutcome'] == 'unattempted'),
        'terminalStatusCounts': dict(Counter(m['terminalStatus'] for m in matrix)),
        'outcomeCounts': dict(Counter(m['spriteReplacementOutcome'] for m in matrix)),
        'verifiedAny': sum(1 for m in matrix if str(m['spriteReplacementOutcome']).startswith('verified')),
        'verifiedUniqueRom': sum(1 for m in matrix if str(m['spriteReplacementOutcome']).startswith('verified')),
        'backgroundAttempted': sum(1 for m in matrix if m['backgroundReplacementAttempt'] == 'yes'),
        'backgroundVerified': sum(1 for m in matrix if str(m['backgroundReplacementOutcome']).startswith('verified')),
        'animationVerifiedRows': sum(1 for m in matrix if 'verified-animation' in str(m['animationOutcome'])),
        'stateIdentical': sum(1 for m in matrix if m['gameplayStateIntegrity'] == 'identical'),
        'stateDifferent': sum(1 for m in matrix if m['gameplayStateIntegrity'] == 'different'),
        'romIntegrityPassed': sum(1 for m in matrix if m['originalRomIntegrity'] == 'passed'),
        'bySplit': {},
        'byMapper': {},
    }
    for split in ('development', 'validation', 'holdout'):
        subset = [m for m in matrix if m['datasetMembership'] == split]
        summary['bySplit'][split] = {
            'total': len(subset),
            'verified': sum(1 for m in subset if str(m['spriteReplacementOutcome']).startswith('verified')),
            'attempted': sum(1 for m in subset if m['spriteReplacementAttempt'] == 'yes'),
        }
    for mapper, group in sorted(defaultdict(list, {k: [m for m in matrix if m['mapper'] == k]
                                                 for k in {m['mapper'] for m in matrix}}).items(),
                                key=lambda kv: (-len(kv[1]), str(kv[0]))):
        summary['byMapper'][str(mapper)] = {
            'total': len(group),
            'verified': sum(1 for m in group if str(m['spriteReplacementOutcome']).startswith('verified')),
            'attempted': sum(1 for m in group if m['spriteReplacementAttempt'] == 'yes'),
        }
    json.dump(summary, open(args.out + '-summary.json', 'w', encoding='utf-8'), indent=1)
    print(json.dumps(summary, indent=1))


if __name__ == '__main__':
    main()
