---
title: Finding the GML functions
description: How the loader rebuilds the name-to-address table of every compiled GML function from the live image, with no hard-coded addresses.
---

A YYC build compiles every GML script and object event to native x64 code. Nothing exports those
functions, but the runner registers each one by name, so the exe carries a table that maps each
function's name to its code. `src/symbols.cpp`
rebuilds that table from the loaded image at startup. Every other part of the loader that names a
game function goes through it: hooks, `gml::CallAs`, the interop generator and the overlay's
Symbols tab.

## The registration rows {#rows}

YYC emits one row per function into `.data`:

| Offset | Field | Points into |
|---|---|---|
| +0x00 | `const char* name` | `.rdata`, a NUL-terminated `"gml_..."` string |
| +0x08 | `void* func` | `.text`, the compiled function |
| +0x10 | `void* slot` | runner bookkeeping (not used) |

The obvious approach is to find the first row and walk forward at a fixed 24-byte stride. That
does not work: the rows are fragmented, and a contiguous walk from one known row stops after
about 175 entries. So the scanner assumes no stride at all. It looks at *every* pointer-aligned
slot in `.data` and accepts any pair of adjacent pointers that has the right shape.

```cpp
// Runtime resolver for GameMaker YYC script registrations.
//
// YYC emits rows of {const char* name, void* func, void* slot} into .data, where
// the name points at a "gml_..." string in .rdata and func points into .text.
// We scan for that shape rather than assuming a stride, because the rows are
// fragmented (a contiguous walk from ConsoleCommand runs out after 175 entries).
//
// Everything is resolved by NAME against the live image, so a game update that
// relocates code changes nothing here.
```

