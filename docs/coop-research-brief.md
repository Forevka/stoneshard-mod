# Brief: static research toward co-op multiplayer in Stoneshard

You are continuing an existing native modding project. **Read this whole brief before
touching anything.** A great deal has already been established — re-deriving it wastes your
time and mine.

## Your task

Statically analyse the game and produce a **feasibility and implementation map** for adding
co-op multiplayer **using GameMaker's own built-in networking functions**, which are already
compiled into the executable.

**This is research and documentation only. Do not modify the mod's source, do not build, do
not launch the game.** Read-only analysis.

## Deliverable

Write findings incrementally to **`docs/multiplayer-research.md`** (relative to the repo
root, `D:\projects\stoneshard-mod`).

Append as you go — do not save everything for the end. Someone checks this file
periodically to follow your progress, so it should always reflect what you currently know.
Start it in the first few minutes with a skeleton and fill it in.

For every claim, state the **evidence** (address, symbol name, string, call-site count).
Mark anything unverified as unverified. A confident wrong answer is worse than a clear
"unknown" — this project has already lost time to plausible-sounding guesses.

## The target

- Game: `D:\torrent\Stoneshard (Early Access)\Stoneshard\StoneShard.exe` (166 MB) and
  `data.win` (109 MB)
- Engine: **GameMaker Studio 2, compiled with YYC** — native C++, *no* GML bytecode.
  `data.win` has no `CODE`/`VARI`/`FUNC` chunk. UndertaleModTool cannot decompile this.
- The existing mod loads as `version.dll` (a proxy) and already calls game functions.

## What is already known (do not re-derive)

**Symbol table.** YYC emits rows of `{const char* name, void* func, void* slot}` into
`.data`, where the name points at a `gml_...` string in `.rdata`. Scanning for that shape
resolves **34,167 functions** by name. A pre-built map is at
`<scratchpad>/script_table.json` (name → static VA, image base `0x140000000`).

**YYC calling convention** (verified by live calls):
```c
RValue* Script(CInstance* self /*rcx*/, CInstance* other /*rdx*/,
               RValue* result /*r8*/, int argc /*r9d*/, RValue** args /*[rsp+0x28]*/);
```
`args` is an array of **pointers**. `RValue` is 16 bytes: value at `+0`, flags at `+8`,
kind at `+0xC`. Kinds: 0=real, 1=string, 2=array, 5=undefined, 6=object, 10=int64, 13=bool,
15=**ref**, 0xFFFFFF=unset.

**Object events use a DIFFERENT, smaller signature** — `void Event(CInstance* self,
CInstance* other)`. Calling one with the script signature corrupts the stack.

**GameMaker's networking API is compiled into the exe.** Confirmed present in the runtime's
builtin-name blob: `network_create_socket`, `network_create_socket_ext`,
`network_create_server`, `network_create_server_raw`, `network_connect`,
`network_connect_raw`, `network_connect_async`, `network_send_packet`, `network_send_raw`,
`network_send_broadcast`, `network_send_udp`, `network_send_udp_raw`, `network_resolve`,
`network_destroy`, `network_set_timeout`, `network_set_config`, plus the whole `buffer_*`
API. `random_set_seed` is also present.

**The central unsolved problem.** Those networking functions are **builtins, not scripts** —
they are direct-called and do **not** appear in the `gml_*` name table. A pointer scan for
their name strings found **zero** `{name, func}` pairs, so the script-table trick does not
reach them. Solving this is your highest-value objective.

## Research questions, in priority order

### 1. How can the mod call `network_*` from outside? (blocking everything else)

Concrete lead: the networking implementation references distinctive error strings that *are*
in the binary, e.g.
`"unable to enable reliable UDP on a server it must be a socket"`,
`"network_set_config : unknown parameter"`,
`"send_text option with non-WebSocket socket"`.
Cross-reference those strings to the functions that use them (`tools/re/xref.py` does exactly
this), then work outward to the actual `network_create_server` / `network_send_packet`
entry points. Document their addresses **and a name- or pattern-based way to find them at
runtime** — this project's hard rule is that **nothing may be hardcoded to an address**,
because the mod must survive game updates. Every existing resolver uses consensus pattern
scanning; follow that precedent.

Also worth checking: is there a builtin dispatch table (name → function pointer + argc)
built at runtime rather than statically? If so, describe its shape and how to walk it.

### 2. How does the game deliver async network events?

GameMaker delivers networking through the **Async Networking event** (`Other_68`). Search the
symbol table for `_Other_68` / other async events. If no object has one, the mod must create
its own event path — say so explicitly and describe the options.

### 3. What state actually has to be synchronised?

- The player object is **`o_player`** (24 events, including `Step_0`); a per-frame hook on it
  already exists in the mod.
- Enumerate what defines player state: position, stats, inventory, buffs, skills.
- Items are `o_inv_*` objects (984 spawnable); weapons/armor are **not objects** but CSV rows
  keyed by display name.
- Assess how singleton the design is: how much code assumes exactly one player? Quantify it
  (call-site counts), do not hand-wave.

### 4. Determinism vs authoritative sync

`random_set_seed` exists. Assess honestly whether lockstep determinism is realistic
(float determinism, `ds_map` iteration order, time-advances-on-action turn structure) versus
a host-authoritative model where the host simulates and clients render synced state.
**Give a recommendation with reasoning**, not a survey.

### 5. Turn structure

Stoneshard is real-time-with-pause where time advances on player action. Find the function(s)
that advance time / process a turn. This determines whether co-op can be turn-interleaved or
must be concurrent.

## Tools you already have

In `tools/re/` (Python 3, `capstone` is installed):

| script | what it does |
|---|---|
| `scan.py` | rebuilds the full name → address map from the exe |
| `whocalls.py` | lists callers of a symbol (this is the highest-value tool) |
| `callers.py` | dumps disassembly around each call site |
| `xref.py` | finds which functions reference a given string, and what they call next |
| `strfind.py` | string literals referenced inside a function |
| `argsem.py` / `argsem2.py` | decodes constant arguments at call sites (reals and strings) |
| `disasm2.py` / `raw.py` | disassemble a symbol or a raw address |
| `assets.py` / `strg.py` / `tree.py` | `data.win` chunk parsing: objects, strings, parent hierarchy |
| `dump_strings.py` | extracts all exe strings to a file |

Large derived data (already generated, regenerable) lives in the session scratchpad:
`C:\Users\forevkassh\AppData\Local\Temp\claude\D--torrent-Stoneshard--Early-Access--Stoneshard\34263b16-7ccc-4d28-8b58-74be3007940b\scratchpad\`
— `script_table.json`, `exe_strings.txt` (32 MB), `datawin_strings.txt`, `objt_names.txt`,
`sprt_names.txt`, `csv_rows.json`. If any are missing, regenerate with the scripts above.

**Windows note:** use PowerShell for anything touching paths; Git Bash mangles `C:\...`.
Python is on PATH.

## Existing mod source worth reading

`src/symbols.cpp` (the resolver and its health check), `src/gml.cpp` (the ABI, consensus
resolution, capture-and-replay), `src/tracer.cpp` (one-hook tracing of ~9,260 functions).
These show the established patterns — reuse them rather than inventing new ones.

## Structure for your findings file

1. Summary and current verdict on feasibility
2. Calling `network_*` from the mod — status, evidence, proposed runtime resolution
3. Async event delivery
4. State inventory: what must sync, with evidence of scale
5. Determinism assessment and recommended architecture
6. Turn/time structure
7. Concrete next implementation steps, ordered
8. Open questions and dead ends (record dead ends — they save the next person time)
