import struct
d=open(r"D:/torrent/Stoneshard (Early Access)/Stoneshard/StoneShard.exe",'rb').read()
pe=struct.unpack('<I',d[0x3c:0x40])[0]; opt=pe+24
base=struct.unpack('<Q',d[opt+24:opt+32])[0]
nsec=struct.unpack('<H',d[pe+6:pe+8])[0]
so=opt+struct.unpack('<H',d[pe+20:pe+22])[0]
S=[]
for i in range(nsec):
    e=d[so+i*40:so+(i+1)*40]
    nm=e[0:8].rstrip(b'\x00').decode(); vsz,va,rsz,ra=struct.unpack('<IIII',e[8:24])
    S.append((nm,va,vsz,ra,rsz))
def sec(nm):
    for s in S:
        if s[0]==nm: return s
def rva2o(r):
    for nm,va,vsz,ra,rsz in S:
        if va<=r<va+max(vsz,rsz): return ra+(r-va)
def in_text(va):
    t=sec('.text'); return t[1]<=va-base<t[1]+t[2]
def in_rdata(va):
    t=sec('.rdata'); return t[1]<=va-base<t[1]+t[2]

_,dva,dvsz,dra,drsz = sec('.data')
print(f'.data raw {dra:#x} size {drsz:#x}')
strcache={}
def cstr(va):
    if va in strcache: return strcache[va]
    o=rva2o(va-base); r=None
    if o is not None:
        e=d.find(b'\x00',o,o+220)
        if e>o:
            s=d[o:e]
            if all(32<=c<127 for c in s): r=s.decode()
    strcache[va]=r; return r

entries={}
buf=d[dra:dra+drsz]
n=len(buf)//8
q=struct.unpack_from('<%dQ'%n, buf, 0)
for i in range(n-1):
    a=q[i]
    if not (base<=a<base+0xB000000) or not in_rdata(a): continue
    s=cstr(a)
    if not s or not s.startswith('gml_'): continue
    f=q[i+1]
    if base<=f<base+0xB000000 and in_text(f):
        entries.setdefault(s,f)
print('resolved gml_ name->func pairs:', len(entries))
pref={}
for k in entries:
    p=k.split('_')[1] if '_' in k else k
    pref[p]=pref.get(p,0)+1
print('by prefix:', dict(sorted(pref.items(), key=lambda x:-x[1])[:6]))
print()
tests=['gml_Script_scr_console_sethp','gml_Script_scr_console_spawn','gml_Script_scr_console_atr_set',
 'gml_Script_scr_console_godmode','gml_Script_ConsoleCommand','gml_Script_scr_console_execute',
 'gml_Script_scr_console_lvl','gml_Script_scr_console_allskills','gml_Script_scr_console_setmp',
 'gml_Script_scr_console_boost','gml_Script_scr_console_drop','gml_Script_scr_console_nocd']
print('--- console command lookups ---')
for t in tests:
    print(f'  {t:44} {"func=%#x"%entries[t] if t in entries else "NOT FOUND"}')
cc=sum(1 for k in entries if 'scr_console_' in k)
print(f'\nscr_console_* entries resolved: {cc}')
import json
json.dump({k:hex(v) for k,v in entries.items()}, open(r'C:/Users/forevkassh/AppData/Local/Temp/claude/D--torrent-Stoneshard--Early-Access--Stoneshard/34263b16-7ccc-4d28-8b58-74be3007940b/scratchpad/script_table.json','w'), indent=0)
