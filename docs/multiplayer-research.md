# Stoneshard co-op multiplayer — static feasibility & implementation map

**Research only.** No mod source was modified, nothing was built, the game was never launched.
Everything below is static analysis of the shipped binaries. Date: 2026-09-08.

Target: `D:\torrent\Stoneshard (Early Access)\Stoneshard\StoneShard.exe` (166,577,664 bytes,
GameMaker Studio 2 / YYC, native C++) and `data.win`. Image base `0x140000000`.

Section layout, all VAs at that image base:

```
.text    RVA 0x0001000  size 0x54D9B14      compiled GML (below ~0x145194AB0) + runner C++ (above)
.rdata   RVA 0x54DB000  size 0x405AD02      strings, constants
.data    RVA 0x9536000  size 0x0633C9C      script table, runner globals
.pdata   RVA 0x9B6A000  size 0x065EB18      556,610 RUNTIME_FUNCTION entries = bounds for every function
```

`.pdata` turned out to be important: the 34,167-entry `gml_*` symbol table stops at `0x145194AB0`, and
**all runner C++ above that address is unnamed**. `.pdata` gives exact function bounds there anyway.

Every claim carries its evidence — address, symbol name, string literal, or call-site count.
Anything not directly confirmed against the binary is marked **UNVERIFIED**.

---

## 1. Summary and current verdict on feasibility

**Verdict: feasible, and the blocking question is solved.** The path is host-authoritative co-op, not
lockstep. The two hard prerequisites — calling `network_*`, and receiving packets — both have clean,
name-anchored solutions that need no hardcoded addresses.

### The four things that decide this

**1. `network_*` is reachable — via the runner's builtin registry, not the script table.** (§2)
GameMaker registers every builtin at startup through `Builtin_Add` (`0x14529D2C0`, identified beyond
doubt: it passes the source path
`D:\a\GameMaker\GameMaker\GameMaker\Runner\VC_Runner\Files\Code\Code_Function.cpp` to its
growth-realloc). Registrations write 80-byte `RFunction { char name[64]; TRoutine f; int argc; int id; }`
entries into a heap array reachable through three adjacent `.data` globals — `0x14992A310` (pointer),
`0x14992A318` (count), `0x14992A31C` (capacity). Walking it yields **2,533 builtins by name**,
including all 17 `network_*`, all 41 `buffer_*`, and the reflection API. The anchor is a name
*string*, so it satisfies the no-hardcoded-address rule.

Independent validation that the decode is right: the registered `argc` matches the documented GML
arity for every network builtin (`network_create_socket`=1, `network_create_server`=3,
`network_send_udp`=5, `network_set_config`=-1/variadic).

**2. The builtin ABI is NOT the script ABI.** (§2.4) Result is the **first** parameter and `args` is a
contiguous `RValue*`, not `RValue**`. Reusing `gml.cpp`'s script caller will crash. This is the single
most likely way to lose a day on this project.

**3. Packets must be caught in the runner — no GML handler exists.** (§3) There is exactly one
`Other_68` (Async Networking) event in all 34,167 symbols, it belongs to the shipped-but-inert GMLive
plugin, and it is a two-instruction stub. However, the socket poll runs unconditionally from the main
frame loop (chain traced, every link single-caller), so once the mod opens a socket the runner
receives and packages data every frame — it just has nowhere to deliver it. The fix is to hook the
data `async_load` builder, `sub_145348230`, which hands over `(socket, buffer, size)` directly in
registers. It is reachable by a fully name-anchored chain: builtin table -> `event_perform_async` ->
its trailing `jmp` -> the async dispatcher -> its call sites carrying the constant `68` -> `.pdata`
for enclosing bounds.

**4. Host-authoritative, not lockstep.** (§5) Not because of floating point — a single x86-64/SSE2
binary is bit-reproducible — but because there is no input boundary to capture (`scr_allturn` alone is
reached from **98 distinct callers**), because iteration order over `with` and `ds_map` is a live
hazard (`scr_global_turn` has 29 `with` setups), and because the turn tick is global rather than
per-player. Meanwhile the reflection API (`variable_instance_get_names` / `_get` / `_set`, all
registered) makes name-based state replication tractable *and* update-stable.

### The two numbers that shape the design

- **`o_player` is referenced by 385 distinct GML functions** (910 compiled immediate sites), against a
  median of **0** for comparable objects. Of those sites, **176 are hard singleton dereferences**
  (`o_player.something`) that silently bind to whichever instance is found first. Under host
  authority the host keeps one `o_player` and all 176 keep working; spawning a second player instance
  breaks all 176 quietly. (§4.6)
- **`scr_allturn` (`0x1416C1E60`) is the time-advance entry point** — 109 call sites, 98 distinct
  callers — and it drives `scr_global_turn` (`0x1416C2A60`, 12.5 KB), which applies hunger, thirst,
  pain, intoxication, psyche, fatigue, bleeding and regeneration **once per turn, globally**. Co-op
  must therefore be turn-interleaved with a single shared turn clock; a concurrent design would tick
  every survival stat twice per round. (§6)

### What is still unknown

The socket-type and `network_config_*` constant *values*. GML constant names are absent from the
binary (YYC folds them), so they must be established experimentally. This gates transport bring-up but
nothing else. Full list in §8.2.

---

## 2. Calling `network_*` from the mod — **SOLVED**

**Status: solved and statically verified.** All 17 `network_*` builtins are reachable, and a single
runtime table yields *all 2,533* GameMaker builtins by name.

### 2.1 The problem restated
`network_*` are GameMaker *builtins*, not GML scripts. They have no row in the
`{const char* name, void* func, void* slot}` table in `.data` that yields the 34,167 `gml_*`
scripts, so the script-table trick does not reach them.

### 2.2 What was found: `Builtin_Add` and the `RFunction` array

The runner registers every builtin at startup through one registrar:

| item | value | evidence |
|---|---|---|
| `Builtin_Add` | **`0x14529D2C0`** | 2,535 call/jmp sites |
| source file (proof of identity) | `D:\a\GameMaker\GameMaker\GameMaker\Runner\VC_Runner\Files\Code\Code_Function.cpp` | passed as the `file` argument of the growth-realloc at `0x14529D2F1` (`lea r8,[rip+0x2A4AC88]`) |
| `g_pBuiltInFunctions` | **`0x14992A310`** (qword, `.data`) | `mov rax,[rip+0x468CF9F]` @ `0x14529D36A`; also read @ `0x14529D34E`, `0x14529D2FB` |
| `g_BuiltInFunctionCount` | **`0x14992A318`** (int32, `.data`) | `mov eax,[rip+0x468D043]` @ `0x14529D2CF`; written @ `0x14529D32B` |
| `g_BuiltInFunctionCapacity` | **`0x14992A31C`** (int32, `.data`) | `mov r9d,[rip+0x468D03D]` @ `0x14529D2D8`; grows by `+0x1F4` (500) @ `0x14529D2EA` |

Registrar signature (Win64):

```c
void Builtin_Add(const char* name /*rcx*/, TRoutine f /*rdx*/,
                 int argc /*r8d*/, int /* r9d - DEAD */);
```

`r9d` is **overwritten** at `0x14529D2D8` before any read, so the 4th argument is ignored. Callers
still zero it (`xor r9d,r9d`) because MSVC then builds `argc` with `lea r8d,[r9+N]`.

### 2.3 `RFunction` entry layout — 0x50 (80) bytes

```c
struct RFunction {          /* sizeof == 0x50 */
    char     name[0x40];    /* +0x00  inline, NUL-terminated, copied with no bounds check */
    TRoutine f;             /* +0x40 */
    int32_t  argc;          /* +0x48   -1 == variadic */
    int32_t  id;            /* +0x4C   always initialised to 0xFFFFFFFF */
};
```

Evidence for each field, read out of `Builtin_Add`:

- **stride 0x50** — `lea rcx,[rcx+rcx*4]` @ `0x14529D346` then `shl rcx,4` @ `0x14529D34A`
  = `idx * 5 * 16`. The growth allocation computes `lea rdx,[rax+rax*4]; shl rdx,4`
  @ `0x14529D30F`/`0x14529D313` = `capacity * 80`.
- **`name` at +0x00, stored inline** — the entry base (`... add rcx,[rip->g_ptr]` @ `0x14529D34E`)
  is passed as the *destination* to the memcpy at `0x14529D355`, with `rdx` = the caller's name
  pointer and `r8` = strlen+1 computed by the byte loop at `0x14529D331..0x14529D339`.
- **`f` at +0x40** — `mov [rax+rdx*8+0x40], rsi` @ `0x14529D378`, where `rdx = idx*10`, so
  `rdx*8 = idx*80`. `rsi` holds the incoming `rdx` (the function pointer).
- **`argc` at +0x48** — `mov dword [rax+rdx*8+0x48], edi` @ `0x14529D39B`; `edi` holds the
  incoming `r8d`.
- **`id` at +0x4C** — `mov dword [rax+rdx*8+0x4C], 0xFFFFFFFF` @ `0x14529D3B8`.

The 64-byte name field is bounded by convention only — the memcpy is unchecked.

### 2.4 The builtin ABI — **DIFFERENT from the YYC script ABI**

This is the single most important implementation detail in this document.

