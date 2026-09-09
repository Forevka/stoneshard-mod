"""who.py <symbol> [...]  -- caller counts + calling symbols, GML-aware."""
import sys, collections
sys.path.insert(0, '.')
import relib as R, idx, pdata as P

T = R.table()
SET = {v: k for k, v in T.items()}

def label(va):
    f = P.func_of(va)
    if f and f[0] in SET: return SET[f[0]]
    if f: return 'sub_%X' % f[0]
    return 'no-pdata@%#x' % va

def report(name, show=12):
    if name not in T:
        print('%s: NOT IN SYMBOL TABLE' % name); return
    a = T[name]
    sites = idx.calls_to(a) + idx.jmps_to(a)
    c = collections.Counter(label(s) for s in sites)
    print('=== %s  %#x  %d call sites, %d distinct callers ===' % (name, a, len(sites), len(c)))
    for k, v in c.most_common(show):
        print('    %3dx  %s' % (v, k))
    if len(c) > show: print('    ... +%d more distinct callers' % (len(c) - show))
    print()

if __name__ == '__main__':
    for n in sys.argv[1:]:
        report(n)
