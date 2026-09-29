"""whorefs.py <object name> [...] -- NAME the GML functions that embed an asset index.

assetref.py counts them; this prints which. Reuses assetref's strict test
(the immediate must follow an immediate-bearing opcode) rather than scanning
for the raw dword, which for small indices matches coincidentally almost
everywhere.
"""
import json
import sys

sys.path.insert(0, '.')
import relib as R  # noqa: E402
import assetref as A  # noqa: E402
import pdata as P  # noqa: E402

SET = {v: k for k, v in R.table().items()}


def main(names):
    objt = json.load(open(R.cache_path('objt_indexed.json')))
    by_name = {n: i for i, n in enumerate(objt) if n}

    for name in names:
        if name not in by_name:
            print('%s: not an object in OBJT' % name)
            continue
        index = by_name[name]
        r = A.report(name, index)

        print('=== %s (index %d) ===' % (name, index))
        print('    %d raw hits, %d strict, %d function(s), %d named'
              % (r['int_hits'], r['int_hits_strict'], r['funcs'], r['named_funcs']))

        # Re-derive the strict sites so each function can be shown with its count.
        ih = [h for h in A.int_hits(index) if A._B[h - A.TVA0 - 1] in A.PREV_OK]
        per = {}
        for h in ih:
            f = P.func_of(h)
            if f:
                per.setdefault(f[0], []).append(h)

        for a, sites in sorted(per.items(), key=lambda kv: -len(kv[1])):
            print('    %-58s %d site(s)' % (SET.get(a, 'sub_%X' % a), len(sites)))
        print()


if __name__ == '__main__':
    main(sys.argv[1:] or ['o_player'])