<small>Source: [src/symbols.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/symbols.cpp#L1-L9)</small>

## Section ranges from the PE headers {#section-ranges}

"Points into `.rdata`" needs the section bounds of the running image. `SectionRanges` reads them
from the module's own PE headers (`IMAGE_DOS_HEADER` -> `IMAGE_NT_HEADERS64` -> section table) and
records `.text`, `.rdata` and `.data` as `[lo, hi)` address ranges. It reads the loaded module
(`GetModuleHandleW(nullptr)`), not the file on disk, so ASLR and relocation are already applied and
every pointer in `.data` can be compared directly against those ranges.

<small>Source: [src/symbols.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/symbols.cpp#L44-L68)</small>

## Validating a name pointer {#gmlstringat}

Most pointer-sized values in `.data` are not names, and some point to the very end of `.rdata`.
`GmlStringAt` rejects a candidate unless all of this holds:

- the `gml_` prefix lies entirely inside `.rdata` (not just its first byte);
- every byte up to the terminator is printable ASCII (`0x20`..`0x7E`);
- the NUL terminator appears within 220 bytes and after at least one character past the prefix.

```cpp
const char* GmlStringAt(std::uintptr_t p) {
    // The whole "gml_" prefix must be inside .rdata, not just its first byte.
    if (!g_rdata.contains(p) || p + 4 > g_rdata.hi) return nullptr;

    const auto* s = reinterpret_cast<const char*>(p);
    if (s[0] != 'g' || s[1] != 'm' || s[2] != 'l' || s[3] != '_') return nullptr;

    const std::uintptr_t limit = std::min<std::uintptr_t>(p + 220, g_rdata.hi);
    for (std::uintptr_t q = p; q < limit; ++q) {
        const auto c = static_cast<unsigned char>(*reinterpret_cast<const char*>(q));
        if (c == 0) return (q > p + 4) ? s : nullptr;
        if (c < 0x20 || c > 0x7e) return nullptr;
    }
    return nullptr;
}
```

<small>Source: [src/symbols.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/symbols.cpp#L71-L85)</small>

Because the pointer must land inside the image's own `.rdata`, the reads never leave committed,
readable memory, so the scan needs no exception guard.

## The scan loop {#scan-loop}

With both checks in place, the scan is one pass over `.data`. A slot qualifies when `p[0]` is a valid
`gml_` name and `p[1]` points into `.text`. The third field is not read: the shape of the first two
is already a strong signal.

```cpp
// Walk .data at pointer alignment looking for {name -> .rdata "gml_*", func -> .text}.
const auto* p   = reinterpret_cast<const std::uintptr_t*>(g_data.lo);
const auto* end = reinterpret_cast<const std::uintptr_t*>(g_data.hi - 16);

for (; p < end; ++p) {
    const char* name = GmlStringAt(p[0]);
    if (!name) continue;
    if (!g_text.contains(p[1])) continue;

    Entry e{name, reinterpret_cast<void*>(p[1])};
    if (g_index.emplace(name, e.func).second)
        g_entries.push_back(e);
}
```

<small>Source: [src/symbols.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/symbols.cpp#L153-L165)</small>

The first row for a name wins; a duplicate name is dropped. The `Entry::name` pointer refers to the
game's own `.rdata` string, so it stays valid for the life of the process and nothing is copied.

The pass takes well under 100 ms even on Stoneshard. Real runs from the two test games:

| Game | Runtime | `.data` | Functions resolved |
|---|---|---|---|
| Stoneshard | older | 6,351 KB | 34,167 |
| Dwarf Eats Mountain Demo | 2024.14 | 4,592 KB | 4,968 |

## Three indexes {#indexes}

After the pass, the entries are kept three ways:

- `g_index`, a hash map from name to address, behind `sym::Find("gml_Script_...")`;
- `g_entries`, sorted by name, behind `sym::All()` and the overlay's `sym::Search` filter;
- `g_byAddress`, sorted by address, behind `sym::OwnerOf`.

## OwnerOf: from an address to a name {#ownerof}

`OwnerOf` answers "which GML function contains this address?". It finds the last function that
starts at or below the address with a binary search. Compiled GML has no reliable end markers in
this table, so the only guard is distance: an address more than 1 MiB past the nearest start is not
attributed to anything, which keeps a runner address (C++ code after the last GML function) from
being named after an unrelated script.

```cpp
const char* OwnerOf(const void* addr) {
    if (g_byAddress.empty() || !addr) return nullptr;
    // Last entry whose start is <= addr.
    auto it = std::upper_bound(g_byAddress.begin(), g_byAddress.end(), addr,
                               [](const void* a, const Entry& e) { return a < e.func; });
    if (it == g_byAddress.begin()) return nullptr;
    --it;
    // Guard against attributing an address that lies far past the last function.
    const auto delta = reinterpret_cast<std::uintptr_t>(addr) -
                       reinterpret_cast<std::uintptr_t>(it->func);
    return delta < (1u << 20) ? it->name : nullptr;
}
```

<small>Source: [src/symbols.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/symbols.cpp#L234-L245)</small>

What uses it:

- **Hook installation.** `ApiHookInstall` refuses an address unless `OwnerOf(target)` names a
  function *and* `Find(name)` gives back exactly that address. A hook can only start at the first
  byte of a known function, never in the middle of one.
- **Failure messages.** `gml::CallAs` and `gml::CallEvent` log the failing function's name rather
  than a bare address.
- **The hook engine's log.** Each installed hook is logged by name (`hooks: #3 script gml_Script_...`).

<small>Source: [src/host/core_api.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/host/core_api.cpp#L222-L237)</small>

## Health check and the YYC test {#health-check}

The scan cannot tell "this game has few scripts" from "the row shape changed and nothing matched"
by count alone, so the floor is set low, at 50 functions. Any real YYC game clears it by a wide
margin, while a changed table shape matches almost nothing.

```cpp
// Minimum plausible symbol count for ANY YYC game. Stoneshard resolves 34,167
// and Dwarf Eats Mountain 4,968, so the floor only has to separate "the table
// shape changed and nothing matched" from a real, if small, game.
constexpr std::size_t kMinEntries = 50;
```

<small>Source: [src/symbols.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/symbols.cpp#L39-L42)</small>

Below the floor, the scanner checks one more thing before it gives up: whether the game is YYC at
all. A VM-compiled GameMaker game ships its GML as bytecode in a non-empty `CODE` chunk of
`data.win`, and a YYC build does not. `DataWinHasBytecode` reads only the chunk headers. The two
outcomes call for different reactions, so they get different messages:

- `not a YYC game: data.win holds VM bytecode, ...`: expected. The loader stands down cleanly with
  GML features off.
- `only N symbols resolved (expected >= 50) - table shape changed?`: a real regression to
  investigate.

Either way `sym::Healthy()` stays false. Builtin resolution and runtime helper discovery both check
it first and fail closed. [Boot](./boot.md) covers what the rest of start-up does in that case.

<small>Source: [src/symbols.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/symbols.cpp#L87-L126) and [L179-L192](https://github.com/Forevka/stoneshard-mod/blob/main/src/symbols.cpp#L179-L192)</small>

## Naming conventions {#naming}

The names are the ones the GameMaker IDE gives the code, so they tell you what kind of function you
are looking at and how it is called:

| Prefix | What it is | Example (Stoneshard) | Count (Stoneshard) |
|---|---|---|---|
| `gml_Script_<name>` | The body of a script function | `gml_Script_scr_player_move` | 6,738 |
| `gml_GlobalScript_<name>` | A script asset's top-level code (GameMaker 2.3+ global-script initialiser) | `gml_GlobalScript_scr_player_move` | 3,280 |
| `gml_Object_<object>_<Event>_<n>` | One object event | `gml_Object_o_player_observer_Step_0`, `..._Draw_64` | 15,232 |
| `gml_RoomCC_<room>_<n>_Create` | An instance's creation code in a room | `gml_RoomCC_r_logo_0_Create` | 8,897 |
| `gml_Room_<room>_Create` | A room's creation code | `gml_Room_r_main_menu_Create` | 20 |

`<n>` is the event number within its type: `Step_0` is the normal Step, `Draw_64` is Draw GUI,
`Other_10` is User Event 0.

The prefix also decides the calling convention, which is why hooks check it:

- `gml_Script_*` takes `(self, other, result, argc, args)` and returns the result pointer;
- `gml_Object_*`, `gml_RoomCC_*` and `gml_GlobalScript_*` take only `(self, other)`.

See [the hook engine](./hook-engine.md) for how each kind is detoured.

## The offline twin {#offline-twin}

`tools/re/relib.py` rebuilds the same table from the exe file on disk, with the same rules: a name
pointer into `.rdata` that starts with `gml_` within 220 bytes, followed by a function pointer into
`.text`. Every offline tool uses it to put names on addresses.

```python
def _build_table():
    """Rebuild name->func from the {const char* name, void* func, void* slot} rows in .data."""
    _, dvsz, dva, drsz, dra = DATA
    buf = d[dra:dra+drsz]
    n = len(buf)//8
    q = struct.unpack_from('<%dQ' % n, buf, 0)
    ent = {}
    for i in range(n-1):
        a = q[i]
        if not (BASE <= a < BASE+0xB000000) or not in_rdata(a): continue
        s = cstr(a, 220)
        if not s or not s.startswith('gml_'): continue
        f = q[i+1]
        if BASE <= f < BASE+0xB000000 and in_text(f):
            ent.setdefault(s, f)
    return ent
```

<small>Source: [tools/re/relib.py](https://github.com/Forevka/stoneshard-mod/blob/main/tools/re/relib.py#L64-L79)</small>

The file is not relocated, so addresses come out at the exe's preferred base (`0x140000000`), and
the result is cached as `script_table.json` per game. The [reverse-engineering toolkit](./re-toolkit.md)
covers the tools built on top of it.
