import os
import re, sys

# Game install. Override with the STONESHARD_DIR environment variable;
# the default is the usual Steam location.
_SS = os.environ.get("STONESHARD_DIR",
                     r"C:/Program Files (x86)/Steam/steamapps/common/Stoneshard")

src = os.path.join(_SS, "StoneShard.exe")
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
