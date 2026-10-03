---
title: Runtime differences
description: What changed between Stoneshard's older GameMaker runtime and the 2024 runtime, and how the loader detects each difference from the code itself instead of a version number.
---

Lodestone is tested on two GameMaker runtimes that differ in ways the native loader cares about:

- **Stoneshard**: an older runtime.
- **Dwarf Eats Mountain Demo**: runtime 2024.14.

The loader never reads a runtime version. Each difference is detected from the code or data it affects, at the point where the loader needs it. A runtime between, before or after these two works as long as each piece matches one of the known shapes; a piece that matches neither fails closed, and only the feature that depends on it becomes unavailable.

## At a glance {#at-a-glance}

| What | Older (Stoneshard) | 2024+ (Dwarf Eats Mountain 2024.14) | Detected by | Details |
|---|---|---|---|---|
| Builtin registry row | 80 bytes, name stored inline | 24 bytes, name by pointer | reading the first 16 live rows both ways | [Builtins](./builtins.md) |
| Registrar's loads of the registry globals | count read into `eax` | count into `ecx`, capacity into `eax` | collecting every rip-relative load, no fixed encoding | [Builtins](./builtins.md) |
| String literals | built inside each script with `YYSetString` | static `RValue`s built once by initialisers | the in-script tally finds no validated candidate, then the initialiser tally runs | [Runtime bridge](./runtime-bridge.md) |
| Current `self` | a global every script prologue writes | kept in a register, no global | the prologue-store vote is too weak | [Runtime bridge](./runtime-bridge.md), [Hook engine](./hook-engine.md) |
| `COPY_RValue` | callable as a whole | inlined; only `COPY_RValue__Post` is callable | behaviour of the copy on an undefined destination | [Runtime bridge](./runtime-bridge.md) |
| Resource handles (e.g. buffers) | plain numbers | typed references, kind 15 | the value's kind, at every use | [Runtime bridge](./runtime-bridge.md) |
| Instance references | numbers or kind-15 references | numbers or kind-15 references | the value's kind, at every use | [Runtime bridge](./runtime-bridge.md) |

Some things are the same in both and need no detection: the 16-byte `RValue`, the instance id hash map and the inlined code that reads it, `FREE_RValue` as the most-called function, and `YYGMLException` as the type thrown for a GML error.

## Builtin registry rows {#builtin-rows}

