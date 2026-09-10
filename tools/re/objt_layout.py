import os
import struct

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
off,size=ch['OBJT']
cnt=struct.unpack('<I',d[off:off+4])[0]
offs=struct.unpack_from('<%dI'%cnt,d,off+4)
def cs(p):
    e=d.find(b'\x00',p); return d[p:e].decode('latin1','replace')
names=[cs(struct.unpack('<I',d[o:o+4])[0]) for o in offs]
idx={n:i for i,n in enumerate(names)}
print('objects:',cnt)
# dump first 12 int32 fields for a few known items
probe=['o_inv_acorn','o_inv_ale','o_inv_amethyst','o_inv_food_parent','o_inv_ammo_stone','o_inv_arrows_parent']
for nm in probe:
    if nm not in idx: continue
    o=offs[idx[nm]]
    vals=struct.unpack_from('<12i', d, o)
    print(f'{nm:24}', ' '.join(f'{v:>6}' for v in vals))
print()
print('legend: [0]=nameptr(as int) [1]=sprite [2]=visible ...')
# try each field position as "parent" and score how often it points to a *_parent object
for f in range(1,12):
    good=0; tot=0
    for i,o in enumerate(offs):
        if not names[i].startswith('o_inv_'): continue
        v=struct.unpack_from('<i', d, o+4*f)[0]
        tot+=1
        if 0<=v<cnt and names[v].endswith('_parent'): good+=1
    if tot: print(f'field[{f}] -> points at a *_parent object for {good}/{tot} o_inv_ objects')
