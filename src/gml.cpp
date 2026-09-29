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
#include "builtins.h"
#include "hookengine.h"
#include "log.h"
#include "symbols.h"

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

// Whether the image carries an RTTI type of this decorated name. Type
// descriptors sit in .data or .rdata depending on the toolchain.
bool HasTypeName(const char* decorated) {
    const std::size_t n = std::strlen(decorated) + 1;   // with the terminator
    for (const auto range : {sym::DataRange(), sym::RdataRange()}) {
        if (!range.hi) continue;
        for (auto p = reinterpret_cast<const char*>(range.lo); p + n <= reinterpret_cast<const char*>(range.hi);) {
            p = static_cast<const char*>(std::memchr(p, decorated[0], reinterpret_cast<const char*>(range.hi) - p));
            if (!p || p + n > reinterpret_cast<const char*>(range.hi)) break;
            if (std::memcmp(p, decorated, n) == 0) return true;
            ++p;
        }
    }
    return false;
}

} // namespace

bool ScanInstanceLookup();   // below, with the rest of the id lookup

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

    // Neither is needed for the bridge itself; each reports its own status.
    // Both run before g_ready is published: the game thread starts using what
    // they set (the id table, the lookup state) as soon as the bridge is ready.
    Logf("gml: GML error text: %s", HasTypeName(".?AVYYGMLException@@")
             ? "the runtime throws YYGMLException; failed calls report its message"
             : "no YYGMLException in this runtime; failed calls report the exception type or code only");
    if (ScanInstanceLookup()) Logf("gml: instance lookup: table found; proven on the game thread before use");

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

