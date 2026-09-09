import struct, sys, json
from capstone import *
P=r"D:/torrent/Stoneshard (Early Access)/Stoneshard/StoneShard.exe"
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
def secn(va):
    r=va-base
    for x in S:
        if x[2]<=r<x[2]+max(x[1],x[3]): return x[0]
def cstr(va):
    o=rva2o(va-base)
    if o is None: return None
    e=d.find(b'\x00',o,o+300)
    if e<=o: return None
    s=d[o:e]
    return s.decode('latin1') if all(32<=c<127 for c in s) else None
tbl=json.load(open(sys.argv[1]))
md=Cs(CS_ARCH_X86, CS_MODE_64)
name=sys.argv[2]
va=int(tbl[name],16); o=rva2o(va-base)
print(f'=== string-literal loads + following call in {name} ===')
ins_list=list(md.disasm(d[o:o+9000], va))
for i,ins in enumerate(ins_list):
    if ins.mnemonic=='lea' and 'rip +' in ins.op_str:
        try: disp=int(ins.op_str.split('rip + ')[1].rstrip(']'),16)
        except: continue
        tgt=ins.address+ins.size+disp
        s=cstr(tgt)
        if s and len(s)>6 and secn(tgt)=='.rdata':
            nxt=[x for x in ins_list[i+1:i+6] if x.mnemonic=='call']
            call=nxt[0].op_str if nxt else '?'
            print(f'  +{ins.address-va:#06x} {ins.op_str[:22]:22} -> "{s[:58]}"   next call: {call}')
