import struct,sys,json
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
def rva2o(r):
    for nm,vsz,va,rsz,ra in [(x[0],x[1],x[2],x[3],x[4]) for x in S]:
        if va<=r<va+max(vsz,rsz): return ra+(r-va)
t=json.load(open(sys.argv[1]))
target=int(t[sys.argv[2]],16)
_,tvsz,tva,trsz,tra = sec('.text')
# map: which named function contains an address
items=sorted(((int(v,16),k) for k,v in t.items()))
def owner(a):
    lo,hi=0,len(items)-1; best=None
    while lo<=hi:
        m=(lo+hi)//2
        if items[m][0]<=a: best=items[m]; lo=m+1
        else: hi=m-1
    return best[1] if best else '?'
hits=[]
start=tra
for i in range(tra, tra+trsz-5):
    if d[i]!=0xE8: continue
    rel=struct.unpack('<i', d[i+1:i+5])[0]
    va = tva + (i-tra) + 5 + rel + base
    if va==target: hits.append(i)
print(f'call sites of {sys.argv[2]}: {len(hits)}')
md=Cs(CS_ARCH_X86, CS_MODE_64)
for h in hits[:3]:
    callva = base+tva+(h-tra)
    print(f'\n===== caller {owner(callva)}  (call at {callva:#x}) =====')
    startoff=h-260
    ins=[x for x in md.disasm(d[startoff:h+5], base+tva+(startoff-tra))]
    for x in ins[-26:]:
        print(f'   {x.mnemonic:<9} {x.op_str}')
