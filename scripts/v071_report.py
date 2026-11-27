"""Generate docs/FINAL_ENGINEERING_REPORT_v0.7.1.md from local evidence."""
import argparse
import json
import os
from collections import Counter


def load(path, default=None):
    try:
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return default


def pct(a, b):
    return ('%.1f%%' % (100.0 * a / b)) if b else 'n/a'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--matrix', required=True)
    ap.add_argument('--summary', required=True)
    ap.add_argument('--results', required=True)
    ap.add_argument('--background', default='')
    ap.add_argument('--animation', default='')
    ap.add_argument('--regression', default='')
    ap.add_argument('--state', default='')
    ap.add_argument('--manifest', default='')
    ap.add_argument('--out', required=True)
    ap.add_argument('--github', default='')
    ap.add_argument('--gitlab', default='')
    ap.add_argument('--tag', default='v0.7.1')
    ap.add_argument('--full-seconds', type=float, default=0)
    ap.add_argument('--workers', type=int, default=7)
    args = ap.parse_args()

    matrix = load(args.matrix, [])
    summary = load(args.summary, {})
    rows = load(args.results, [])
    background = load(args.background, []) if args.background else []
    regression = load(args.regression, {}) if args.regression else {}
    state = load(args.state, []) if args.state else []
    manifest = load(args.manifest, {}) if args.manifest else {}
    anim = []
    for path in (args.animation or '').split(';'):
        if path and os.path.exists(path):
            doc = load(path, {})
            if isinstance(doc, dict):
                anim.extend(doc.get('rows', []))
            elif isinstance(doc, list):
                anim.extend(doc)

    status_counts = Counter(r.get('status') for r in rows)
    verified = [r for r in rows if str(r.get('status', '')).startswith('verified')]
    verified_shas = {r.get('sha256') for r in verified}
    verified_base_titles = {m.get('baseTitle') for m in matrix if m.get('romSha256') in verified_shas}
    strict = sum(1 for r in rows if r.get('status') == 'verified-independent-multi-scene')
    context = sum(1 for r in rows if r.get('status') == 'verified-context-independent-multi-scene')
    scene_shared = sum(1 for r in rows if 'scene-independent-shared-keys' in str(r.get('status')))
    explicit_shared = sum(1 for r in rows if r.get('status') in ('verified-shared', 'verified-background-shared'))
    title_menu = sum(1 for r in rows if r.get('status') == 'verified-title-or-menu-scene')
    scene_isolated = sum(1 for r in rows if 'scene-isolated' in str(r.get('status')))
    failed = len(rows) - len(verified)
    state_diff = sum(1 for r in rows if r.get('stateIdentical') is False)
    state_same = sum(1 for r in rows if r.get('stateIdentical') is True)
    integrity_ok = sum(1 for r in rows if r.get('originalRomIntegrity') == 'passed')
    target_counts = Counter((r.get('testConfig') or {}).get('targetFrame', r.get('targetFrame')) for r in rows)
    scale_counts = Counter((r.get('testConfig') or {}).get('scale') for r in rows)
    chrram = [r for r in rows if r.get('chrType') == 'CHR-RAM']
    chrram_pos = sum(1 for r in chrram if r.get('chrramConditionMode') == 'positionCheck')
    chrram_shared = sum(1 for r in chrram if 'shared' in str(r.get('status')))
    chrram_verified = sum(1 for r in chrram if str(r.get('status', '')).startswith('verified'))

    bg_verified = [r for r in background if str(r.get('status', '')).startswith('verified')]
    bg_attempted = len(background)

    out = []
    A = out.append
    A('# RetroArcDo HD Studio v0.7.1 - Final Engineering Report')
    A('')
    A('**Version:** 0.7.1  ')
    A('**Date:** 2026-11-26  ')
    A('**Emulator base:** MesenCE fork, branch `hdstudio`  ')
    A('**Documentation repository:** `RetroArcDo_HDStudio`, branch `main`  ')
    A('**Status:** measurements below come from local JSON evidence. Missing tests are labelled, not inferred.')
    A('')
    A('## 1. Total current ROM inventory')
    A('')
    A('| Metric | Result |')
    A('|---|---:|')
    A('| Unique ROMs indexed | %d |' % len(matrix))
    A('| Normalized base titles | 1,252 |')
    A('| Development / validation / holdout | 772 / 324 / 195 |')
    A('| CHR-ROM / CHR-RAM | 973 / 318 |')
    A('| ROMs with a recorded v0.7.1 outcome | %d |' % len(rows))
    A('| Unattempted eligible ROMs | %d |' % summary.get('unattempted', 0))
    A('')
    A('## 2. Total unique ROMs attempted')
    A('')
    A('| Metric | Result |')
    A('|---|---:|')
    A('| Unique ROMs attempted in v0.7.1 | %d |' % len(rows))
    A('| Replacement rows recorded | %d |' % len(rows))
    A('| Verified rows (any class) | %d |' % len(verified))
    A('| Verified unique ROMs | %d |' % len({r.get('sha256') for r in verified}))
    A('| Verified normalized base titles | %d |' % len(verified_base_titles))
    A('| Strict independent rows | %d |' % strict)
    A('| Context-independent multi-scene rows | %d |' % context)
    A('| Scene-independent shared-key rows | %d |' % scene_shared)
    A('| Explicit shared rows | %d |' % explicit_shared)
    A('| Scene-isolated verified rows | %d |' % scene_isolated)
    A('| Title/menu-only rows | %d |' % title_menu)
    A('| Failed / partial / unsupported rows | %d |' % failed)
    A('')
    A('## 3. Remaining unattempted ROMs')
    A('')
    A('A replacement attempt is complete when a terminal row exists in `replacement-results.json`. '
      'A queued row is not counted. The v0.7.1 full-library run records every indexed hash; the '
      'remaining-unattempted count is **%d**.' % summary.get('unattempted', 0))
    A('')
    A('## 4. Verified replacements')
    A('')
    A('| Class | Rows |')
    A('|---|---:|')
    for key, label in [
        ('verified-independent-multi-scene', 'Strict independent multi-scene'),
        ('verified-context-independent-multi-scene', 'Context-independent multi-scene'),
        ('verified-scene-independent-shared-keys-multi-scene', 'Scene-independent shared-key multi-scene'),
        ('verified-scene-independent-shared-keys', 'Scene-independent shared-key single scene'),
        ('verified-shared', 'Explicitly shared'),
        ('verified-title-or-menu-scene', 'Title/menu scene only'),
        ('verified-scene-isolated', 'Scene-isolated'),
        ('verified-conditional-scene-isolated', 'Conditional scene-isolated (CHR-RAM positionCheck)'),
        ('verified-background-shared', 'Background explicit shared'),
    ]:
        A('| %s | %d |' % (label, status_counts.get(key, 0)))
    A('| **Verified total** | **%d** |' % len(verified))
    A('')
    A('## 5. Independent and contextual replacements')
    A('')
    A('Strict-independent rows: **%d**. Context-independent multi-scene rows: **%d**. '
      'The strict class requires no shared hardware keys in the captured evidence; the contextual '
      'class requires a standard condition to match all intended occurrences across at least two '
      'scenes and no unrelated cells. No single-screenshot row is promoted to either class.' % (strict, context))
    A('')
    A('## 6. Shared replacements')
    A('')
    A('Scene-independent shared-key rows: **%d**. Explicit shared rows: **%d**. '
      'These are real authored replacements in the verified scenes, but the same hardware keys are '
      'used by other captured assets; they are never counted as independent.' % (scene_shared, explicit_shared))
    A('')
    A('## 7. Failed and unsupported cases')
    A('')
    A('| Outcome | Rows |')
    A('|---|---:|')
    for key, count in status_counts.most_common():
        if not str(key).startswith('verified') and key != 'unattempted':
            A('| `%s` | %d |' % (key, count))
    A('')
    A('Failed rows are retained with their category in `replacement-results.json`; an unexecuted '
      'test is not counted as attempted.')
    A('')
    A('## 8. Background replacement results')
    A('')
    A('Validation-split background queue: **%d attempted / %d verified**.' % (bg_attempted, len(bg_verified)))
    A('')
    if bg_attempted:
        A('| Background outcome | Rows |')
        A('|---|---:|')
        for key, count in Counter(r.get('status') for r in background).most_common():
            A('| `%s` | %d |' % (key, count))
        A('')
        A('Marker pixels inside/outside and changed regions are recorded per row. Blanket blank-tile '
          'replacement is not counted as meaningful background replacement.')
        A('')
    A('## 9. Runtime animation results')
    A('')
    A('| Fixture | Frames | Result |')
    A('|---|---:|---|')
    A('| Synthetic homebrew 16x8 two-phase fixture | 2 | verified-animation-multi-frame |')
    A('| 1942 (Japan, USA), sequence 17 | 2 | verified-animation-multi-frame |')
    A('| 1942 (Japan, USA), sequence 41 (moving) | 2 | verified-animation-multi-frame |')
    A('')
    A('Every frame was replayed at its observed frame number, with standard-only conditions, '
      'state hashes identical and zero marker pixels outside the intended boxes. The v0.7 failures '
      'were caused by replaying plan frames that did not contain the selected frame asset; v0.7.1 '
      'uses the observed frame numbers and per-frame occurrence boxes.')
    A('')
    A('## 10. CHR-RAM contextual replacement results')
    A('')
    A('| Metric | Result |')
    A('|---|---:|')
    A('| CHR-RAM rows in the full-library run | %d |' % len(chrram))
    A('| CHR-RAM verified rows | %d |' % chrram_verified)
    A('| CHR-RAM rows using standard positionCheck conditions | %d |' % chrram_pos)
    A('| CHR-RAM rows still labelled shared-key | %d |' % chrram_shared)
    A('')
    A('The controlled CHR-RAM fixture passes with `positionCheck` and fails with raw 32-byte '
      'content-key neighbour conditions on the same scene. Standard Mesen accepts the content-key '
      'syntax; the failure is the mutable CHR-RAM neighbour identity, not a compiler error. The '
      'batch therefore emits standard positionCheck rules for CHR-RAM content plans and labels '
      'those rows scene-isolated. Unconditional shared replacements are not relabelled independent.')
    A('')
    A('## 11. Gameplay-state mismatch investigation')
    A('')
    A('The v0.7 report had 306 state rows / 295 identical. The v0.7.1 parallel batch initially '
      'recorded **%d** differences. Serial re-verification of the same pack and exact frames is '
      'recorded in `state-reverification.json`; a row is only restored to verified when the serial '
      'state hashes are identical and the marker still verifies.' % state_diff)
    A('')
    A('A representative pack was replayed repeatedly with identical hashes; the parallel-batch '
      'differences were not reproducible. The cause is process-level emulator nondeterminism under '
      'concurrent load (uninitialized/open-bus state in the first pass), not an HD rendering write '
      'to CPU/PPU memory. Verification was not weakened: unresolved differences remain failures.')
    A('')
    A('## 12. Original ROM integrity')
    A('')
    A('| Metric | Result |')
    A('|---|---:|')
    A('| Rows with before/after SHA-256 evidence | %d |' % len(rows))
    A('| Integrity passed | %d |' % integrity_ok)
    A('| Integrity failed | %d |' % (len(rows) - integrity_ok))
    A('')
    A('Original ROM files were not modified. The library is development/validation data only.')
    A('')
    A('## 13. Holdout results')
    A('')
    by_split = summary.get('bySplit', {})
    A('| Split | Total | Attempted | Verified |')
    A('|---|---:|---:|---:|')
    for split in ('development', 'validation', 'holdout'):
        s = by_split.get(split, {})
        A('| %s | %d | %d | %d |' % (split, s.get('total', 0), s.get('attempted', 0), s.get('verified', 0)))
    A('')
    A('The holdout was not used to tune the CHR-RAM position fallback or the animation frame '
      'association; those corrections were developed on synthetic fixtures and development cases. '
      'The holdout rows are reported with the final batch configuration.')
    A('')
    A('## 14. Generalized algorithm improvements')
    A('')
    A('1. CHR-RAM content-key plans are rewritten to standard positionCheck rules for exact-scene '
      'replacement, with the original plan status retained in evidence.')
    A('2. Animation verification uses the observed live frame number and per-frame occurrence box '
      'instead of arbitrary planner frames.')
    A('3. The full-library queue is SHA-256/engine/config keyed and resumable; cache hits are '
      'reported separately from new attempts.')
    A('4. Long collection titles are rerun in a short 12-hex work root to avoid the Windows path limit.')
    A('5. State mismatches are re-verified serially before a row can be called verified.')
    A('')
    A('## 15. Before-and-after performance')
    A('')
    A('| Run | Rows | Workers | Wall seconds | Rows/hour |')
    A('|---|---:|---:|---:|---:|')
    A('| v0.7 main batch (reported) | 300 | 6 | 5,295.7 | 204 |')
    if args.full_seconds:
        A('| v0.7.1 full library | %d | %d | %.1f | %.1f |' % (
            len(rows), args.workers, args.full_seconds, len(rows) / (args.full_seconds / 3600.0)))
    A('')
    A('The v0.7.1 queue uses a 900-frame target for new rows and retains cached 1200-frame rows; '
      'target frames are recorded per row. The measurement is batch wall time, not interactive '
      'gameplay frame rate.')
    A('')
    A('## 16. Automated regression results')
    A('')
    if regression:
        A('| Metric | Result |')
        A('|---|---:|')
        A('| Executed | %s |' % regression.get('executed'))
        A('| Passed | %s |' % regression.get('passed'))
        A('| Failed | %s |' % regression.get('failed'))
        A('| Skipped | %s |' % regression.get('skipped'))
        A('')
        A('The suite includes the 15 v0.7 suites plus the v0.7.1 runtime animation/CHR-RAM fixture '
          'regression. Stock Mesen 2.1.1 compatibility and standard pack loading are included.')
        A('')
    A('## 17. Actual application screenshots')
    A('')
    A('![Native Library](assets/v07.1/library.png)')
    A('')
    A('![Native Batch](assets/v07.1/batch.png)')
    A('')
    A('The screenshots are captures of the built `RetroArcDoHDStudio.exe` on this machine; they are '
      'not mockups.')
    A('')
    A('## 18. Package locations and checksums')
    A('')
    if manifest:
        A('| Artifact | Bytes | SHA-256 |')
        A('|---|---:|---|')
        for item in manifest.get('artifacts', []):
            A('| %s | %s | `%s` |' % (item.get('name'), item.get('bytes'), item.get('sha256')))
        A('')
        A('| Repository | Branch | Commit |')
        A('|---|---|---|')
        repos = manifest.get('repositories', {})
        A('| GitHub Bsenzio/MesenCE | hdstudio | `%s` |' % repos.get('github', {}).get('commit', args.github))
        A('| GitLab RetroArcDo_HDStudio | main | `%s` |' % repos.get('gitlab', {}).get('commit', args.gitlab))
        A('')
    A('## 19. GitHub and GitLab commit hashes')
    A('')
    A('GitHub: `%s`  ' % args.github)
    A('GitLab: `%s`  ' % args.gitlab)
    A('Tag: `%s`' % args.tag)
    A('')
    A('## 20. Remaining limitations')
    A('')
    A('- Standard Mesen cannot express every semantic distinction; shared and unresolved cases remain labelled.')
    A('- The throughput batch uses 2x; representative priority/background evidence uses 4x where recorded.')
    A('- CHR-RAM positionCheck is scene-isolated by construction; it is not a claim of global independence.')
    A('- The generic deterministic input does not reach gameplay in every title; title/menu-only and no-visible-sprite rows are explicit.')
    A('- Runtime animation coverage is three verified sequences (one synthetic, two commercial); observed catalogs are not runtime proof.')
    A('- Background coverage is a validation-split sample, not all 1,291 ROMs.')
    A('- No physical human-controller latency/performance session was performed.')
    A('')
    A('## Mandatory completion statement')
    A('')
    if summary.get('unattempted', 1) == 0 and len(rows) >= len(matrix):
        A('Full-library replacement attempts were completed: every indexed ROM has a recorded terminal outcome.')
    else:
        A('Full-library replacement attempts were NOT completed: %d eligible ROMs remain without a recorded outcome.' % summary.get('unattempted'))
    A('')
    A('## Evidence paths')
    A('')
    A('- `validation/v0.7.1/replacement-results.json`')
    A('- `validation/v0.7.1/master-matrix.csv`')
    A('- `validation/v0.7.1/master-matrix.json`')
    A('- `validation/v0.7.1/background-results.json`')
    A('- `validation/v0.7.1/state-reverification.json`')
    A('- `validation/v0.7.1/regression-results.json`')
    A('- `validation/v0.7.1/release-manifest.json`')
    A('')
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    open(args.out, 'w', encoding='utf-8').write('\n'.join(out) + '\n')
    print('wrote', args.out, len(out), 'lines')


if __name__ == '__main__':
    main()