YYC *scripts* (what `src/gml.cpp` already calls):

```c
RValue* Script(CInstance* self /*rcx*/, CInstance* other /*rdx*/,
               RValue* result /*r8*/, int argc /*r9d*/, RValue** args /*[rsp+0x28]*/);
```

Builtins (`TRoutine`) use a **different parameter order, no return value, and a different `args`
shape**:

```c
void TRoutine(RValue*    result /*rcx*/,          /* OUT, and it is the FIRST parameter */
              CInstance* self   /*rdx*/,
              CInstance* other  /*r8*/,
              int        argc   /*r9d*/,
              RValue*    args   /*[rsp+0x28]*/);  /* CONTIGUOUS ARRAY, not array-of-pointers */
```

Evidence:

- **`rcx` is the result.** `network_create_socket` (`0x145345FF0`) opens with
  `mov r14,rcx; xor ebp,ebp; mov dword [rcx+0xC],ebp; movabs rax,0xBFF0000000000000; mov [rcx],rax`
  — it writes `kind = 0 (real)` at `+0xC` and the double `-1.0` at `+0x0`. That is the already
  established `RValue` layout (value +0, kind +0xC), so `rcx` is an `RValue*` out-parameter.
  `network_send_packet` (`0x145346580`) does exactly the same at `0x14534659D` / `0x1453465AE`.
- **`args` is the 5th stack parameter.** In `network_send_packet` the prologue is
  `push r14` + `sub rsp,0x40` (0x48 total displacement), and it then does
  `mov rbx,[rsp+0x70]` @ `0x1453465EC` — which is `[entry_rsp+0x28]`, the 5th Win64 slot.
- **`args` is `RValue*`, NOT `RValue**`.** `network_send_packet` calls `0x14519A620` with
  `rcx = args` and `edx = 0`, then `edx = 1`, then `edx = 2` (@ `0x1453465F4`, `0x145346604`, ...).
  That helper begins
  `movsxd rdi,edx; mov rbx,rdi; shl rbx,4; add rbx,rcx; mov eax,[rbx+0xC]; and eax,0xFFFFFF`
  then dispatches through a 16-entry jump table. A **16-byte stride** with the kind read at `+0xC`
  is exactly the documented `RValue`. This is the runner's `YYGetInt32(RValue* args, int idx)`,
  identified at **`0x14519A620`**.

Passing `RValue**` here — as the existing script caller does — would dereference argument *values*
as pointers and crash. **The mod needs a second, separate invoker for builtins.**

### 2.5 The 17 `network_*` builtins

Static VAs, image base `0x140000000`. `argc` is the value the runner registered; it matches the
documented GML arity for every entry, which independently validates the whole decode.

| name | func VA | argc | GML signature |
|---|---|---|---|
| `network_create_socket`      | `0x145345FF0` | 1  | `(type)` |
| `network_create_socket_ext`  | `0x145346180` | 2  | `(type, port)` |
| `network_create_server`      | `0x145345FB0` | 3  | `(type, port, maxclients)` |
| `network_create_server_raw`  | `0x145345FD0` | 3  | `(type, port, maxclients)` |
| `network_connect`            | `0x145345AC0` | 3  | `(socket, url, port)` |
| `network_connect_raw`        | `0x145345E90` | 3  | `(socket, url, port)` |
| `network_connect_async`      | `0x145345BF0` | 3  | `(socket, url, port)` |
| `network_connect_raw_async`  | `0x145345D50` | 3  | `(socket, url, port)` |
| `network_send_packet`        | `0x145346580` | 3  | `(socket, buffer, size)` |
| `network_send_raw`           | `0x1453466B0` | 3  | `(socket, buffer, size)` |
| `network_send_broadcast`     | `0x1453463F0` | 3  | `(socket, port, buffer, size)` * |
| `network_send_udp`           | `0x145346880` | 5  | `(socket, url, port, buffer, size)` |
| `network_send_udp_raw`       | `0x145346960` | 5  | `(socket, url, port, buffer, size)` |
| `network_resolve`            | `0x145346360` | 1  | `(url)` |
| `network_destroy`            | `0x145346340` | 1  | `(socket)` |
| `network_set_timeout`        | `0x145346CE0` | 3  | `(socket, read, write)` |
| `network_set_config`         | `0x145346A40` | **-1 (variadic)** | `(parameter, value)` |

\* `network_send_broadcast` registers `argc = 3` while the GM manual lists four parameters. The
registered value is authoritative for this build; which parameter is dropped is **UNVERIFIED**.

`network_set_config` is registered by a **tail call** (`add rsp,0x28; jmp 0x14529D2C0`
@ `0x1453473D7`/`0x1453473DB`), not a `call`. A scanner that only looks for `E8` misses it — this
cost one iteration during this analysis. It is the last registration in the block.

Cross-checks that these addresses are the real implementations:

- `"network_set_config : unknown parameter"` (`0x147D00C20`) has exactly **one** `lea` xref, at
  `0x145346C8D` — inside `network_set_config` (`0x145346A40`).
- `"unable to enable reliable UDP on a server it must be a socket"` (`0x147D00B60`) has exactly
  **one** `lea` xref, at `0x145346B3D` — also inside `network_set_config`.
- `network_create_server` is a four-instruction thunk: `sub rsp,0x38; mov rax,[rsp+0x60]`
  (= `args`); `mov byte [rsp+0x28],0` (a `raw` flag); `mov [rsp+0x20],rax`;
  `call 0x1453484F0` — the shared server implementation. `network_create_server_raw`
  (`0x145345FD0`) is the same thunk with the flag set.

The whole networking implementation is contiguous, `0x145345AC0 .. 0x145346CE0`, and all 17
registrations sit in one straight-line run at `0x145347222 .. 0x1453473DB`.

### 2.6 Other builtins that matter for this project

- **`buffer_*` — 41 builtins**, `0x145218470 .. 0x14521ABD0`. Present with the expected arities:
  `buffer_create`(3), `buffer_write`(3), `buffer_read`(2), `buffer_seek`(3), `buffer_tell`(1),
  `buffer_poke`(4), `buffer_peek`(3), `buffer_delete`(1), `buffer_exists`(1), `buffer_get_size`(1),
  `buffer_resize`(2), `buffer_fill`(5), `buffer_copy`(5), `buffer_compress`(3),
  `buffer_decompress`(1), `buffer_crc32`(3), `buffer_md5`(3), `buffer_sha1`(3),
  `buffer_get_address`(1), `buffer_set_used_size`(2), `buffer_save`/`buffer_load`.
  **`buffer_get_address` (`0x145219A10`) is the important one** — it returns the raw pointer, so
  packets can be filled with `memcpy` from native code instead of one `buffer_write` builtin call
  per field.
- `random_set_seed`(1) `0x1451D6800`, `random_get_seed`(0) `0x1451D6760`, `random`(1) `0x1451D6710`,
  `random_range`(2) `0x1451D6780`, `randomize`/`randomise` `0x1451D6840` (one address, two names).
- `script_execute` `0x145336DB0` and `script_execute_ext` `0x145336F30`, both variadic.
- `json_encode` `0x145222040`, `json_decode` `0x145221D40`, `json_parse` `0x145221FA0`,
  `json_stringify` `0x1452220D0`; `base64_encode` `0x1452AE810`, `base64_decode` `0x1452AE750`.
- `os_is_network_connected` `0x1452AEE80`.
- `http_get` `0x145221190`, `http_request` `0x145221510`, `http_post_string` `0x145221440` —
  a fallback transport, and a way to reach a matchmaking/relay service without raw sockets.

2,535 registration sites resolve to **2,533 distinct names**; the two collisions are alias pairs
(`randomize` / `randomise` is one). Full dump: `<scratchpad>/builtin_table.json`.

### 2.7 Proposed runtime resolution — no hardcoded addresses

The project rule is that nothing may be pinned to an address. This design pins to a **name string**,
the most update-stable anchor available, and resolves *every* builtin from **one** anchor.

**Step 1 — anchor on a builtin name string.** Scan `.rdata` for the NUL-delimited literal
`"network_create_socket"`. In this build it occurs **exactly once** (`0x147D00C48`). All 17 network
names occur exactly once each, so a consensus vote across several of them is free.

**Step 2 — find the registration site.** Scan `.text` for `lea rcx,[rip+disp32]` (`48 8D 0D`) whose
computed target is that string. There is **exactly one** such site per network name (verified for
all 17; e.g. `network_create_socket` -> `0x145347230`). The instruction immediately after it is
`call rel32` (`E8`), or for the final registration `add rsp,imm8` + `jmp rel32` (`E9`). Its target
is `Builtin_Add`.

**Step 3 — consensus.** Repeat steps 1-2 for several names spread across the runtime — e.g.
`network_create_socket`, `buffer_create`, `random_set_seed`, `json_encode`, `date_get_year`. All
must vote for the same `Builtin_Add`. This is the pattern `src/symbols.cpp` already uses. If the
vote is not unanimous, fail loudly rather than proceed.

**Step 4 — extract the globals from `Builtin_Add`'s body.** Walk the first ~0x40 bytes and collect
the rip-relative operands. Actual leading bytes in this build (mask the four `d32` fields when
matching):

