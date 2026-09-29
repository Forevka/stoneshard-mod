"""Where the game under study lives, for every tool in tools/re.

Any tool takes `--exe <path to the game's exe>` anywhere on its command line,
or reads the RE_GAME_EXE environment variable. data.win is expected next to the
exe. STONESHARD_DIR (the game FOLDER, holding StoneShard.exe) is still honoured
for setups made before the tools became game-agnostic.

The derived caches (*.npy, script_table.json, objt_indexed.json,
builtin_table.json) are per game, since they are addresses and indices into one
exe and data.win. They live in tools/re/cache/<exe name without extension>/
(CACHE below), so studying a second game never reuses the first one's tables.
RELIB_CACHE overrides the folder; give each game its own if you set it.
"""
import os
import sys


def _from_argv():
    # Removed from argv, since the tools read their own arguments positionally.
    if '--exe' in sys.argv:
        i = sys.argv.index('--exe')
        if i + 1 < len(sys.argv):
            path = sys.argv[i + 1]
            del sys.argv[i:i + 2]
            return path
    return None


EXE = _from_argv() or os.environ.get('RE_GAME_EXE')
if not EXE and os.environ.get('STONESHARD_DIR'):
    EXE = os.path.join(os.environ['STONESHARD_DIR'], 'StoneShard.exe')
if not EXE:
    sys.exit('tools/re: pass --exe <path to the game exe>, or set RE_GAME_EXE')

DATAWIN = os.path.join(os.path.dirname(os.path.abspath(EXE)), 'data.win')

CACHE = os.environ.get('RELIB_CACHE') or os.path.join(
    os.path.dirname(os.path.abspath(__file__)), 'cache',
    os.path.splitext(os.path.basename(EXE))[0])
os.makedirs(CACHE, exist_ok=True)


def cache_path(name):
    """Where a derived file for the game under study is read and written."""
    return os.path.join(CACHE, name)
