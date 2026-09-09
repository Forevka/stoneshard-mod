"""Function bounds for EVERY function (including unnamed runner C++) from .pdata
RUNTIME_FUNCTION { DWORD BeginAddress; DWORD EndAddress; DWORD UnwindData; }
"""
import sys, bisect, numpy as np
sys.path.insert(0, '.')
import relib as R

_, PVSZ, PVA, PRSZ, PRA = R.sec('.pdata')
_n = PVSZ // 12
_raw = np.frombuffer(R.d[PRA:PRA + _n * 12], dtype=np.uint32).reshape(-1, 3)
BEG = (_raw[:, 0].astype(np.int64) + R.BASE)
END = (_raw[:, 1].astype(np.int64) + R.BASE)
_ord = np.argsort(BEG, kind='stable')
BEG = BEG[_ord]; END = END[_ord]
COUNT = len(BEG)

def func_of(va):
    """(start, end) of the function containing va, or None"""
    i = int(np.searchsorted(BEG, va, 'right')) - 1
    if i < 0: return None
    if va < END[i]: return (int(BEG[i]), int(END[i]))
    return None

def start_of(va):
    f = func_of(va)
    return f[0] if f else None

# named-symbol lookup that refuses to guess across the runner boundary
_T = R.table()
_S = sorted((v, k) for k, v in _T.items())
_SA = [x[0] for x in _S]
_SET = {v: k for k, v in _T.items()}

def name_of(va):
    """exact GML symbol name for the function containing va, else None"""
    f = func_of(va)
    if f and f[0] in _SET: return _SET[f[0]]
    if va in _SET: return _SET[va]
    return None

def label(va):
    f = func_of(va)
    if not f: return 'no-pdata@%#x' % va
    n = _SET.get(f[0])
    if n: return '%s+%#x' % (n, va - f[0])
    return 'sub_%X+%#x' % (f[0], va - f[0])

if __name__ == '__main__':
    print('pdata entries:', COUNT)
    named = sum(1 for b in BEG if int(b) in _SET)
    print('pdata starts that are named gml_ symbols:', named)
    print('gml symbol addr range: %#x .. %#x' % (min(_SA), max(_SA)))
    print('pdata addr range:      %#x .. %#x' % (int(BEG[0]), int(END[-1])))
