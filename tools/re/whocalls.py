import os
import struct,sys,json

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
t=json.load(open(sys.argv[1]))
_,tvsz,tva,trsz,tra=sec('.text')
funcs=sorted(((int(v,16),k) for k,v in t.items()))
def owner(a):
    lo,hi=0,len(funcs)-1;best=None
    while lo<=hi:
        m=(lo+hi)//2
        if funcs[m][0]<=a: best=funcs[m];lo=m+1
        else: hi=m-1
    return best[1] if best else '?'
for name in sys.argv[2:]:
    if name not in t: print(f'{name}: NOT FOUND'); continue
    target=int(t[name],16)
    callers={}
    for i in range(tra,tra+trsz-5):
        if d[i]!=0xE8: continue
        rel=struct.unpack('<i',d[i+1:i+5])[0]
        va=tva+(i-tra)+5+rel+base
        if va==target:
            c=owner(base+tva+(i-tra))
            callers[c]=callers.get(c,0)+1
    print(f'=== callers of {name} ({sum(callers.values())} sites) ===')
    for c,n in sorted(callers.items(), key=lambda x:-x[1])[:14]:
        print(f'   {n:>3}x  {c}')
    print()
