"""callees.py <symbol> [...] -- outgoing GML calls of a function, in address order."""
import sys, collections
sys.path.insert(0, '.')
import relib as R, idx, pdata as P

T = R.table()
SET = {v: k for k, v in T.items()}

def name_at(va):
    if va in SET: return SET[va]
    f = P.func_of(va)
    if f and f[0] in SET: return SET[f[0]] + '+%#x' % (va - f[0])
    if f: return 'sub_%X' % f[0]
    return '?%#x' % va

def body(name):
    """gml_Script_X is the compiled body; gml_GlobalScript_X is a 0xC6 trampoline."""
    if name.startswith('gml_GlobalScript_'):
        g = 'gml_Script_' + name[len('gml_GlobalScript_'):]
        if g in T: return T[g], g
    return T[name], name

def report(name):
    if name not in T:
        print('%s: NOT FOUND' % name); return
    a, real = body(name)
    f = P.func_of(a)
    if not f:
        print('%s: no pdata bounds' % name); return
    print('=== %s (%s) %#x..%#x size %#x ===' % (name, real, f[0], f[1], f[1] - f[0]))
    strs = []
    for x, tg in idx.leas_in(f[0], f[1]):
        s = R.cstr(tg, 100)
        if s and s.isprintable() and 2 < len(s) < 80: strs.append(s)
    if strs: print('  strings: %s' % (strs[:14],))
    seen = collections.Counter()
    order = []
    for s, tg in idx.calls_in(f[0], f[1]):
        n = name_at(tg)
        if n not in seen: order.append(n)
        seen[n] += 1
    for n in order:
        if n.startswith('gml_'):
            print('    %2dx %s' % (seen[n], n))
    other = [n for n in order if not n.startswith('gml_')]
    if other:
        print('    runtime helpers: %s' % ', '.join('%s(x%d)' % (n, seen[n]) for n in other[:10]))
    print()

if __name__ == '__main__':
    for n in sys.argv[1:]:
        report(n)
