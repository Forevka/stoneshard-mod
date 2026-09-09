import re, sys
src = r"D:/torrent/Stoneshard (Early Access)/Stoneshard/StoneShard.exe"
out = sys.argv[1]
d = open(src, 'rb').read()
pat = re.compile(rb'[\x20-\x7e]{4,200}')
seen = set()
with open(out, 'w', encoding='utf-8') as f:
    for m in pat.finditer(d):
        s = m.group().decode('latin1')
        if s not in seen:
            seen.add(s)
            f.write(s + '\n')
print("unique strings:", len(seen))
