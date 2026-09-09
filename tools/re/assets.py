import struct, sys
P=r"D:/torrent/Stoneshard (Early Access)/Stoneshard/data.win"
d=open(P,'rb').read()
pos=8; end=8+struct.unpack('<I',d[4:8])[0]
ch={}
while pos<end:
    n=d[pos:pos+4].decode('latin1'); s=struct.unpack('<I',d[pos+4:pos+8])[0]
    ch[n]=(pos+8,s); pos+=8+s
def cstr_at(p):
    e=d.find(b'\x00',p)
    try: return d[p:e].decode('utf-8')
    except: return None
def names(chunk):
    off,size=ch[chunk]
    cnt=struct.unpack('<I',d[off:off+4])[0]
    offs=struct.unpack_from('<%dI'%cnt,d,off+4)
    out=[]
    for o in offs:
        if o<=0 or o+4>len(d): continue
        nameptr=struct.unpack('<I',d[o:o+4])[0]
        s=cstr_at(nameptr)
        if s: out.append(s)
    return out
for c in ('SPRT','OBJT'):
    ns=names(c)
    print(f'=== {c}: {len(ns)} assets ===')
    open(f'{sys.argv[1]}/{c.lower()}_names.txt','w',encoding='utf-8').write('\n'.join(ns))
    for x in ns[:6]: print('   ',x)
    print()
