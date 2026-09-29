"""objfind.py <name-or-substring> [...] -- resolve object names to asset indices.

Exact names print alone; anything else is treated as a substring search.
Indices are what instance_create_depth and object_get_parent take.
"""
import json
import sys

# The per-game cache datawin.py writes; takes --exe / RE_GAME_EXE like the others.
from gamepath import cache_path

OBJT = json.load(open(cache_path('objt_indexed.json')))
BY_NAME = {n: i for i, n in enumerate(OBJT) if n}


def main(args):
    for a in args:
        if a.lstrip('-').isdigit():
            i = int(a)
            print('%-40s %d' % (OBJT[i] if 0 <= i < len(OBJT) else '<out of range>', i))
            continue
        if a in BY_NAME:
            print('%-40s %d' % (a, BY_NAME[a]))
            continue
        low = a.lower()
        hits = [(i, n) for i, n in enumerate(OBJT) if n and low in n.lower()]
        print('--- %r: %d match(es) ---' % (a, len(hits)))
        for i, n in hits[:40]:
            print('   %6d  %s' % (i, n))
        if len(hits) > 40:
            print('   ... %d more' % (len(hits) - 40))


if __name__ == '__main__':
    main(sys.argv[1:])