The registry that maps builtin names to functions changed its entry shape. Older runtimes store a 64-byte name inline, followed by the function pointer, arity and id (`0x50` bytes). The 2024 runtime stores a pointer to the name instead (`0x18` bytes). Both structs are on the [Builtins](./builtins.md#layouts) page.

**Detection.** `DetectLayout` reads the first 16 entries of the live table both ways and counts how many give a printable name, a code pointer inside `.text` and an arity between -1 and 63. The reading that fits wins. The logs show the result:

```text
Stoneshard:  builtins: inline-name layout, 2532 of 2535 entries usable
Dwarf:       builtins: by-pointer-name layout, 2861 of 2861 entries usable
```

## How the registrar reads its globals {#registrar-loads}

The registrar keeps three adjacent `.data` globals: the array pointer, an `int` count at `+8` and an `int` capacity at `+0xC`. Both runtimes read them rip-relatively, but into different registers: Stoneshard's reads the count into `eax`, the 2024 runtime reads the count into `ecx` and the capacity into `eax`.

**Detection.** No single encoding is assumed. `ExtractGlobals` collects every rip-relative `mov` in the registrar's first `0x200` bytes, splits them into qword and dword loads, and picks the qword global whose `+8` is also loaded as a dword ([src/builtins.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/builtins.cpp#L173-L212)).

## String literals {#string-literals}

Older runtimes build a string literal at the point of use, so `YYSetString` is called from inside thousands of scripts:

```nasm
48 8D 15 xx xx xx xx    lea  rdx, [rip+str]
E8 xx xx xx xx          call YYSetString        ; within a few bytes
```

The 2024 runtime makes every literal a static `RValue`, built once at startup by a three-instruction initialiser:

```nasm
48 8D 15 xx xx xx xx    lea  rdx, [rip+str]     ; .rdata string
48 8D 0D xx xx xx xx    lea  rcx, [rip+value]   ; static RValue in .data
E8 xx xx xx xx          call YYSetString
```

**Detection.** The in-script tally always runs first. On the 2024 runtime it still produces candidates, but none passes the structural check (`mov dword [reg+0Ch], 1`), so the loader falls back to the initialiser tally over all of `.text`:

```text
Stoneshard:  gml: YYSetString candidate #1 ... (189 votes) -> VALIDATED
Dwarf:       gml: YYSetString candidate #1 ... (206 votes) -> rejected
             gml: YYSetString candidate #2 ... (16 votes) -> rejected
             gml: no in-script string construction; trying static string initialisers
             gml: YYSetString (static init) candidate #1 ... (6391 votes) -> VALIDATED
```

This is the reason the validation step exists. On Dwarf Eats Mountain the vote winner is the wrong function; trusting votes alone would have handed every string to it.

## The current self {#current-self}

Builtins and many scripts need a `self` instance, and the loader borrows the one the game last ran code as. Older runtimes store it in a global at the top of every script:

```nasm
48 89 0D xx xx xx xx    mov [rip+g_pCurrentSelf], rcx
```

The 2024 runtime keeps `self` in a register and never stores it.

**Detection.** The prologue-store vote needs at least 40 votes and twice the runner-up. Stoneshard gives 4098 votes; the 2024 runtime gives none, and `gml::Init` logs "no current-self global in this runtime; using hook-observed instances".

**Fallback.** When there is no global, the init thread installs self observers: detours on up to 64 Step and 64 Draw event functions. Whichever objects owning those events are alive in the current room supply a fresh instance every frame. Draw is included because some games' long-lived controllers only draw.

```cpp
// Runtimes without a current-self global need a live instance from
// somewhere before any builtin can be called; watching Step events is the
// generic source. Needs MinHook, so after InstallHooks.
if (mod::gml::Ready() && !mod::gml::HasSelfGlobal())
    mod::hk::InstallSelfObservers(64);
```

<small>Source: [src/dllmain.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/dllmain.cpp#L30-L34)</small>

The observers are queued with `MH_QueueEnableHook` and enabled in one `MH_ApplyQueued`, because every MinHook enable freezes and resumes all threads. Each observed event calls `gml::NoteSelf(self)`; scripts do not, because a script's `self` can be a struct rather than an instance. The observed instance is cleared at the end of every frame, so it is always one that ran during the frame in progress. On Dwarf Eats Mountain the log shows `hooks: watching 54 Step and 56 Draw events for a live instance`.

The hook engine's details are on the [Hook engine](./hook-engine.md) page.

## COPY_RValue is inlined {#copy-rvalue}

Both runtimes have a callable function that the structural check accepts as the copy helper, but they are not the same function. In Stoneshard it is the whole `COPY_RValue(dst, src)`, the second most-called function in the sample. In the 2024 runtime `COPY_RValue` is inlined at its call sites, and what remains callable is its reference half, `COPY_RValue__Post`: it writes the pointer and takes the reference but leaves `kind` and `flags` to the inlined caller (it ranks 17th by calls there).

```text
Stoneshard:  gml: COPY_RValue -> ... (rank 2, 28380 calls)
Dwarf:       gml: COPY_RValue -> ... (rank 17, 1761 calls)
```

**Detection.** `VerifyValueLifetime` copies a probe string into an undefined destination. If the destination ends up sharing the pointer with one more reference but still says `kUndefined`, the helper is the `__Post` half, and `CopyValue` from then on writes kind and flags itself. The log line at the end of the test says which: `copy yes` on Stoneshard, `copy yes, reference half` on Dwarf Eats Mountain.

## Handles: numbers versus kind-15 references {#handles}

Older runtimes return plain numbers for resource handles such as buffers. The 2024 runtime returns typed references, kind 15 (`kRef`): the id in the low 32 bits and what it refers to in the high 32 bits. A reference cannot be built from scratch, only passed back as received.

Instance references do not split cleanly by runtime. Even on Stoneshard, where an instance's `id` variable reads as a number, `instance_find` can hand out a kind-15 reference; the instance lookup check proves that reference kind too, and Stoneshard's log reports `instance lookup proven (...; 1 reference kind)` ([src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L1304-L1330)).

```text
Stoneshard:  builtins:   buffer_create(64,1,1) -> 8
Dwarf:       builtins:   buffer_create(64,1,1) -> handle 0800000100000002
```

**Detection.** There is no switch: every place that consumes a handle accepts both forms. The builtin self-test passes whatever `buffer_create` returned straight back to `buffer_get_size`. The instance lookup reads the id from a number or from the low half of a reference, and only trusts a reference whose high half it has proven to name instances (see [Runtime bridge](./runtime-bridge.md)). Mods see the same thing: an instance or buffer value may be a number in one game and a reference in another, so pass it back unchanged rather than doing arithmetic on it.

## Why not a version check {#why-not-version}

A version number says what a runtime *should* look like; the code says what it *does* look like. Detecting each difference where it shows up has three practical advantages:

- **New runtimes degrade piece by piece.** If a future runtime changes only the registry row, only the builtin registry fails to resolve, and the log says exactly which step failed. Everything else keeps working.
- **No table to maintain.** There is no list of known versions to update when GameMaker ships a release.
- **The check is the proof.** The same validation that tells the shapes apart also rejects a wrong candidate, as the Dwarf Eats Mountain string constructor shows.

This is also why every native change must be validated on both test games (see [Contributing](./contributing.md)): a change that only runs on one runtime has tested only half of these paths.
