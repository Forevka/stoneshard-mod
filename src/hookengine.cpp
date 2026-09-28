#include "hookengine.h"

#include "log.h"
#include "symbols.h"

#include <windows.h>
#include <atomic>
#include <cstring>
#include <deque>
#include <mutex>
#include <unordered_map>

#include "MinHook.h"

namespace mod::hk {
namespace {

using ScriptFn = gml::RValue* (*)(void*, void*, gml::RValue*, int, gml::RValue**);
using EventFn  = void (*)(void*, void*);

struct Hook {
    int                id       = -1;
    Kind               kind     = Kind::Script;
    void*              target   = nullptr;
    void*              original = nullptr;   // MinHook trampoline
    void*              thunk    = nullptr;
    std::atomic<bool>  managed{false};
    bool               enabled  = false;
};

// ---- thunk arena -------------------------------------------------------------
//
// One RWX block: two UNWIND_INFOs at the start, then fixed 64-byte thunk slots.
// A RUNTIME_FUNCTION per slot is registered once, up front; the unwinder reads
// the table live, so a slot's entry is simply pointed at the right unwind info
// before its thunk is enabled.

constexpr std::size_t kSlotSize   = 64;
constexpr std::size_t kSlots      = 4096;
constexpr std::size_t kHeaderSize = 64;
constexpr std::size_t kArenaSize  = kHeaderSize + kSlotSize * kSlots;

constexpr DWORD kUnwindFramed = 0;    // offset of the script-thunk unwind info
constexpr DWORD kUnwindLeaf   = 16;   // offset of the leaf (event-thunk) unwind info

unsigned char*    g_arena = nullptr;
RUNTIME_FUNCTION* g_pdata = nullptr;
std::size_t       g_used  = 0;

std::mutex                        g_lock;
std::deque<Hook>                  g_hooks;       // stable addresses: thunks point at them
std::unordered_map<void*, int>    g_byTarget;
std::atomic<ManagedDispatch>      g_managed{nullptr};

bool EnsureArena() {
    if (g_arena) return true;

    g_arena = static_cast<unsigned char*>(
        VirtualAlloc(nullptr, kArenaSize, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    if (!g_arena) { Logf("[!] hooks: could not allocate the thunk arena"); return false; }
    std::memset(g_arena, 0xCC, kArenaSize);

    // Script thunk: `sub rsp,38h` is its whole prologue (4 bytes).
    //   Version 1, no flags | prolog 4 | 1 code | no frame register
    //   code: at offset 4, UWOP_ALLOC_SMALL (2) with OpInfo (0x38-8)/8 = 6
    const unsigned char framed[] = {0x01, 0x04, 0x01, 0x00, 0x04, 0x62, 0x00, 0x00};
    // Event thunk never touches rsp: a leaf as far as unwinding is concerned.
    const unsigned char leaf[]   = {0x01, 0x00, 0x00, 0x00};
    std::memcpy(g_arena + kUnwindFramed, framed, sizeof(framed));
    std::memcpy(g_arena + kUnwindLeaf, leaf, sizeof(leaf));

    g_pdata = new RUNTIME_FUNCTION[kSlots];
    for (std::size_t i = 0; i < kSlots; ++i) {
        const DWORD begin = static_cast<DWORD>(kHeaderSize + i * kSlotSize);
        g_pdata[i].BeginAddress = begin;
        g_pdata[i].EndAddress   = begin + kSlotSize;
        g_pdata[i].UnwindData   = kUnwindLeaf;
    }
    if (!RtlAddFunctionTable(g_pdata, static_cast<DWORD>(kSlots),
                             reinterpret_cast<DWORD64>(g_arena))) {
        Logf("[!] hooks: RtlAddFunctionTable failed - thunks would break unwinding");
        VirtualFree(g_arena, 0, MEM_RELEASE);
        g_arena = nullptr;
        delete[] g_pdata;
        g_pdata = nullptr;
        return false;
    }
    return true;
}

// ---- dispatch ----------------------------------------------------------------

gml::RValue* ScriptDispatch(void* self, void* other, gml::RValue* result, int argc,
                            gml::RValue** args, Hook* h) {
    // No NoteSelf here: a script's self can be a struct (a bound method, a
    // `with` over a struct), which is not a CInstance. Only object events,
    // whose self always is one, feed CurrentSelf().
    auto* managed = h->managed.load(std::memory_order_acquire)
                        ? g_managed.load(std::memory_order_acquire) : nullptr;
    Call c{self, other, result, args, argc, kBefore, 0, h->id};
    if (managed) managed(&c);

    gml::RValue* ret = result;
    if (!c.skip) ret = reinterpret_cast<ScriptFn>(h->original)(self, other, result, argc, args);

    if (managed) {
        c.phase = kAfter;
        managed(&c);
    }
    return ret;
}

void EventDispatch(void* self, void* other, Hook* h) {
    gml::NoteSelf(self);

    auto* managed = h->managed.load(std::memory_order_acquire)
                        ? g_managed.load(std::memory_order_acquire) : nullptr;
    Call c{self, other, nullptr, nullptr, 0, kBefore, 0, h->id};
    if (managed) managed(&c);

    if (!c.skip) reinterpret_cast<EventFn>(h->original)(self, other);

    if (managed) {
        c.phase = kAfter;
        managed(&c);
    }
}

void* EmitThunk(Hook* h) {
    if (g_used >= kSlots) { Logf("[!] hooks: thunk arena full (%zu)", kSlots); return nullptr; }
    const std::size_t slot = g_used++;
    unsigned char* p = g_arena + kHeaderSize + slot * kSlotSize;
    unsigned char* o = p;

    auto imm64 = [&](const void* v) {
        const auto x = reinterpret_cast<std::uint64_t>(v);
        std::memcpy(o, &x, 8);
        o += 8;
    };
    auto bytes = [&](std::initializer_list<unsigned char> b) {
        for (unsigned char c : b) *o++ = c;
    };

    if (h->kind == Kind::Script) {
        bytes({0x48, 0x83, 0xEC, 0x38});               // sub rsp, 38h
        bytes({0x48, 0x8B, 0x44, 0x24, 0x60});         // mov rax, [rsp+60h]  (caller's arg 5)
        bytes({0x48, 0x89, 0x44, 0x24, 0x20});         // mov [rsp+20h], rax  (our arg 5)
        bytes({0x48, 0xB8}); imm64(h);                 // mov rax, hook
        bytes({0x48, 0x89, 0x44, 0x24, 0x28});         // mov [rsp+28h], rax  (our arg 6)
        bytes({0x48, 0xB8}); imm64(reinterpret_cast<void*>(&ScriptDispatch));
        bytes({0xFF, 0xD0});                           // call rax
        bytes({0x48, 0x83, 0xC4, 0x38});               // add rsp, 38h
        bytes({0xC3});                                 // ret
        g_pdata[slot].UnwindData = kUnwindFramed;
    } else {
        bytes({0x49, 0xB8}); imm64(h);                 // mov r8, hook
        bytes({0x48, 0xB8}); imm64(reinterpret_cast<void*>(&EventDispatch));
        bytes({0xFF, 0xE0});                           // jmp rax
        g_pdata[slot].UnwindData = kUnwindLeaf;
    }

    FlushInstructionCache(GetCurrentProcess(), p, kSlotSize);
    return p;
}

Hook* ById(int id) {
    return (id >= 0 && static_cast<std::size_t>(id) < g_hooks.size()) ? &g_hooks[id] : nullptr;
}

} // namespace

void SetManagedDispatch(ManagedDispatch fn) { g_managed.store(fn, std::memory_order_release); }

int Install(void* target, Kind kind) {
    if (!target) return -1;
    std::lock_guard<std::mutex> lock(g_lock);

    if (auto it = g_byTarget.find(target); it != g_byTarget.end()) {
        Hook& h = g_hooks[it->second];
        if (h.kind != kind) {
            Logf("[!] hooks: %p already hooked as a %s", target, h.kind == Kind::Script ? "script" : "event");
            return -1;
        }
        return h.id;
    }
    if (!EnsureArena()) return -1;

    Hook& h = g_hooks.emplace_back();
    h.id     = static_cast<int>(g_hooks.size() - 1);
    h.kind   = kind;
    h.target = target;
    h.thunk  = EmitThunk(&h);
    if (!h.thunk) { g_hooks.pop_back(); return -1; }

    const MH_STATUS cs = MH_CreateHook(target, h.thunk, &h.original);
    if (cs != MH_OK) {
        // MH_ERROR_ALREADY_CREATED means one of the older single-purpose
        // detours owns this target; the engine cannot share it.
        Logf("[!] hooks: MH_CreateHook(%p) failed: %s", target, MH_StatusToString(cs));
        g_hooks.pop_back();
        --g_used;
        return -1;
    }
    const MH_STATUS es = MH_EnableHook(target);
    if (es != MH_OK) {
        Logf("[!] hooks: MH_EnableHook(%p) failed: %s", target, MH_StatusToString(es));
        MH_RemoveHook(target);
        g_hooks.pop_back();
        --g_used;
        return -1;
    }
    h.enabled = true;
    g_byTarget.emplace(target, h.id);

    const char* name = sym::OwnerOf(target);
    Logf("hooks: #%d %s %s", h.id, kind == Kind::Script ? "script" : "event", name ? name : "?");
    return h.id;
}

bool SetManaged(int id, bool managed) {
    std::lock_guard<std::mutex> lock(g_lock);
    Hook* h = ById(id);
    if (!h) return false;
    h->managed.store(managed, std::memory_order_release);
    return true;
}

bool Disable(int id) {
    std::lock_guard<std::mutex> lock(g_lock);
    Hook* h = ById(id);
    if (!h || !h->enabled) return h != nullptr;
    if (MH_DisableHook(h->target) != MH_OK) return false;
    h->enabled = false;
    return true;
}

bool Enable(int id) {
    std::lock_guard<std::mutex> lock(g_lock);
    Hook* h = ById(id);
    if (!h || h->enabled) return h != nullptr;
    if (MH_EnableHook(h->target) != MH_OK) return false;
    h->enabled = true;
    return true;
}

int Count() {
    std::lock_guard<std::mutex> lock(g_lock);
    return static_cast<int>(g_hooks.size());
}

bool CallOriginal(const Call* call, gml::RValue* result) {
    if (!call || !result) return false;
    Hook* h;
    {
        std::lock_guard<std::mutex> lock(g_lock);
        h = ById(call->hookId);
    }
    if (!h || h->kind != Kind::Script || !h->original) return false;

    // The trampoline IS the original: calling it skips our detour, so no
    // handler sees this call. CallAs supplies the same fault guard as any
    // other call into the game.
    return gml::CallAs(h->original, result, call->args, call->argc, call->self, call->other);
}

void InstallSelfObservers(int maxEvents) {
    // Step and Draw events of a spread of objects: whichever of them are alive
    // in the current room keep supplying a fresh instance every frame. Draw is
    // included because some games' long-lived controllers only draw.
    int steps = 0, draws = 0;
    for (const sym::Entry& e : sym::All()) {
        if (steps >= maxEvents && draws >= maxEvents) break;
        if (std::strncmp(e.name, "gml_Object_", 11) != 0) continue;
        const bool isStep = std::strstr(e.name, "_Step_") != nullptr;
        const bool isDraw = !isStep && std::strstr(e.name, "_Draw_") != nullptr;
        if (isStep && steps < maxEvents && Install(e.func, Kind::Event) >= 0) ++steps;
        if (isDraw && draws < maxEvents && Install(e.func, Kind::Event) >= 0) ++draws;
    }
    Logf("hooks: watching %d Step and %d Draw events for a live instance", steps, draws);
}

} // namespace mod::hk
