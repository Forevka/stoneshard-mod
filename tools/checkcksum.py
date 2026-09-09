"""Verify the Stoneshard save checksum scheme against the live save files.

Scheme (origin: MikaBuchholz/stoneshard-editor, app/src/codec/save.ts; the
general form below was confirmed against every file in a live save directory on
2026-09-09, 7/7):

    salt     = "stOne!" + "!".join(dir components from characters_v1 down) + "!shArd"
    checksum = md5(jsonText + salt)          # lowercase hex, 32 chars
    body     = jsonText + checksum + "\\0"
    file     = zlib.compress(body)

so the salt is just the containing directory's path:

    characters.map                  -> stOne!characters_v1!shArd
    character_2/character.map       -> stOne!characters_v1!character_2!shArd
    character_2/autosave_1/data.sav -> stOne!characters_v1!character_2!autosave_1!shArd

Because the salt embeds the folder path, a save copied into a different
character folder fails its own checksum - which is what makes re-signing
necessary rather than optional.

This script recomputes rather than trusting: run it after any change to the
save layout, and before relying on the scheme for anything.
"""

import hashlib
import os
import sys
import zlib

ROOT = os.path.join(os.environ.get("LOCALAPPDATA", ""), "StoneShard", "characters_v1")


def salt_for(rel_dir):
    """rel_dir is relative to the parent of characters_v1, e.g. 'character_2/autosave_1'."""
    parts = ["characters_v1"] + [p for p in rel_dir.replace("\\", "/").split("/") if p and p != "."]
    return "stOne!" + "!".join(parts) + "!shArd"


def split(path):
    raw = zlib.decompress(open(path, "rb").read()).decode("utf-8", "replace").rstrip("\x00")
    return raw[:-32], raw[-32:]          # json text, stored checksum


def check(path):
    json_text, stored = split(path)
    rel_dir = os.path.relpath(os.path.dirname(path), ROOT)
    want = hashlib.md5((json_text + salt_for(rel_dir)).encode("utf-8")).hexdigest()

    ok = want == stored
    print(f"{os.path.relpath(path, ROOT):44} {'MATCH' if ok else 'MISMATCH':8} {stored}")
    return ok


def main():
    results = []
    for dirpath, _, files in os.walk(ROOT):
        for fn in sorted(files):
            if fn.endswith((".map", ".sav")):
                results.append(check(os.path.join(dirpath, fn)))

    print(f"\n{sum(results)}/{len(results)} matched")
    return 0 if results and all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
