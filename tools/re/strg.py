import struct, sys
P=r"D:/torrent/Stoneshard (Early Access)/Stoneshard/data.win"
d=open(P,'rb').read()
# walk top-level chunks
pos=8; end=8+struct.unpack('<I',d[4:8])[0]
chunks={}
while pos<end:
    name=d[pos:pos+4].decode('latin1'); size=struct.unpack('<I',d[pos+4:pos+8])[0]
    chunks[name]=(pos+8,size); pos+=8+size
print('chunks:', ', '.join(chunks))
off,size=chunks['STRG']
count=struct.unpack('<I',d[off:off+4])[0]
print('STRG entries:',count)
offs=struct.unpack_from('<%dI'%count, d, off+4)
out=open(sys.argv[1],'w',encoding='utf-8')
n=0
for o in offs:
    ln=struct.unpack('<I',d[o:o+4])[0]
    if ln<=0 or ln>4000: continue
    s=d[o+4:o+4+ln]
    try: t=s.decode('utf-8')
    except: continue
    out.write(t.replace('\n','\n')+'\n'); n+=1
out.close()
print('written',n)
