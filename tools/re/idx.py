"""Fast global xref indexes over .text, cached to .npy"""
import numpy as np, os, struct
import relib as R

_T = R.d[R.TRA:R.TRA+R.TRSZ]
_B = np.frombuffer(_T, dtype=np.uint8)
TEXT_VA0 = R.BASE + R.TVA          # va of _T[0]
N = len(_B)

def _i32(idx):
    """read little-endian int32 at byte offsets idx"""
    return (_B[idx].astype(np.int64)
            | (_B[idx+1].astype(np.int64) << 8)
            | (_B[idx+2].astype(np.int64) << 16)
            | (_B[idx+3].astype(np.int64) << 24)
            ).astype(np.int32).astype(np.int64)

def _cache(name, build):
    p = os.path.join(R.SCR, name)
    if os.path.exists(p):
        return np.load(p)
    a = build()
    np.save(p, a)
    return a

# --- LEA rip-relative index: (site_va, target_va) ---
def _build_lea():
    b = _B
    m = (((b[:-7] & 0xF8) == 0x48) & (b[1:-6] == 0x8D) & ((b[2:-5] & 0xC7) == 0x05))
    idx = np.flatnonzero(m)
    disp = _i32(idx+3)
    site = TEXT_VA0 + idx.astype(np.int64)
    tgt = site + 7 + disp
    return np.stack([site, tgt])
LEA = _cache('lea_idx.npy', _build_lea)          # shape (2, n)
LEA_SITE, LEA_TGT = LEA[0], LEA[1]
_lo = np.argsort(LEA_TGT, kind='stable')
LEA_TGT_S = LEA_TGT[_lo]; LEA_SITE_S = LEA_SITE[_lo]

def lea_to(target_va):
    """call-site VAs of `lea reg,[rip+d]` whose target == target_va"""
    a = np.searchsorted(LEA_TGT_S, target_va, 'left')
    b = np.searchsorted(LEA_TGT_S, target_va, 'right')
    return sorted(int(x) for x in LEA_SITE_S[a:b])

# --- E8 call index: (site_va, target_va) ---
def _build_call():
    b = _B
    m = (b[:-5] == 0xE8)
    idx = np.flatnonzero(m)
    rel = _i32(idx+1)
    site = TEXT_VA0 + idx.astype(np.int64)
    tgt = site + 5 + rel
    ok = (tgt >= TEXT_VA0) & (tgt < TEXT_VA0 + N)
    return np.stack([site[ok], tgt[ok]])
CALL = _cache('call_idx.npy', _build_call)
CALL_SITE, CALL_TGT = CALL[0], CALL[1]
_co = np.argsort(CALL_TGT, kind='stable')
CALL_TGT_S = CALL_TGT[_co]; CALL_SITE_S = CALL_SITE[_co]
_so = np.argsort(CALL_SITE, kind='stable')
CALL_SITE_B = CALL_SITE[_so]; CALL_TGT_B = CALL_TGT[_so]

def calls_to(target_va):
    a = np.searchsorted(CALL_TGT_S, target_va, 'left')
    b = np.searchsorted(CALL_TGT_S, target_va, 'right')
    return sorted(int(x) for x in CALL_SITE_S[a:b])

def calls_in(lo_va, hi_va):
    """(site,target) for calls with site in [lo,hi)"""
    a = np.searchsorted(CALL_SITE_B, lo_va, 'left')
    b = np.searchsorted(CALL_SITE_B, hi_va, 'left')
    return [(int(CALL_SITE_B[i]), int(CALL_TGT_B[i])) for i in range(a,b)]

def leas_in(lo_va, hi_va):
    a = np.searchsorted(LEA_SITE, lo_va, 'left')
    b = np.searchsorted(LEA_SITE, hi_va, 'left')
    return [(int(LEA_SITE[i]), int(LEA_TGT[i])) for i in range(a,b)]

# --- E9 jmp index (thunks) ---
def _build_jmp():
    b = _B
    m = (b[:-5] == 0xE9)
    idx = np.flatnonzero(m)
    rel = _i32(idx+1)
    site = TEXT_VA0 + idx.astype(np.int64)
    tgt = site + 5 + rel
    ok = (tgt >= TEXT_VA0) & (tgt < TEXT_VA0 + N)
    return np.stack([site[ok], tgt[ok]])
JMP = _cache('jmp_idx.npy', _build_jmp)
JMP_SITE, JMP_TGT = JMP[0], JMP[1]
_jo = np.argsort(JMP_TGT, kind='stable')
JMP_TGT_S = JMP_TGT[_jo]; JMP_SITE_S = JMP_SITE[_jo]
def jmps_to(t):
    a=np.searchsorted(JMP_TGT_S,t,'left'); b=np.searchsorted(JMP_TGT_S,t,'right')
    return sorted(int(x) for x in JMP_SITE_S[a:b])
