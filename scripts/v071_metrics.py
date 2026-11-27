import argparse, json, os
from collections import Counter

def load(p, d=None):
    try:
        with open(p, encoding='utf-8') as f: return json.load(f)
    except Exception: return d

ap = argparse.ArgumentParser()
ap.add_argument('--matrix-summary', required=True)
ap.add_argument('--results', required=True)
ap.add_argument('--background', default='')
ap.add_argument('--animation', default='')
ap.add_argument('--integrity', default='')
ap.add_argument('--state', default='')
ap.add_argument('--performance', default='')
ap.add_argument('--out', required=True)
args = ap.parse_args()

summary = load(args.matrix_summary, {})
results = load(args.results, [])
background = load(args.background, []) if args.background else []
animation = load(args.animation, {}) if args.animation else {}
integrity = load(args.integrity, {}) if args.integrity else {}
state = load(args.state, []) if args.state else []
perf = load(args.performance, {}) if args.performance else {}

verified = [r for r in results if str(r.get('status','')).startswith('verified')]
metrics = {
  'version': '0.7.1',
  'inventory': {'uniqueRoms': 1291, 'normalizedBaseTitles': 1252,
                'splits': {'development': 772, 'validation': 324, 'holdout': 195},
                'chrRom': 973, 'chrRam': 318},
  'replacement': {
    'rowsAttempted': len(results),
    'rowsVerified': len(verified),
    'uniqueRomsAttempted': len({r.get('sha256') for r in results}),
    'uniqueRomsVerified': len({r.get('sha256') for r in verified}),
    'unattempted': summary.get('unattempted', 0),
    'statusCounts': dict(Counter(r.get('status') for r in results)),
    'terminalStatusCounts': summary.get('terminalStatusCounts', {}),
    'bySplit': summary.get('bySplit', {}),
    'byMapper': summary.get('byMapper', {}),
  },
  'background': {
    'rowsAttempted': len(background),
    'rowsVerified': sum(1 for r in background if str(r.get('status','')).startswith('verified')),
    'statusCounts': dict(Counter(r.get('status') for r in background)),
  },
  'animation': animation.get('summary', {}) if isinstance(animation, dict) else {},
  'integrity': {k: integrity.get(k) for k in ('uniqueRoms','passed','failed')} if integrity else {},
  'stateReverification': {
    'rows': len(state),
    'identical': sum(1 for r in state if r.get('stateIdenticalSerial')),
    'different': sum(1 for r in state if r.get('stateIdenticalSerial') is False),
    'restoredVerified': sum(1 for r in state if str(r.get('status','')).startswith('verified')),
  },
  'performance': perf,
}
json.dump(metrics, open(args.out,'w',encoding='utf-8'), indent=1)
print(json.dumps({k: metrics[k] for k in ('replacement','background','animation','stateReverification')}, indent=1))
