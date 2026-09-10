import os
import struct
from collections import Counter

# Game install. Override with the STONESHARD_DIR environment variable;
# the default is the usual Steam location.
_SS = os.environ.get("STONESHARD_DIR",
                     r"C:/Program Files (x86)/Steam/steamapps/common/Stoneshard")

P=os.path.join(_SS, "data.win")
d=open(P,'rb').read()
pos=8; end=8+struct.unpack('<I',d[4:8])[0]
ch={}
while pos<end:
    n=d[pos:pos+4].decode('latin1'); s=struct.unpack('<I',d[pos+4:pos+8])[0]
    ch[n]=(pos+8,s); pos+=8+s
off,_=ch['OBJT']
cnt=struct.unpack('<I',d[off:off+4])[0]
offs=struct.unpack_from('<%dI'%cnt,d,off+4)
def cs(p):
    e=d.find(b'\x00',p); return d[p:e].decode('latin1','replace')
names=[cs(struct.unpack('<I',d[o:o+4])[0]) for o in offs]
parent=[struct.unpack_from('<i', d, o+28)[0] for o in offs]
def pname(i): return names[i] if 0<=i<cnt else None
def root_chain(i):
    seen=[]
    while 0<=i<cnt and i not in seen:
        seen.append(i); i=parent[i]
    return seen
inv=[i for i,n in enumerate(names) if n.startswith('o_inv_')]
c=Counter()
for i in inv:
    p=pname(parent[i])
    c[p if p else '(no parent)']+=1
print(f'=== direct parents of the {len(inv)} o_inv_* objects ===')
for k,v in c.most_common(30):
    print(f'  {v:>5}  {k}')
print()
print('=== example chains ===')
for nm in ['o_inv_acorn','o_inv_ale','o_inv_amethyst','o_inv_ammo_stone','o_inv_antivenom']:
    i=names.index(nm)
    print('  ', ' -> '.join(names[x] for x in root_chain(i)))
