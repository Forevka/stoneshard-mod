---
title: Runtime bridge
description: How the native loader builds GameMaker values, finds the runtime's string, copy and free helpers, proves them on the game thread, and calls into GML without letting a GML error escape.
---

`src/gml.cpp` is the native loader's bridge into the game's compiled GML. Everything that hands a value to the game or takes one back goes through it: building strings, copying and releasing values, finding a `self` instance to call with, and turning a failed call into a readable error.

None of the helpers it uses has a name in the executable. YYC strips the runtime's own symbols and keeps only the `gml_*` functions (see [GML functions](./gml-functions.md)). So each helper is found the same way: vote across thousands of compiled scripts for the code shape that uses it, check the winner's body structurally, and then prove it by behaviour on the game thread before anything else may call it. A helper that fails any step is treated as absent.

## The RValue {#rvalue}

Every GML value is a 16-byte `RValue`: an 8-byte payload, a flags word and a kind. The layout was read off the compiled code, where every local starts life as `mov qword [x],0` / `mov dword [x+0xC],0xffffff`:

```cpp
// 16 bytes: value at +0, flags at +8, kind at +0xC. Confirmed from the
// compiled locals (`mov qword [x],0` / `mov dword [x+0xC],0xffffff`) and from
// YYSetString writing kind 1 at +0xC.
struct RValue {
    union {
        double         real;
        void*          ptr;
        std::int64_t   i64;
        std::int32_t   i32;
    };
    std::int32_t flags;
    std::int32_t kind;
};
static_assert(sizeof(RValue) == 16, "RValue must be 16 bytes");
```

