"""Extract every Builtin_Add(name, func, nargs, flag) registration statically."""
import sys; sys.path.insert(0,'.')
import relib as R, idx, struct, json, re
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
BUILTIN_ADD = 0x14529d2c0
md = Cs(CS_ARCH_X86, CS_MODE_64)

W32 = {'eax':'rax','ecx':'rcx','edx':'rdx','ebx':'rbx','esi':'rsi','edi':'rdi','ebp':'rbp','esp':'rsp'}
for i in range(8,16): W32['r%dd'%i] = 'r%d'%i
def canon(r):
    r = r.strip()
    return W32.get(r, r)

def extract():
    sites = sorted(set(idx.calls_to(BUILTIN_ADD)) | set(idx.jmps_to(BUILTIN_ADD)))
    out, dup, fails = {}, 0, []
    for s in sites:
        win_lo = s - 0x40
        best = None
        for st in range(0, 0x40):
            addr = win_lo + st
            o = R.va2o(addr)
            ins = list(md.disasm(R.d[o:o+(s-addr)], addr))
            if ins and ins[-1].address + ins[-1].size == s:
                best = ins; break
        if best is None: fails.append(('nodecode', s)); continue
        regs = {}
        for i in best:
            m, ops = i.mnemonic, i.op_str
            if m == 'lea':
                dst = canon(ops.split(',')[0])
                inner = ops.split('[',1)[1].rsplit(']',1)[0]
                if 'rip' in inner:
                    o = R.va2o(i.address)
                    disp = struct.unpack('<i', R.d[o+i.size-4:o+i.size])[0]
                    regs[dst] = i.address + i.size + disp
                else:
                    toks = re.split(r'\s*\+\s*', inner)
                    val, ok = 0, True
                    for t in toks:
                        t = t.strip()
                        if re.fullmatch(r'0x[0-9a-f]+|\d+', t): val += int(t,0)
                        elif '*' in t: ok = False; break
                        else:
                            c = canon(t)
                            if c in regs: val += regs[c]
                            else: ok = False; break
                    if ok: regs[dst] = val
                    else: regs.pop(dst, None)
            elif m == 'xor':
                a,b = [canon(x) for x in ops.split(',')]
                if a == b: regs[a] = 0
                else: regs.pop(a, None)
            elif m == 'mov':
                a,b = [x.strip() for x in ops.split(',',1)]
                a = canon(a)
                if re.fullmatch(r'0x[0-9a-f]+|\d+', b): regs[a] = int(b,0)
                elif canon(b) in regs: regs[a] = regs[canon(b)]
                else: regs.pop(a, None)
            elif m in ('nop','int3'): pass
            else:
                d0 = ops.split(',')[0].strip()
                regs.pop(canon(d0), None)
        name_va, func = regs.get('rcx'), regs.get('rdx')
        if name_va is None or func is None: fails.append(('regs', s)); continue
        nm = R.cstr(name_va, 80)
        if not nm: fails.append(('str', s)); continue
        if nm in out: dup += 1
        out[nm] = dict(func=func, nargs=regs.get('r8'), flag=regs.get('r9'), site=s, name_va=name_va)
    return out, sites, dup, fails

if __name__ == '__main__':
    out, sites, dup, fails = extract()
    print('call sites: %d   unique names decoded: %d   dup: %d   failed: %d' % (len(sites), len(out), dup, len(fails)))
    from collections import Counter
    print('failure kinds:', Counter(k for k,_ in fails))
    json.dump({k:{'func':hex(v['func']),'nargs':v['nargs'],'flag':v['flag'],'site':hex(v['site']),'name_va':hex(v['name_va'])}
               for k,v in out.items()}, open('builtin_table.json','w'), indent=0)
