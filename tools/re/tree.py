import os
import struct
from collections import Counter, defaultdict

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
idx={n:i for i,n in enumerate(names)}
kids=defaultdict(list)
for i,p in enumerate(parent):
    if 0<=p<cnt: kids[p].append(i)
def chain(i):
    seen=[]
    while 0<=i<cnt and i not in seen:
        seen.append(i); i=parent[i]
    return seen
def descendants(i):
    out=[]; stack=[i]
    while stack:
        x=stack.pop()
        for k in kids.get(x,()):
            out.append(k); stack.append(k)
    return out
print("=== direct children of o_inv_slot_parent (top categories) ===")
for c in kids.get(idx['o_inv_slot_parent'],[]):
    print(f'  {len(descendants(c)):>5} descendants  {names[c]}')
print()
print("=== direct children of o_inv_slot ===")
for c in kids.get(idx['o_inv_slot'],[]):
    print(f'  {len(descendants(c)):>5} descendants  {names[c]}')
print()
print("=== where do weapons live? objects containing 'sword' ===")
for n in names:
    if 'sword' in n.lower() and n.startswith('o_'):
        print('  ', n, ' chain:', ' -> '.join(names[x] for x in chain(idx[n])[:5]))
        break
sw=[n for n in names if 'sword' in n.lower()]
print('   total objects with "sword":', len(sw), sw[:6])
