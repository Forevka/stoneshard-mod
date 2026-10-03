---
title: Hosting .NET
description: How the native loader finds a .NET runtime, starts CoreLoader.dll inside the game, and the C ABI the two sides talk through.
---

The native loader is C++ inside the game process. Mods are C#. Between them sits `src/host/`: it locates
`hostfxr` (the component `dotnet app.dll` uses), starts the .NET 10 runtime inside the game, binds one managed
entry point, and hands it a table of C function pointers, the `CoreApi`. Everything a mod does to the game
goes through that table.

`host::Start()` runs on the init thread, last, after the symbol scan and runtime helper discovery (see
[Boot sequence](./boot.md)). Bringing the runtime up takes a few hundred milliseconds and touches no GML, so it
does not need the game thread. A missing runtime or a missing `CoreLoader.dll` is logged and leaves the
native overlay working; it never takes the game down.

## Finding the runtime {#locating-hostfxr}

The loader looks for `hostfxr.dll` by hand, in this order:

1. An app-local runtime in `<game>\Lodestone\dotnet\`.
2. `DOTNET_ROOT`.
3. The registry key `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64`, value `InstallLocation`.
4. `%ProgramFiles%\dotnet`.

```cpp
bool LocateHostfxr(const fs::path& loaderDir, fs::path& fxr, fs::path& appLocalRoot) {
    const fs::path local = loaderDir / L"dotnet";
    if (fxr = NewestFxr(local); !fxr.empty()) { appLocalRoot = local; return true; }

    std::vector<fs::path> roots;
    if (auto r = EnvPath(L"DOTNET_ROOT"); !r.empty()) roots.push_back(r);
    if (auto r = RegistryDotnetRoot(); !r.empty()) roots.push_back(r);
    if (auto r = EnvPath(L"ProgramFiles"); !r.empty()) roots.push_back(r / L"dotnet");

    for (const auto& root : roots)
        if (fxr = NewestFxr(root); !fxr.empty()) return true;
    return false;
}
```

<small>Source: [src/host/dotnet_host.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/host/dotnet_host.cpp#L174-L186)</small>

Within a root, `NewestFxr` takes the highest version folder under `host\fxr\` that contains a `hostfxr.dll`.
`VersionKey` compares the numeric fields first and sorts a pre-release (`10.0.0-rc.2`) below its release.

Why not `nethost`, the library Microsoft provides for exactly this lookup? It is one more static library that
has to match the loader's static CRT, and the lookup it performs is three documented steps. Doing it by hand
also lets the app-local runtime win over everything else, so a release can ship a private runtime and never
depend on what the player has installed.

## Starting the runtime {#start}

`Start()` expects `CoreLoader.dll` and `CoreLoader.runtimeconfig.json` in the loader folder
(`<game>\Lodestone\`). Then:

1. `LoadLibraryW` on the chosen `hostfxr.dll`, and `GetProcAddress` for
   `hostfxr_initialize_for_runtime_config`, `hostfxr_get_runtime_delegate` and `hostfxr_close`. If
   `hostfxr_set_error_writer` exists, hostfxr's own error text is routed into `lodestone.log`.
2. `hostfxr_initialize_for_runtime_config` with the runtimeconfig. For an app-local runtime, `dotnet_root`
   is set to that folder. Return codes 0, 1 and 2 are all success (1 and 2 mean the runtime was already up,
   or the config differs from the running one).
3. `hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer)`. The init handle is closed
   straight after; the runtime stays loaded.
4. `load_assembly_and_get_function_pointer` binds `CoreLoader.Runtime.Entry.Init`. The delegate type name
   is the sentinel `UNMANAGEDCALLERSONLY_METHOD`, `(const char_t*)-1`, which tells the runtime the target is
   an `[UnmanagedCallersOnly]` method and no delegate marshalling is needed.
5. `Entry.Init(api, &exports)` runs. It fills `ManagedExports` with four function pointers.

```cpp
void* initRaw = nullptr;
const int lrc = loadFn(assembly.c_str(), L"CoreLoader.Runtime.Entry, CoreLoader", L"Init",
                       kUnmanagedCallersOnly, nullptr, &initRaw);
