import struct
d=open(r"D:/torrent/Stoneshard (Early Access)/Stoneshard/StoneShard.exe",'rb').read()
pe=struct.unpack('<I',d[0x3c:0x40])[0]; opt=pe+24
base=struct.unpack('<Q',d[opt+24:opt+32])[0]
nsec=struct.unpack('<H',d[pe+6:pe+8])[0]
so=opt+struct.unpack('<H',d[pe+20:pe+22])[0]
S=[]
for i in range(nsec):
    e=d[so+i*40:so+(i+1)*40]
    S.append((e[0:8].rstrip(b'\x00').decode(),)+struct.unpack('<IIII',e[8:24]))
def sec(nm):
    for s in S:
        if s[0]==nm: return s
def rva2o(r):
    for nm,vsz,va,rsz,ra in [(x[0],x[1],x[2],x[3],x[4]) for x in S]:
        if va<=r<va+max(vsz,rsz): return ra+(r-va)
def secname(va):
    r=va-base
    for x in S:
        if x[2]<=r<x[2]+max(x[1],x[3]): return x[0]
    return None

# find pointers to the builtin function-name strings
targets=[b'variable_global_set',b'instance_create_depth',b'instance_create_layer',
         b'ds_map_find_value',b'script_execute',b'variable_global_get',b'instance_find',
         b'string_length',b'show_debug_message',b'variable_instance_set']
for t in targets:
    off=d.find(b'\x00'+t+b'\x00')
    if off<0:
        print(f'{t.decode():24} string NOT FOUND'); continue
    off+=1
    rva=None
    for x in S:
        if x[3]<=off<x[3]+x[4]: rva=x[2]+(off-x[3]); break
    va=base+rva
    needle=struct.pack('<Q',va)
    hits=[]
    i=0
    while True:
        i=d.find(needle,i)
        if i<0: break
        hits.append(i); i+=1
    info=[]
    for h in hits[:3]:
        hs=None
        for x in S:
            if x[3]<=h<x[3]+x[4]: hs=x[0]; break
        nxt=struct.unpack('<Q', d[h+8:h+16])[0]
        prv=struct.unpack('<Q', d[h-8:h])[0]
        info.append(f'in {hs} next={secname(nxt) or hex(nxt)} prev={secname(prv) or hex(prv)}')
    print(f'{t.decode():24} ptrs={len(hits):3}  ' + ' | '.join(info))