// Reads game memory defensively: an allocation may be smaller than the window
// read, and a fault here must not take the game down.
bool SafeRead(const void* src, void* dst, int n) {
    __try {
        std::memcpy(dst, src, static_cast<std::size_t>(n));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

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
// error - which is far more useful than "it failed". A vectored handler sees
// the throw first and only looks: it copies what it needs and always lets the
// search continue, so the game's own try/catch works exactly as before.
//
// The throw's parameters are MSVC's: [1] the thrown object, [2] its ThrowInfo,
// [3] the image base the ThrowInfo's RVAs are relative to. ThrowInfo leads to a
// CatchableTypeArray whose first entry is the most-derived type, each with an
// RTTI TypeDescriptor naming it (".?AVYYGMLException@@"). Both runtimes seen so
// far (Stoneshard's and 2024.14) throw YYGMLException for a GML runtime error
// and for GML's `throw`: a 16-byte object that is just the thrown RValue - for
// a runtime error a struct with message, longMessage, script, line and
// stacktrace, built by the runtime's error reporter just before the throw.
constexpr DWORD kCppException = 0xE06D7363;

thread_local int   g_inCall = 0;   // depth: calls nest (a hook can call back into the game)
thread_local char  g_lastError[512] = {};

// What the last C++ throw during a guarded call carried. Filled by the
// vectored handler, so only plain data and fault-guarded reads.
struct Thrown {
    bool   valid = false;
    std::uintptr_t object = 0;  // the thrown object (ExceptionInformation[1])
    char   type[96] = {};       // most-derived type, demangled ("YYGMLException")
    bool   hasValue = false;    // a YYGMLException: `value` is its RValue
    RValue value{};
    char   what[256] = {};      // a std::exception: what()
};
thread_local Thrown t_thrown;

// The thrown object that last reached one of our guards' handlers (0 for a
// fault that was not a C++ throw). Set by NoteHandled from the filter.
thread_local std::uintptr_t t_handledObject = 0;

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

// ".?AVYYGMLException@@" -> "YYGMLException", ".?AVbad_alloc@std@@" ->
// "std::bad_alloc". Templates and anything unusual keep the decorated name.
// No allocation: this runs inside the vectored handler.
void Demangle(const char* decorated, char* out, std::size_t cap) {
    std::snprintf(out, cap, "%s", decorated);
    if (std::strncmp(decorated, ".?AV", 4) != 0 && std::strncmp(decorated, ".?AU", 4) != 0) return;
    const char* body = decorated + 4;
    const std::size_t len = std::strlen(body);
    if (len < 3 || std::strcmp(body + len - 2, "@@") != 0 || std::strchr(body, '?')) return;
    // Name parts are innermost first: "bad_alloc@std" is std::bad_alloc, so
    // they are emitted from the last one back.
    std::size_t end = len - 2, n = 0;
    while (end > 0 && n + 1 < cap) {
        std::size_t start = end;
        while (start > 0 && body[start - 1] != '@') --start;
        if (n > 0) n += static_cast<std::size_t>(std::snprintf(out + n, cap - n, "::"));
        if (n + 1 >= cap) break;
        n += static_cast<std::size_t>(std::snprintf(out + n, cap - n, "%.*s", static_cast<int>(end - start), body + start));
        end = start > 0 ? start - 1 : 0;
    }
    out[std::min(n, cap - 1)] = '\0';
}

// Everything is read through SafeRead: the parameters come from whoever threw,
// and a malformed record must cost nothing but the message.
void DecodeThrow(const EXCEPTION_RECORD* er) {
    Thrown& t = t_thrown;
    // A throw this cannot decode must not leave an earlier one's record to be
    // read (or released) as if it were this one's.
    if (er->NumberParameters < 4) { t.valid = false; return; }   // x64 throws carry the image base
    const auto obj  = static_cast<std::uintptr_t>(er->ExceptionInformation[1]);
    const auto info = static_cast<std::uintptr_t>(er->ExceptionInformation[2]);
    const auto base = static_cast<std::uintptr_t>(er->ExceptionInformation[3]);
    if (!obj && !info) return;   // a rethrow (`throw;`) keeps what the first throw recorded
    t.valid = false;
    if (!obj || !info || !base) return;

    std::int32_t ctaRva = 0, count = 0;
    if (!SafeRead(reinterpret_cast<const void*>(info + 12), &ctaRva, 4) || ctaRva <= 0) return;
    const std::uintptr_t cta = base + static_cast<std::uint32_t>(ctaRva);
    if (!SafeRead(reinterpret_cast<const void*>(cta), &count, 4) || count <= 0 || count > 32) return;

    // Written in place: a throw from deep recursion reaches this handler with
    // little stack left, so no copy of the record lives on it.
    t.object = obj;
    t.type[0] = '\0';
    t.hasValue = false;
    t.what[0] = '\0';
    for (std::int32_t i = 0; i < count; ++i) {
        std::int32_t ctRva = 0;
        if (!SafeRead(reinterpret_cast<const void*>(cta + 4 + 4 * i), &ctRva, 4) || ctRva <= 0) return;
        // CatchableType: properties, pType, thisDisplacement {mdisp, pdisp, vdisp}, sizeOrOffset, copyFunction.
        struct { std::uint32_t props; std::int32_t type, mdisp, pdisp, vdisp, size, copy; } ct{};
        if (!SafeRead(reinterpret_cast<const void*>(base + static_cast<std::uint32_t>(ctRva)), &ct, sizeof(ct)) ||
            ct.type <= 0)
            return;
        // TypeDescriptor: vtable, spare, then the decorated name.
        char name[96] = {};
        const char* namePtr = reinterpret_cast<const char*>(base + static_cast<std::uint32_t>(ct.type) + 16);
        if (!ReadableString(namePtr, 4) || !SafeRead(namePtr, name, sizeof(name) - 1)) return;
        name[sizeof(name) - 1] = '\0';

        if (i == 0) Demangle(name, t.type, sizeof(t.type));
        if (std::strcmp(name, ".?AVYYGMLException@@") == 0 && ct.size == sizeof(RValue) && ct.mdisp == 0 &&
            SafeRead(reinterpret_cast<const void*>(obj), &t.value, sizeof(RValue)))
            t.hasValue = true;
        // std::exception: vtable, then {const char* what; bool doFree}.
        if (std::strcmp(name, ".?AVexception@std@@") == 0 && ct.mdisp >= 0) {
            const char* what = nullptr;
            if (SafeRead(reinterpret_cast<const void*>(obj + ct.mdisp + 8), &what, sizeof(what)) &&
                ReadableString(what, 1))
                SafeRead(what, t.what, static_cast<int>(strnlen(what, sizeof(t.what) - 1)));
        }
    }
    t.valid = true;
}

LONG CALLBACK ExceptionProbe(EXCEPTION_POINTERS* info) {
    if (g_inCall <= 0) return EXCEPTION_CONTINUE_SEARCH;
    const EXCEPTION_RECORD* er = info->ExceptionRecord;
    if (er->ExceptionCode != kCppException) return EXCEPTION_CONTINUE_SEARCH;
    __try {
        DecodeThrow(er);
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
int GuardFilter(DWORD code, EXCEPTION_POINTERS* info) {
    if (code == kCppException && hk::DispatchDepth() > 0 && t_managedDepth == 0)
        return EXCEPTION_CONTINUE_SEARCH;
    NoteHandled(info);
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
    } __except (GuardFilter(*code = GetExceptionCode(), GetExceptionInformation())) {
        return false;
    }
}

bool GuardedEvent(EventFn fn, void* self, void* other, DWORD* code) {
    __try {
        fn(self, other);
        return true;
    } __except (GuardFilter(*code = GetExceptionCode(), GetExceptionInformation())) {
        return false;
    }
}

thread_local bool t_explaining = false;

// The text a thrown value stands for. A GML runtime error is a struct: its
// `message` is the one-line reason, and `script`/`line` say where. Anything
// else a script threw (`throw "text"`, a number, a user struct) is shown as is.
std::string ThrownText(const RValue& v, void* self) {
    const std::int32_t kind = v.kind & 0x00FFFFFF;
    if (kind != kObject) return kind == kUnset ? std::string() : ToString(v);

    // Members are read through the runtime (variable_struct_get): the struct's
    // layout is the runtime's business. A read that fails leaves that part out.
    auto member = [&](const char* name) {
        RValue out{};
        if (!builtins::StructGet(v, name, &out, self)) return std::string();
        std::string s = (out.kind & 0x00FFFFFF) == kString || (out.kind & 0x00FFFFFF) == kReal ? ToString(out)
                                                                                              : std::string();
        FreeValue(out);   // variable_struct_get hands out its own reference
        return s;
    };
    std::string text = member("message");
    if (text.empty()) text = member("longMessage");
    if (text.empty()) return "a struct was thrown";
    const std::string script = member("script");
    if (!script.empty()) {
        const std::string line = member("line");
        text += " (in " + script + (line.empty() || line == "0" ? "" : ", line " + line) + ")";
    }
    return text;
}

const char* Describe(DWORD code) {
    switch (code) {
    case EXCEPTION_ACCESS_VIOLATION: return "access violation";
    case EXCEPTION_STACK_OVERFLOW:   return "stack overflow";
    case EXCEPTION_INT_DIVIDE_BY_ZERO: return "integer division by zero";
    case EXCEPTION_ILLEGAL_INSTRUCTION: return "illegal instruction";
    default: return "exception";
    }
}

} // namespace

ManagedScope::ManagedScope()  { ++t_managedDepth; }
ManagedScope::~ManagedScope() { --t_managedDepth; }
bool InManagedCode() { return t_managedDepth > 0; }

const char* LastError() { return g_lastError; }
void        ClearLastError() { g_lastError[0] = '\0'; }

ErrorProbe::ErrorProbe() {
    std::call_once(g_vehOnce, [] { AddVectoredExceptionHandler(1, &ExceptionProbe); });
    g_lastError[0] = '\0';
    t_thrown.valid = false;
    t_handledObject = 0;
    ++g_inCall;
}

// Runs inside an __except filter: plain reads only.
void NoteHandled(const void* exceptionPointers) {
    const auto* info = static_cast<const EXCEPTION_POINTERS*>(exceptionPointers);
    const EXCEPTION_RECORD* er = info ? info->ExceptionRecord : nullptr;
    t_handledObject = er && er->ExceptionCode == kCppException && er->NumberParameters >= 2
                          ? static_cast<std::uintptr_t>(er->ExceptionInformation[1])
                          : 0;
}

ErrorProbe::~ErrorProbe() { --g_inCall; }

const char* ExplainFailure(unsigned long code, void* self) {
    // Taken before anything else runs: reading a struct member is itself a
    // guarded call, and its probe starts by clearing what was recorded.
    Thrown thrown = t_thrown;
    t_thrown.valid = false;
    const std::uintptr_t handled = t_handledObject;
    t_handledObject = 0;

    std::string text;
    if (code == kCppException && thrown.valid) {
        // Explaining a failure while explaining one would recurse for as long
        // as reading the error struct itself throws; the inner one gets no text.
        if (thrown.hasValue && !t_explaining && Ready()) {
            t_explaining = true;
            text = ThrownText(thrown.value, self ? self : CurrentSelf());
            t_explaining = false;
        }
        if (text.empty() && thrown.what[0]) text = thrown.what;
        if (text.empty()) text = std::string(thrown.type[0] ? thrown.type : "a C++ exception") + ", no message recovered";
        else if (!thrown.hasValue) text = std::string(thrown.type) + ": " + text;
        // Our guard's __except handled the throw, so the YYGMLException's
        // destructor never ran and nobody else will release its value: this
        // stands in for it, once. Only when the object that reached the guard
        // is the one decoded - after a rethrow, or when the probe missed the
        // throw, the record belongs to another object whose destructor may
        // already have run, and it is left alone.
        if (thrown.hasValue && thrown.object != 0 && thrown.object == handled) FreeValue(thrown.value);

    } else {
        char buf[96];
        std::snprintf(buf, sizeof(buf), "%s 0x%08lX, no message recovered", Describe(code), code);
        text = buf;
    }
    std::snprintf(g_lastError, sizeof(g_lastError), "%s", text.c_str());
    return g_lastError;
}

bool Call(void* func, RValue* result, RValue** args, int argc) {
    // Borrow whatever instance the game last ran code as, since a null self
    // would fault.
    void* self = CurrentSelf();
    return CallAs(func, result, args, argc, self, self);
}

bool CallAs(void* func, RValue* result, RValue** args, int argc, void* self, void* other) {
    if (!g_ready || !func || !result) return false;

    result->ptr   = nullptr;
    result->flags = 0;
    result->kind  = kUnset;

    DWORD code = 0;
    bool ok;
    {
        ErrorProbe probe;   // restored even if a GML exception passes through to the game
        ok = GuardedScript(reinterpret_cast<ScriptFn>(func), self, other, result, argc, args, &code);
    }
    if (ok) return true;
    AfterGuardedFault(code);
    const char* owner = sym::OwnerOf(func);
    Logf("[!] gml: %s failed (0x%08lX): %s", owner ? owner : "script", code, ExplainFailure(code, self));
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

namespace {
bool g_abiTested = false;
bool g_abiProven = false;
} // namespace

bool IsEventSymbol(const std::string& symbol) {
    return symbol.rfind("gml_Object_", 0) == 0 || symbol.rfind("gml_RoomCC_", 0) == 0;
}

bool CallEvent(void* func, void* self, void* other) {
    if (!g_ready || !func) return false;

    // No instance given: fall back the same way Call() does, since an event
    // always runs as some instance and a null self would fault immediately.
    if (!self) self = CurrentSelf();
    if (!self) return false;

    DWORD code = 0;
    bool ok;
    {
        ErrorProbe probe;
        ok = GuardedEvent(reinterpret_cast<EventFn>(func), self, other ? other : self, &code);
    }
    if (ok) return true;
    AfterGuardedFault(code);
    const char* owner = sym::OwnerOf(func);
    Logf("[!] gml: event %s failed (0x%08lX): %s", owner ? owner : "?", code, ExplainFailure(code, self));
    return false;
}

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

// ------------------------------------------------------------ id -> instance
//
// The runner keeps every instance in a hash map keyed by id (CInstance's
// ms_ID2Instance). No function wraps the lookup - it is inlined wherever an id
// is resolved (instance iterators, `with`, instance_exists) - so it is the MAP
// that is located, from the shape all those copies share:
//     cmp   id, 100000            ; ids below are object indices
//     jge   ...
//     movsxd rcx, dword [rip+M]   ; mask   (map + 8)
//     mov   rax, qword [rip+B]    ; buckets (map + 0)
//     and   rcx, id ; add rcx, rcx ; mov rdx, [rax+rcx*8]   ; 16-byte buckets
//     cmp   dword [rdx+10h], id   ; node key; next at +8, CInstance* at +18h
// Identical in Stoneshard's runtime and 2024.14. The layout is then PROVEN on
// the live game before it is used (VerifyInstanceLookup).

namespace {

constexpr std::int32_t kFirstInstanceId = 100000;

std::uintptr_t g_idMap = 0;

enum class Lookup { Pending, Proven, Failed };
Lookup      g_lookup = Lookup::Pending;
std::string g_lookupStatus = "not proven yet (needs a live instance)";
int         g_lookupRetries = 0;
// High halves of the kind-15 references proven to name instances (a runtime
// can use one for `id` and another for what instance_find returns).
std::int64_t g_refTags[2] = {};
int          g_refTagCount = 0;

bool RipOperand(std::uintptr_t at, unsigned char op, std::uintptr_t& target) {
    const auto* b = reinterpret_cast<const unsigned char*>(at);
    if ((b[0] & 0xF8) != 0x48 || b[1] != op || (b[2] & 0xC7) != 0x05) return false;
    std::int32_t disp;
    std::memcpy(&disp, b + 3, 4);
    target = at + 7 + static_cast<std::intptr_t>(disp);
    return true;
}

// `cmp dword [reg+10h], r32` - [REX] 39 /r, mod=01, disp8 = 0x10.
bool HasKeyCompare(std::uintptr_t from, std::size_t window) {
    for (std::size_t i = 0; i < window; ++i) {
        const auto* b = reinterpret_cast<const unsigned char*>(from + i);
        const std::size_t r = (b[0] & 0xF0) == 0x40 ? 1 : 0;
        if (b[r] == 0x39 && (b[r + 1] & 0xC0) == 0x40 && (b[r + 1] & 0x07) != 0x04 && b[r + 2] == 0x10) return true;
    }
    return false;
}

// The id threshold, 100000 (or 99999 for a `jle`), as an imm32 shortly before.
bool HasIdThreshold(std::uintptr_t before, std::size_t window) {
    static const unsigned char k100000[] = {0xA0, 0x86, 0x01, 0x00};
    static const unsigned char k99999[]  = {0x9F, 0x86, 0x01, 0x00};
    const std::uintptr_t from = before - window;
    for (std::size_t i = 0; i + 4 <= window; ++i) {
        const void* p = reinterpret_cast<const void*>(from + i);
        if (std::memcmp(p, k100000, 4) == 0 || std::memcmp(p, k99999, 4) == 0) return true;
    }
    return false;
}

void TallyIdMap(std::unordered_map<std::uintptr_t, int>& votes) {
    const auto tx = sym::TextRange();
    const auto data = sym::DataRange();
    for (std::uintptr_t at = tx.lo + 96; at + 64 < tx.hi; ++at) {
        const auto* b = reinterpret_cast<const unsigned char*>(at);
        if (b[1] != 0x63) continue;   // cheap reject before decoding
        std::uintptr_t mask = 0;
        if (!RipOperand(at, 0x63, mask)) continue;
        std::uintptr_t buckets = 0;
        bool paired = false;
        for (std::size_t k = 7; k < 16 && !paired; ++k)
            paired = RipOperand(at + k, 0x8B, buckets) && buckets + 8 == mask;
        if (!paired || !data.contains(buckets)) continue;
        if (!HasKeyCompare(at + 7, 40) || !HasIdThreshold(at, 96)) continue;
        ++votes[buckets];
    }
}

void* LookupId(std::int32_t id) {
    if (!g_idMap || id < kFirstInstanceId) return nullptr;
    std::uintptr_t buckets = 0;
    std::int32_t   mask = 0;
    if (!SafeRead(reinterpret_cast<const void*>(g_idMap), &buckets, sizeof(buckets)) ||
        !SafeRead(reinterpret_cast<const void*>(g_idMap + 8), &mask, sizeof(mask)))
        return nullptr;
    // A power-of-two table: anything else is not the map this code expects.
    const auto umask = static_cast<std::uint32_t>(mask);
    if (!buckets || mask <= 0 || (umask & (umask + 1)) != 0) return nullptr;

    std::uintptr_t node = 0;
    const std::uintptr_t slot = buckets + static_cast<std::uintptr_t>(id & mask) * 16;
    if (!SafeRead(reinterpret_cast<const void*>(slot), &node, sizeof(node))) return nullptr;
    struct Node { std::uintptr_t prev, next; std::int32_t key, pad; void* value; };
    for (int hops = 0; node && hops < 4096; ++hops) {
        Node n{};
        if (!SafeRead(reinterpret_cast<const void*>(node), &n, sizeof(n))) return nullptr;
        if (n.key == id) return n.value;
        node = n.next;
    }
    return nullptr;
}

// An instance id from a GML value: a whole number, or a kind-15 reference
// whose low half is the id (the high half says what it refers to).
bool IdOf(const RValue& v, std::int32_t& id, bool& isRef, std::int64_t& tag) {
    isRef = false;
    switch (v.kind & 0x00FFFFFF) {
    case kReal:
        if (!(v.real >= 0.0 && v.real <= 2147483647.0) || v.real != static_cast<double>(static_cast<std::int32_t>(v.real)))
            return false;
        id = static_cast<std::int32_t>(v.real);
        return true;
    case kInt32: id = v.i32; return true;
    case kInt64:
        if (v.i64 < 0 || v.i64 > 0x7FFFFFFF) return false;
        id = static_cast<std::int32_t>(v.i64);
        return true;
    case kRef:
        isRef = true;
        tag   = v.i64 >> 32;
        id    = static_cast<std::int32_t>(v.i64 & 0xFFFFFFFF);
        return true;
    default:
        return false;
    }
}

bool RefTagProven(std::int64_t tag) {
    for (int i = 0; i < g_refTagCount; ++i)
        if (g_refTags[i] == tag) return true;
    return false;
}

void NoteRefTag(std::int64_t tag) {
    if (!RefTagProven(tag) && g_refTagCount < 2) g_refTags[g_refTagCount++] = tag;
}

void LookupFailed(const std::string& why) {
    g_lookup = Lookup::Failed;
    g_lookupStatus = "unavailable: " + why;
    Logf("[!] gml: instance lookup %s", g_lookupStatus.c_str());
}

// Reads an instance's `id` through the runtime and resolves it.
bool ReadId(void* instance, std::int32_t& id, bool& isRef, std::int64_t& tag) {
    RValue v{};
    return builtins::GetVar(builtins::SelfHandle(instance), "id", &v) && IdOf(v, id, isRef, tag);
}

} // namespace

bool        InstanceLookupProven() { return g_lookup == Lookup::Proven; }
const char* InstanceLookupStatus() { return g_lookupStatus.c_str(); }

bool ScanInstanceLookup() {
    std::unordered_map<std::uintptr_t, int> votes;
    TallyIdMap(votes);
    g_idMap = Winner(votes, 3, "instance id table");
    if (!g_idMap) LookupFailed("the runtime's instance id table was not found");
    return g_idMap != 0;
}

void VerifyInstanceLookup() {
    if (g_lookup != Lookup::Pending || !g_ready || !g_idMap || !builtins::Ready()) return;
    void* self = CurrentSelf();
    if (!self || !LooksLikeInstance(self)) return;   // nothing to prove with yet

    // Every half second rather than every frame: an attempt that cannot
    // conclude (below) makes builtin calls, and may log a failed one.
    static int calls = 0;
    if (calls++ % 30 != 0) return;

    // The borrowed self can be something other than a live object instance:
    // a struct running a method (no object_index), or an instance destroyed
    // since it was observed. Neither says anything about the table, so such
    // attempts only retry, for a while.
    auto inconclusive = [](const char* why) {
        if (++g_lookupRetries >= 40) LookupFailed(why);
    };
    RValue args[2]{};
    std::int32_t objectIndex = -1;
    bool objRef = false;
    std::int64_t objTag = 0;
    if (!builtins::GetVar(builtins::SelfHandle(self), "object_index", &args[0]) ||
        !IdOf(args[0], objectIndex, objRef, objTag)) {
        inconclusive("the current self never had an object_index");
        return;
    }
    std::int32_t id = 0;
    bool isRef = false;
    std::int64_t tag = 0;
    RValue idValue{};
    if (!builtins::GetVar(builtins::SelfHandle(self), "id", &idValue) || !IdOf(idValue, id, isRef, tag) ||
        id < kFirstInstanceId) {
        inconclusive("could not read the current instance's id");
        return;
    }
    RValue live{};
    if (!builtins::Call("instance_exists", &live, &idValue, 1, self) || live.real == 0.0) {
        inconclusive("the current self was never a live instance");
        return;
    }

    void* found = LookupId(id);
    if (found != self) {
        // A live instance whose id finds another pointer (or none) is what a
        // wrong table looks like; a few in a row settle it.
        static int mismatches = 0;
        char buf[160];
        std::snprintf(buf, sizeof(buf), "id %d of self %p looked up to %p", id, self, found);
        Logf("[!] gml: instance lookup check: %s", buf);
        if (++mismatches >= 5) LookupFailed(buf);
        return;
    }
    if (LookupId(0x7FFFFFF0) || LookupId(id ^ 0x40000000)) {
        LookupFailed("a bogus id resolved to an instance");
        return;
    }
    if (isRef) NoteRefTag(tag);

    // instance_find may hand out references even where `id` reads as a
    // number: whatever it returns for self's own object must resolve to an
    // instance whose `id` is that same number, which proves its reference kind.
    RValue found0{};
    {
        SetReal(args[1], 0.0);
        std::int32_t fid = 0, back = 0;
        bool fRef = false, bRef = false;
        std::int64_t fTag = 0, bTag = 0;
        if (builtins::Call("instance_find", &found0, args, 2, self) && IdOf(found0, fid, fRef, fTag)) {
            void* p = LookupId(fid);
            if (!p || !ReadId(p, back, bRef, bTag) || back != fid) {
                LookupFailed("instance_find's result did not resolve to the instance it names");
                return;
            }
            if (fRef) NoteRefTag(fTag);
        }
    }

    g_lookup = Lookup::Proven;
    char buf[200];
    std::snprintf(buf, sizeof(buf), "proven (table %p; self %p id %d found; bogus ids rejected; %d reference kind%s)",
                  reinterpret_cast<void*>(g_idMap), self, id, g_refTagCount, g_refTagCount == 1 ? "" : "s");
    g_lookupStatus = buf;
    Logf("gml: instance lookup %s", g_lookupStatus.c_str());
}

void* InstanceFromId(const RValue& v) {
    VerifyInstanceLookup();   // a mod may ask before the frame tick has proven it
    if (g_lookup != Lookup::Proven) return nullptr;
    std::int32_t id = 0;
    bool isRef = false;
    std::int64_t tag = 0;
    if (!IdOf(v, id, isRef, tag) || (isRef && !RefTagProven(tag))) return nullptr;
    void* p = LookupId(id);
    if (!p || !LooksLikeInstance(p)) return nullptr;

    // The table also holds deactivated instances and ones being destroyed;
    // instance_exists is the runtime's own word on whether it is live.
    RValue args[1] = {v};
    RValue live{};
    if (!builtins::Call("instance_exists", &live, args, 1, p)) return nullptr;
    const std::int32_t kind = live.kind & 0x00FFFFFF;
    return (kind == kReal || kind == kBool) && live.real != 0.0 ? p : nullptr;
}

} // namespace mod::gml
