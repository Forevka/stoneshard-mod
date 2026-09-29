// Bridge into the game's compiled GML.
//
// Two runtime helpers are needed, and both are located by CONSENSUS pattern
// scanning over many named functions rather than by hardcoded address:
//
//   YYSetString(RValue* dst, const char* src)
//       Every script that touches a string literal does
//           lea rdx, [rip+disp]        ; -> a printable string in .rdata
//           call <YYSetString>
//       so we tally the call target across hundreds of functions and take the
//       one that dominates.
//
//   g_pCurrentSelf  (CInstance** )
//       Every script prologue stores its `self` into one global:
//           mov [rip+disp], rcx
//       Same tally trick. Reading it gives us a valid CInstance to pass along,
//       which matters because some scripts dereference `self`.

#include "gml.h"
#include "hookengine.h"
#include "log.h"
#include "symbols.h"
#include "builtins.h"
#include "tracer.h"

#include <windows.h>
#include <intrin.h>
#include <MinHook.h>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <unordered_set>

namespace mod::gml {
namespace {

using SetStringFn = void (*)(RValue*, const char*);
using ScriptFn    = RValue* (*)(void* self, void* other, RValue* result,
                                int argc, RValue** args);
// Object events take only the instance pair.
using EventFn     = void (*)(void* self, void* other);

// Defined with the player tracker further down; the weapon recorder feeds it a
// known instance/x/y triple so the position offset can be found exactly.
void CalibrateFromKnown(const void* inst, double x, double y);

SetStringFn g_setString    = nullptr;

// Value lifetime: FREE_RValue(v) drops the reference a string/array/struct
// value holds; COPY_RValue(dst, src) makes dst a second owner of src's value.
using FreeFn = void (*)(RValue*);
using CopyFn = void (*)(RValue*, const RValue*);
FreeFn      g_free = nullptr;
CopyFn      g_copy = nullptr;
// Newer runtimes inline COPY_RValue and call only its reference half,
// COPY_RValue__Post, which writes the pointer and takes the reference but
// leaves kind and flags to the (inlined) caller. The self-test tells them apart.
bool        g_copyIsPost = false;
void**      g_pCurrentSelf = nullptr;

// Recorded from a genuine call by the game; see InstallCapture below.
Capture     g_capture;
bool        g_useCaptured     = true;
int         g_captureHook     = -1;      // hook-engine id of the capture target
void*       g_capturedFn      = nullptr;

// Always-on recorder for the weapon spawner, kept separate from the
// user-driven capture so the two never contend for one hook slot.
Capture     g_weaponRec;
bool        g_weaponRecording = false;
bool        g_capturedIsEvent = false;

bool        g_ready  = false;
std::string g_status = "not initialised";

// How far into each function we look for the patterns.
constexpr std::size_t kScanWindow = 2048;
// How many resolved functions to sample. Larger samples make the vote margins
// clearer; the whole scan still costs well under a tenth of a second.
constexpr std::size_t kSampleSize = 4000;

bool Readable(std::uintptr_t p, std::size_t n) {
    return sym::TextRange().contains(p) && sym::TextRange().contains(p + n);
}

bool PrintableStringAt(std::uintptr_t p, std::size_t minLen) {
    auto rdata = sym::RdataRange();
    if (!rdata.contains(p)) return false;

    const auto* s = reinterpret_cast<const unsigned char*>(p);
    for (std::size_t i = 0; i < 64; ++i) {
        if (p + i >= rdata.hi) return false;
        if (s[i] == 0) return i >= minLen;
        if (s[i] < 0x20 || s[i] > 0x7e) return false;
    }
    return true;
}

// Tally `call rel32` targets that immediately follow a `lea rdx, [rip+d32]`
// pointing at a printable .rdata string.
void TallySetString(std::uintptr_t fn, std::unordered_map<std::uintptr_t, int>& votes) {
    for (std::size_t i = 0; i + 12 < kScanWindow; ++i) {
        const std::uintptr_t at = fn + i;
        if (!Readable(at, 12)) return;

        const auto* b = reinterpret_cast<const unsigned char*>(at);
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
    }
}

// Newer runtimes (2024+) no longer build string literals inside scripts: every
// literal is a static RValue constructed once at startup by a tiny initialiser,
//     lea rdx, [rip+str]     ; 48 8D 15 d32  -> printable .rdata string
//     lea rcx, [rip+value]   ; 48 8D 0D d32  -> the static RValue in .data
//     call <YYSetString>     ; E8 rel32
// so the call target is tallied across the whole of .text instead.
void TallyStaticStringInits(std::unordered_map<std::uintptr_t, int>& votes) {
    const auto tx = sym::TextRange();
    for (std::uintptr_t at = tx.lo; at + 19 < tx.hi; ++at) {
        const auto* b = reinterpret_cast<const unsigned char*>(at);
        // Either order of the two leas: 48 8D 15 = rdx (the string), 48 8D 0D = rcx (the value).
        if (!(b[0] == 0x48 && b[1] == 0x8D && (b[2] == 0x15 || b[2] == 0x0D))) continue;
        if (!(b[7] == 0x48 && b[8] == 0x8D && b[9] == (b[2] == 0x15 ? 0x0D : 0x15))) continue;
        if (b[14] != 0xE8) continue;

        std::int32_t d1, d2, rel;
        std::memcpy(&d1, b + 3, 4);
        std::memcpy(&d2, b + 10, 4);
        std::memcpy(&rel, b + 15, 4);
        const std::uintptr_t first  = at + 7 + static_cast<std::intptr_t>(d1);
        const std::uintptr_t second = at + 14 + static_cast<std::intptr_t>(d2);
        const bool strFirst = b[2] == 0x15;
        const std::uintptr_t str = strFirst ? first : second;
        const std::uintptr_t val = strFirst ? second : first;
        const std::uintptr_t fn  = at + 19 + static_cast<std::intptr_t>(rel);
        if (!PrintableStringAt(str, 1) || !sym::DataRange().contains(val) || !tx.contains(fn)) continue;
        ++votes[fn];
    }
}

// Every direct call a script makes. The runtime's value helpers (free, copy)
// are among the most-called targets in any YYC game, which is what makes them
// findable without a name: votes rank them, the structural checks below decide.
void TallyCalls(std::uintptr_t fn, std::unordered_map<std::uintptr_t, int>& votes) {
    for (std::size_t i = 0; i + 5 < kScanWindow; ++i) {
        const std::uintptr_t at = fn + i;
        if (!Readable(at, 5)) return;
        const auto* b = reinterpret_cast<const unsigned char*>(at);
        if (b[0] != 0xE8) continue;
        std::int32_t rel;
        std::memcpy(&rel, b + 1, 4);
        const std::uintptr_t target = at + 5 + static_cast<std::intptr_t>(rel);
        if (sym::TextRange().contains(target)) ++votes[target];
    }
}

bool Contains(std::uintptr_t fn, std::size_t window, const unsigned char* pat, std::size_t n) {
    for (std::size_t i = 0; i + n <= window; ++i) {
        if (!Readable(fn + i, n)) return false;
        if (std::memcmp(reinterpret_cast<const void*>(fn + i), pat, n) == 0) return true;
    }
    return false;
}

// Both runtimes mask RValue.kind with 0xFFFFFF before switching on it, as
// `and eax,0FFFFFFh` (25 FF FF FF 00) or `mov eax,0FFFFFFh` (B8 FF FF FF 00).
bool HasKindMask(std::uintptr_t fn) {
    static const unsigned char andMask[] = {0x25, 0xFF, 0xFF, 0xFF, 0x00};
    static const unsigned char movMask[] = {0xB8, 0xFF, 0xFF, 0xFF, 0x00};
    return Contains(fn, 0x30, andMask, sizeof(andMask)) || Contains(fn, 0x30, movMask, sizeof(movMask));
}

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

// COPY_RValue(dst, src): reads the kind of its SECOND argument - [rdx+0Ch]
// directly, or after copying the whole source (movups xmm0,[rdx]).
bool LooksLikeCopy(std::uintptr_t fn) {
    if (!HasKindMask(fn)) return false;
    static const unsigned char kindRdx[] = {0x8B, 0x42, 0x0C};    // mov eax,[rdx+0C]
    static const unsigned char loadRdx[] = {0x0F, 0x10, 0x02};    // movups xmm0,[rdx]
    return Contains(fn, 0x20, kindRdx, 3) || Contains(fn, 0x20, loadRdx, 3);
}

// Tally `mov [rip+d32], rcx` (48 89 0D d32) in function prologues.
void TallyCurrentSelf(std::uintptr_t fn, std::unordered_map<std::uintptr_t, int>& votes) {
    for (std::size_t i = 0; i + 7 < 0x100; ++i) {
        const std::uintptr_t at = fn + i;
        if (!Readable(at, 7)) return;

        const auto* b = reinterpret_cast<const unsigned char*>(at);
        if (!(b[0] == 0x48 && b[1] == 0x89 && b[2] == 0x0D)) continue;

        std::int32_t disp;
        std::memcpy(&disp, b + 3, 4);
        const std::uintptr_t target = at + 7 + static_cast<std::intptr_t>(disp);
        if (sym::DataRange().contains(target)) ++votes[target];
    }
}

// Returns the winner only if it clearly dominates; a weak consensus means the
// pattern changed and we must not guess.
std::uintptr_t Winner(const std::unordered_map<std::uintptr_t, int>& votes,
                      int minVotes, const char* what) {
    std::uintptr_t best = 0, second = 0;
    int bestN = 0, secondN = 0;
    for (const auto& [addr, n] : votes) {
        if (n > bestN) { second = best; secondN = bestN; best = addr; bestN = n; }
        else if (n > secondN) { second = addr; secondN = n; }
    }
    Logf("gml: %s consensus -> %p (%d votes; runner-up %p %d)",
         what, reinterpret_cast<void*>(best), bestN,
         reinterpret_cast<void*>(second), secondN);

    if (bestN < minVotes || bestN < secondN * 2) {
        Logf("[!] gml: %s consensus too weak", what);
        return 0;
    }
    return best;
}

// Structural check that a candidate really is YYSetString: its body must write
// the STRING kind (1) into offset 0x0C of the RValue it was handed, i.e.
//   mov dword ptr [reg+0x0C], 1   ->   C7 4x 0C 01 00 00 00
// Register-agnostic, so a recompile that picks a different register still matches.
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

// Ranks candidates by votes, then returns the first that passes validation.
std::uintptr_t BestValidated(const std::unordered_map<std::uintptr_t, int>& votes,
                             bool (*validate)(std::uintptr_t), const char* what) {
    std::vector<std::pair<int, std::uintptr_t>> ranked;
    ranked.reserve(votes.size());
    for (const auto& [addr, n] : votes) ranked.emplace_back(n, addr);
    std::sort(ranked.rbegin(), ranked.rend());

    for (std::size_t i = 0; i < ranked.size() && i < 8; ++i) {
        const bool ok = validate(ranked[i].second);
        Logf("gml: %s candidate #%zu %p (%d votes) -> %s",
             what, i + 1, reinterpret_cast<void*>(ranked[i].second),
             ranked[i].first, ok ? "VALIDATED" : "rejected");
        if (ok) return ranked[i].second;
    }
    Logf("[!] gml: no %s candidate passed validation", what);
    return 0;
}

// Like BestValidated, but ranks only the most-called candidates and logs just
// the outcome: the call tally has thousands of entries.
std::uintptr_t BestValidatedQuiet(const std::unordered_map<std::uintptr_t, int>& votes,
                                  bool (*validate)(std::uintptr_t), const char* what) {
    std::vector<std::pair<int, std::uintptr_t>> ranked;
    ranked.reserve(votes.size());
    for (const auto& [addr, n] : votes) ranked.emplace_back(n, addr);
    std::sort(ranked.rbegin(), ranked.rend());
    for (std::size_t i = 0; i < ranked.size() && i < 64; ++i) {
        if (!validate(ranked[i].second)) continue;
        Logf("gml: %s -> %p (rank %zu, %d calls)", what,
             reinterpret_cast<void*>(ranked[i].second), i + 1, ranked[i].first);
        return ranked[i].second;
    }
    Logf("gml: %s not found; value %s unavailable", what,
         std::strstr(what, "FREE") ? "freeing" : "copying");
    return 0;
}

} // namespace

bool Init() {
    g_ready = false;

    if (!sym::Healthy()) {
        g_status = "symbol resolver unhealthy";
        return false;
    }

    std::unordered_map<std::uintptr_t, int> strVotes, selfVotes, callVotes;

    std::size_t sampled = 0;
    for (const sym::Entry& e : sym::All()) {
        if (sampled >= kSampleSize) break;
        if (std::strncmp(e.name, "gml_Script_", 11) != 0) continue;
        ++sampled;

        const auto fn = reinterpret_cast<std::uintptr_t>(e.func);
        TallySetString(fn, strVotes);
        TallyCurrentSelf(fn, selfVotes);
        TallyCalls(fn, callVotes);
    }

    // Value lifetime helpers. Not required for the bridge to work, so a miss
    // is logged and the API reports "unavailable" instead of failing Init.
    g_free = reinterpret_cast<FreeFn>(BestValidatedQuiet(callVotes, &LooksLikeFree, "FREE_RValue"));
    g_copy = reinterpret_cast<CopyFn>(BestValidatedQuiet(callVotes, &LooksLikeCopy, "COPY_RValue"));
    Logf("gml: sampled %zu functions", sampled);

    // Votes narrow the field; the structural check picks the winner, so a thin
    // margin between similar string helpers cannot pick the wrong one.
    std::uintptr_t setStr = BestValidated(strVotes, &LooksLikeSetString, "YYSetString");
    if (!setStr) {
        Logf("gml: no in-script string construction; trying static string initialisers");
        std::unordered_map<std::uintptr_t, int> initVotes;
        TallyStaticStringInits(initVotes);
        setStr = BestValidated(initVotes, &LooksLikeSetString, "YYSetString (static init)");
    }

    // Newer runtimes keep `self` in a register and never store it globally, so
    // this may legitimately find nothing. That is not fatal: CurrentSelf() then
    // falls back to instances observed by the loader's own hooks.
    const std::uintptr_t selfP = Winner(selfVotes, 40, "currentSelf");
    if (!selfP) Logf("gml: no current-self global in this runtime; using hook-observed instances");

    if (!setStr) {
        g_status = "could not resolve the string constructor";
        return false;
    }

    g_setString    = reinterpret_cast<SetStringFn>(setStr);
    g_pCurrentSelf = selfP ? reinterpret_cast<void**>(selfP) : nullptr;

    g_ready  = true;
    g_status = "ok";
    Logf("gml: bridge ready");
    return true;
}

bool        Ready()  { return g_ready; }
const char* Status() { return g_status.c_str(); }

namespace {
// The helpers are found by shape; this is the proof of behaviour. Until it has
// run (on the game thread, first frame) they are not handed out at all.
bool g_lifetimeVerified = false;
bool g_lifetimeTested   = false;

bool SafeRead(const void* src, void* dst, int n);   // defined with the player tracker

int RefCountOf(const RValue& v) {
    int rc = -1;
    if (v.ptr) SafeRead(static_cast<const char*>(v.ptr) + 8, &rc, 4);
    return rc;
}

// The string probe proves the reference path. This proves the other half: a
// value that holds no reference must come out bit for bit, and its payload
// must never be treated as a pointer. A helper that bumps "+8 of whatever the
// payload points at" without checking the kind would pass the string probe
// and then corrupt memory on the first number; here the payload points at a
// canary instead, so such a helper is caught before any mod runs.
bool CopiesPlainValuesVerbatim() {
    static const std::int32_t kKinds[] = {kReal, kInt32, kInt64, kBool, kPtr, kUndefined};
    alignas(16) std::int32_t canary[16] = {};
    for (const std::int32_t kind : kKinds) {
        RValue src{};
        src.ptr   = canary;
        src.flags = 0;
        src.kind  = kind;
        RValue dst{};
        if (g_copyIsPost) dst = src;
        else { dst.i64 = 0; dst.flags = 0; dst.kind = kUndefined; }
        __try { g_copy(&dst, &src); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        // An int32 only owns its low half; everything else is the full 8 bytes.
        const bool same = (kind == kInt32 ? dst.i32 == src.i32 : dst.i64 == src.i64) &&
                          dst.kind == src.kind && dst.flags == src.flags;
        if (!same) return false;
        for (const std::int32_t c : canary) if (c != 0) return false;
    }
    return true;
}

// FREE's counterpart of the copy canary: freeing a number, bool, pointer or
// undefined must not treat the payload as a pointer to a refcount. The payload
// points at a canary; any write to it means the helper would corrupt memory
// the first time a mod releases a plain value.
bool FreesPlainValuesHarmlessly() {
    static const std::int32_t kKinds[] = {kReal, kInt32, kInt64, kBool, kPtr, kUndefined};
    alignas(16) std::int32_t canary[16] = {};
    for (const std::int32_t kind : kKinds) {
        RValue v{};
        v.ptr   = canary;
        v.flags = 0;
        v.kind  = kind;
        __try { g_free(&v); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        for (const std::int32_t c : canary) if (c != 0) return false;
    }
    return true;
}

std::atomic<DWORD> g_gameThread{0};
} // namespace

void NoteGameThread() {
    DWORD expected = 0;
    if (g_gameThread.compare_exchange_strong(expected, GetCurrentThreadId()))
        Logf("gml: game thread is %lu", static_cast<unsigned long>(GetCurrentThreadId()));
}

// Fails closed: until the first Present names the game thread, nothing is
// allowed to touch GML - there is no game code to call into yet anyway.
bool OnGameThread() {
    const DWORD t = g_gameThread.load(std::memory_order_relaxed);
    return t != 0 && t == GetCurrentThreadId();
}

void VerifyValueLifetime() {
    if (g_lifetimeTested || !g_ready) return;
    g_lifetimeTested = true;
    if (!g_free && !g_copy) return;

    auto fail = [](const char* why) {
        Logf("[!] gml: value lifetime self-test failed (%s) - freeing/copying disabled, values will leak instead",
             why);
        g_free = nullptr;
        g_copy = nullptr;
    };

    static const char kProbe[] = "coreloader-lifetime-probe";   // static: stays valid forever
    RValue a{};
    if (!SetString(a, kProbe) || !a.ptr) { fail("could not build a probe string"); return; }

    // The runtime must treat our characters as external, or freeing any string
    // we built around our own buffer would free that buffer.
    std::int32_t len = 0;
    const char*  chars = nullptr;
    SafeRead(static_cast<const char*>(a.ptr), &chars, sizeof(chars));
    SafeRead(static_cast<const char*>(a.ptr) + 12, &len, 4);
    if (chars != kProbe || (len & 0x80000000) == 0) { fail("string not marked external"); return; }

    const int rc0 = RefCountOf(a);
    if (rc0 != 1) { fail("unexpected initial refcount"); return; }

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
        if (!tookRef || b.kind != kString) {
            Logf("[!] gml: COPY_RValue did not behave like a copy; copying disabled");
            g_copy = nullptr;
        } else if (g_free) {
            __try { g_free(&b); } __except (EXCEPTION_EXECUTE_HANDLER) { fail("free faulted"); return; }
            if (RefCountOf(a) != rc0) { fail("free did not release the copy's reference"); return; }
        }
    }
    if (g_copy && !CopiesPlainValuesVerbatim()) {
        Logf("[!] gml: COPY_RValue mishandles plain values; copying disabled");
        g_copy = nullptr;
    }
    if (g_free && !g_copy) {
        // No verified copy to test with: take a second reference by hand (the
        // probe string is ours alone, so its refcount is ours to set).
        *reinterpret_cast<std::int32_t*>(static_cast<char*>(a.ptr) + 8) = 2;
        RValue b = a;
        __try { g_free(&b); } __except (EXCEPTION_EXECUTE_HANDLER) { fail("free faulted"); return; }
        if (RefCountOf(a) != 1) { fail("free did not release a reference"); return; }
    }
    if (g_free && !FreesPlainValuesHarmlessly()) {
        fail("FREE_RValue touches the payload of plain values");
        return;
    }
    if (g_free) {
        // Never let the probe's count reach zero: that would be the first time
        // the helper releases a string whose characters are OURS (static data),
        // and "it honours the external flag" is exactly what is not proven yet.
        // A spare reference keeps it alive; the 16-byte header simply leaks.
        *reinterpret_cast<std::int32_t*>(static_cast<char*>(a.ptr) + 8) += 1;
        __try { g_free(&a); } __except (EXCEPTION_EXECUTE_HANDLER) { fail("free faulted"); return; }
    }

    g_lifetimeVerified = true;
    Logf("gml: value lifetime self-test passed (free %s, copy %s)", g_free ? "yes" : "no",
         g_copy ? (g_copyIsPost ? "yes, reference half" : "yes") : "no");
}

bool CanFreeValues() { return g_lifetimeVerified && g_free != nullptr; }
bool CanCopyValues() { return g_lifetimeVerified && g_copy != nullptr; }

bool FreeValue(RValue& v) {
    if (!g_lifetimeVerified || !g_free) return false;
    // Structs are garbage-collected, not counted: there is no reference to
    // drop, and the runtime's helpers for them expect to run inside the game's
    // own code (they consult the GC's context).
    if ((v.kind & 0x00FFFFFF) == kObject) {
        v.i64 = 0; v.flags = 0; v.kind = kUndefined;
        return true;
    }
    __try {
        g_free(&v);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Logf("[!] gml: fault freeing a value of kind %d", v.kind);
        return false;
    }
    v.i64 = 0; v.flags = 0; v.kind = kUndefined;
    return true;
}

bool CopyValue(RValue& dst, const RValue& src) {
    if (!g_lifetimeVerified || !g_copy) return false;
    if (&dst == &src) return true;   // already its own copy; clearing dst would clear src
    // A struct is copied bit for bit. The runtime's copy of one only feeds the
    // GC's root bookkeeping, which reads the GC context of the GML code that
    // is running - there is none at Present, where mods mostly run. A copy
    // held outside GML is invisible to the GC either way; the managed side
    // keeps such structs alive by rooting them in a GML array.
    if ((src.kind & 0x00FFFFFF) == kObject) {
        dst = src;
        return true;
    }
    // dst's previous contents are overwritten, never released: starting from
    // undefined keeps the full COPY_RValue from freeing whatever was there.
    if (g_copyIsPost) {
        dst = src;   // kind, flags and plain values; __Post then takes the reference
    } else {
        dst.i64 = 0; dst.flags = 0; dst.kind = kUndefined;
    }
    __try {
        g_copy(&dst, &src);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Logf("[!] gml: fault copying a value of kind %d", src.kind);
        // Never leave dst looking like a value that holds a reference it never took.
        dst.i64 = 0; dst.flags = 0; dst.kind = kUndefined;
        return false;
    }
    return true;
}

void SetReal(RValue& v, double value) {
    v.real  = value;
    v.flags = 0;
    v.kind  = kReal;
}

void SetUndefined(RValue& v) {
    v.i64   = 0;
    v.flags = 0;
    v.kind  = kUndefined;
}

bool SetString(RValue& v, const char* text) {
    if (!g_ready || !g_setString) return false;
    // Note: the game stores `text` by pointer, so the caller's buffer must
    // stay alive until the call that consumes this RValue returns.
    g_setString(&v, text);
    return v.kind == kString;
}

const char* Intern(const std::string& text) {
    static std::mutex lock;
    static std::unordered_set<std::string> pool;   // node-based: c_str() never moves
    std::lock_guard<std::mutex> g(lock);
    return pool.insert(text).first->c_str();
}

std::string ToString(const RValue& v) {
    switch (v.kind) {
    case kReal:   { char b[64]; std::snprintf(b, sizeof(b), "%g", v.real); return b; }
    // Runtimes hold a bool as a 0.0/1.0 double; some have been seen to use the
    // low int instead. A double that is exactly 0 or 1 settles which.
    case kBool:   return (v.real == 1.0 || v.real == 0.0 ? v.real != 0.0 : v.i32 != 0) ? "true" : "false";
    case kInt32:  { char b[32]; std::snprintf(b, sizeof(b), "%d", v.i32); return b; }
    case kInt64:  { char b[32]; std::snprintf(b, sizeof(b), "%lld", static_cast<long long>(v.i64)); return b; }
    case kUndefined: return "<undefined>";
    case kRef:       return "<ref>";
    case kUnset:     return "<unset>";
    case kString: {
        const auto* rs = static_cast<const RefString*>(v.ptr);
        if (!rs || !rs->chars) return "<null string>";
        const int len = rs->length & 0x7FFFFFFF;
        if (len < 0 || len > (1 << 20)) return "<bad string>";
        return std::string(rs->chars, static_cast<std::size_t>(len));
    }
    default: {
        char b[64];
        std::snprintf(b, sizeof(b), "<kind %d>", v.kind);
        return b;
    }
    }
}

namespace {

// 0xE06D7363 is a C++ throw. When the game's GML runtime rejects a call it
// raises one of these rather than faulting, and the thrown object carries the
// error text - which is far more useful than "it failed". A vectored handler
// lets us read that text without disturbing normal exception handling: we only
// look, then always continue the search.
constexpr DWORD kCppException = 0xE06D7363;

thread_local int   g_inCall = 0;   // depth: calls nest (a hook can call back into the game)
thread_local char  g_lastError[512] = {};

bool ReadableString(const char* p, std::size_t minLen) {
    if (!p) return false;
    __try {
        std::size_t i = 0;
        for (; i < 300; ++i) {
            const auto c = static_cast<unsigned char>(p[i]);
            if (c == 0) break;
            if (c < 0x09 || c > 0x7e) return false;
        }
        return i >= minLen;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

LONG CALLBACK ExceptionProbe(EXCEPTION_POINTERS* info) {
    if (g_inCall <= 0) return EXCEPTION_CONTINUE_SEARCH;
    const EXCEPTION_RECORD* er = info->ExceptionRecord;
    if (er->ExceptionCode != kCppException) return EXCEPTION_CONTINUE_SEARCH;
    if (er->NumberParameters < 2) return EXCEPTION_CONTINUE_SEARCH;

    // ExceptionInformation[1] points at the thrown object. Rather than decode
    // MSVC's throw metadata, scan the first few slots for a pointer to text.
    auto* obj = reinterpret_cast<const char* const*>(er->ExceptionInformation[1]);
    if (!obj) return EXCEPTION_CONTINUE_SEARCH;

    __try {
        for (int i = 0; i < 8; ++i) {
            const char* candidate = obj[i];
            if (ReadableString(candidate, 8)) {
                std::snprintf(g_lastError, sizeof(g_lastError), "%s", candidate);
                return EXCEPTION_CONTINUE_SEARCH;
            }
        }
        // Some throws hold the text inline.
        if (ReadableString(reinterpret_cast<const char*>(obj), 8))
            std::snprintf(g_lastError, sizeof(g_lastError), "%s",
                          reinterpret_cast<const char*>(obj));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

std::once_flag g_vehOnce;

thread_local int t_managedDepth = 0;

// What a guarded call into the game does with an exception from it:
//   * a GML exception (a C++ throw) is left to the game's own try/catch when
//     one can be above us - inside a hook dispatch with only native frames
//     between (it must never unwind through .NET frames);
//   * everything else - access violations, stack overflow, a GML exception
//     with nobody to catch it - is handled here and the call reports failure.
int GuardFilter(DWORD code) {
    if (code == kCppException && hk::DispatchDepth() > 0 && t_managedDepth == 0)
        return EXCEPTION_CONTINUE_SEARCH;
    return EXCEPTION_EXECUTE_HANDLER;
}

// After a handled stack overflow the guard page is gone; without it the next
// overflow would kill the process without a trace.
void AfterGuardedFault(DWORD code) {
    if (code == EXCEPTION_STACK_OVERFLOW) _resetstkoflw();
}

// The fault guards live in functions of their own: __try cannot share a
// function with objects that need unwinding (InCall below).
bool GuardedScript(ScriptFn fn, void* self, void* other, RValue* result, int argc, RValue** args,
                   DWORD* code) {
    __try {
        fn(self, other, result, argc, args);
        return true;
    } __except (GuardFilter(*code = GetExceptionCode())) {
        return false;
    }
}

bool GuardedEvent(EventFn fn, void* self, void* other, DWORD* code) {
    __try {
        fn(self, other);
        return true;
    } __except (GuardFilter(*code = GetExceptionCode())) {
        return false;
    }
}

struct InCall {
    InCall()  { ++g_inCall; }
    ~InCall() { --g_inCall; }
    InCall(const InCall&) = delete;
    InCall& operator=(const InCall&) = delete;
};

} // namespace

ManagedScope::ManagedScope()  { ++t_managedDepth; }
ManagedScope::~ManagedScope() { --t_managedDepth; }
bool InManagedCode() { return t_managedDepth > 0; }

const char* LastError() { return g_lastError; }

bool Call(void* func, RValue* result, RValue** args, int argc) {
    // Prefer a self/other pair recorded from a genuine call - many scripts only
    // behave when run as the right instance. Otherwise borrow whatever instance
    // the game last ran code as, since a null self would fault.
    void* self  = CurrentSelf();
    void* other = self;
    if (g_useCaptured && g_capture.valid && g_capture.self) {
        self  = g_capture.self;
        other = g_capture.other;
    }
    return CallAs(func, result, args, argc, self, other);
}

bool CallAs(void* func, RValue* result, RValue** args, int argc, void* self, void* other) {
    if (!g_ready || !func || !result) return false;

    std::call_once(g_vehOnce, [] { AddVectoredExceptionHandler(1, &ExceptionProbe); });
    g_lastError[0] = '\0';

    result->ptr   = nullptr;
    result->flags = 0;
    result->kind  = kUnset;

    DWORD code = 0;
    bool ok;
    {
        InCall scope;   // restored even if a GML exception passes through to the game
        ok = GuardedScript(reinterpret_cast<ScriptFn>(func), self, other, result, argc, args, &code);
    }
    if (ok) return true;
    AfterGuardedFault(code);
    if (g_lastError[0])
        Logf("[!] gml: %s rejected the call: %s",
             code == kCppException ? "the game" : "fault", g_lastError);
    else
        Logf("[!] gml: exception 0x%08lX calling %p (no message recovered)", code, func);
    return false;
}

bool CallByName(const std::string& symbol, RValue* result, RValue** args, int argc) {
    void* fn = sym::Find(symbol);
    if (!fn) {
        Logf("[!] gml: symbol not found: %s", symbol.c_str());
        return false;
    }
    return Call(fn, result, args, argc);
}

// ------------------------------------------------------------- observation

namespace {

// Scripts and events share one handler through the hook engine: an event is
// simply a call without arguments.
void CaptureBefore(hk::Call* c, void*) {
    void* self = c->self;
    void* other = c->other;
    const int argc = c->argc;
    RValue** args = c->args;
    // Who invoked us matters as much as the arguments: it names the routine that
    // actually builds the thing we are trying to reproduce.
    const char* caller  = sym::OwnerOf(hk::CurrentCaller());
    g_capture.self  = self;
    g_capture.other = other;
    g_capture.argc  = argc;
    g_capture.args.clear();
    g_capture.raw.clear();
    ++g_capture.hits;

    if (args) {
        for (int i = 0; i < argc && i < 12; ++i) {
            char line[160];
            if (args[i]) {
                std::snprintf(line, sizeof(line), "kind=%d %s",
                              args[i]->kind, ToString(*args[i]).c_str());
            } else {
                std::snprintf(line, sizeof(line), "<null>");
            }
            g_capture.args.emplace_back(line);
            // Keep the exact value too: argument 1 is a REF, which we cannot
            // construct ourselves and can only replay.
            g_capture.raw.push_back(args[i] ? *args[i] : RValue{});
        }
    }
    g_capture.valid  = true;
    g_capture.caller = caller ? caller : "<unknown>";

    // The game calls this often; a few samples are plenty and keep the log usable.
    if (g_capture.hits <= 5) {
        Logf("capture: %s called by the game - self=%p other=%p argc=%d",
             g_capture.symbol.c_str(), self, other, argc);
        Logf("capture:    <- called from %s", caller ? caller : "<unknown>");
        for (std::size_t i = 0; i < g_capture.args.size(); ++i)
            Logf("capture:    arg[%zu] %s", i, g_capture.args[i].c_str());
    }
}

// Records every scr_weapon_loot the game makes, so a replay is always available
// without the player having to run a capture by hand.
void WeaponRecBefore(hk::Call* c, void*) {
    void* self = c->self;
    void* other = c->other;
    const int argc = c->argc;
    RValue** args = c->args;
    if (args && argc > 0) {
        const int n = argc > 16 ? 16 : argc;
        g_weaponRec.raw.assign(static_cast<std::size_t>(n), RValue{});
        for (int i = 0; i < n; ++i)
            if (args[i]) g_weaponRec.raw[static_cast<std::size_t>(i)] = *args[i];
        g_weaponRec.self  = self;
        g_weaponRec.other = other;
        g_weaponRec.argc  = argc;
        if (!g_weaponRec.valid)
            Logf("weapon recorder: first sample captured (argc=%d)", argc);
        g_weaponRec.valid = true;
        // A real call gives an instance together with the exact x/y it was
        // handed - a known answer for locating the position fields.
        if (argc > 2 && args[1] && args[2] &&
            args[1]->kind == kReal && args[2]->kind == kReal)
            CalibrateFromKnown(self, args[1]->real, args[2]->real);
        ++g_weaponRec.hits;
    }
}

bool g_abiTested = false;
bool g_abiProven = false;

} // namespace

bool InstallCapture(const std::string& symbol) {
    void* fn = sym::Find(symbol);
    if (!fn) {
        fn = sym::Find("gml_Script_" + symbol);
        if (!fn) { Logf("[!] capture: symbol not found: %s", symbol.c_str()); return false; }
    }
    // Re-targeting must work: the whole point of the tool is to move it from one
    // function to the next while hunting a signature.
    if (g_capturedFn) {
        if (g_capturedFn == fn) {
            Logf("capture: already watching %s - resetting samples", symbol.c_str());
            const std::string keep = g_capture.symbol;
            g_capture = Capture{};
            g_capture.symbol = keep;
            return true;
        }
        hk::RemoveNative(g_captureHook, &CaptureBefore, nullptr, nullptr);
        Logf("capture: stopped watching %s", g_capture.symbol.c_str());
        g_capturedFn  = nullptr;
        g_captureHook = -1;
    }

    const bool isEvent = IsEventSymbol(symbol);
    // Through the shared hook engine, so capturing never fights a mod's hook
    // on the same function.
    g_captureHook = hk::AddNative(fn, isEvent ? hk::Kind::Event : hk::Kind::Script,
                                  &CaptureBefore, nullptr, nullptr);
    if (g_captureHook < 0) {
        Logf("[!] capture: could not hook %s", symbol.c_str());
        return false;
    }

    g_capturedFn      = fn;
    g_capturedIsEvent = isEvent;
    g_capture         = Capture{};
    g_capture.symbol  = symbol;
    Logf("capture: watching %s at %p - trigger it in game", symbol.c_str(), fn);
    return true;
}

bool IsEventSymbol(const std::string& symbol) {
    return symbol.rfind("gml_Object_", 0) == 0 || symbol.rfind("gml_RoomCC_", 0) == 0;
}

bool CallEvent(void* func, void* self, void* other) {
    if (!g_ready || !func) return false;

    // No instance given: fall back the same way Call() does, since an event
    // always runs as some instance and a null self would fault immediately.
    if (!self) {
        if (g_useCaptured && g_capture.valid && g_capture.self) {
            self  = g_capture.self;
            other = g_capture.other;
        } else {
            self = CurrentSelf();
        }
    }
    if (!self) return false;

    std::call_once(g_vehOnce, [] { AddVectoredExceptionHandler(1, &ExceptionProbe); });
    g_lastError[0] = '\0';

    DWORD code = 0;
    bool ok;
    {
        InCall scope;
        ok = GuardedEvent(reinterpret_cast<EventFn>(func), self, other ? other : self, &code);
    }
    if (ok) return true;
    AfterGuardedFault(code);
    if (g_lastError[0]) Logf("[!] gml: event rejected: %s", g_lastError);
    else                Logf("[!] gml: exception 0x%08lX in event %p", code, func);
    return false;
}

const Capture& LastCapture()  { return g_capture; }
const Capture& WeaponRecord() { return g_weaponRec; }

// ---------------------------------------------------------------- player

namespace {

void*    g_playerInst       = nullptr;
ULONGLONG g_playerSeenAt    = 0;     // GetTickCount64 of o_player's last Step
bool     g_playerTracking   = false;
int      g_posOffset        = -1;    // byte offset of x inside the CInstance
bool     g_posExact         = false; // true once derived from a known-answer sample
bool     g_posFromReflection = false;
int      g_posCandidate     = -1;
int      g_posCandidateHits = 0;

constexpr int kSnapshotBytes = 0x200;
unsigned char g_prevSnap[kSnapshotBytes];
bool          g_haveSnap = false;

// World coordinates are hundreds to thousands of pixels. Rejecting zero and
// near-zero matters: the first attempt at this latched onto a field that merely
// wobbled around 0.0, and the game then dropped items at the tile origin.
bool PlausibleCoord(double v) {
    if (v != v) return false;                       // NaN
    const double a = v < 0.0 ? -v : v;
    return a > 8.0 && a < 1.0e6;
}

// Reads instance bytes defensively: the allocation may be smaller than our
// window, and faulting inside a per-frame hook would take the game down.
bool SafeRead(const void* src, void* dst, int n) {
    __try {
        std::memcpy(dst, src, static_cast<std::size_t>(n));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Exact calibration. A recorded scr_weapon_loot call hands us an instance plus
// the very x/y it was given, so the offset can be found by matching rather than
// guessing. This always wins over the movement heuristic below.
void CalibrateFromKnown(const void* inst, double x, double y) {
    if (g_posExact || !inst) return;
    if (!PlausibleCoord(x) || !PlausibleCoord(y)) return;

    unsigned char buf[kSnapshotBytes];
    if (!SafeRead(inst, buf, kSnapshotBytes)) return;

    for (int off = 0; off + 16 <= kSnapshotBytes; off += 8) {
        double a, b;
        std::memcpy(&a, buf + off, 8);
        std::memcpy(&b, buf + off + 8, 8);
        if (a == x && b == y) {
            g_posOffset = off;
            g_posExact  = true;
            Logf("player tracker: position offset 0x%X confirmed against a real spawn "
                 "(x=%.1f y=%.1f)", off, x, y);
            return;
        }
    }
}

// Fallback heuristic. The first version of this took the first offset that
// looked like movement and stopped there, so an unrelated low field always won
// and the real coordinates were never reached. Instead, score every candidate
// across many frames and take the one that consistently behaves like a
// position - adjacent doubles, sane magnitude, small per-frame deltas.
constexpr int kMaxCandidates = 64;
int g_candOff[kMaxCandidates];
int g_candHits[kMaxCandidates];
int g_candCount  = 0;
int g_calibFrames = 0;

void NoteCandidate(int off) {
    for (int i = 0; i < g_candCount; ++i)
        if (g_candOff[i] == off) { ++g_candHits[i]; return; }
    if (g_candCount < kMaxCandidates) {
        g_candOff[g_candCount]  = off;
        g_candHits[g_candCount] = 1;
        ++g_candCount;
    }
}

void CalibratePosition(const void* inst) {
    if (g_posExact || g_posOffset >= 0 || !inst) return;

    unsigned char cur[kSnapshotBytes];
    if (!SafeRead(inst, cur, kSnapshotBytes)) return;

    if (!g_haveSnap) {
        std::memcpy(g_prevSnap, cur, kSnapshotBytes);
        g_haveSnap = true;
        return;
    }

    bool moved = false;
    for (int off = 0; off + 16 <= kSnapshotBytes; off += 8) {
        double oldX, newX, oldY, newY;
        std::memcpy(&oldX, g_prevSnap + off, 8);
        std::memcpy(&newX, cur + off, 8);
        std::memcpy(&oldY, g_prevSnap + off + 8, 8);
        std::memcpy(&newY, cur + off + 8, 8);

        if (!PlausibleCoord(newX) || !PlausibleCoord(newY)) continue;
        if (!PlausibleCoord(oldX) || !PlausibleCoord(oldY)) continue;

        const double dx = newX - oldX, dy = newY - oldY;
        if (dx == 0.0 && dy == 0.0) continue;                  // needs movement
        if (dx < -64.0 || dx > 64.0 || dy < -64.0 || dy > 64.0) continue;

        NoteCandidate(off);
        moved = true;
    }
    std::memcpy(g_prevSnap, cur, kSnapshotBytes);
    if (!moved) return;

    // Decide once there is enough evidence, and only if one candidate clearly wins.
    if (++g_calibFrames < 40) return;

    int best = -1, bestHits = 0, secondHits = 0;
    for (int i = 0; i < g_candCount; ++i) {
        if (g_candHits[i] > bestHits) { secondHits = bestHits; best = g_candOff[i]; bestHits = g_candHits[i]; }
        else if (g_candHits[i] > secondHits) { secondHits = g_candHits[i]; }
    }
    if (best < 0 || bestHits < 12 || bestHits < secondHits + 4) {
        g_calibFrames = 0;                                     // keep gathering
        return;
    }

    double vx, vy;
    std::memcpy(&vx, cur + best, 8);
    std::memcpy(&vy, cur + best + 8, 8);
    g_posOffset = best;
    Logf("player tracker: position at instance+0x%X by movement (x=%.1f y=%.1f, %d/%d votes)",
         best, vx, vy, bestHits, secondHits);
}

void PlayerStepBefore(hk::Call* c, void*) {
    void* self = c->self;
    if (self) {
        if (!g_playerInst) Logf("player tracker: player instance %p", self);
        g_playerInst = self;
        g_playerSeenAt = GetTickCount64();
        CalibratePosition(self);

        // Feeds the activity log so movement can be lined up against the
        // script trace on a shared clock.
        double px = 0.0, py = 0.0;
        const bool havePos = PlayerPosition(px, py);
        tracer::NotePlayerStep(px, py, havePos);
    }
}

} // namespace

// Every page of [p, p+n) committed, readable and not a guard page. Touching a
// guard page (a thread's stack growth sentinel) would fault once and silently
// disarm it; that thread then dies later with no stack left to grow into.
static bool PlainReadable(const void* p, std::size_t n) {
    auto at = reinterpret_cast<std::uintptr_t>(p);
    const std::uintptr_t end = at + n;
    if (end < at) return false;
    while (at < end) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<const void*>(at), &mbi, sizeof(mbi))) return false;
        if (mbi.State != MEM_COMMIT) return false;
        const DWORD prot = mbi.Protect;
        if (prot & (PAGE_GUARD | PAGE_NOACCESS)) return false;
        if (!(prot & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ |
                      PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
        at = reinterpret_cast<std::uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
    }
    return true;
}

bool ReadMemory(const void* src, void* dst, int bytes) {
    if (!src || !dst || bytes <= 0 || bytes > 1 << 20) return false;
    if (!PlainReadable(src, static_cast<std::size_t>(bytes))) return false;
    return SafeRead(src, dst, bytes);
}

// The player is only known while its Step runs. After quitting to the menu or
// through a room/save transition the instance may be destroyed and its memory
// reused - writing through a remembered pointer would land in another object.
void* PlayerInstance() {
    if (g_playerInst && GetTickCount64() - g_playerSeenAt > 1000) {
        Logf("player tracker: o_player stopped stepping; forgetting %p", g_playerInst);
        g_playerInst = nullptr;
    }
    return g_playerInst;
}

namespace {
std::atomic<void*> g_observedSelf{nullptr};
}

bool HasSelfGlobal() { return g_pCurrentSelf != nullptr; }

void NoteSelf(void* self) {
    if (self) g_observedSelf.store(self, std::memory_order_relaxed);
}

void ClearObservedSelf() { g_observedSelf.store(nullptr, std::memory_order_relaxed); }

// The runtime's own global when it has one; otherwise the instance a hook saw
// most recently.
// A CInstance is a C++ object: its first word is a vtable in the image's
// .rdata. The self global was found by vote, and a wrong winner (some other
// slot stored from rcx in prologues) would hand out pointers scripts then
// WRITE instance variables through; this check turns that into "no self".
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

bool PlayerPosition(double& x, double& y) {
    if (!PlayerInstance()) return false;

    // Preferred: ask the game for "x"/"y" by NAME through GameMaker's reflection
    // API. Two earlier attempts guessed the CInstance layout by watching which
    // doubles changed while walking; both latched onto the wrong field. Reading
    // the named variable removes the guesswork entirely, and it stays correct
    // across game updates because nothing is pinned to an offset.
    gml::RValue vx{}, vy{};
    if (builtins::GetInstanceVar(g_playerInst, "x", &vx) &&
        builtins::GetInstanceVar(g_playerInst, "y", &vy) &&
        vx.kind == kReal && vy.kind == kReal) {
        if (!g_posFromReflection) {
            Logf("player position via reflection: x=%.2f y=%.2f", vx.real, vy.real);
            g_posFromReflection = true;
        }
        x = vx.real;
        y = vy.real;
        return true;
    }

    // Fallback: the offset the movement heuristic settled on, if it ever did.
    if (g_posOffset < 0) return false;
    const auto* base = static_cast<const unsigned char*>(g_playerInst) + g_posOffset;
    double bx, by;
    if (!SafeRead(base, &bx, 8) || !SafeRead(base + 8, &by, 8)) return false;
    if (!PlausibleCoord(bx) || !PlausibleCoord(by)) return false;
    x = bx;
    y = by;
    return true;
}

bool InstallPlayerTracker() {
    if (g_playerTracking) return true;
    void* fn = sym::Find("gml_Object_o_player_Step_0");
    if (!fn) { Logf("[!] player tracker: o_player Step not found"); return false; }

    // Shared hook engine: C# mods can hook o_player's Step alongside this.
    if (hk::AddNative(fn, hk::Kind::Event, &PlayerStepBefore, nullptr, nullptr) < 0) {
        Logf("[!] player tracker: hook failed");
        return false;
    }
    g_playerTracking = true;
    Logf("player tracker: watching o_player Step");
    return true;
}

bool InstallWeaponRecorder() {
    if (g_weaponRecording) return true;
    void* fn = sym::Find("gml_Script_scr_weapon_loot");
    if (!fn) { Logf("[!] weapon recorder: scr_weapon_loot not found"); return false; }

    if (hk::AddNative(fn, hk::Kind::Script, &WeaponRecBefore, nullptr, nullptr) < 0) {
        Logf("[!] weapon recorder: hook failed");
        return false;
    }
    g_weaponRecording = true;
    g_weaponRec.symbol = "scr_weapon_loot";
    Logf("weapon recorder: watching scr_weapon_loot");
    return true;
}

void SetUseCapturedContext(bool on) { g_useCaptured = on; }
bool UseCapturedContext()           { return g_useCaptured; }

bool AbiProven() { return g_abiProven; }

void AbiSelfTest() {
    if (g_abiTested || !g_ready) return;
    g_abiTested = true;

    // Wait until the game has actually run some GML, otherwise the borrowed
    // `self` global is still null.
    if (!CurrentSelf()) {
        g_abiTested = false;   // try again next frame
        return;
    }

    Logf("gml: --- ABI self-test (self=%p) ---", CurrentSelf());

    int passed = 0;
    int ran    = 0;

    // The two script probes are Stoneshard scripts. In another YYC game they
    // simply do not exist, which is "not probed", not "failed" - the verdict
    // is taken over the checks that could actually run.

    // 1) Zero-argument call. Only probes that do not need a loaded character,
    //    since at the main menu there is no player and game-state lookups fault.
    if (sym::Find("gml_Script_scr_console_sethp_help")) {
        ++ran;
        RValue result{};
        if (CallByName("gml_Script_scr_console_sethp_help", &result, nullptr, 0)) {
            Logf("gml:   no-arg call        -> kind=%d (%s)",
                 result.kind, ToString(result).c_str());
            if (result.kind != kUnset) ++passed;
        } else {
            Logf("gml:   no-arg call        -> FAILED");
        }
    }

    // 2) Argument marshalling, using a pure numeric helper so nothing in the
    //    game's state is involved: approach(0, 10, 3) must return 3.
    if (sym::Find("gml_Script_scr_approach")) {
        ++ran;
        RValue a{}, b{}, c{};
        SetReal(a, 0.0);
        SetReal(b, 10.0);
        SetReal(c, 3.0);
        RValue* args[3] = {&a, &b, &c};

        RValue result{};
        if (CallByName("gml_Script_scr_approach", &result, args, 3)) {
            Logf("gml:   approach(0,10,3)   -> kind=%d value=%s",
                 result.kind, ToString(result).c_str());
            if (result.kind == kReal && result.real == 3.0) ++passed;
        } else {
            Logf("gml:   approach(0,10,3)   -> FAILED");
        }
    }

    // 3) String construction round-trip through the game's own allocator.
    {
        ++ran;
        static const char kProbe[] = "stoneshard-mod";   // must outlive the call
        RValue s{};
        if (SetString(s, kProbe)) {
            const std::string back = ToString(s);
            Logf("gml:   string round-trip  -> kind=%d value=\"%s\"", s.kind, back.c_str());
            if (back == kProbe) ++passed;
        } else {
            Logf("gml:   string round-trip  -> FAILED");
        }
    }

    // Stoneshard keeps its old bar (two of three); with only the string check
    // available, that one has to pass.
    g_abiProven = ran >= 3 ? passed >= 2 : passed == ran;
    Logf("gml: --- ABI self-test %s (%d/%d checks%s) ---",
         g_abiProven ? "PASSED" : "FAILED", passed, ran,
         ran < 3 ? "; script probes absent in this game" : "");
}

} // namespace mod::gml
