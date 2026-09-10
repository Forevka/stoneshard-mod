import os
import struct

# Game install. Override with the STONESHARD_DIR environment variable;
# the default is the usual Steam location.
_SS = os.environ.get("STONESHARD_DIR",
                     r"C:/Program Files (x86)/Steam/steamapps/common/Stoneshard")

d=open(os.path.join(_SS, "StoneShard.exe"),'rb').read()
pe=struct.unpack('<I',d[0x3c:0x40])[0]; opt=pe+24
base=struct.unpack('<Q',d[opt+24:opt+32])[0]
nsec=struct.unpack('<H',d[pe+6:pe+8])[0]
so=opt+struct.unpack('<H',d[pe+20:pe+22])[0]
secs=[]
for i in range(nsec):
    e=d[so+i*40:so+(i+1)*40]
    secs.append((e[0:8].rstrip(b'\x00').decode(),)+struct.unpack('<IIII',e[8:24]))
def rva2o(r):
    for n,vsz,va,rsz,ra in [(s[0],s[1],s[2],s[3],s[4]) for s in secs]:
        if va<=r<va+max(vsz,rsz): return ra+(r-va)
def secof_rva(r):
    for s in secs:
        if s[2]<=r<s[2]+max(s[1],s[3]): return s[0]
    return '?'
def cstr(va):
    o=rva2o(va-base)
    if o is None: return None
    e=d.find(b'\x00',o)
    s=d[o:e]
    return s.decode('latin1') if 0<len(s)<200 and all(32<=c<127 for c in s) else None

START=0x96cd938-16  # entry base for ConsoleCommand row
# walk backwards to find table start
def entry(off):
    name,func,slot=struct.unpack('<QQQ', d[off:off+24])
    return name,func,slot
def valid(off):
    try: name,func,slot=entry(off)
    except: return False
    if not (base<=name<base+0xB000000): return False
    if not (base<=func<base+0xB000000): return False
    n=cstr(name)
    return n is not None and n.startswith('gml_')

# find true row alignment near ConsoleCommand
anchor=None
for cand in range(0x96cd938-48, 0x96cd938+8, 8):
    if valid(cand):
        nm=cstr(entry(cand)[0])
        if nm=='gml_Script_ConsoleCommand': anchor=cand; break
print('anchor row offset', hex(anchor))
lo=anchor
while valid(lo-24): lo-=24
hi=anchor
while valid(hi+24): hi+=24
count=(hi-lo)//24+1
print('table start',hex(lo),'end',hex(hi),'entries',count)
print()
print('--- first 5 ---')
for i in range(5):
    n,f,s=entry(lo+i*24); print(f'  {cstr(n):55} func={f:#x} ({secof_rva(f-base)})')
print('--- last 3 ---')
for i in range(3):
    n,f,s=entry(hi-(2-i)*24); print(f'  {cstr(n):55} func={f:#x} ({secof_rva(f-base)})')
print()
# locate specific console commands
want=['gml_Script_scr_console_sethp','gml_Script_scr_console_spawn','gml_Script_scr_console_atr_set',
      'gml_Script_scr_console_godmode','gml_Script_ConsoleCommand','gml_Script_scr_console_execute']
found={}
for i in range(count):
    n,f,s=entry(lo+i*24); nm=cstr(n)
    if nm in want: found[nm]=(f,lo+i*24)
print('--- targets ---')
for w in want:
    if w in found: print(f'  {w:42} func={found[w][0]:#x}')
    else: print(f'  {w:42} NOT FOUND')
print()
print('--- prologue bytes of ConsoleCommand ---')
f=found['gml_Script_ConsoleCommand'][0]; o=rva2o(f-base)
print(' '.join(f'{b:02x}' for b in d[o:o+64]))
print()
print('--- prologue of scr_console_sethp ---')
if 'gml_Script_scr_console_sethp' in found:
    f2=found['gml_Script_scr_console_sethp'][0]; o2=rva2o(f2-base)
    print(' '.join(f'{b:02x}' for b in d[o2:o2+64]))