```
48 89 5C 24 08  48 89 74 24 10  57  48 83 EC 20
8B 05 <d32>        ; mov  eax, [rip+d32]   -> g_BuiltInFunctionCount   (int32)
41 8B F8           ; mov  edi, r8d
44 8B 0D <d32>     ; mov  r9d, [rip+d32]   -> g_BuiltInFunctionCapacity(int32)
48 8B F2           ; mov  rsi, rdx
48 8B D9           ; mov  rbx, rcx
41 3B C1           ; cmp  eax, r9d
7C 38              ; jl   ...
41 81 C1 F4 01 00 00
```

`g_pBuiltInFunctions` is the global read as a **qword** (`48 8B 05 <d32>` @ `0x14529D36A`),
distinct from the two int32 globals read with `8B 05` / `44 8B 0D`. In this build the three sit
adjacent: `0x14992A310` (ptr), `0x14992A318` (count), `0x14992A31C` (capacity) — a useful sanity
check, since count/capacity should land at `ptr_global + 8` and `+ 0xC`.

**Step 5 — walk the array at runtime.**

```c
RFunction* tbl = *(RFunction**)g_pBuiltInFunctions;
int        n   = *(int*)g_BuiltInFunctionCount;      /* expect ~2533 */
for (int i = 0; i < n; ++i)
    map[tbl[i].name] = { tbl[i].f, tbl[i].argc };
```

Health check, mirroring `src/symbols.cpp`: assert `n` is in a sane band (1,500-6,000); assert every
`name` is printable ASCII and NUL-terminated inside 64 bytes; assert every `f` lands inside `.text`;
assert a spread of known names resolve with the expected `argc` — `network_create_socket`->1,
`network_send_udp`->5, `buffer_copy`->5, `date_create_datetime`->6, `buffer_get_size`->1. Those
arities are a cheap, strong correctness signal that an address check alone does not give.

**Timing.** The array is built during runner startup, so the mod must resolve *after* registration
completes. Resolving lazily on first use — well after the first frame — is sufficient; the existing
`o_player` per-frame hook is already far past that point.

**Step 6 — a second invoker.** `gml.cpp`'s script caller cannot be reused; see §2.4. A builtin call
needs a **contiguous** `RValue args[N]` and the result as the *first* parameter. Sketch:

```c
typedef void (*TRoutine)(RValue* result, CInstance* self, CInstance* other,
                         int argc, RValue* args);

RValue CallBuiltin(TRoutine f, CInstance* self, int argc, RValue* args) {
    RValue r; r.kind = 5 /*undefined*/;      /* callee overwrites */
    f(&r, self, self, argc, args);           /* args is the ARRAY, not &array_of_ptrs */
    return r;
}
```

**Fallback if the array walk ever breaks.** Steps 1-2 alone already give a per-name answer: from the
`lea rcx` site, decode backwards ~0x20 bytes to the `lea rdx,[rip+disp32]` (`48 8D 15`) that
immediately precedes it — that operand *is* the `TRoutine`. Verified against all 17 network names.
No globals needed, but it must be run once per name.

### 2.8 Dead ends recorded here

- The `network_*` name strings sit at `0x147D00C48 .. 0x147D00DC8` with an apparent regular 0x38
  stride. **That is not a table.** They are ordinary 8-byte-aligned `.rdata` string literals and the
  stride is a coincidence of their lengths. Structure-scanning there finds nothing.
- Scanning `.data` for `{name_ptr, func_ptr}` pairs — the trick that resolves the 34,167 `gml_*`
  scripts — cannot ever reach builtins: the builtin name is stored **inline** in the entry rather
  than as a pointer, and the array lives on the **heap**, not in `.data`. This is why the earlier
  pointer scan returned zero pairs. That result was correct; the conclusion to draw from it was
  "wrong container", not "unreachable".
- Any registration scanner must include `E9` tail-call sites, not just `E8`.

---

## 3. Async network event delivery — **no GML handler exists; hook the runner instead**

**Status: answered.**

### 3.1 No object handles the Async Networking event

Across all 34,167 symbols there is **exactly one** `_Other_68` (Async Networking) symbol:

```
gml_Object_obj_gmlive_Other_68     0x1445F3E10
```

and it is a **stub**:

```
0x1445F3E10  mov  qword ptr [rip+0x5317921], rcx   ; -> 0x14990B738
0x1445F3E17  ret
```

Two instructions, no body. `obj_gmlive_Draw_64` (`0x1445F3E20`) and `obj_gmlive_CleanUp_0`
(`0x1445F3E30`) are byte-identical and write the **same** global `0x14990B738` — this is what YYC
emits for an empty event. `obj_gmlive` is YellowAfterlife's **GMLive** hot-reload plugin, which is
compiled into the shipping build (26 `GMLive*` global scripts, `0x1400316F0 .. 0x142078 3D0`), but
its networking event carries no code. Its HTTP event `obj_gmlive_Other_62` (`0x1445F3BA0`) *does*
have a real body, so GMLive's transport here is HTTP, not sockets.

Full async-event census from the symbol table (`_Other_N` suffix counts):

```
Other_0:6  Other_3:2  Other_4:61  Other_5:63  Other_7:300  Other_10:1313  Other_11:406
Other_12:210  Other_13:350  Other_14:118  Other_15:181  Other_16:118  Other_17:316
Other_18:24  Other_19:202  Other_20:417  Other_21:164  Other_22:40  Other_23:58
Other_24:265  Other_25:283  Other_40:3   Other_62:1   Other_68:1
```

Everything at 62+ is an async event; only `Other_62` (HTTP) and `Other_68` (networking) exist, one
object each, and only the HTTP one has code.

**Conclusion: the mod cannot rely on GML to receive packets.** There is no handler to piggyback on,
and adding an event to a compiled YYC object is not practical. The mod must intercept in the runner.

### 3.2 The async dispatch machinery (identified)

| function | VA | signature |
|---|---|---|
| `CreateAsyncEventWithDSMap` | **`0x1452FFC90`** | `(int dsmap /*ecx*/, int event_subtype /*edx*/)` |
| `CreateAsyncEventWithDSMapAndBuffer` | **`0x1452FFD20`** | `(int dsmap /*ecx*/, int buffer /*edx*/, int event_subtype /*r8d*/)` |

Evidence:
- The builtin `event_perform_async` (`0x1451EBD60`) reads two args with `YYGetInt32` and tail-jumps
  (`jmp 0x1452FFC90` @ `0x1451EBD8C`) with `ecx = arg1`, `edx = arg0`.
- Both dispatchers allocate a 12-byte record (`call 0x1451C0360` with `ecx = 0xC`) laid out
  `{ +0: dsmap, +4: event_subtype, +8: buffer (0xFFFFFFFF when none) }`, then allocate a 0x68-byte
  queue node, and finally `mov dword [rax+0x44], 7` — **7 = `ev_other`**, the event type. The
  subtype is what selects `Other_68`.

The 12 call sites of `CreateAsyncEventWithDSMap`, with the subtype constant decoded, map exactly onto
GameMaker's `ev_async_*` enum — an independent confirmation that the argument in `edx` is the subtype:

| site | enclosing fn | subtype | meaning |
|---|---|---|---|
| `0x145348207` | `sub_145348140` | **68** | async networking |
| `0x1453484B0` | `sub_1453483A0` | **68** | async networking |
| `0x14534B6E5` | `sub_14534B620` | 70 | async social |
| `0x14534C25A` | `sub_14534C19A` | 70 | async social |
| `0x1453630B3` | `sub_145362F66` | 72 | async save/load |
| `0x145286493` | `sub_145286210` | 74 | async audio playback |
| `0x14528B208` | `sub_14528B0F8` | 74 | async audio playback |
| `0x145275371` | `sub_14527531F` | 75 | async system event |
| `0x14534925D` | `sub_1453491FD` | 75 | async system event |
| `0x14534947B` | `sub_1453493D0` | 75 | async system event |
| `0x14537EABD` | `sub_14537EA70` | 75 | async system event |
| `0x1451EBD8C` | `event_perform_async` | dynamic | GML-driven |

`CreateAsyncEventWithDSMapAndBuffer` has 2 call sites: `0x14528AF42` (subtype **73**, audio
recording; keys `data_len`, `channel_index`, `buffer_id`) and `0x145348364` (subtype **68**).

### 3.3 The three network `async_load` builders — the recommended hook points

All three live in the networking module and are the last runner code touched before an event is
queued. Hooking them hands the mod the payload with no GML involvement.

**A. Data received — `sub_145348230`** (`0x145348230 .. 0x145348364`)

```c
void NetAsyncData(int socket_id /*ecx*/, int buffer /*edx*/,
                  int size /*r8d*/, void* peer /*r9*/);
```
Register mapping from the prologue: `movsxd rdi,ecx` (socket), `mov ebp,edx` (buffer),
`mov r14d,r8d` (size), `mov rsi,r9` (peer info). It builds the map with `ds_map_create`
(`0x1451C6EA0`) and five `ds_map_add(map, key, double)` calls (`0x1451E37C0`):

| key | value |
|---|---|
| `type` | constant **3.0** (double at `0x145AAC2C0`) |
| `id` | `socket_id` |
| `buffer` | `buffer` |
| `size` | `size` |
| `port` | `[rdi+0xF4]` |
| `message_type` | `[rsi]` |