<small>Source: [src/gml.h](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.h#L24-L37)</small>

The kinds the loader understands:

| Kind | Value | Payload |
|---|---|---|
| `kReal` | 0 | `double` |
| `kString` | 1 | pointer to a `RefString` |
| `kArray` | 2 | pointer to a reference-counted array |
| `kPtr` | 3 | raw pointer |
| `kUndefined` | 5 | none |
| `kObject` | 6 | pointer to a struct (garbage-collected) |
| `kInt32` | 7 | low 4 bytes |
| `kInt64` | 10 | 8 bytes |
| `kBool` | 13 | usually a 0.0/1.0 `double` |
| `kRef` | 15 | a typed handle: id in the low half, what it refers to in the high half. It cannot be built from scratch. |
| `kUnset` | `0x00FFFFFF` | the "never assigned" marker the compiled code writes into fresh locals |

The runtime masks the kind with `0xFFFFFF` before switching on it (the top byte carries other bits), and the loader masks it the same way where it matters most: freeing and copying values, handling structs, building error text and parsing ids. A few comparisons against a specific kind (`ToString`, `SetString`, the builtin self-test) compare unmasked.

## Strings are borrowed, not copied {#strings}

A string value points at a small header the runtime allocates:

```cpp
// What YYSetString builds. `chars` is stored WITHOUT copying; the high bit of
// `length` marks the buffer as external, so our own storage must outlive the call.
struct RefString {
    const char*  chars;
    std::int32_t refCount;
    std::int32_t length;   // | 0x80000000 when chars is external
};
```

<small>Source: [src/gml.h](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.h#L39-L45)</small>

`YYSetString(RValue* dst, const char* src)` allocates that header but keeps your `chars` pointer as it is. If a script stores its argument in a variable, the variable now points at your buffer, possibly for the rest of the game. A string built from a stack buffer or a temporary `std::string` becomes a dangling pointer the moment the caller returns.

So anything handed to `SetString` comes from static storage or from `gml::Intern`, a process-lifetime pool that never moves its strings:

```cpp
const char* Intern(const std::string& text) {
    static std::mutex lock;
    static std::unordered_set<std::string> pool;   // node-based: c_str() never moves
    std::lock_guard<std::mutex> g(lock);
    return pool.insert(text).first->c_str();
}
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L613-L618)</small>

The pool is deduplicated, so the same text costs memory once however often it is sent.

## Finding YYSetString {#yysetstring}

`gml::Init` samples the first 4000 `gml_Script_*` functions and looks at up to 2048 bytes of each (`kSampleSize`, `kScanWindow`). Three tallies run over the same sample: string construction, the current-self store and every direct call. The scan reads memory only, so it runs on the loader's init thread, long before the game executes any GML.

### Older runtimes: literals built inside scripts {#yysetstring-old}

In Stoneshard's runtime every script that uses a string literal builds it on the spot:

```nasm
48 8D 15 xx xx xx xx    lea  rdx, [rip+d32]   ; -> a printable string in .rdata
...                                           ; up to ~20 bytes of argument setup
E8 xx xx xx xx          call YYSetString
```

`TallySetString` finds each `lea rdx,[rip+d32]` whose target is a printable `.rdata` string of at least 3 characters, then takes the first `call rel32` within the next few bytes and gives its target one vote:

```cpp
// 48 8D 15 d32  =  lea rdx, [rip+d32]
if (!(b[0] == 0x48 && b[1] == 0x8D && b[2] == 0x15)) continue;

std::int32_t disp;
std::memcpy(&disp, b + 3, 4);
const std::uintptr_t strTarget = at + 7 + static_cast<std::intptr_t>(disp);
if (!PrintableStringAt(strTarget, 3)) continue;

// Look a short way ahead for the consuming call.
for (std::size_t j = 7; j < 28 && Readable(at + j, 5); ++j) {
    const auto* c = reinterpret_cast<const unsigned char*>(at + j);
    if (c[0] != 0xE8) continue;
    std::int32_t rel;
    std::memcpy(&rel, c + 1, 4);
    const std::uintptr_t target = at + j + 5 + static_cast<std::intptr_t>(rel);
    if (sym::TextRange().contains(target)) ++votes[target];
    break;
}
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L92-L109)</small>

A single site proves nothing: the string could feed a logging helper, a variable lookup, or a builtin. Across a few thousand scripts the string constructor dominates. In Stoneshard the winner collects 189 votes.

### 2024 runtimes: static string initialisers {#yysetstring-2024}

The 2024 runtime moved literals out of scripts. Each literal is a static `RValue` in `.data`, built once at startup by a tiny initialiser:

```nasm
48 8D 15 xx xx xx xx    lea  rdx, [rip+str]     ; printable .rdata string
48 8D 0D xx xx xx xx    lea  rcx, [rip+value]   ; the static RValue in .data
E8 xx xx xx xx          call YYSetString
```

The two `lea`s may come in either order. When the in-script tally produces no validated candidate, `Init` falls back to `TallyStaticStringInits`, which walks the whole of `.text` for exactly this 19-byte shape and requires the string in `.rdata`, the value in `.data` and the call target in `.text`:

```cpp
// Votes narrow the field; the structural check picks the winner, so a thin
// margin between similar string helpers cannot pick the wrong one.
std::uintptr_t setStr = BestValidated(strVotes, &LooksLikeSetString, "YYSetString");
if (!setStr) {
    Logf("gml: no in-script string construction; trying static string initialisers");
    std::unordered_map<std::uintptr_t, int> initVotes;
    TallyStaticStringInits(initVotes);
    setStr = BestValidated(initVotes, &LooksLikeSetString, "YYSetString (static init)");
}
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L341-L349)</small>

On Dwarf Eats Mountain the in-script tally still produces candidates (206 and 16 votes), but both are rejected by the structural check; the static initialiser tally then finds the real constructor with 6391 votes. That is why the order matters: votes rank, validation decides.

### The structural check {#looks-like-set-string}

A string constructor must write `kString` (1) into the `kind` field at `+0x0C` of the `RValue` it was handed. That store has a fixed shape whatever base register the compiler picked, except `rsp` and `r12`: with those, rm=100 means a SIB byte follows and the displacement moves one byte later, so the check does not match them.

```nasm
C7 4x 0C 01 00 00 00    mov dword ptr [reg+0Ch], 1   ; mod=01, reg=000, rm=base (not rsp/r12), disp8=0Ch
```

```cpp
bool LooksLikeSetString(std::uintptr_t fn) {
    for (std::size_t i = 0; i + 7 < 0x140; ++i) {
        if (!Readable(fn + i, 7)) return false;
        const auto* b = reinterpret_cast<const unsigned char*>(fn + i);
        if (b[0] != 0xC7) continue;
        if ((b[1] & 0xF8) != 0x40) continue;   // mod=01, reg=000, rm=any base
        if (b[2] != 0x0C) continue;            // displacement = RValue.kind
        std::int32_t imm;
        std::memcpy(&imm, b + 3, 4);
        if (imm == kString) return true;
    }
    return false;
}
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L241-L253)</small>

