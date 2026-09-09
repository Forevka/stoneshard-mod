import struct, sys
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
md=Cs(CS_ARCH_X86, CS_MODE_64)
mode=sys.argv[1]
if mode=='dis':
    for a in sys.argv[2:]:
        va=int(a,16); o=rva2o(va-base)
        print(f'===== {va:#x} =====')
        n=0
        for ins in md.disasm(d[o:o+300], va):
            print(f'  {ins.address-va:>4x}: {ins.mnemonic:<10} {ins.op_str}')
            n+=1
            if ins.mnemonic in ('ret','jmp') or n>=24: break
        print()
elif mode=='u32':
    for a in sys.argv[2:]:
        va=int(a,16); o=rva2o(va-base)
        print(f'{va:#x} -> u32 {struct.unpack("<I", d[o:o+4])[0]}')