then `CreateAsyncEventWithDSMapAndBuffer(map, buffer, 68)` @ `0x145348364`.
Also references `ip` and `SocketMutex`. Single caller: `0x14538E0F0`.

**B. Connect / disconnect — `sub_145348140`** (`0x145348140 .. 0x14534822A`)
Keys: `type` (from `r10d`, dynamic), `id`, `socket`, `port`, `other_port`, `ip`.
`CreateAsyncEventWithDSMap(map, 68)` @ `0x145348207`.
Four callers: `0x14538F30C`, `0x14538F78D`, `0x14538F8CB`, `0x14538FEED`.

**C. Non-blocking connect result — `sub_1453483A0`** (`0x1453483A0 .. 0x1453484E3`)
Keys: `type` = constant **4.0**, `socket`, `id`, `succeeded`, `ip`, `port`.
`CreateAsyncEventWithDSMap(map, 68)` @ `0x1453484B0`.
Five callers: `0x14538D727`, `0x14538D760`, `0x14538D802`, `0x14538D84E`, `0x14538D862`.

**Observed `type` enum in this runtime: data = 3, non-blocking-connect = 4.** Connect and disconnect
are passed dynamically in `r10d` and are therefore **UNVERIFIED**; 1 and 2 respectively is the
natural reading given 3 and 4, but it was not confirmed statically. Note this runtime's numbering is
**one higher** than the values usually quoted for the GML `network_type_*` constants — do not assume
the documented numbers. The GML constant *names* (`network_type_data`, `network_socket_tcp`,
`network_config_*`, …) are **absent from the binary entirely** — searched, zero hits — because YYC
folds constants to numeric literals at compile time. Any constant the mod passes must therefore be a
raw number, and the numbers above are the only ones this analysis has confirmed.

### 3.4 The receive path runs unconditionally from the main loop

Traced upward from the data builder, the call chain is:

```
sub_14548BEA0                          (top of the runner loop)
 -> sub_145232380
   -> sub_14523AC10                    strings: "TimingWait"
     -> sub_1452396C0                  strings: "Garbage Collector", "IO&YoYo", "Draw", "Scroll"   <- frame loop
       -> sub_145239950                strings: "Update"
         -> sub_145347530              strings: "SocketMutex"                                       <- per-frame socket poll
           -> sub_14538D570
             -> sub_14538DFB0          strings: "RCV : "                                            <- receive
               -> sub_145348230        (builds the data async_load map)
```

Each of `sub_145232380`, `sub_14523AC10`, `sub_1452396C0`, `sub_145239950`, `sub_145347530`,
`sub_14538D570` has exactly **one** caller, so this chain is unambiguous.

This matters: the socket poll is part of the runner's per-frame `Update` phase and is **not**
conditional on any GML code using networking. Once the mod calls `network_create_server` /
`network_connect`, sockets are polled every frame with no extra pumping. The events it queues then
go nowhere, because no object has `Other_68` — which is precisely why the hook must sit at the
builder, not at the GML end.

### 3.5 Recommended design

Hook `sub_145348230` (data), and optionally `sub_145348140` / `sub_1453483A0` (connection lifecycle),
resolved by pattern rather than address (see §7). In the hook:

1. Read `socket_id`, `buffer`, `size` straight from the incoming registers — **no `ds_map` lookup is
   needed**, which avoids the map entirely.
2. Call the now-reachable builtin `buffer_get_address` (`0x145219A10`) on `buffer` and `memcpy` the
   `size` bytes into the mod's own queue. Copy inside the hook: the queued async event owns the
   buffer and the runner frees it after dispatch.
3. Call the original function afterwards so runner bookkeeping and cleanup stay intact. The event it
   queues is harmless — nothing handles `Other_68`.

The alternative — hooking `sub_14538DFB0` ("RCV : ") one level lower to catch raw bytes before the
buffer is allocated — is possible but gives up the socket/size framing the builder has already
normalised. **UNVERIFIED**: its exact signature was not decoded.

Do **not** hook `CreateAsyncEventWithDSMap` itself: it is shared by audio, social, save/load and
system events, so a hook there would sit on hot unrelated paths.

---

## 4. State inventory: what must synchronise

### 4.1 The key enabler — player state is reachable **by name** at runtime

Because §2 makes every builtin callable, the mod gets GameMaker's whole reflection API. All of these
are registered and have the expected arity:

| builtin | VA | argc |
|---|---|---|
| `variable_instance_get_names` | `0x1451EE150` | 1 |
| `variable_instance_names_count` | `0x1451EE2B0` | 1 |
| `variable_instance_get` | `0x1451EDFA0` | 2 |
| `variable_instance_set` | `0x1451EE370` | 3 |
| `variable_instance_exists` | `0x1451EDD30` | 2 |
| `variable_global_get` / `_set` / `_exists` | `0x1451EDBD0` / `0x1451EDCA0` / `0x1451EDB00` | 1 / 2 / 1 |
| `instance_number` / `instance_find` / `instance_exists` | `0x1451F2C10` / `0x1451F2980` / `0x1451F28B0` | 1 / 2 / 1 |
| `instance_create_depth` / `instance_create_layer` | `0x1451F21E0` / `0x1451F22D0` | 4 / 4 |
| `instance_nearest` / `instance_place` / `instance_position` | `0x1451F2B70` / `0x1451F2C90` / `0x1451F2E80` | 3 / 3 / 3 |
| full `ds_map_*` set incl. `find_first`/`find_next`/`keys_to_array`/`write`/`read` | `0x1451E33D0 ..` | — |

(`variable_struct_get`/`_set`/`_exists`/`_get_names` resolve to the **same addresses** as the
`variable_instance_*` four — they are aliases in this runtime.)

**This changes the shape of the problem.** The state inventory does not have to be reverse-engineered
into a static offset map that breaks on every patch. At runtime the mod can call
`variable_instance_get_names(player)` to enumerate every field `o_player` actually has, then read and
write them by name. That is inherently update-stable and is the approach this document recommends.

What follows is the static picture of *how much* there is, which is what determines the sync budget.

### 4.2 Scale of `o_player`

- `o_player` is **object index 5376** (from `data.win` `OBJT`, 9,663 slots, all named).
- **40 events** (exact list; excludes `o_player_AI`, `_corpse`, `_observer`, `_SimpleNPC`, `_chest`):
  `Create_0`, `Destroy_0`, `CleanUp_0`, `Step_0`, `Step_2`, `Draw_0/64/72/73`,
  `Alarm_0/1/2/4/5/7/8/11`, `Mouse_5`, `Mouse_54`, `Other_5/10/11/12/14/15/16/17/18`,
  and **12 `KeyPress_112..123`** handlers (F1–F12).
- Compiled sizes, which are a fair proxy for how much state each touches:

| symbol | VA | size |
|---|---|---|
| `gml_Object_o_player_Create_0` | `0x143E8DED0` | **`0xF72F` (63,279 bytes)** |
| `gml_Object_o_player_Step_0` | `0x143EACEE0` | `0x8507` (34,055 bytes) |
| `gml_Object_o_player_Step_2` | `0x143EB96A0` | `0x452` |
| `gml_Script_scr_atr_calc` | `0x140B66380` | **`0x214D4` (136,404 bytes)**, 55 call sites / 51 distinct callers |

A 63 KB `Create_0` is the initialisation of the player's variable set; a 136 KB `scr_atr_calc` is the
derived-stat recalculation. Neither is something to reimplement — they are things to *drive*.

### 4.3 Script families that define player state

Counts of `gml_Script_*` symbols by prefix (each is one compiled GML function):

```
scr_unit*   79    scr_skill*  73    scr_inv*    59    scr_quest*  49
scr_player* 34    scr_item*   29    scr_state*  26    scr_psy*    25
scr_time*   18    scr_atr*    12    scr_buff*   11    scr_save*    9
scr_perk*    7    scr_hunger*  5    scr_pain*    5    scr_intoxication* 4
scr_load*    4    scr_thirst*  3    scr_fatigue* 3
scr_npc*   343
```

Total symbol population, for scale: **15,232** object events, **8,897** room-creation-code blocks,
**6,738** scripts, **3,280** global-script trampolines, 20 rooms.

### 4.4 Serialisers that already exist (reuse these, do not invent a format)

| symbol | VA | size | call sites |
|---|---|---|---|
| `gml_Script_scr_savegame` | `0x14104C210` | `0x2F29` (12,073 B) | 3 |
| `gml_Script_scr_loadGame` | `0x141600410` | `0x11AF` | 1 |
| `gml_Script_saveSelfData` | `0x1406AB970` | `0x378C` (14,220 B) | 2 |
| `gml_Script_loadSelfData` | `0x1406B0280` | `0x770F` (30,479 B) | 2 |
| `gml_Script_saveSelfBuffs` | `0x140A12CF0` | `0x17AB` | 5 |
| `gml_Script_loadSelfBuffs` | `0x140A15090` | `0x18FB` | 4 |
| `gml_Script_scr_load_player` | `0x141A4AE70` | `0x444A` | 10 |

Important corrections to the obvious readings:

- **`saveSelfData` / `loadSelfData` are the *unit* serialiser, not the player's.** Their only callers
  are `scr_locationRoomEntityMobsSaveDataGet/Set` and `scr_locationRoomEntityNpcSaveDataGet/Set`.
  They are still highly relevant — `o_player` shares the unit variable set — but they are wired to
  mobs/NPCs, so a co-op sync would call them on a player instance rather than find them already
  wired up for one.
- **`scr_load_player` is character-class instantiation, not save loading.** Its 10 callers are the
  `Create_0` events of the playable classes: `o_dervish`, `o_agemon`, `o_beastslayer`, `o_verren`,
  `o_reaver`, `o_revenger`, `o_knight_maiden`, `o_runaway_wizzard`, `o_woodward`, plus
  `scr_player_jail_exit`. This is the closest thing to a "spawn a second player character" entry
  point that already exists.
- **`scr_savegame` is called only from `o_smoothRoomChanger_Other_15/16/17`** — the game persists on
  room transition. It calls `saveSelfBuffs`, `scr_save_item`, `scr_globalmapFogSave`,
  `scr_globalmapPaperSave`, `scr_slotUpdate`.
- **`saveSelfData` contains no field-name strings** (5 string literals total, all diagnostics). YYC
  compiles variable names to numeric ids and the save is a **positional array**, not a keyed map. So
  the save format is *not* self-describing, and reading it statically would require a variable-id
  table that this analysis did not locate (see §8). This is another reason to go through
  `variable_instance_get_names` instead.

### 4.5 Items

- `data.win` `OBJT` holds **9,663** objects: **1,005 `o_inv_*`**, 991 `o_loot_*`, 606 `o_skill_*`,
  149 `o_b_*` (buffs), 110 `c_*` (containers/controllers).
- Consistent with the brief: item *instances* are objects, while weapon/armour statistics are CSV
  rows keyed by display name (`csv_rows.json`), not objects. Any inventory sync therefore has to move
  **(object index, CSV key, per-instance modifiers)**, not an object id — a spawned item on the
  client will have a different instance id.

### 4.6 How singleton is the design? — quantified

`o_player`'s asset index (5376) is compiled into the game's code as a numeric immediate. Counting
those, attributed to the owning function via `.pdata` bounds:

- **910 immediate-load sites**, spread across **385 distinct named GML functions**
  (251 object events, 133 scripts, 1 room creation code).

**Baseline for comparison** — 14 randomly sampled objects with index > 4000 (so the immediate is
rare enough not to be numeric noise):

```
o_skill_dark_blessing_ico 0   o_inv_lockpicks 1   o_skill_throw_axe 0   o_wickerfence11 0
o_loot_treatise_combat3   0   o_verrencart    0   o_snowwoodpile01  0   o_church_lever  0
o_pelt04                  0   o_vinery01light04 0 o_inv_mussel_cooked 0 o_catacombs01part02 0
o_corpsepice              0   o_loot_ruby     0
median = 0 functions, max = 1
```

So `o_player` is a **385-vs-0** outlier. It is referenced by roughly one in every forty named GML
functions in the game.

**How those 910 sites use it**, classified by the first runtime helper called within 0x30 bytes of
the immediate load:

| helper | sites | what it is | multi-player safe? |
|---|---|---|---|
| `sub_1451C3EC0` | **429** | instance lookup/iteration; compares the id against the `all` sentinel `-3`; 1,305 global callers | yes (iterates) |
| `sub_1451F7150` | **274** | walks the instance list and returns a bool from a liveness flag test (`[rbx+0xB0] & 0x100003`); 2,952 global callers — this is the `instance_exists` core | yes |
| `sub_1451B31D0` | **169** | carries `"Unable to find any instance for object index '%d' name '%s'"` and `"Variable Get %d (%d, %d)"` — resolves an object index to **the single instance** and reads a variable | **no** |
| `sub_140001A00` | 22 | ubiquitous YYC helper (257,835 global callers) | n/a |
| `sub_1451C4700` | 8 | — | unknown |
| `sub_1451C3B30` | 7 | also carries `"Unable to find any instance for object index"` | **no** |
| `sub_1451B3450` | 1 | `"invalid with reference"` — a `with(o_player)` statement | yes |

**The number that matters: 176 hard singleton dereferences** (169 + 7). Those are GML written as
`o_player.something`, which in GameMaker binds to *whichever single instance the runtime finds first*
and warns when there is none. With two `o_player` instances alive they do not error — they silently
pick one. That is the dominant correctness hazard for any design that spawns a second `o_player`.

Against that, ~703 of the 910 sites are existence checks and iteration, which tolerate multiple
instances structurally (though `instance_nearest`-style semantics still change meaning).

**Identification confidence:** `sub_1451B31D0` and `sub_1451C3B30` are identified from their own
error strings — high confidence. `sub_1451F7150` is identified from its instance-list walk and
boolean return — high confidence. `sub_1451C3EC0`'s exact GML-level identity is **UNVERIFIED**; only
that it is an instance lookup/iteration helper taking an object index and returning a bool.

---

## 5. Determinism vs authoritative sync — recommendation

**Recommendation: host-authoritative, with the client as a thin renderer plus intent sender.
Do not attempt lockstep determinism.** Reasoning below, then the honest case for the other side.

### 5.1 Floating point is *not* the reason to reject lockstep

This deserves saying plainly because it is the usual first objection and it is the weakest one here.
GML reals are IEEE-754 doubles, and this is a single x86-64 Windows binary compiled with YYC using
SSE2. Two machines running **the same executable** on the same instruction sequence produce
bit-identical double results. There is no x87 80-bit excess precision to worry about, and no
cross-platform or cross-compiler variation, because there is only one build.

Float determinism would become a problem only if Linux/Proton or a different game version were on the
other end. That is a version-matching problem, not a numerical one.

### 5.2 The reasons that actually rule out lockstep

**1. There is no input boundary to capture.** Lockstep requires a single choke point where all
non-deterministic input enters. Stoneshard has no such point. Player intent enters through, at
minimum: `o_player` `Mouse_5`, `Mouse_54`, and **12 `KeyPress_112..123` handlers**;
`o_floor_target_Mouse_53` (3 call sites into `scr_player_move`); `o_enemy_Mouse_4`; `o_loot_Mouse_4`;
`o_context_button_Mouse_4` (5 sites into `scr_allturn`); `o_inv_slot_Other_21/Other_15/Step_2`;
`scr_keyboard_control`; `o_NPC_Other_23`; `o_delayed_move_Alarm_0`; `o_delayed_move_grid_Alarm_0`.
`scr_allturn` alone is reached from **98 distinct callers**. Intercepting and serialising every one
of those, in order, is not a bounded task — and missing one desynchronises silently.

**2. Iteration order is a live hazard.** The pipeline is built on `with` statements and `ds_map`s.
`scr_global_turn` alone contains **29 `with`-statement setups**. `with` iterates the instance list in
runtime order, which depends on instance-id allocation and on activation/deactivation
(`instance_activate_object` / `instance_deactivate_object` are both registered builtins, so the game
can and does deactivate instances). `ds_map` in GameMaker is a hash map; `ds_map_find_first` /
`ds_map_find_next` (`0x1451E3FF0` / `0x1451E4270`) expose bucket order, which depends on insertion
history. Any divergence in instance creation order between host and client changes iteration order
and therefore results.

**3. The RNG is seedable but the call sites are not enumerable.** `random_set_seed` (`0x1451D6800`),
`random_get_seed` (`0x1451D6760`), `random` (`0x1451D6710`), `random_range` (`0x1451D6780`),
`randomize`/`randomise` (`0x1451D6840`, one address, two names) all exist, so the stream *can* be
pinned. But — see §5.4 — the compiled game does not call the registered builtin wrappers, so there is
no static way to count or locate the game's RNG draws. Seeding both ends only helps if both ends draw
the same number of times in the same order, and that cannot be verified statically.

**4. The turn tick is global and per-turn, not per-player** (§6.3). Two independently-simulating
clients would each have to agree on exactly when a turn boundary occurs, and the boundary is triggered
from 98 different places.

### 5.3 Why host-authoritative is the right call

- **State is addressable by name at runtime** (§4.1): `variable_instance_get_names`,
  `variable_instance_get`, `variable_instance_set` are all registered and callable once §2 is
  implemented. A replication layer can enumerate the player's fields and push deltas without a static
  offset map, and therefore without breaking on game updates.
- **The singleton problem is bounded and measurable, not open-ended**: 176 hard `o_player.x`
  dereferences (§4.6). Under host-authority the host has exactly one `o_player`, so all 176 sites keep
  working unchanged. Under lockstep with two real player instances, all 176 become silent
  mis-bindings.
- **The existing mod already has the right primitives**: a per-frame `o_player` hook, a symbol
  resolver with consensus pattern scanning, and capture-and-replay in `gml.cpp`. Host-authority is an
  extension of what exists; lockstep is a rewrite.
- **The transport suits it.** `network_create_server` / `network_connect` / `network_send_packet` over
  TCP (§2.5) with `buffer_get_address` (`0x145219A10`) for zero-copy packet fill, plus
  `buffer_compress`/`buffer_decompress` (`0x145218D90` / `0x145219720`) for state snapshots. Reliable
  ordered delivery is what a state-replication model wants; it is what lockstep wants least.