`BestValidated` ranks candidates by votes and returns the first of the top 8 that passes. If none passes, `Init` fails with "could not resolve the string constructor" and the bridge stays down. Without a string constructor no variable name can be passed to a builtin, so continuing would only produce crashes.

The constructor is proven by behaviour later, on the game thread: `AbiSelfTest` builds `"coreloader"` and reads it back (see [Self-tests](#self-tests)).

## The current-self global {#current-self}

Every script and builtin takes a `self` instance, and some dereference it even when the GML never mentions `self`. The loader has no instance of its own, so it borrows the one the game last ran code as.

Older runtimes keep it in a global that every script prologue writes:

```nasm
48 89 0D xx xx xx xx    mov [rip+d32], rcx    ; rcx = self, the first argument
```

`TallyCurrentSelf` counts such stores into `.data` within the first `0x100` bytes of each sampled script. `Winner` accepts the leader only with at least 40 votes and at least twice the runner-up, because a weak consensus means the pattern changed and the loader must not guess:

```cpp
if (bestN < minVotes || bestN < secondN * 2) {
    Logf("[!] gml: %s consensus too weak", what);
    return 0;
}
return best;
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L230-L234)</small>

In Stoneshard the global wins with 4098 votes and no runner-up. In the 2024 runtime `self` stays in a register and the tally is empty. That is not fatal: `CurrentSelf()` then falls back to an instance observed by the loader's own hooks on object Step and Draw events (see [Hook engine](./hook-engine.md) and [Runtime differences](./runtime-differences.md)).

Even a winning global is not trusted blindly. A wrong winner would hand out pointers that scripts then write instance variables through. A `CInstance` is a C++ object whose first word is a vtable in the image's `.rdata`, so every read checks that:

```cpp
bool LooksLikeInstance(void* p) {
    std::uintptr_t vtable = 0;
    return p && SafeRead(p, &vtable, sizeof(vtable)) && sym::RdataRange().contains(vtable);
}

void* CurrentSelf() {
    if (g_pCurrentSelf) {
        void* s = *g_pCurrentSelf;
        if (s && LooksLikeInstance(s)) return s;
    }
    return g_observedSelf.load(std::memory_order_relaxed);
}
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L1037-L1048)</small>

The observed instance is cleared at the end of every frame (`ClearObservedSelf`), so what `CurrentSelf()` returns was seen running during the frame in progress, never an instance destroyed by a room change long ago.

## Finding the copy and free helpers {#free-copy}

A string, array or struct value holds a reference. The runtime's `FREE_RValue(v)` drops it, and `COPY_RValue(dst, src)` makes `dst` a second owner. The managed side needs both: to release the values it pools each frame, and to keep a value across frames (see [values](../modding/concepts.md#values)).

They are among the most-called functions in any YYC game, which is what makes them findable without names. `TallyCalls` counts every `E8 rel32` target in the sampled scripts. `BestValidatedQuiet` then tries only the 64 most-called targets against a structural check.

Both helpers mask the kind before switching on it, either as `and eax,0FFFFFFh` (`25 FF FF FF 00`) or `mov eax,0FFFFFFh` (`B8 FF FF FF 00`) within the first `0x30` bytes. Then they differ in which argument they inspect:

| Helper | Reads kind of | Bytes required | Extra |
|---|---|---|---|
| `FREE_RValue(RValue*)` | first argument | `8B 41 0C` `mov eax,[rcx+0Ch]` or `23 41 0C` `and eax,[rcx+0Ch]` | `F6 4x 08 08` `test byte [reg+8],8`, the reference-flag test, within `0x40` bytes |
| `COPY_RValue(dst, src)` | second argument | `8B 42 0C` `mov eax,[rdx+0Ch]` or `0F 10 02` `movups xmm0,[rdx]` | none |

```cpp
// FREE_RValue(RValue*): reads the kind of its FIRST argument, [rcx+0Ch], and
// tests the reference flag bit (flags & 8 at +8) of a pointer-kind value -
// `test byte [reg+8],8` = F6 4x 08 08.
bool LooksLikeFree(std::uintptr_t fn) {
    if (!HasKindMask(fn)) return false;
    static const unsigned char kindRcx1[] = {0x8B, 0x41, 0x0C};   // mov eax,[rcx+0C]
    static const unsigned char kindRcx2[] = {0x23, 0x41, 0x0C};   // and eax,[rcx+0C]
    if (!Contains(fn, 0x20, kindRcx1, 3) && !Contains(fn, 0x20, kindRcx2, 3)) return false;
    for (std::size_t i = 0; i + 4 <= 0x40; ++i) {
        if (!Readable(fn + i, 4)) return false;
        const auto* b = reinterpret_cast<const unsigned char*>(fn + i);
        if (b[0] == 0xF6 && (b[1] & 0xF8) == 0x40 && b[2] == 0x08 && b[3] == 0x08) return true;
    }
    return false;
}
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L175-L189)</small>

These are not required for the bridge. A miss is logged ("value freeing unavailable") and the managed API reports the capability as absent instead of failing `Init`. In both test games `FREE_RValue` is the single most-called function (63867 and 67451 calls in the sample).

## Proving them: VerifyValueLifetime {#verify-value-lifetime}

A shape match is a strong hint, not a proof. A wrong free helper corrupts the heap the first time a mod releases a value, so `VerifyValueLifetime` exercises both helpers once, on the game thread, on the first frame and before any mod runs. Until it passes, `CanFreeValues()` and `CanCopyValues()` return false and `FreeValue`/`CopyValue` refuse.

The steps, in order ([src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L466-L539)):

1. **Build a probe string** from static storage, `"coreloader-lifetime-probe"`.
2. **External flag.** Its `RefString` must point at the static characters and have the high bit of `length` set. Otherwise freeing any string the loader built around its own buffer would free that buffer.
3. **Initial refcount** must be 1.
4. **Copy.** Copy the probe into an undefined `dst`. The copy must share the pointer and raise the refcount by exactly one. Then free the copy, and the refcount must come back down.
5. **Plain values copy verbatim.** Copy a real, int32, int64, bool, pointer and undefined whose payload points at a zeroed canary. Each must come out bit for bit, and the canary must stay zero. A helper that bumps "+8 of whatever the payload points at" without checking the kind would pass the string test and corrupt memory on the first number.
6. **Free without a verified copy.** If the copy was rejected, take a second reference by hand (the probe is the loader's alone) and check that free drops it.
7. **Plain values free harmlessly.** The same canary test for free.
8. **Release the probe**, after adding a spare reference so its count never reaches zero. Reaching zero would be the first time the helper frees a string whose characters are static data, and honouring the external flag is exactly what is not proven yet. The 16-byte header leaks once.

A failed step disables freeing and copying: values then leak instead of corrupting memory.

### COPY_RValue__Post {#copy-post}

In the 2024 runtime `COPY_RValue` is inlined at its call sites. What survives as a callable function is only its reference half, `COPY_RValue__Post`, which writes the pointer and takes the reference but leaves kind and flags to the inlined caller. The copy test tells the two apart by what the helper did to an undefined destination:

```cpp
if (g_copy) {
    RValue b{};
    b.kind = kUndefined;
    __try { g_copy(&b, &a); } __except (EXCEPTION_EXECUTE_HANDLER) { fail("copy faulted"); return; }
    const bool tookRef = b.ptr == a.ptr && RefCountOf(a) == rc0 + 1;
    if (tookRef && b.kind == kUndefined) {
        g_copyIsPost = true;   // the reference half only; CopyValue writes kind/flags itself
        b.flags = a.flags;
        b.kind  = a.kind;
    }
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L493-L502)</small>

When `g_copyIsPost` is set, `CopyValue` copies the whole 16 bytes first and lets `__Post` take the reference. Otherwise it starts `dst` as undefined, so the full `COPY_RValue` never frees whatever `dst` held before. The log shows which one was found: `copy yes` on Stoneshard, `copy yes, reference half` on Dwarf Eats Mountain.

## Structs are collected, not counted {#structs}

Strings and arrays are reference-counted; structs (`kObject`) are garbage-collected. There is no reference to drop, and the runtime's struct helpers consult the GC context of the GML code that is running, which does not exist at `Present` where most mod code runs. So `FreeValue` only clears a struct value, and `CopyValue` copies it bit for bit:

```cpp
// Structs are garbage-collected, not counted: there is no reference to
// drop, and the runtime's helpers for them expect to run inside the game's
// own code (they consult the GC's context).
if ((v.kind & 0x00FFFFFF) == kObject) {
    v.i64 = 0; v.flags = 0; v.kind = kUndefined;
    return true;
}
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L546-L552)</small>

That leaves one problem: a struct pointer held only in C# is invisible to the collector, which frees the struct as soon as GML stops using it. The managed side solves it in `Values.Keep`: a kept struct is pushed into a GML array stored in the global `__coreloader_roots`, where the collector sees it, and `Values.Free` takes it out again. The managed side also remembers which mod rooted what, so a mod that unloads loses its roots, and roots wiped with the game's globals are pushed again ([managed/CoreLoader/Values.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Values.cs#L60-L89)).

## Self-tests on the game thread {#self-tests}

GML may only be called from the game's own thread. The first `Present` records it (`NoteGameThread`); before that, `OnGameThread()` is false everywhere, so nothing can call in. The per-frame tick in `OverlayRender` then runs the self-tests after frame 120, and `VerifyValueLifetime` on every frame until it settles (the tick is quoted on the [Overlay](./overlay.md#frame-tick) page).

The 120-frame delay gives the game time to run its own code, so a borrowed `self` exists. Each test retries next frame while `CurrentSelf()` is still null.

| Test | What it proves | Pass condition |
|---|---|---|
| `gml::AbiSelfTest` | the string constructor and `RefString` layout, end to end | `"coreloader"` built with `SetString` reads back unchanged through `ToString` |
| `builtins::SelfTest` | the builtin calling convention and registry ([Builtins](./builtins.md)) | `buffer_create(64,1,1)`, then `buffer_get_size` on its result returns exactly 64; `random_get_seed` returns a real. At least 2 of 3 points. |
| `gml::VerifyInstanceLookup` | the id-to-instance table | see below |

The buffer test accepts either a number or a kind-15 handle from `buffer_create` and passes it straight back, because older runtimes return a buffer index and 2024 runtimes a typed handle.

### The instance id table {#instance-lookup}

GML hands out instance ids, but scripts need the `CInstance*`. The runtime keeps every instance in a hash map keyed by id, and no function wraps the lookup: it is inlined wherever an id is resolved. So the loader locates the map itself from the shape all those inlined copies share:

```nasm
cmp    id, 100000              ; ids below are object indices
jge    ...
movsxd rcx, dword [rip+M]      ; mask    (map + 8)
mov    rax, qword [rip+B]      ; buckets (map + 0)
and    rcx, id
add    rcx, rcx
mov    rdx, [rax+rcx*8]        ; 16-byte buckets
cmp    dword [rdx+10h], id     ; node key; next at +8, CInstance* at +18h
```

`TallyIdMap` scans `.text` for a `movsxd` from `[rip+M]` followed within 16 bytes by a qword load from `[rip+B]` with `B + 8 == M`, a key compare at `+10h` shortly after, and the constant 100000 (or 99999) shortly before. `Winner(votes, 3, ...)` takes the bucket address. The shape is identical in both test runtimes ([src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L1085-L1162)).

The proof is attempted on every 30th call (about every half second, since the frame tick calls it every frame) until it settles:

- the current self must be a live object instance (it has an `object_index`, a readable `id` of at least 100000, and `instance_exists` agrees); if not, the attempt is inconclusive and retried, up to 40 times;
- looking up self's own `id` must return the very same pointer; five mismatches mark the table wrong;
- two bogus ids (`0x7FFFFFF0` and `id ^ 0x40000000`) must find nothing;
- whatever `instance_find` returns for self's object must resolve to an instance whose `id` reads back as that same number. This also records which kind-15 reference tags name instances, so `InstanceFromId` rejects a reference to anything else.

Even after the proof, `InstanceFromId` asks `instance_exists` for every lookup, because the table also holds deactivated instances and ones being destroyed.

## Calling into the game {#calling}

Scripts and object events have different signatures, and mixing them up corrupts the stack:

```cpp
// Calls a YYC script with an explicit instance context:
//   RValue* f(CInstance* self, CInstance* other, RValue* result, int argc, RValue** args)
bool CallAs(void* func, RValue* result, RValue** args, int argc, void* self, void* other);

// Object events compile to a SMALLER signature than scripts:
//     void Event(CInstance* self, CInstance* other)
// Calling one with the 5-argument script signature corrupts the stack, so
// anything named gml_Object_* must go through here instead.
bool CallEvent(void* func, void* self, void* other);
```

<small>Source: [src/gml.h](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.h#L136-L144)</small>

Builtins use a third convention, with the result first and the arguments as a contiguous array rather than an array of pointers (see [Builtins](./builtins.md)).

Each call runs inside an `ErrorProbe` and a structured-exception guard. The guard lives in a function of its own, because `__try` cannot share a function with objects that need unwinding:

```cpp
DWORD code = 0;
bool ok;
{
    ErrorProbe probe;   // restored even if a GML exception passes through to the game
    ok = GuardedScript(reinterpret_cast<ScriptFn>(func), self, other, result, argc, args, &code);
    if (!ok) probe.Failed();
}
if (ok) return true;
AfterGuardedFault(code);
const char* owner = sym::OwnerOf(func);
Logf("[!] gml: %s failed (0x%08lX): %s", owner ? owner : "script", code, ExplainFailure(code, self));
return false;
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L951-L962)</small>

