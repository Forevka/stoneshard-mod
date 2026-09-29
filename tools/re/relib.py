import struct, json, os, sys, bisect

# The game under study: --exe <path> or RE_GAME_EXE (see gamepath.py).
from gamepath import EXE, DATAWIN, CACHE, cache_path
# Derived-data dir, one per game: tools/re/cache/<exe stem>/ unless RELIB_CACHE
# overrides it (see gamepath.py).
SCR = CACHE
# optional pre-built script_table.json from an earlier session; regenerated if absent
OLDSCR = os.environ.get('RELIB_TABLE_DIR', SCR)

d = open(EXE,'rb').read()
_pe = struct.unpack('<I', d[0x3c:0x40])[0]
_opt = _pe + 24
BASE = struct.unpack('<Q', d[_opt+24:_opt+32])[0]
_nsec = struct.unpack('<H', d[_pe+6:_pe+8])[0]
_so = _opt + struct.unpack('<H', d[_pe+20:_pe+22])[0]
SECS = []  # (name, vsz, va, rsz, ra)
for i in range(_nsec):
    e = d[_so+i*40:_so+(i+1)*40]
    SECS.append((e[0:8].rstrip(b'\x00').decode(),) + struct.unpack('<IIII', e[8:24]))

def sec(n):
    for x in SECS:
        if x[0] == n: return x
    return None

TEXT = sec('.text'); RDATA = sec('.rdata'); DATA = sec('.data')
_,TVSZ,TVA,TRSZ,TRA = TEXT

def o2va(o):
    for nm,vsz,va,rsz,ra in SECS:
        if ra <= o < ra+rsz: return BASE+va+(o-ra)
    return None

def va2o(va):
    r = va - BASE
    for nm,vsz,va2,rsz,ra in SECS:
        if va2 <= r < va2+max(vsz,rsz):
            off = ra+(r-va2)
            return off if off < len(d) else None
    return None

def in_text(va):
    return TVA <= va-BASE < TVA+TVSZ
def in_rdata(va):
    return RDATA[2] <= va-BASE < RDATA[2]+RDATA[1]
def in_data(va):
    return DATA[2] <= va-BASE < DATA[2]+DATA[1]

def cstr(va, maxlen=400):
    o = va2o(va)
    if o is None: return None
    e = d.find(b'\x00', o, o+maxlen)
    if e <= o: return None
    s = d[o:e]
    try:
        t = s.decode('utf-8')
    except Exception:
        return None
    if any(c < 9 for c in s): return None
    return t

# ---- symbol table ----
def _build_table():
    """Rebuild name->func from the {const char* name, void* func, void* slot} rows in .data."""
    _, dvsz, dva, drsz, dra = DATA
    buf = d[dra:dra+drsz]
    n = len(buf)//8
    q = struct.unpack_from('<%dQ' % n, buf, 0)
    ent = {}
    for i in range(n-1):
        a = q[i]
        if not (BASE <= a < BASE+0xB000000) or not in_rdata(a): continue
        s = cstr(a, 220)
        if not s or not s.startswith('gml_'): continue
        f = q[i+1]
        if BASE <= f < BASE+0xB000000 and in_text(f):
            ent.setdefault(s, f)
    return ent

_TBL = None
def table():
    global _TBL
    if _TBL is None:
        p = os.path.join(OLDSCR, 'script_table.json')
        if os.path.exists(p):
            _TBL = {k: int(v, 16) for k, v in json.load(open(p)).items()}
        else:
            _TBL = _build_table()
            try:
                json.dump({k: hex(v) for k, v in _TBL.items()},
                          open(os.path.join(SCR, 'script_table.json'), 'w'), indent=0)
            except OSError:
                pass
    return _TBL

_SORTED = None
def sorted_funcs():
    global _SORTED
    if _SORTED is None:
        t = table()
        _SORTED = sorted(((v,k) for k,v in t.items()))
    return _SORTED