### 5.4 A finding that constrains all of this

**The compiled game does not call the registered builtin wrappers.** Every `network_*`, every
`buffer_*`, `random_set_seed`, and so on has **zero** direct `E8` call sites in `.text`. That is not
evidence the game avoids them — it is because YYC emits direct calls to the runner's *internal*
helpers and reserves the registered `TRoutine` entries for the GML-facing path. Confirmed by example:
the registered `instance_exists` wrapper is `0x1451F28B0`, while the internal instance-walk helper it
wraps is `0x1451F7150`, and it is `0x1451F7150` that has 2,952 direct callers.

Two consequences:
1. **Caller counts on builtin addresses are meaningless as a usage metric.** Do not read "0 callers of
   `buffer_create`" as "the game never uses buffers".
2. **The mod calling the registered wrapper is safe and is the intended path** — it is the same entry
   GML would use, so it performs the same argument coercion and bookkeeping.

### 5.5 The honest case against this recommendation

Host-authority costs bandwidth and adds input latency on the client, and Stoneshard's UI is heavily
client-side (1,005 `o_inv_*` objects, inventory manipulation calling `scr_allturn` directly from
`o_inv_slot_*` events). Making the client's inventory feel responsive while the host owns the truth is
real work, and inventory is exactly where the object-id-vs-CSV-key split (§4.5) bites.

A middle design is worth considering: **host-authoritative for world/turn/combat, with the client
owning its own UI-local state** and only round-tripping actions that call `scr_allturn`. That keeps
the 98 time-advancing call sites on one authority while leaving pure-presentation work local.

**UNVERIFIED**: no bandwidth estimate was made. Deriving one needs the field count from
`variable_instance_names_count` on a live `o_player`, which requires running the game — out of scope
for this static pass.

---

## 6. Turn / time structure

**Status: mapped.** Stoneshard's clock is driven by an explicit turn pipeline, not by frame time.

### 6.1 The pipeline

| symbol | VA | size | call sites | distinct callers |
|---|---|---|---|---|
| `gml_Script_scr_allturn` | `0x1416C1E60` | `0x5DB` | **109** | **98** |
| `gml_Script_scr_global_turn` | `0x1416C2A60` | `0x30E2` (12,514 B) | 12 | 12 |
| `gml_Script_scr_global_turn_end` | `0x1419C0E50` | `0x2523` | 1 | 1 |
| `gml_Script_scr_turn` | `0x1416F6D70` | `0x4EF1` | 10 | 10 |
| `gml_Script_scr_turn_npc` | `0x1413E60A0` | `0x4CC9` | 26 | 20 |
| `gml_Script_scr_enemy_turn` | `0x1411232F0` | `0x546D` | 5 | 2 |
| `gml_Script_scr_unitTurnGetTime` | `0x1416A2830` | `0x2A5` | 18 | 18 |
| `gml_Script_scr_unitTurnNext` | `0x141699BE0` | `0x4D7` | 9 | 7 |
| `gml_Script_scr_unitTurnBreak` | `0x14169A780` | `0x3B4C` | 2 | 2 |
| `gml_Script_scr_unitTurnEvents` | `0x1416A13B0` | `0xDC6` | 3 | 2 |
| `gml_Script_scr_unitTurnPositionEvents` | `0x1416A3A20` | `0x25BF` | 2 | 2 |
| `gml_Script_scr_timeUpdate` | `0x141999BB0` | `0xEA9` | 6 | 5 |
| `gml_Script_scr_characterTimeUpdate` | `0x141D2E760` | `0xD0A` | 1 | 1 |
| `gml_Script_scr_timeIsFrozen` | `0x1415C71C0` | `0xAB4` | 2 | 2 |
| `gml_Script_scr_skip_turn` | `0x141355280` | `0x24CF` | 4 | 3 |
| `gml_Script_scr_turnallow` | `0x1416EF900` | `0xC94` | 1 | 1 |
| `gml_Script_scr_player_move` | `0x141805180` | `0x35FA` | 12 | 9 |
| `gml_Script_scr_attack` | `0x14129BB90` | `0x5BCD` | 20 | 12 |
| `gml_Script_scr_unitTurnCheckPlayerTurn` | `0x1416A73D0` | `0x1FF` | **0** | 0 (dead) |

### 6.2 `scr_allturn` is the time-advance entry point

**109 call sites across 98 distinct callers** — an order of magnitude more than anything else in the
pipeline. Its callers are exactly the things that cost time in Stoneshard: `o_context_button_Mouse_4`
(5x), `o_inv_slot_Other_21` (3x), `o_inv_slot_Step_2`, `o_inv_slot_Other_15`,
`o_skill_throw_item_Other_13`, `o_trap_Other_20`, `o_door_parent_Other_10`,
`scr_item_helm_toggle_visor`, `scr_crossbow_insert_bolt`, `scr_crossbow_reject_bolt`,
`scr_skip_turn`, … (+86 more).

Its body is small (`0x5DB`) and it calls `scr_global_turn` once, inside a `with` block
(one `sub_1451B3450` "invalid with reference" setup). So: **`scr_allturn` = "an action was taken;
advance the world one turn"**, and it fans out to per-unit work.

### 6.3 `scr_global_turn` is the per-turn survival tick

12,514 bytes, and its outgoing GML calls are the whole survival model:

```
scr_is_cutscene            scr_timeIsFrozen           scr_timeUpdate
scr_states_duration_dec x2 scr_characterTimeUpdate    scr_unit_regen
scr_hunger_thirsty_add     scr_hungercheck            scr_thirsty_check
scr_pain_decrease          scr_intoxication_decrese   scr_psy_check
scr_fatigue_change         scr_actionsLogBleeding     scr_perkTriggerCall
scr_atr_calc               scr_instance_exists_in_list x2
math_round                 scr_noise_receiver_global_clear
```

It contains **1** `o_player` immediate reference and 29 `with`-statement setups.

**This is the single most important constraint on co-op.** Hunger, thirst, pain, intoxication,
psyche, fatigue, bleeding and regeneration are all applied **once per turn**, globally. If two
players each independently trigger a turn, every survival stat ticks twice per round of play. Any
co-op design must make "a turn" a single global event, not a per-player one.

### 6.4 Action cost and unit ordering

- `scr_unitTurnGetTime` (18 distinct callers, incl. `scr_contattack`, `scr_skip_turn`,
  `is_allow_actions`, `o_player_Step_0`, `o_player_Other_17`, `o_controller_Step_0`) computes how much
  time an action costs — this is an energy/time-cost system, not fixed-length turns.
- `scr_unitTurnNext` (callers: `scr_attack` x2, `scr_unitTurnBreak` x2, `o_player_Alarm_1`,
  `o_player_Other_17`, `o_offhand_atack_Create_0`, …) advances to the next unit.
- `scr_unitTurnBreak` is the only caller of `scr_global_turn_end`, and calls `scr_global_turn` and
  `scr_atr_calc` (x2). It is the end-of-turn boundary.
- NPCs run through `scr_turn_npc` (26 sites) and `scr_enemy_turn` (5 sites).

### 6.5 Answer to the brief's question

**Co-op must be turn-interleaved, not concurrent.** The evidence is §6.3: the global survival tick is
attached to the turn, not to a player. A concurrent design in which each player advances their own
turn would double every hunger/thirst/pain/fatigue/regen application, and would also double-advance
the world clock via `scr_timeUpdate`.

The workable shape is a **shared turn clock**: a turn advances when the acting player acts, and the
other player is either (a) blocked from acting until the turn resolves, or (b) allowed to queue an
action that is applied within the same turn. `scr_unitTurnGetTime`'s existence — per-action time cost
rather than fixed turns — means (b) is structurally plausible: a second player could be inserted into
the unit order as another time-cost participant, much as NPCs already are.

**UNVERIFIED**: whether the unit turn order is a data structure that can accept an extra participant
was not established. `scr_unitTurnNext` (`0x141699BE0`) is the function to read next; it is only
`0x4D7` bytes, so it should be tractable.

`scr_unitTurnCheckPlayerTurn` (`0x1416A73D0`) exists but has **zero call sites** — dead code. It is
suggestively named for this problem, but nothing calls it, so it cannot be assumed to work.

---

## 7. Concrete next implementation steps, ordered

Each step is independently testable. Steps 1–3 are the load-bearing ones; everything after depends on
them.

### Step 1 — Resolve the builtin table (`src/symbols.cpp`)

Add a `BuiltinResolver` alongside the existing script-table resolver:

1. Scan `.rdata` for the NUL-delimited literals `network_create_socket`, `buffer_create`,
   `random_set_seed`, `json_encode`, `date_get_year`. Each occurs exactly once in the current build.
2. For each, scan `.text` for `lea rcx,[rip+d32]` (`48 8D 0D`) resolving to that string; take the
   following `call rel32` (`E8`) — **and accept `E9` as well**, because the last registration in a
   block is a tail call.
3. Require **unanimous consensus** on the resulting `Builtin_Add` address. Fail loudly otherwise.
4. Parse `Builtin_Add`'s first ~0x40 bytes for its rip-relative globals: the qword-read one
   (`48 8B 05 <d32>`) is `g_pBuiltInFunctions`; the int32 compared against capacity is the count.
   Sanity-check that count and capacity land at `ptr + 8` and `ptr + 0xC`.
