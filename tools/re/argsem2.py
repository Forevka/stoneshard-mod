import os
import struct,sys,json,re
from capstone import *

# Game install. Override with the STONESHARD_DIR environment variable;
# the default is the usual Steam location.
_SS = os.environ.get("STONESHARD_DIR",
                     r"C:/Program Files (x86)/Steam/steamapps/common/Stoneshard")

P=os.path.join(_SS, "StoneShard.exe")
d=open(P,'rb').read()
pe=struct.unpack('<I',d[0x3c:0x40])[0]; opt=pe+24
base=struct.unpack('<Q',d[opt+24:opt+32])[0]
nsec=struct.unpack('<H',d[pe+6:pe+8])[0]
so=opt+struct.unpack('<H',d[pe+20:pe+22])[0]
S=[]
for i in range(nsec):
    e=d[so+i*40:so+(i+1)*40]
    S.append((e[0:8].rstrip(b'\x00').decode(),)+struct.unpack('<IIII',e[8:24]))
def sec(n):
    for x in S:
        if x[0]==n: return x
def rva2o(r):
    for nm,vsz,va,rsz,ra in [(x[0],x[1],x[2],x[3],x[4]) for x in S]:
        if va<=r<va+max(vsz,rsz): return ra+(r-va)
def cstr(va):
    o=rva2o(va-base)
    if o is None: return None
    e=d.find(b'\x00',o,o+160)
    if e<=o: return None
    s=d[o:e]
    return s.decode('latin1') if all(32<=c<127 for c in s) else None
def const_at(va):
    """Decode a static RValue: real, or string via its RefString."""
    o=rva2o(va-base)
    if o is None or o+16>len(d): return None
    lo=struct.unpack('<Q', d[o:o+8])[0]
    kind=struct.unpack('<i', d[o+12:o+16])[0]
    if kind==0:
        return ('real', struct.unpack('<d',struct.pack('<Q',lo))[0])
    if kind==1 and lo>base:
        ro=rva2o(lo-base)
        if ro is None: return None
        cp=struct.unpack('<Q', d[ro:ro+8])[0]
        s=cstr(cp)
        return ('str', s) if s else None
    return None
t=json.load(open(sys.argv[1]))
target=int(t[sys.argv[2]],16)
_,tvsz,tva,trsz,tra=sec('.text')
funcs=sorted(((int(v,16),k) for k,v in t.items()))
def owner(a):
    lo,hi=0,len(funcs)-1;best=None
    while lo<=hi:
        m=(lo+hi)//2
        if funcs[m][0]<=a: best=funcs[m];lo=m+1
        else: hi=m-1
    return best
md=Cs(CS_ARCH_X86, CS_MODE_64)
sites=[]
for i in range(tra,tra+trsz-5):
    if d[i]!=0xE8: continue
    rel=struct.unpack('<i',d[i+1:i+5])[0]
    if tva+(i-tra)+5+rel+base==target: sites.append(base+tva+(i-tra))
print(f'{sys.argv[2]}: {len(sites)} call sites\n')
shown=0
for site in sites:
    fstart,fname=owner(site)
    o=rva2o(fstart-base)
    if o is None or site-fstart>0x9000: continue
    ins=list(md.disasm(d[o:o+(site-fstart)+5], fstart))
    argc=None; consts=[]
    for x in ins[-70:]:
        if x.mnemonic=='mov' and x.op_str.startswith('r9d, '):
            try: argc=int(x.op_str.split(', ')[1],0)
            except: pass
        if x.mnemonic=='lea' and 'rip + ' in x.op_str and x.op_str.startswith(('rdx','rax','r8')):
            try: disp=int(x.op_str.split('rip + ')[1].rstrip(']'),16)
            except: continue
            c=const_at(x.address+x.size+disp)
            if c: consts.append(c)
    if not consts: continue
    print(f'argc={argc}  {fname[:52]}')
    for k,v in consts[:10]:
        print(f'      {k}: {v!r}')
    shown+=1
    if shown>=5: break