`probe.Failed()` clears the runtime's sticky array-error byte if this call set it. Nothing in the runtime ever clears that byte, because a real array error ends the game; left set, it would make the next unrelated array access raise the game's error box with the loader's stale index. How the byte is found is on the [Builtins](./builtins.md) page.

## Error capture {#error-capture}

When the GML runtime rejects a call (`Variable ... not set before reading it`, a bad index, a GML `throw`), it raises a C++ exception (code `0xE06D7363`) rather than faulting. The thrown object carries the error text, which is far more useful than "it failed".

The first `ErrorProbe` installs a vectored exception handler. It sees the throw before any frame-based handler, only acts while a guarded call is in progress on that thread, copies what it needs into thread-local storage, and always returns `EXCEPTION_CONTINUE_SEARCH`. The game's own `try`/`catch` therefore works exactly as before.

The handler decodes MSVC's throw record, with every read fault-guarded because the parameters come from whoever threw ([src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L723-L773)). It walks the `ThrowInfo` to the RTTI type names of the thrown object, most-derived first. A `.?AVYYGMLException@@` is the thrown `RValue` itself, for a runtime error a struct with `message`, `script`, `line` and more; a `std::exception` contributes its `what()` string.

After the guard has handled the failure, `ExplainFailure` turns the record into text. `ThrownText` reads the struct's members through the runtime's own `variable_struct_get`, because the struct layout is the runtime's business, and formats `message (in script, line N)`. A fault that was not a C++ throw is reported by name and code ("access violation 0xC0000005, no message recovered"). Since the `__except` skipped the exception object's destructor, `ExplainFailure` also releases the thrown value in its place, but only when the handled object is the one the probe decoded.