5. Walk `RFunction[80]` entries (`char name[64]; TRoutine f; int argc; int id`) and build a
   `name -> {f, argc}` map.

Health check (mirror the existing resolver's style): count in 1,500–6,000; every name printable ASCII
and NUL-terminated within 64 bytes; every `f` inside `.text`; and an arity spot-check —
`network_create_socket`=1, `network_send_udp`=5, `buffer_copy`=5, `date_create_datetime`=6,
`buffer_get_size`=1. Expected count in the current build: **2,533**.

Resolve lazily on first use, not at DLL load — the array is populated during runner startup.

### Step 2 — Add a builtin invoker (`src/gml.cpp`)

Do **not** reuse the script caller. The builtin ABI is different (§2.4):

```c
typedef void (*TRoutine)(RValue* result, CInstance* self, CInstance* other,
                         int argc, RValue* args);   /* args is a CONTIGUOUS array */
```

Result is the **first** parameter; `args` is `RValue*`, not `RValue**`. Passing the script-style
array-of-pointers will dereference argument values as pointers and crash.

Verify with three cheap, side-effect-free calls before trusting it:
`buffer_create(64, 1, 1)` -> a positive id; `buffer_get_size(id)` -> 64; `buffer_delete(id)`.
Then `random_get_seed()` -> a real. If those three pass, the ABI is right.

### Step 3 — Hook the async data path (new `src/net.cpp`)

Resolve `sub_145348230` by pattern, not address (see §7.1), and hook it:

```c
void NetAsyncData(int socket_id /*ecx*/, int buffer /*edx*/,
                  int size /*r8d*/, void* peer /*r9*/);
```

In the hook: call `buffer_get_address(buffer)`, `memcpy` `size` bytes into the mod's own queue, then
call the original. **Copy inside the hook** — the async event owns the buffer and the runner frees it
after dispatch, and nothing consumes the event because no object has `Other_68` (§3.1).

Optionally also hook `sub_145348140` (connect/disconnect) and `sub_1453483A0` (non-blocking connect
result) for connection lifecycle.

Do **not** hook `CreateAsyncEventWithDSMap` (`0x1452FFC90`) — it is shared with audio, social,
save/load and system events.

### Step 3.1 — How to find those three by pattern

They are unnamed runner code, so there is no symbol. The stable anchors available:

- All three reference the string `"SocketMutex"`, and the data builder additionally references the
  key set `type`, `id`, `buffer`, `size`, `ip`, `port`, `message_type`. Anchoring on the
  **`message_type` string** is the most selective single test — `sub_145348230` is the only network
  function that uses it.
- Alternative anchor: find the two `E8`/`E9` sites that call `CreateAsyncEventWithDSMap` with
  `edx = 68`, and the one that calls `CreateAsyncEventWithDSMapAndBuffer` with `r8d = 68`; walk up to
  the enclosing function start via `.pdata` (the exception directory is present in memory and
  reachable from the PE headers at runtime — 556,610 `RUNTIME_FUNCTION` entries).
- `CreateAsyncEventWithDSMap` itself is reachable by name: `event_perform_async` is in the builtin
  table (`0x1451EBD60`) and its final instruction is `jmp` to it. That is a **name-anchored** route to
  an unnamed function, and it is the cleanest one available — use it.

Recommended chain: builtin table -> `event_perform_async` -> its trailing `jmp` -> dispatcher ->
scan its call sites for the `68` constant -> `.pdata` to get enclosing function starts -> confirm by
checking the enclosing function references `message_type` (data) or `succeeded` (connect result) or
`other_port` (connect/disconnect). Every link in that chain is a name or a string, never an address.

### Step 4 — Transport bring-up

Host: `network_create_server(type, port, maxclients)` — note `argc = 3`.
Client: `network_create_socket(type)` then `network_connect(socket, url, port)`.
Packets: `buffer_create` / `buffer_get_address` + `memcpy` / `network_send_packet(socket, buf, size)`.

**Constants must be raw numbers.** The GML constant names (`network_socket_tcp`,
`network_type_data`, `network_config_*`) are **absent from the binary** — YYC folds them at compile
time. The only values this analysis confirmed are the runtime's async `type` codes: **data = 3**,
**non-blocking connect = 4** (§3.3). Socket-type and config values are **UNVERIFIED** and will have to
be established by experiment.

Optional: `network_set_config` is variadic (`argc = -1`) and validates its parameter — it emits
`"network_set_config : unknown parameter"` from `0x145346C8D`, which makes a bad value easy to
recognise in a debugger.

### Step 5 — Second player representation

Under the recommended host-authoritative model, **do not spawn a second `o_player` on the host** —
that is what breaks the 176 singleton dereferences (§4.6). Instead:

- Host keeps its single `o_player`.
- The remote player is represented by a **different object** on each machine. `o_player_observer`
  (index 1796, events `Create_0` / `Step_0` / `Draw_64` / `CleanUp_0`) already exists and is
  referenced by only one function — it is the natural candidate for a lightweight remote avatar.
  **UNVERIFIED**: its actual purpose was not determined; the name is suggestive but the code was not
  read.
- `scr_load_player` (`0x141A4AE70`) is the existing entry point for instantiating a playable class
  (called from the `Create_0` of `o_dervish`, `o_agemon`, `o_beastslayer`, `o_verren`, `o_reaver`,
  `o_revenger`, `o_knight_maiden`, `o_runaway_wizzard`, `o_woodward`). It is the right thing to study
  before building character instantiation by hand.

### Step 6 — State replication

Enumerate with `variable_instance_get_names(player)` (`0x1451EE150`) at runtime, then read/write by
name with `variable_instance_get`/`_set`. Diff per turn boundary rather than per frame — the turn is
the natural sync tick (§6), and hooking `scr_allturn` (`0x1416C1E60`) gives exactly one well-defined
moment per turn from all 98 of its callers at once.

### Step 7 — Turn arbitration

Hook `gml_Script_scr_allturn` (`0x1416C1E60`). It is small (`0x5DB`), has 109 call sites converging on
it, and is the single point where "the world advances one turn" happens. Gate it so that only the
authority advances the turn, and so that a turn advances **once** per round regardless of how many
players acted — otherwise every survival stat in `scr_global_turn` ticks twice (§6.3).

---

## 8. Open questions and dead ends

### 8.1 Dead ends — recorded so nobody spends time on them again

1. **The `network_*` strings at `0x147D00C48..0x147D00DC8` are not a table.** Their apparent regular
   0x38 stride is a coincidence of string lengths plus 8-byte `.rdata` alignment. Structure-scanning
   there finds nothing.
2. **`.data` `{name_ptr, func_ptr}` scanning cannot reach builtins.** The builtin name is stored
   *inline* in the 80-byte `RFunction` entry, and the array is heap-allocated, not in `.data`. The
   earlier zero-result was correct; the conclusion to draw was "wrong container", not "unreachable".
3. **A registration scanner that only looks for `E8` misses `network_set_config`** — it is registered
   by a tail call (`jmp`, `E9`) at `0x1453473DB`. This cost one iteration during this analysis.
4. **Do not call a builtin with the YYC script ABI.** Different parameter order, and `RValue*` vs
   `RValue**` for `args` (§2.4).
5. **`gml_GlobalScript_X` is not the body — `gml_Script_X` is.** Every `gml_GlobalScript_*` symbol is
   an identical `0xC6`-byte trampoline that references its own name string. Reading callees of
   `gml_GlobalScript_scr_global_turn` yields nothing useful; `gml_Script_scr_global_turn`
   (`0x1416C2A60`, `0x30E2` bytes) is the real code. The `.data` rows are
   `{char* name, void* func, void* slot}` = 24 bytes, and both names appear as separate rows pointing
   at different functions.
6. **Caller counts on registered builtin addresses are always 0 and mean nothing** (§5.4). YYC calls
   the runner's internal helpers directly, not the GML-facing wrappers.
7. **`sub_1451A4330`** — the function carrying `"Unable to find instance for object index %d"` — has
   **0 direct callers**. It is dispatch-reached. It is not a useful hook or anchor.
8. **`gml_Script_scr_unitTurnCheckPlayerTurn` (`0x1416A73D0`) is dead code** — 0 call sites. The name
   is very suggestive for this project; the function is not wired up. Do not build on it.
9. **`obj_gmlive_Other_68` is a stub**, not a working async networking handler (§3.1). GMLive is
   compiled into the release build but its socket path is empty; its live-reload transport here is
   HTTP (`obj_gmlive_Other_62`, `0x1445F3BA0`).
10. **`saveSelfData` / `loadSelfData` are the mob/NPC serialiser, and `scr_load_player` is
    character-class instantiation** — neither is the player save path. `scr_savegame`
    (`0x14104C210`) is, and it is called only from `o_smoothRoomChanger_Other_15/16/17` (§4.4).
11. **The GML `network_*` / `network_socket_*` / `network_config_*` constant names are not in the
    binary.** Searched, zero hits. YYC folds constants to numeric literals. Their values must come
    from experiment or documentation, not from the executable.
12. **`saveSelfData` has no field-name strings** — the save format is a positional array over numeric
    variable ids, not a keyed map. It is not self-describing.

### 8.2 Open questions

**High priority**

- **Socket-type and config constant values.** `network_create_socket(type)` and
  `network_set_config(param, value)` need numbers this analysis could not confirm (§8.1 item 11). Only
  the async `type` codes are known (data = 3, non-blocking connect = 4), and those are one higher than
  the values usually quoted for GML's `network_type_*`. Establish these experimentally first — they
  gate all transport work.
- **Is the unit turn order a data structure that accepts an extra participant?** Read
  `gml_Script_scr_unitTurnNext` (`0x141699BE0`, only `0x4D7` bytes) and `scr_unitTurnGetTime`
  (`0x1416A2830`, `0x2A5`). This determines whether a second player can be inserted into the existing
  order the way NPCs are, or whether turn arbitration has to be bolted on outside.
- **What is `o_player_observer` (index 1796)?** Four events; referenced by only one function. If it is
  what the name suggests, it is a ready-made remote-avatar object. Not read.

**Medium priority**

- **The connect/disconnect `type` values.** `sub_145348140` takes them dynamically in `r10d`; the four
  call sites (`0x14538F30C`, `0x14538F78D`, `0x14538F8CB`, `0x14538FEED`) load them from variables,
  not immediates, so a deeper trace is needed. Inferred 1 and 2 from data = 3 / non-blocking = 4, but
  **UNVERIFIED**.
- **`network_send_broadcast` arity.** Registered `argc = 3`; the GM manual documents four parameters.
  Which one is dropped in this runtime is unknown.
- **`sub_1451C3EC0`'s exact identity** (429 of the 910 `o_player` reference sites, 1,305 global
  callers). Known to be an instance lookup/iteration helper that compares against the `all` sentinel
  `-3` and returns a bool; the GML-level function it backs was not pinned down.
