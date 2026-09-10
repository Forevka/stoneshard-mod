"""Read Stoneshard save files.

The on-disk format is zlib(<json text><32 hex chars><NUL>), not the positional
array over numeric variable ids that `saveSelfData` having no field-name
strings suggests. The compiled serialiser may well be positional; what it
*writes* is keyed JSON.

The 32-hex trailer looks like an MD5 but its input is UNVERIFIED: it is not
md5 of the JSON body in UTF-8 or UTF-16, nor of the character or folder name.
Do not assume it can be recomputed. Anything that edits a save must either
work out the real input first or let the game write the file itself.

    python tools/savepeek.py list              parse every save, report trailers
    python tools/savepeek.py keys <file>       top-level keys and sizes
    python tools/savepeek.py dump <file> [k]   pretty-print, optionally one key
"""


import json
import os
import sys
import zlib

SAVE_ROOT = os.path.join(os.environ.get("LOCALAPPDATA", ""), "StoneShard", "characters_v1")


def read(path):
    """-> (parsed json, trailer text, raw decompressed text)"""
    raw = open(path, "rb").read()
    txt = zlib.decompress(raw).decode("utf-8", "replace")
    obj, end = json.JSONDecoder().raw_decode(txt)
    return obj, txt[end:], txt[:end]


def each_save(root):
    for dirpath, _, files in os.walk(root):
        for fn in sorted(files):
            if fn.endswith((".map", ".sav")):
                full = os.path.join(dirpath, fn)
                yield os.path.relpath(full, root), full


def cmd_list(root):
    """Parses every save. A file that decompresses and yields valid JSON is
    structurally intact - which is what a concurrent-write test needs to know.
    The trailer is reported, never validated: its input is unidentified."""
    bad = 0
    for rel, full in each_save(root):
        try:
            obj, trailer, body = read(full)
        except Exception as exc:                       # noqa: BLE001 - report, do not raise
            print(f"{rel:44} UNREADABLE: {exc}")
            bad += 1
            continue

        keys = len(obj) if isinstance(obj, dict) else "-"
        name = obj.get("nameKey", "") if isinstance(obj, dict) else ""
        print(f"{rel:44} json ok  {len(body):>7}B  keys={str(keys):>4}  "
              f"{name:12} trailer={trailer.strip(chr(0))[:32]}")
    print(f"\n{'every file parsed' if not bad else f'{bad} file(s) unreadable'}")
    return 1 if bad else 0


def cmd_keys(path):
    obj, trailer, body = read(path)
    print(f"{path}: {len(body)} bytes of json, trailer {trailer.strip(chr(0))!r}")
    if isinstance(obj, dict):
        for k, v in obj.items():
            n = len(v) if isinstance(v, (list, dict, str)) else v
            print(f"  {k:40} {type(v).__name__:6} {str(n)[:70]}")
    return 0


def cmd_dump(path, key=None):
    obj, _, _ = read(path)
    print(json.dumps(obj[key] if key else obj, indent=1)[:20000])
    return 0


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    cmd = argv[1]
    if cmd == "list":
        return cmd_list(argv[2] if len(argv) > 2 else SAVE_ROOT)
    if cmd == "keys":
        return cmd_keys(argv[2])
    if cmd == "dump":
        return cmd_dump(argv[2], argv[3] if len(argv) > 3 else None)
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