def owner(va):
    """nearest symbol at or below va"""
    fs = sorted_funcs()
    addrs = [f[0] for f in fs] if not hasattr(owner,'_a') else owner._a
    if not hasattr(owner,'_a'):
        owner._a = addrs
    i = bisect.bisect_right(owner._a, va) - 1
    if i < 0: return ('?', 0)
    return (fs[i][1], va - fs[i][0])

# ---- rip-relative lea xrefs ----
def lea_xrefs(target_va, region=None):
    """all VAs of `lea reg,[rip+d]` in .text whose target == target_va"""
    out = []
    lo, hi = (TRA, TRA+TRSZ) if region is None else region
    for i in range(lo, hi-7):
        b0 = d[i]
        if b0 & 0xF8 != 0x48: continue      # REX.W (48-4F)
        if d[i+1] != 0x8d: continue
        if (d[i+2] & 0xC7) != 0x05: continue
        disp = struct.unpack('<i', d[i+3:i+7])[0]
        ip = BASE+TVA+(i-TRA)
        if ip+7+disp == target_va:
            out.append(ip)
    return out

def mov_imm_xrefs(target_va):
    """mov reg, imm64 (48 B8+r) loading target_va, plus rip-rel lea"""
    out = []
    pat = struct.pack('<Q', target_va)
    i = TRA
    while True:
        i = d.find(pat, i, TRA+TRSZ)
        if i < 0: break
        # check preceding bytes look like 48 B8+r
        if i >= 2 and (d[i-2] & 0xF8) == 0x48 and 0xB8 <= d[i-1] <= 0xBF:
            out.append(BASE+TVA+(i-2-TRA))
        i += 1
    return out

def data_xrefs(target_va):
    """qword occurrences of target_va anywhere in file, returned as VA of the slot"""
    out = []
    pat = struct.pack('<Q', target_va)
    i = 0
    while True:
        i = d.find(pat, i)
        if i < 0: break
        va = o2va(i)
        if va: out.append(va)
        i += 8 if False else 1
    return out

def find_string(s, encoding='utf-8'):
    """offsets of a string literal in the file; returns list of (offset, va)"""
    b = s.encode(encoding) if isinstance(s,str) else s
    out = []
    i = 0
    while True:
        i = d.find(b, i)
        if i < 0: break
        out.append((i, o2va(i)))
        i += 1
    return out

def calls_from(func_va, limit=0x4000):
    """direct E8 call targets within a function's byte range (heuristic: until next symbol)"""
    fs = sorted_funcs()
    i = bisect.bisect_right(owner._a if hasattr(owner,'_a') else [f[0] for f in fs], func_va)
    end = func_va + limit
    o = va2o(func_va)
    out = []
    if o is None: return out
    j = o
    while j < o + limit and j < len(d)-5:
        if d[j] == 0xE8:
            rel = struct.unpack('<i', d[j+1:j+5])[0]
            t = o2va(j)+5+rel
            if t and in_text(t): out.append((o2va(j), t))
        j += 1
    return out

def callers_of(target_va):
    """VAs of E8 call sites targeting target_va"""
    out = []
    for i in range(TRA, TRA+TRSZ-5):
        if d[i] != 0xE8: continue
        rel = struct.unpack('<i', d[i+1:i+5])[0]
        va = BASE+TVA+(i-TRA)+5+rel
        if va == target_va:
            out.append(BASE+TVA+(i-TRA))
    return out

def callers_multi(targets):
    """dict target_va -> list of caller VAs, single pass"""
    tset = set(targets)
    out = {t: [] for t in targets}
    for i in range(TRA, TRA+TRSZ-5):
        if d[i] != 0xE8: continue
        rel = struct.unpack('<i', d[i+1:i+5])[0]
        va = BASE+TVA+(i-TRA)+5+rel
        if va in tset:
            out[va].append(BASE+TVA+(i-TRA))
    return out

def disasm(va, n=60, count=None):
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    o = va2o(va)
    code = d[o:o+ (count*16 if count else n*8)]
    out = []
    for k,ins in enumerate(md.disasm(code, va)):
        out.append(f'{ins.address:#x}  {ins.mnemonic:<9} {ins.op_str}')
        if count and k+1>=count: break
        if not count and k+1>=n: break
    return '\n'.join(out)
