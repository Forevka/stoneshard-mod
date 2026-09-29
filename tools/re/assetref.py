"""Count how many distinct GML functions embed a given asset index as an
immediate (int32) or as a double constant in .rdata referenced from .text.

YYC compiles an asset reference like `o_player` into its numeric index, so this
is a proxy for 'how many places in the game's GML mention this object'.
"""
import sys, struct, json
import numpy as np
sys.path.insert(0, '.')
import relib as R, idx, pdata as P

_T = R.d[R.TRA:R.TRA + R.TRSZ]
_B = np.frombuffer(_T, dtype=np.uint8)
TVA0 = R.BASE + R.TVA

# candidate opcodes that immediately precede a 4-byte immediate we care about
# B8+r  mov r32, imm32        (1 byte opcode)
# 68    push imm32
# 3D    cmp eax, imm32
# and modrm forms: C7 /0 (mov r/m32,imm32) -> imm is last 4 bytes
# We take a looser approach: find the 4-byte LE value anywhere in .text and keep
# hits whose preceding byte looks like an immediate-bearing opcode tail.
PREV_OK = set([0x68, 0x3D] + list(range(0xB8, 0xC0)))

def int_hits(val):
    pat = struct.pack('<I', val)
    out = []
    i = 0
    while True:
        i = _T.find(pat, i)
        if i < 0: break
        out.append(TVA0 + i)
        i += 1
    return out

def dbl_const_va(val):
    """VAs in .rdata holding the double `val`"""
    pat = struct.pack('<d', float(val))
    res = []
    i = R.RDATA[4]
    endr = R.RDATA[4] + R.RDATA[3]
    while True:
        i = R.d.find(pat, i, endr)
        if i < 0: break
        if (i - R.RDATA[4]) % 8 == 0:
            res.append(R.o2va(i))
        i += 1
    return res

def funcs_for(hits):
    s = set()
    for h in hits:
        f = P.func_of(h)
        if f: s.add(f[0])
    return s

_SET = {v: k for k, v in R.table().items()}

def report(name, index):
    ih = int_hits(index)
    ih_strict = [h for h in ih if _B[h - TVA0 - 1] in PREV_OK]
    dvas = dbl_const_va(index)
    dref = []
    for v in dvas:
        dref += idx.lea_to(v)
    fi = funcs_for(ih_strict)
    fd = funcs_for(dref)
    allf = fi | fd
    named = {a: _SET[a] for a in allf if a in _SET}
    return dict(name=name, index=index,
                int_hits=len(ih), int_hits_strict=len(ih_strict),
                dbl_consts=len(dvas), dbl_refs=len(dref),
                funcs=len(allf), named_funcs=len(named), named=named)

if __name__ == '__main__':
    objt = json.load(open(R.cache_path('objt_indexed.json')))
    targets = sys.argv[1:] or ['o_player']
    for t in targets:
        i = objt.index(t)
        r = report(t, i)
        print('%-24s idx=%-6d int_hits=%-6d strict=%-5d dblconst=%-3d dblrefs=%-6d -> %d functions (%d named)'
              % (r['name'], r['index'], r['int_hits'], r['int_hits_strict'],
                 r['dbl_consts'], r['dbl_refs'], r['funcs'], r['named_funcs']))
