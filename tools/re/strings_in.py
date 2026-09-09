"""strings_in.py <symbol> -- distinct string literals referenced inside a function."""
import sys
sys.path.insert(0, '.')
import relib as R, idx, pdata as P

T = R.table()

def strings_of(sym, maxlen=90):
    if sym not in T: return None, None
    a = T[sym]
    f = P.func_of(a)
    if not f: return a, None
    out, seen = [], set()
    for x, tg in idx.leas_in(f[0], f[1]):
        s = R.cstr(tg, maxlen)
        if s and s.isprintable() and 0 < len(s) < maxlen and s not in seen:
            seen.add(s); out.append(s)
    return a, out

if __name__ == '__main__':
    for sym in sys.argv[1:]:
        a, out = strings_of(sym)
        if out is None:
            print('%s: not found / no bounds' % sym); continue
        f = P.func_of(a)
        print('=== %s  %#x  size=%#x  %d distinct strings ===' % (sym, a, f[1] - f[0], len(out)))
        for s in out: print('   %r' % s)
        print()
