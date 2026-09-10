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
def rva2o(r):
    for nm,vsz,va,rsz,ra in [(x[0],x[1],x[2],x[3],x[4]) for x in S]:
        if va<=r<va+max(vsz,rsz): return ra+(r-va)
def cstr(va):
    o=rva2o(va-base)
    if o is None: return None
    e=d.find(b'\x00',o,o+200)
    if e<=o: return None
    s=d[o:e]
    return s.decode('latin1') if all(32<=c<127 for c in s) else None
t=json.load(open(sys.argv[1]))
md=Cs(CS_ARCH_X86, CS_MODE_64)
name=sys.argv[2]
va=int(t[name],16); o=rva2o(va-base)
ins=list(md.disasm(d[o:o+4000], va))
# find the register that receives the args pointer: mov REG, [rbp+X] where X is the 5th param slot
argreg=None
cmps=set(); slots=set(); strs=[]
for i,x in enumerate(ins):
    if x.mnemonic=='ret': break
    if x.mnemonic=='cmp' and x.op_str.startswith('edi,'):
        try: cmps.add(int(x.op_str.split(',')[1].strip(),0))
        except: pass
    if x.mnemonic=='mov' and re.match(r'^r\d+, qword ptr \[rbp \+ 0x[0-9a-f]+\]$', x.op_str):
        argreg = x.op_str.split(',')[0]
    if argreg:
        m=re.match(rf'^\w+, qword ptr \[{argreg}\]$', x.op_str)
        if m: slots.add(0)
        m=re.match(rf'^\w+, qword ptr \[{argreg} \+ (0x[0-9a-f]+)\]$', x.op_str)
        if m: slots.add(int(m.group(1),16)//8)
    if x.mnemonic=='lea' and 'rip + ' in x.op_str:
        try: disp=int(x.op_str.split('rip + ')[1].rstrip(']'),16)
        except: continue
        tgt=x.address+x.size+disp
        s=cstr(tgt)
        if s and 2<len(s)<60 and not s.startswith('gml_'): strs.append(s)
print(f'=== {name} @ {va:#x} ===')
print('args register :', argreg)
print('argc compares :', sorted(cmps))
print('arg slots read:', sorted(slots))
print('string constants referenced (first 25):')
seen=set()
for s in strs:
    if s in seen: continue
    seen.add(s); print('   ', repr(s))
    if len(seen)>=25: break
