import os
import struct, sys, json
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
tbl=json.load(open(sys.argv[1]))
count=int(sys.argv[2])
md=Cs(CS_ARCH_X86, CS_MODE_64)
for name in sys.argv[3:]:
    va=int(tbl[name],16); o=rva2o(va-base)
    print(f'===== {name} @ {va:#x} =====')
    n=0
    for ins in md.disasm(d[o:o+1500], va):
        print(f'  {ins.address-va:>4x}: {ins.mnemonic:<9} {ins.op_str}')
        n+=1
        if ins.mnemonic=='ret' or n>=count: break
    print()
