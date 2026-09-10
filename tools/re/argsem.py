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
t=json.load(open(sys.argv[1]))
names=[l.rstrip('\n') for l in open(sys.argv[2],encoding='utf-8')]
target=int(t['gml_Script_scr_inventory_add_item'],16)
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
def const_at(va):
    o=rva2o(va-base)
    if o is None: return None
    lo,kind=struct.unpack('<Qi', d[o:o+8]+d[o+12:o+16])
    if kind!=0: return None
    return struct.unpack('<d',struct.pack('<Q',lo))[0]
rows=[]
for site in sites:
    fstart,fname=owner(site)
    o=rva2o(fstart-base)
    try: ins=list(md.disasm(d[o:o+(site-fstart)+5], fstart))
    except: continue
    local={}   # rbp offset -> const
    slot={}    # arg slot -> value
    pend=None
    for j,x in enumerate(ins):
        m=re.match(r'^rcx, \[rbp \+ (0x[0-9a-f]+)\]$', x.op_str)
        if x.mnemonic=='lea' and m: pend=int(m.group(1),16); continue
        if x.mnemonic=='lea' and pend is not None and x.op_str.startswith('rdx, [rip + '):
            disp=int(x.op_str.split('rip + ')[1].rstrip(']'),16)
            c=const_at(x.address+x.size+disp)
            if c is not None: local[pend]=c
            pend=None; continue
        m=re.match(r'^rax, \[rbp \+ (0x[0-9a-f]+)\]$', x.op_str)
        if x.mnemonic=='lea' and m: last_lea=int(m.group(1),16); continue
        m=re.match(r'^qword ptr \[rbp \+ (0x[0-9a-f]+)\], rax$', x.op_str)
        if x.mnemonic=='mov' and m:
            dst=int(m.group(1),16)
            if 0x20<=dst<=0x58 and 'last_lea' in dir():
                slot[(dst-0x20)//8]=local.get(last_lea)
    if slot.get(0) is not None:
        rows.append((fname,slot.get(0),slot.get(1),slot.get(2)))
print(f'call sites: {len(sites)}; decoded arg0 constants: {len(rows)}')
print(f'{"arg0 (object)":36} {"arg1":>10} {"arg2":>10}   caller')
seen=set()
for fname,a0,a1,a2 in rows[:28]:
    idx=int(a0) if a0 is not None else -1
    nm=names[idx] if 0<=idx<len(names) else '?'
    print(f'  [{idx:>5}] {nm:<26} {str(a1):>10} {str(a2):>10}   {fname[:38]}')
