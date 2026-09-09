"""Inspect the {name, func, slot} rows in .data and how YYC reaches GML scripts."""
import sys, struct
sys.path.insert(0, '.')
import relib as R, idx, pdata as P

def rows_near(func_va, span=6):
    """find qword occurrences of func_va in .data and dump the neighbourhood"""
    pat = struct.pack('<Q', func_va)
    out = []
    lo, hi = R.DATA[4], R.DATA[4] + R.DATA[3]
    i = lo
    while True:
        i = R.d.find(pat, i, hi)
        if i < 0: break
        out.append(i)
        i += 1
    for o in out:
        base = o - 8 * span
        print('  --- .data row around %#x ---' % R.o2va(o))
        for k in range(span * 2 + 1):
            oo = base + 8 * k
            v = struct.unpack('<Q', R.d[oo:oo + 8])[0]
            va = R.o2va(oo)
            tag = ''
            if R.BASE <= v < R.BASE + 0xB000000:
                if R.in_rdata(v):
                    s = R.cstr(v, 90)
                    if s: tag = 'str %r' % s
                elif R.in_text(v):
                    tag = 'text %s' % ('<-- TARGET' if v == func_va else '')
                elif R.in_data(v): tag = 'data'
            print('    %#x  %016x  %s' % (va, v, tag))
    return out

if __name__ == '__main__':
    T = R.table()
    name = sys.argv[1] if len(sys.argv) > 1 else 'gml_GlobalScript_scr_global_turn'
    fa = T[name]
    print('%s = %#x' % (name, fa))
    print('direct E8 callers:', len(idx.calls_to(fa)))
    print('E9 jmps:', len(idx.jmps_to(fa)))
    print('lea rip-rel refs:', len(idx.lea_to(fa)))
    print()
    rows_near(fa)
