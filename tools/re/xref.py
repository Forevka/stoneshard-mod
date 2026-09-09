import struct,sys,re
from collections import Counter
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
def sec(n):
    for x in S:
        if x[0]==n: return x
def o2rva(o):
    for nm,vsz,va,rsz,ra in [(x[0],x[1],x[2],x[3],x[4]) for x in S]:
        if ra<=o<ra+rsz: return va+(o-ra)
_,tvsz,tva,trsz,tra=sec('.text')
md=Cs(CS_ARCH_X86, CS_MODE_64)
targets=[b'Variable %s.%s(%d, %d) not set before reading it.',
         b'Unable to find instance for object index %d',
         b'local variable %s(%d) not set before reading it.',
         b'Unable to find variable %s']
votes=Counter()
for tstr in targets:
    off=d.find(tstr)
    if off<0: print('missing', tstr); continue
    va=base+o2rva(off)
    # find lea reg,[rip+disp] in .text pointing at it
    found=0
    for i in range(tra, tra+trsz-7):
        if d[i]!=0x48 or d[i+1]!=0x8d: continue
        modrm=d[i+2]
        if (modrm & 0xC7) != 0x05: continue        # rip-relative
        disp=struct.unpack('<i', d[i+3:i+7])[0]
        ip=base+tva+(i-tra)
        if ip+7+disp != va: continue
        found+=1
        # next call within 40 bytes
        for j in range(7, 48):
            k=i+j
            if k+5>len(d): break
            if d[k]==0xE8:
                rel=struct.unpack('<i',d[k+1:k+5])[0]
                tgt=base+tva+(k-tra)+5+rel
                votes[tgt]+=1
                break
    print(f'{tstr[:44].decode():46} xrefs={found}')
print('\n=== shared callee (the error formatter) ===')
for a,n in votes.most_common(6):
    print(f'  {a:#x}  {n} votes')