- **`sub_14538DFB0` ("RCV : ")** — the raw receive function one level below the data builder. Its
  signature was not decoded. Hooking it would give bytes before buffer allocation, which may be
  cheaper, but the builder is the better-normalised hook.

**Lower priority / larger**

- **A variable-id -> name table for user (non-builtin) instance variables was not found.** The builtin
  *variable* registrar is `0x1451C23D0` (224 call sites, capacity capped at `0x1F4` = 500), which
  covers only GameMaker's own built-in variables. `data.win` has no `VARI` chunk. This matters only if
  a static field map is wanted; the runtime route via `variable_instance_get_names` (§4.1) makes it
  unnecessary, which is why it was not pursued further.
- **Bandwidth budget.** Needs `variable_instance_names_count` on a live `o_player`; requires running
  the game.
- **How the async event queue is drained**, and whether an unconsumed `Other_68` event leaks the
  `ds_map` or the buffer over a long session. The dispatcher allocates a 12-byte record plus a
  0x68-byte node per event (`0x1452FFC90`); the cleanup path was not traced. If the mod is generating
  hundreds of packets per second, this is worth confirming before shipping.

### 8.3 Tooling produced by this pass

Added to `tools/re/` alongside the existing scripts (no existing file was overwritten). The set is
self-contained: `relib.table()` rebuilds the 34,167-entry symbol table straight from the exe if no
`script_table.json` is present, and caches are written next to the scripts (override with the
`RELIB_CACHE` / `RELIB_TABLE_DIR` environment variables).

| file | what it does |
|---|---|
| `relib.py` | PE sections, VA/offset mapping, string search, symbol table |
| `idx.py` | numpy-built global xref indexes — 510,126 rip-relative `lea`, 1,815,266 `E8` calls, 70,804 `E9` jmps; builds in <1 s and caches to `.npy` |
| `pdata.py` | function bounds for **all 556,610** functions from `.pdata`, including unnamed runner C++ (33,275 of them match a `gml_` symbol) |
| `builtins.py` | decodes all 2,535 `Builtin_Add` sites -> `builtin_table.json` (2,533 names, func VA, argc) |
| `datawin.py` | `data.win` chunk parse preserving asset **index** alignment (`objt_indexed.json`) |
| `assetref.py` | counts compiled asset-index immediates per function — the §4.6 singleton metric |
| `who.py` / `callees.py` / `strings_in.py` / `slot.py` | symbol-aware caller, callee, string and `.data`-row inspection |
| `q34.py` | reproduces the §4 and §6 measurements in one run |

The existing `tools/re/whocalls.py` works, but only against `gml_Script_*` symbols — against
`gml_GlobalScript_*` it reports 0 for everything (§8.1 item 5), and against runner code it
mis-attributes every address to `gml_Script_angle_normalize`, which is simply the highest-addressed
symbol in the table (`0x145194AB0`). Runner C++ lives **above** that address and is entirely unnamed;
use `.pdata` bounds there instead.

---

## 9. Architecture decision — host-authoritative with per-location client leases

**Decided 2026-09-08.** This section is a decision record, not research. It supersedes any
reading of §5 that implies lockstep is still open.

### 9.1 The decision

**Host-authoritative, with per-location client leases while players are apart, and
deterministic generation throughout. All saves live on the host.**

- **Together** (both players in the same location): the host simulates that location. The
  client sends *input intents*; the host sends back entity state. The client's process still
  renders locally — it is not a thin client.
- **Apart** (different locations): the client holds a **lease** on its own location and
  simulates it locally. The host remains the store of record and takes the serialised
  location blob back on transition.
- **Generation** is deterministic from `(world seed, location id)` in both modes, so a
  location neither player has visited generates identically wherever it is first entered.
  This needs no protocol.

### 9.2 Why the engine forces this shape

A GameMaker process holds **exactly one live location**. Entities are instantiated on entry
and serialised back to data on exit — `scr_locationRoomEntityContainersInstanceCreate` /
`...InstanceDestroy`, `scr_locationRoomEntityMobsSaveDataGet/Set`, `scr_locationRoomDelete`
(165 location scripts total; only 20 GM rooms against 8,897 room-creation-code blocks, so
locations are generated data, not rooms).

The host therefore **cannot** simulate two dungeons at once. Strict server authority would
force tethered co-op — both players always in the same location. Leases exist purely to allow
separated play without asking the host to do something the engine cannot do.

### 9.3 Why not lockstep / deterministic simulation

Floating point is **not** the reason — a single x86-64/SSE2 binary is bit-reproducible. The
reasons are structural:

1. **No input boundary to capture.** `scr_allturn` (`0x1416C1E60`) is reached from **98
   distinct callers**; there is no single funnel through which player intent passes.
2. **Iteration order is a live hazard.** `scr_global_turn` (`0x1416C2A60`) has **29 `with`
   setups`**; ordering depends on instance-id allocation and activation state.
3. **The turn tick is global, not per-player** — survival stats are applied once per turn for
   the whole world.

And decisively: **when players are apart there is nothing for determinism to protect.** Each
machine holds one live location; the other player's dungeon is a blob, not a simulation. Two
independent locations cannot observably diverge. Determinism keeps two computations *of the
same thing* in agreement, and while apart there is no same thing. Paying for lockstep
machinery there would buy a property nobody can observe.

Determinism keeps exactly one job: **generation** (§9.1).

### 9.4 Protocol shape

- **Client -> host:** input intents — move direction, attack target, use item, interact.
  Never positions; the host derives those.
- **Host -> client:** entity state for the shared location (position, health, death) plus
  world state (time, quest flags).
- **Application on the client:** write received values onto local instances by **name**,
  through the reflection API (`variable_instance_set`, reachable via the builtin registry,
  §2). No offset maps, so this survives game updates.
- **Sync tick:** the turn boundary, not the frame. Hooking `scr_allturn` gives exactly one
  well-defined moment per turn from all 98 callers at once (§6).

### 9.5 Transition rules — where the bugs will be

The two modes are straightforward; the **transitions** are the design risk.

| event | rule |
|---|---|
| **Split** (one player leaves a shared location) | Host retains the shared location. The departing player's new location starts from its blob under a fresh lease. |
| **Merge** (a player enters the host's location) | The arriving client **discards** its local state for that location and adopts the host's live state wholesale. Host state always wins. |
| **Handback** (a leased player leaves their location) | The client serialises the location and sends the blob to the host. The host never simulated it, so it must either trust or validate it. |
| **World time** | Both clocks advanced independently while apart. On merge, world time becomes **max** of the two. A player who idled is pulled forward. |
| **Quest flags / shared stash** | Need explicit conflict rules. **Open — not yet designed.** |

### 9.6 Consequences accepted

- The client is not running its own game while together — it renders the host's simulation.
  That is the cost of removing divergence, and it is deliberate.
- While apart, the two worlds are genuinely divergent; merging is a **reconciliation**, not a
  resync.
- Host disconnect ends the session for everyone; there is no migration.
- Save integrity depends on the host. Client-side leases mean the host accepts location blobs
  it did not compute.

### 9.7 Rejected alternatives

- **Lockstep / fully deterministic simulation** — §9.3.
- **Tethered co-op (shared location always)** — simpler and fully server-authoritative, but
  removes separated dungeons, which is a stated requirement. Still a legitimate fallback if
  leases prove too costly.
- **Distributed enemy ownership** ("each machine owns some enemies") — does not simplify.
  Both players must still see them, so their state is synced regardless; it replaces single
  authority with distributed authority, which is harder.