## A GML exception must never unwind through .NET frames {#guard-filter}

A C++ exception unwinding through managed frames kills the process. The loader therefore tracks, per thread, whether managed code is on the stack: every native-to-managed transition (the frame tick, the Mods tab, shutdown, a managed hook phase) holds a `gml::ManagedScope`. `GuardFilter` ([src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L790-L801)) is the filter of the guarded `CallAs`/`CallEvent` calls, and it has one exception to "handle everything":

```cpp
if (code == kCppException && hk::DispatchDepth() > 0 && t_managedDepth == 0)
    return EXCEPTION_CONTINUE_SEARCH;
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L797-L798)</small>

A guarded call that hits a GML exception while a hook dispatch is in progress (depth above zero) and no `ManagedScope` is open on the thread leaves the C++ throw to outer handlers: only native frames stand between it and the game, whose own `try`/`catch` may be waiting. Everything else, including any GML exception while managed code is on the stack, is handled by the guard and the call returns false. The builtin guard in `builtins.cpp` always handles.

### Stack overflow {#stack-overflow}

A guard can catch `EXCEPTION_STACK_OVERFLOW` (deep GML recursion triggered by a mod, for example), but handling it consumes the thread's guard page. Without the guard page, the next overflow kills the process with no trace. Every guard therefore calls `_resetstkoflw()` after a handled overflow:

```cpp
// After a handled stack overflow the guard page is gone; without it the next
// overflow would kill the process without a trace.
void AfterGuardedFault(DWORD code) {
    if (code == EXCEPTION_STACK_OVERFLOW) _resetstkoflw();
}
```

<small>Source: [src/gml.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.cpp#L803-L807)</small>

For the same reason, `ReadMemory` refuses any range that touches a guard page: touching one would fault once and silently disarm it.
