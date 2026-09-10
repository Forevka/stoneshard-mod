import os
"""data.win chunk reader that preserves asset INDEX alignment (no skipped slots)."""
import struct, os, json, sys

# Game install. Override with the STONESHARD_DIR environment variable;
# the default is the usual Steam location.
_SS = os.environ.get("STONESHARD_DIR",
                     r"C:/Program Files (x86)/Steam/steamapps/common/Stoneshard")


P = os.path.join(_SS, "data.win")
d = open(P, 'rb').read()

CH = {}
pos = 8
end = 8 + struct.unpack('<I', d[4:8])[0]
while pos < end:
    n = d[pos:pos+4].decode('latin1')
    s = struct.unpack('<I', d[pos+4:pos+8])[0]
    CH[n] = (pos+8, s)
    pos += 8 + s

def cstr_at(p):
    e = d.find(b'\x00', p)
    try: return d[p:e].decode('utf-8')
    except Exception: return None

def asset_names(chunk):
    """returns a list indexed by asset id; None where the slot is unreadable"""
    off, size = CH[chunk]
    cnt = struct.unpack('<I', d[off:off+4])[0]
    offs = struct.unpack_from('<%dI' % cnt, d, off+4)
    out = []
    for o in offs:
        if o <= 0 or o+4 > len(d):
            out.append(None); continue
        nameptr = struct.unpack('<I', d[o:o+4])[0]
        out.append(cstr_at(nameptr))
    return out

if __name__ == '__main__':
    print('chunks:', ' '.join('%s(%d)' % (k, v[1]) for k, v in CH.items()))
    for c in ('OBJT', 'SPRT', 'ROOM', 'SCPT'):
        if c not in CH: print(c, 'missing'); continue
        ns = asset_names(c)
        print('%s: %d slots, %d named' % (c, len(ns), sum(1 for x in ns if x)))
    objt = asset_names('OBJT')
    json.dump(objt, open('objt_indexed.json', 'w'), indent=0)
    for want in ('o_player', 'o_player_AI', 'o_player_corpse', 'o_abstract_player',
                 'c_player_turn_trigger', 'o_player_observer', 'o_pause', 'o_control'):
        idxs = [i for i, x in enumerate(objt) if x == want]
        print('   %-24s index %s' % (want, idxs))