// ...
g_exports      = ManagedExports{};
g_exports.size = sizeof(ManagedExports);
const std::int32_t ok = reinterpret_cast<ManagedInitFn>(initRaw)(api, &g_exports);
if (!ok || !g_exports.frame || !g_exports.gui || !g_exports.shutdown || !g_exports.hook_dispatch) {
    Fail("CoreLoader.Runtime.Entry.Init reported failure (see managed log lines above)");
    return false;
}
hk::SetManagedDispatch(reinterpret_cast<hk::ManagedDispatch>(g_exports.hook_dispatch));

SetStatus("running");
g_running.store(true, std::memory_order_release);
```

<small>Source: [src/host/dotnet_host.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/host/dotnet_host.cpp#L248-L269)</small>

Every failure path calls `Fail`, which writes the reason to the log and to a status string. The overlay's Mods
tab shows that string ("C# mods are not running.") instead of the managed tabs.

### Publishing the exports {#publication}

`Start()` runs on the init thread while the Present hook is already firing on the game thread. The game
thread reads `g_exports` every frame, so publication is explicit: `g_exports` is filled first, then
`g_running` is stored with release semantics. `Frame()`, `DrawModsTab()` and `Running()` load `g_running`
with acquire semantics before they touch `g_exports`. Without that ordering the game thread could see
`g_running == true` and call a function pointer that is not written yet.

`ManagedExports` has no optional entries. Init fails unless `frame`, `gui`, `shutdown` and `hook_dispatch`
are all set.

## Entry.Init on the managed side {#entry-init}

`Entry.Init` is the only method the host binds by name. The rest of `ManagedExports` is filled from inside it
with `&Frame`, `&Gui` and so on.

```csharp
[UnmanagedCallersOnly]
public static int Init(CoreApi* api, ManagedExports* exports)
{
    try
    {
        if (api == null || exports == null) return 0;
        // Refuse a host built against a different table shape: writing past
        // a smaller ManagedExports, or calling through a missing CoreApi
        // field, would corrupt the game instead of failing.
        if (api->Version != CoreApi.ExpectedVersion || api->Size < sizeof(CoreApi)) return 0;
        if (exports->Size < sizeof(ManagedExports)) return 0;
        Loader.Api = api;
        // ...
        ModManager.DiscoverAndLoad();
        TestHost.Start();
```

<small>Source: [managed/CoreLoader/Runtime/Entry.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Runtime/Entry.cs#L22-L42)</small>

Init runs on the host's start-up thread, so it must not touch GML. It discovers and loads mod assemblies and
constructs the mod classes, but `OnInitialize` waits for the first frame on the game thread (see
[Managed runtime](./managed-runtime.md)).

## The CoreApi table {#coreapi}

`struct CoreApi` in `src/host/core_api.h` is the whole C ABI: one struct of function pointers, built once by
`Build()` in `core_api.cpp` and handed to `Entry.Init`. The header states the conventions so neither side has
to guess:

- Strings are UTF-8 and NUL-terminated. A string returned by the loader is owned by the loader and valid until
  the next call of the same function on the same thread, unless stated otherwise. Copy it.
- `int` results are 1 for success and 0 for failure; the reason goes to the log.
- GML values travel as the runtime's own 16-byte RValue (`CoreRValue`). Argument lists are contiguous arrays;
  the loader builds whatever layout the callee needs (see [Builtins](./builtins.md) and
  [Hook engine](./hook-engine.md) for the two layouts).
- Everything that touches GML runs on the game thread. From version 9 such calls fail on any other thread.

```cpp
struct CoreRValue {
    union {
        double       real;
        void*        ptr;
        std::int64_t i64;
        std::int32_t i32;
    };
    std::int32_t flags;
    std::int32_t kind;
};
```

<small>Source: [src/host/core_api.h](https://github.com/Forevka/stoneshard-mod/blob/main/src/host/core_api.h#L26-L35)</small>

### Append-only, size and version {#append-only}

The struct starts with `size` (`sizeof(CoreApi)` as the loader was built) and `version`
(`kCoreApiVersion`). Fields are only ever appended, never reordered or removed. `version` is bumped when
fields are added or when a field's meaning changes. The managed side refuses to start unless the version
matches exactly and the native struct is at least as large as its own mirror, because calling through a field
the native side does not have would jump to garbage inside the game.

The version history, from the comment on `kCoreApiVersion` (see `src/host/core_api.h` line 43). The comment starts at version 2; the version-1 row is the original table, inferred from the fields that come before the hook entries:

| Version | What it added or changed |
|---|---|
| 1 | The original table (not in the comment): loader info, symbols, the GML bridge, the first UI widgets. |
| 2 | Hooks (`hook_install`, `hook_set_managed`, `hook_count`). |
| 3 | `builtin_arity`. |
| 4 | `hook_call_original`. |
| 5 | `builtin_name`. |
| 6 | `value_free` / `value_copy`. |
| 7 | UI round 2 (child regions, scrolling, input with flags and history). |
| 8 | `memory_read`. |
| 9 | Pick mode, tree nodes, clipboard. GML calls are refused off the game thread. |
| 10 | UI round 3 (combo, selectable, disabled, clipper, `is_item_deactivated_after_edit`, ...), `last_gml_error`, `instance_from_id`. |

`hook_enable`, `builtin_address` and `builtin_name_at` are not named in that comment; their position in the
struct places `hook_enable` with version 6, and `builtin_address` and `builtin_name_at` with version 9.

### The managed mirror {#managed-mirror}

`managed/CoreLoader/Native/CoreApi.cs` mirrors the struct field for field, as a sequential struct of
`delegate* unmanaged<...>` function pointers. Its `ExpectedVersion` must equal `kCoreApiVersion`:

```csharp
// Field-for-field mirror of `struct CoreApi` in src/host/core_api.h. The two MUST
// change together: append only, and bump the version when a meaning changes.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CoreApi
{
    public const int ExpectedVersion = 10;

    public int Size;
    public int Version;

    // loader
    public delegate* unmanaged<int, byte*, byte*, void> Log;
    public delegate* unmanaged<byte*> GameName;
```

<small>Source: [managed/CoreLoader/Native/CoreApi.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/CoreLoader/Native/CoreApi.cs#L5-L17)</small>

The same file mirrors `ManagedExports` and `CoreHookCall`. The mirror is `internal`: mods never see raw
function pointers, only the static facades (`Game`, `UI`, `Hooks`, ...) built on `Loader.Api`. Adding an
entry is a five-step change on both sides; [Contributing](./contributing.md) lists the steps.

## Game thread only {#game-thread-only}

The GameMaker runtime is single-threaded. A GML call, a string, or a reference count touched from another
thread races the game and corrupts it silently, so it would show up much later as an unrelated crash.
Both sides refuse such calls.

Natively, every CoreApi entry that touches GML starts with `GameThreadOnly`:

```cpp
// The runtime is single-threaded: a GML call, a string or a reference count
// touched from another thread races the game and corrupts it silently. The
// managed side already refuses; this also covers native plugins and any path
// that slips past it. Logged a few times, then quietly.
bool GameThreadOnly(const char* what) {
    if (gml::OnGameThread()) return true;
    static std::atomic<int> reported{0};
    if (reported.fetch_add(1) < 8)
        Logf("[!] core api: %s called off the game thread (thread %lu); refused", what,
             static_cast<unsigned long>(GetCurrentThreadId()));
    return false;
}
```

<small>Source: [src/host/core_api.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/host/core_api.cpp#L99-L110)</small>

On the managed side, `Entry.Frame` and `Entry.Gui` call `Loader.MarkGameThread()`, and every public facade
starts with `Loader.EnsureGameThread()`, which throws `InvalidOperationException` on any other thread. The
managed check gives the mod author a stack trace; the native one catches anything that slips past it.

## Exceptions at the boundary {#exceptions}

Two kinds of exception can meet at the native/managed boundary, and neither may cross it.

**Managed exceptions out of managed code.** An exception that escapes an `[UnmanagedCallersOnly]` method
terminates the process. So every entry point in `Entry` (`Init`, `Frame`, `Gui`, `Shutdown`,
`HookDispatch`) catches everything itself. The host deliberately puts no SEH guard around these calls:

```cpp
// No SEH around these calls on purpose. Every managed entry point catches all
// exceptions itself, and GML calls are guarded natively where they happen;
// unwinding managed frames behind the CLR's back from out here would corrupt
// the thread's runtime state rather than contain anything.
```

<small>Source: [src/host/dotnet_host.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/host/dotnet_host.cpp#L274-L277)</small>

**GML exceptions into managed code.** A GML runtime error is a C++ throw. If one unwinds through .NET frames,
the process dies. `gml::ManagedScope` marks that managed code is on the current thread's stack:

```cpp
// Managed code on this thread's stack (Frame, Gui, a managed hook handler).
// A GML exception must never unwind through .NET frames - that kills the
// process - so while one is present every call into the game catches it.
struct ManagedScope {
    ManagedScope();
    ~ManagedScope();
    ManagedScope(const ManagedScope&) = delete;
    ManagedScope& operator=(const ManagedScope&) = delete;
};
bool InManagedCode();
```

<small>Source: [src/gml.h](https://github.com/Forevka/stoneshard-mod/blob/main/src/gml.h#L105-L114)</small>

The host opens one around `g_exports.frame()`, `g_exports.gui()` and `g_exports.shutdown()`, and the hook
engine opens one around each managed dispatch. While the depth is non-zero, the guard on every call into the
game handles a GML exception itself and reports the call as failed. The one exception: a guarded call made
while a hook dispatch is in progress (dispatch depth above zero) and no `ManagedScope` is open on the thread
leaves the C++ throw to outer handlers, since only native frames stand between it and the game.
[Runtime bridge](./runtime-bridge.md) covers the guard filter and how the error text is recovered.

## Re-entrancy {#re-entrancy}

A GML call made from managed code can present a frame: `screen_refresh`, or a script that draws and flips.
Present then re-enters the host while the outer frame's pooled values, UI scopes and mod code are still live.
The nested pass must do no managed work.

```cpp
thread_local bool t_inFrame = false;
thread_local bool t_inGui   = false;

void Frame() {
    if (!g_running.load(std::memory_order_acquire) || t_inFrame || gml::InManagedCode()) return;
    t_inFrame = true;
    {
        gml::ManagedScope inManaged;
        g_exports.frame();
    }
    t_inFrame = false;
}
```

<small>Source: [src/host/dotnet_host.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/host/dotnet_host.cpp#L282-L293)</small>

`Entry` guards the same way with its own `_inFrame` / `_inGui` flags, so a nested pass cannot drain the value
pool the outer pass still uses, or hot-reload a mod whose code is on the stack.

## Shutdown {#shutdown}

Mods are shut down when the game window receives `WM_DESTROY`, from the overlay's window procedure, not from
`DllMain`:

```cpp
LRESULT CALLBACK HookedWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    // The last point where mods can still run on the game thread; DllMain's
    // detach is under the loader lock, where calling into .NET is unsafe.
    if (msg == WM_DESTROY) host::Shutdown();
```

<small>Source: [src/overlay.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/overlay.cpp#L90-L93)</small>

At `DLL_PROCESS_DETACH` during process exit, every other thread is already gone, possibly while holding a
lock, so the loader tears nothing down there.

`host::Shutdown()` runs once (`g_running.exchange(false)`). It first detaches the managed dispatcher, so GML
that runs during `OnShutdown` or during `WM_DESTROY` cannot dispatch hooks into mods that are halfway through
tearing themselves down. The hooks stay installed and only run the originals from then on.

```cpp
void Shutdown() {
    if (!g_running.exchange(false, std::memory_order_acq_rel)) return;
    // ...
    hk::SetManagedDispatch(nullptr);
    // ...
    if (gml::InManagedCode()) {
        Logf("[!] host: window destroyed from inside a mod's handler; skipping managed shutdown");
        return;
    }
    gml::ManagedScope inManaged;
    g_exports.shutdown();
}
```

<small>Source: [src/host/dotnet_host.cpp](https://github.com/Forevka/stoneshard-mod/blob/main/src/host/dotnet_host.cpp#L311-L327)</small>

If `WM_DESTROY` arrives from inside game code that a mod's handler is running under, the managed pass is
skipped: shutting mods down then would unload code that is on the stack. Settings that mods save on change
are already on disk. Otherwise `Entry.Shutdown` calls every running mod's `OnShutdown`, then flushes mod
settings (`ModConfig.FlushAll`) and harvested variable names (`VarHarvest.Flush`), and stops the test host.
