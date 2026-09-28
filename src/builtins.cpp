// GameMaker builtin registry.
//
// The runner registers every builtin at startup through one registrar, writing 80-byte
// entries into a heap array. Three adjacent .data globals hold the array pointer, the
// count and the capacity. Walking that array yields every builtin by name.
//
// Resolution chain, all name- or pattern-anchored so a game update cannot break it:
//   1. find a builtin's name string in .rdata
//   2. find the `lea rcx,[rip+d32]` in .text that loads it
//   3. the following `call`/`jmp` target is the registrar  (consensus across 5 names)
//   4. read the registrar's rip-relative operands to get the three globals
//   5. walk the array
//
// Timing matters: the array is filled during runner startup, so this resolves lazily on
// first use rather than at DLL load.

#include "builtins.h"
#include "log.h"
#include "symbols.h"

#include <windows.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <unordered_map>
#include <vector>

namespace mod::builtins {
namespace {

using TRoutine = void (*)(gml::RValue* result, void* self, void* other,
                          int argc, gml::RValue* args);

// The registry entry changed shape between runtimes, so both are understood and
// the live table decides which one it is (see DetectLayout).
#pragma pack(push, 1)
struct RFunctionInline {    // older runtimes (Stoneshard): name stored inline
    char     name[0x40];
    TRoutine fn;
    int32_t  argc;          // -1 == variadic
    int32_t  id;
};
struct RFunctionRef {       // 2024+ runtimes: name by pointer
    const char* name;
    TRoutine    fn;
    int32_t     argc;
    int32_t     pad;
};
#pragma pack(pop)
static_assert(sizeof(RFunctionInline) == 0x50, "inline RFunction must be 80 bytes");
static_assert(sizeof(RFunctionRef) == 0x18, "pointer RFunction must be 24 bytes");

enum class Layout { Unknown, Inline, Ref };

std::unordered_map<std::string, Builtin> g_map;
bool        g_tried  = false;
bool        g_ready  = false;
std::string g_status = "not initialised";

// Spread across different areas of the runtime so a consensus vote is meaningful.
// Wider than strictly necessary: the first live run resolved only 2 of 5, and a
// bigger sample makes the difference between "the pattern changed" and "these
// particular names are awkward" obvious from the log alone.
const char* const kAnchors[] = {
    "network_create_socket", "network_send_udp",  "network_destroy",
    "buffer_create",         "buffer_get_size",   "buffer_delete",
    "random_set_seed",       "json_encode",       "date_get_year",
    "instance_exists",       "variable_instance_get",
};

// Arity spot-check: a much stronger correctness signal than "the pointer is in .text".
struct ArityCheck { const char* name; int argc; };
const ArityCheck kArity[] = {
    {"network_create_socket", 1}, {"network_send_udp", 5}, {"buffer_copy", 5},
    {"date_create_datetime", 6},  {"buffer_get_size", 1},
};

bool InText(std::uintptr_t p)  { return sym::TextRange().contains(p); }
bool InRdata(std::uintptr_t p) { return sym::RdataRange().contains(p); }

// What one anchor name found, kept so a failure can say which anchor gave up
// and where. The first live run reported only "anchors found 2, votes 2",
// which was not enough to tell a changed pattern from an awkward name.
struct AnchorResult {
    const char* name     = nullptr;
    std::size_t len      = 0;
    int         strings  = 0;   // .rdata occurrences of the exact name
    int         leaSites = 0;   // `lea rcx` sites loading one of them
    // Registrar candidates reached from those sites -> how many sites reached it.
    std::unordered_map<std::uintptr_t, int> targets;
};

// One pass over .rdata for every anchor at once. Matches are NUL-delimited on
// both sides, so "buffer_create" cannot match inside "buffer_create_from_...".
// Every occurrence is kept: assuming the first one is the registration site
// only holds for the handful of names that were checked by hand.
void FindAnchorStrings(std::vector<AnchorResult>&               anchors,
                       std::unordered_map<std::uintptr_t, int>& stringToAnchor) {
    const auto rd = sym::RdataRange();
    if (!rd.hi) return;

    for (std::uintptr_t p = rd.lo + 1; p + 64 < rd.hi; ++p) {
        const char* s = reinterpret_cast<const char*>(p);
        if (s[-1] != '\0') continue;

        for (std::size_t i = 0; i < anchors.size(); ++i) {
            AnchorResult& a = anchors[i];
            if (s[0] != a.name[0]) continue;
            if (std::memcmp(s, a.name, a.len) != 0 || s[a.len] != '\0') continue;

            stringToAnchor.emplace(p, static_cast<int>(i));
            ++a.strings;
            break;
        }
    }
}

// One pass over .text for every anchor at once, looking for
//   lea rcx,[rip+d32]      ; -> an anchor's name string
//   ... call/jmp rel32     ; -> Builtin_Add
// The last registration in a block is a tail call, so E9 counts as well as E8.
void FindRegistrations(std::vector<AnchorResult>&                     anchors,
                       const std::unordered_map<std::uintptr_t, int>& stringToAnchor) {
    const auto tx = sym::TextRange();
    if (!tx.hi) return;

    for (std::uintptr_t at = tx.lo; at + 16 < tx.hi; ++at) {
        const auto* b = reinterpret_cast<const unsigned char*>(at);
        if (!(b[0] == 0x48 && b[1] == 0x8D && b[2] == 0x0D)) continue;

        std::int32_t disp;
        std::memcpy(&disp, b + 3, 4);
        const std::uintptr_t str = at + 7 + static_cast<std::intptr_t>(disp);

        auto it = stringToAnchor.find(str);
        if (it == stringToAnchor.end()) continue;

        AnchorResult& a = anchors[static_cast<std::size_t>(it->second)];
        ++a.leaSites;

        for (std::size_t j = 7; j < 32 && at + j + 5 < tx.hi; ++j) {
            const auto* c = reinterpret_cast<const unsigned char*>(at + j);
            if (c[0] != 0xE8 && c[0] != 0xE9) continue;

            std::int32_t rel;
            std::memcpy(&rel, c + 1, 4);
            const std::uintptr_t callee = at + j + 5 + static_cast<std::intptr_t>(rel);
            if (InText(callee)) ++a.targets[callee];
            break;
        }
    }
}

// Decodes `[REX] 8B /r` with a rip-relative operand (modrm mod=00 rm=101) at `at`.
// Returns the referenced address and whether the load is 64-bit (REX.W).
bool RipLoad(std::uintptr_t at, std::uintptr_t& target, bool& is64, std::size_t& len) {
    const auto* b = reinterpret_cast<const unsigned char*>(at);
    std::size_t i = 0;
    is64 = false;
    if ((b[0] & 0xF0) == 0x40) { is64 = (b[0] & 0x08) != 0; i = 1; }
    if (b[i] != 0x8B) return false;
    if ((b[i + 1] & 0xC7) != 0x05) return false;        // mod=00, rm=101 -> [rip+d32]
    std::int32_t d;
    std::memcpy(&d, b + i + 2, 4);
    len    = i + 6;
    target = at + len + static_cast<std::intptr_t>(d);
    return true;
}

// The registrar keeps three adjacent globals - array ptr, int count at +8 and
// int capacity at +0xC - and reads all of them rip-relatively. Which register
// each load uses varies between runtimes (Stoneshard's reads the count into eax,
// the 2024 runtime into ecx and the capacity into eax), so no single encoding is
// assumed: every rip-relative load in the function is collected, and the answer
// is the qword global whose +8 is also loaded as a dword.
bool ExtractGlobals(std::uintptr_t reg, void*** outPtr, std::int32_t** outCount) {
    // The array pointer is only read at the far end of the function, so the
    // window has to cover the whole registrar, not just its prologue.
    constexpr std::size_t kWindow = 0x200;

    std::vector<std::uintptr_t> qwordReads, dwordReads;
    for (std::size_t i = 0; i + 8 < kWindow; ++i) {
        const std::uintptr_t at = reg + i;
        if (!InText(at + 8)) break;
        std::uintptr_t target; bool is64; std::size_t len;
        if (!RipLoad(at, target, is64, len)) continue;
        if (!sym::DataRange().contains(target)) continue;
        (is64 ? qwordReads : dwordReads).push_back(target);
    }

    std::uintptr_t ptrG = 0;
    for (std::uintptr_t q : qwordReads) {
        for (std::uintptr_t d : dwordReads) {
            if (d == q + 8) { ptrG = q; break; }
        }
        if (ptrG) break;
    }

    if (!ptrG) {
        Logf("[!] builtins: could not extract globals (%zu qword / %zu dword rip loads, none adjacent)",
             qwordReads.size(), dwordReads.size());
        return false;
    }

    *outPtr   = reinterpret_cast<void**>(ptrG);
    *outCount = reinterpret_cast<std::int32_t*>(ptrG + 8);
    Logf("builtins: globals ptr=%p count=%p cap=%p", (void*)ptrG, (void*)(ptrG + 8), (void*)(ptrG + 0xC));
    return true;
}

bool PrintableName(const char* s, std::size_t max) {
    for (std::size_t c = 0; c < max; ++c) {
        const auto ch = static_cast<unsigned char>(s[c]);
        if (ch == 0) return c > 0;
        if (ch < 0x20 || ch > 0x7e) return false;
    }
    return false;
}

// Reading a candidate name pointer that may be garbage: never fault on it.
bool SafePrintableName(const char* s, std::size_t max) {
    __try {
        return PrintableName(s, max);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Decides the entry shape from the first entries of the live table: whichever
// reading gives printable names, code pointers and sane arities for all of
// them is the right one.
Layout DetectLayout(const void* table, int n) {
    const int probe = n < 16 ? n : 16;
    int inlineOk = 0, refOk = 0;
    __try {
        const auto* in = static_cast<const RFunctionInline*>(table);
        const auto* rf = static_cast<const RFunctionRef*>(table);
        for (int i = 0; i < probe; ++i) {
            if (PrintableName(in[i].name, 0x40) && InText(reinterpret_cast<std::uintptr_t>(in[i].fn)) &&
                in[i].argc >= -1 && in[i].argc < 64)
                ++inlineOk;
            if (SafePrintableName(rf[i].name, 0x80) && InText(reinterpret_cast<std::uintptr_t>(rf[i].fn)) &&
                rf[i].argc >= -1 && rf[i].argc < 64)
                ++refOk;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return Layout::Unknown;
    }
    if (inlineOk == probe) return Layout::Inline;
    if (refOk == probe) return Layout::Ref;
    Logf("[!] builtins: entry layout not recognised (inline %d/%d, by-pointer %d/%d)",
         inlineOk, probe, refOk, probe);
    return Layout::Unknown;
}

} // namespace

bool        Ready()  { return g_ready; }
const char* Status() { return g_status.c_str(); }
std::size_t Count()  { return g_map.size(); }

const std::vector<const char*>& Names() {
    static std::vector<const char*> names;
    if (names.empty() && g_ready) {
        names.reserve(g_map.size());
        for (const auto& [name, b] : g_map) names.push_back(name.c_str());
        std::sort(names.begin(), names.end(),
                  [](const char* a, const char* b) { return std::strcmp(a, b) < 0; });
    }
    return names;
}

namespace {

// Resolved once. The registrar and its globals are fixed at link time; only
// the table they point to fills in during startup, so a retry never needs to
// rescan .text - it only re-reads the globals.
void**        g_pArray   = nullptr;
std::int32_t* g_pCount   = nullptr;
bool          g_scanned  = false;
DWORD         g_lastTry  = 0;

bool ResolveRegistrar() {
    std::vector<AnchorResult> anchors;
    anchors.reserve(std::size(kAnchors));
    for (const char* n : kAnchors)
        anchors.push_back(AnchorResult{n, std::strlen(n), 0, 0, {}});

    std::unordered_map<std::uintptr_t, int> stringToAnchor;
    FindAnchorStrings(anchors, stringToAnchor);
    FindRegistrations(anchors, stringToAnchor);

    // Tally by DISTINCT anchor, not by site: a name referenced from twenty
    // places must not outvote one referenced from a single registration.
    std::unordered_map<std::uintptr_t, int> byAnchor;
    for (const AnchorResult& a : anchors) {
        Logf("builtins:   anchor %-24s strings=%d lea=%d candidates=%zu",
             a.name, a.strings, a.leaSites, a.targets.size());
        for (const auto& t : a.targets) {
            Logf("builtins:       -> %p (%d site%s)", (void*)t.first, t.second,
                 t.second == 1 ? "" : "s");
            ++byAnchor[t.first];
        }
    }

    std::uintptr_t agreed = 0;
    int votes = 0, runnerUp = 0;
    for (const auto& c : byAnchor) {
        if (c.second > votes)      { runnerUp = votes; agreed = c.first; votes = c.second; }
        else if (c.second > runnerUp) runnerUp = c.second;
    }

    const int total = static_cast<int>(std::size(kAnchors));
    if (!agreed || votes < 3 || votes <= runnerUp) {
        char buf[192];
        std::snprintf(buf, sizeof(buf),
                      "no registrar consensus (best %d/%d anchors, runner-up %d)",
                      votes, total, runnerUp);
        g_status = buf;
        Logf("[!] builtins: %s", g_status.c_str());
        return false;
    }
    Logf("builtins: registrar %p (%d/%d anchors agree, runner-up %d)",
         (void*)agreed, votes, total, runnerUp);

    if (!ExtractGlobals(agreed, &g_pArray, &g_pCount)) {
        g_status = "could not read the registrar's globals";
        return false;
    }
    return true;
}

} // namespace

bool Init() {
    if (g_ready) return true;
    if (!sym::Healthy()) { g_status = "symbol resolver unhealthy"; return false; }

    // The registrar scan is deterministic: if it failed once it fails forever.
    if (!g_scanned) {
        g_scanned = true;
        if (!ResolveRegistrar()) { g_tried = true; return false; }
    }
    if (!g_pArray || !g_pCount) return false;

    // Before the runner has registered its builtins the table is empty or
    // half-built. Look again at most twice a second rather than every call,
    // so an early caller neither spins nor floods the log.
    const DWORD now = GetTickCount();
    if (g_tried && now - g_lastTry < 500) return false;
    g_tried   = true;
    g_lastTry = now;

    const void* table = *g_pArray;
    const int   n     = *g_pCount;
    if (!table || n < 500 || n > 20000) {
        char buf[128];
        std::snprintf(buf, sizeof(buf), "builtin table not populated yet (ptr=%p count=%d)", table, n);
        g_status = buf;
        return false;
    }

    const Layout layout = DetectLayout(table, n);
    if (layout == Layout::Unknown) {
        g_status = "builtin entry layout not recognised";
        return false;
    }

    g_map.clear();
    g_map.reserve(static_cast<std::size_t>(n));
    for (int i = 0; i < n; ++i) {
        const char* name;
        TRoutine    fn;
        int         argc;
        if (layout == Layout::Inline) {
            const auto& e = static_cast<const RFunctionInline*>(table)[i];
            if (!PrintableName(e.name, 0x40)) continue;
            name = e.name; fn = e.fn; argc = e.argc;
        } else {
            const auto& e = static_cast<const RFunctionRef*>(table)[i];
            if (!SafePrintableName(e.name, 0x80)) continue;
            name = e.name; fn = e.fn; argc = e.argc;
        }
        if (!InText(reinterpret_cast<std::uintptr_t>(fn))) continue;
        g_map.emplace(name, Builtin{reinterpret_cast<void*>(fn), argc});
    }
    Logf("builtins: %s-name layout, %zu of %d entries usable",
         layout == Layout::Inline ? "inline" : "by-pointer", g_map.size(), n);

    // --- arity spot-check ----------------------------------------------------
    for (const auto& chk : kArity) {
        auto it = g_map.find(chk.name);
        if (it == g_map.end() || it->second.argc != chk.argc) {
            char buf[160];
            std::snprintf(buf, sizeof(buf), "arity check failed: %s expected %d, got %d",
                          chk.name, chk.argc,
                          it == g_map.end() ? -999 : it->second.argc);
            g_status = buf;
            Logf("[!] builtins: %s", g_status.c_str());
            g_map.clear();
            return false;
        }
    }

    g_ready  = true;
    g_status = "ok";
    Logf("builtins: %zu resolved of %d registered, arity checks passed", g_map.size(), n);
    return true;
}

Builtin Find(const std::string& name) {
    if (!g_ready && !Init()) return {};
    auto it = g_map.find(name);
    return it == g_map.end() ? Builtin{} : it->second;
}

bool Call(const std::string& name, gml::RValue* result,
          gml::RValue* args, int argc, void* self, void* other) {
    const Builtin b = Find(name);
    if (!b.fn || !result) return false;
    if (b.argc >= 0 && b.argc != argc) {
        Logf("[!] builtins: %s expects %d args, got %d", name.c_str(), b.argc, argc);
        return false;
    }

    result->ptr   = nullptr;
    result->flags = 0;
    result->kind  = gml::kUndefined;

    auto fn = reinterpret_cast<TRoutine>(b.fn);
    __try {
        fn(result, self, other ? other : self, argc, args);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Logf("[!] builtins: exception 0x%08lX calling %s", GetExceptionCode(), name.c_str());
        return false;
    }
    return true;
}

bool GetInstanceVar(void* instance, const char* name, gml::RValue* out) {
    if (!instance || !name || !out) return false;

    gml::RValue args[2]{};
    gml::SetReal(args[0], -1.0);               // GML "self": use the instance context below
    if (!gml::SetString(args[1], name)) return false;

    return Call("variable_instance_get", out, args, 2, instance) &&
           out->kind != gml::kUndefined;
}

// ---------------------------------------------------------------- reflection

namespace {

double g_playerObjIndex = -1.0;     // asset index is stable; the reference is not
bool   g_refFailLogged  = false;

// A handle is usable if it is anything the runtime would accept as an instance:
// a number, or the reference kind instance_find actually returns here.
bool UsableHandle(const gml::RValue& v) {
    return v.kind == gml::kReal || v.kind == gml::kInt32 || v.kind == gml::kInt64 ||
           v.kind == gml::kRef  || v.kind == gml::kObject;
}

// asset_get_index("o_player") -> instance_find(index, 0).
//
// instance_find returns kind 15 (kRef) in this runtime, not a number. The first
// version of this demanded a real and discarded the answer, which is what made
// the id path look broken. The reference is handed straight back to the
// reflection builtins and never decoded.
bool ResolvePlayerRef(void* self, gml::RValue* out) {
    if (!self || !out) return false;

    if (g_playerObjIndex < 0.0) {
        static const char kObject[] = "o_player";   // must outlive the call
        gml::RValue nameArg{};
        if (!gml::SetString(nameArg, kObject)) return false;

        gml::RValue idx{};
        if (!Call("asset_get_index", &idx, &nameArg, 1, self) ||
            idx.kind != gml::kReal || idx.real < 0.0) {
            if (!g_refFailLogged) {
                g_refFailLogged = true;
                Logf("[!] builtins: asset_get_index(\"o_player\") failed (kind=%d)", idx.kind);
            }
            return false;
        }
        g_playerObjIndex = idx.real;
        Logf("builtins: o_player asset index %.0f", g_playerObjIndex);
    }

    gml::RValue findArgs[2]{};
    gml::SetReal(findArgs[0], g_playerObjIndex);
    gml::SetReal(findArgs[1], 0.0);

    gml::RValue inst{};
    if (!Call("instance_find", &inst, findArgs, 2, self) || !UsableHandle(inst)) {
        if (!g_refFailLogged) {
            g_refFailLogged = true;
            Logf("[!] builtins: instance_find gave an unusable handle (kind=%d)", inst.kind);
        }
        return false;
    }

    *out = inst;
    return true;
}

} // namespace

Handle SelfHandle(void* instance) {
    Handle h;
    gml::SetReal(h.id, -1.0);
    h.self = instance;
    return h;
}

Handle PlayerHandle() {
    void* inst = gml::PlayerInstance();
    Handle h = SelfHandle(inst ? inst : gml::CurrentSelf());

    // Re-resolved per call rather than cached: the mod does not own the
    // reference's lifetime, and holding one across frames is not worth the risk
    // for what amounts to two builtin calls.
    gml::RValue ref{};
    if (inst && ResolvePlayerRef(inst, &ref)) {
        h.id      = ref;
        h.haveRef = true;
    }
    return h;
}

bool GetVar(const Handle& h, const char* name, gml::RValue* out) {
    if (!name || !out) return false;

    gml::RValue args[2]{};
    args[0] = h.id;
    if (!gml::SetString(args[1], name)) return false;

    return Call("variable_instance_get", out, args, 2, h.self) &&
           out->kind != gml::kUndefined && out->kind != gml::kUnset;
}

bool SetVar(const Handle& h, const char* name, const gml::RValue& value) {
    if (!name) return false;

    gml::RValue args[3]{};
    args[0] = h.id;
    if (!gml::SetString(args[1], name)) return false;
    args[2] = value;

    gml::RValue ignored{};
    return Call("variable_instance_set", &ignored, args, 3, h.self);
}

int VarNames(const Handle& h, std::vector<std::string>& out, int limit) {
    out.clear();

    gml::RValue one = h.id;

    gml::RValue count{};
    if (!Call("variable_instance_names_count", &count, &one, 1, h.self) ||
        count.kind != gml::kReal)
        return -1;
    const int n = static_cast<int>(count.real);

    gml::RValue names{};
    if (!Call("variable_instance_get_names", &names, &one, 1, h.self)) return n;

    // A GML array's memory layout was never established, so index it with the
    // game's own array_get and hand the array straight back as an argument
    // rather than decoding it.
    if (!Find("array_get").fn) {
        Logf("builtins: array_get is not registered - reporting the count (%d) only", n);
        return n;
    }

    const int take = (limit > 0 && limit < n) ? limit : n;
    for (int i = 0; i < take; ++i) {
        gml::RValue args[2]{};
        args[0] = names;
        gml::SetReal(args[1], static_cast<double>(i));

        gml::RValue s{};
        if (!Call("array_get", &s, args, 2, h.self)) break;
        out.push_back(gml::ToString(s));
    }
    return n;
}

// ---------------------------------------------------------------- self-test

namespace {

bool        g_abiDone = false, g_abiOk = false;
bool        g_reflDone = false;
std::string g_report = "not run";

// Phase A: the three-call ABI gate. Needs no character, so it runs at the menu.
void PhaseA(void* self) {
    Logf("builtins: --- self-test phase A (ABI) ---");
    int passed = 0;

    gml::RValue mk[3]{};
    gml::SetReal(mk[0], 64.0);
    gml::SetReal(mk[1], 1.0);      // alignment
    gml::SetReal(mk[2], 1.0);      // buffer_fixed
    gml::RValue buf{};

    // Older runtimes hand back a numeric buffer index; 2024+ runtimes a typed
    // handle (kind 15). Either way the value is passed straight back unchanged.
    if (Call("buffer_create", &buf, mk, 3, self) &&
        ((buf.kind == gml::kReal && buf.real >= 0.0) || buf.kind == gml::kRef)) {
        if (buf.kind == gml::kReal) Logf("builtins:   buffer_create(64,1,1) -> %g", buf.real);
        else                        Logf("builtins:   buffer_create(64,1,1) -> handle %p", buf.ptr);

        gml::RValue id = buf;

        gml::RValue size{};
        if (Call("buffer_get_size", &size, &id, 1, self) && size.kind == gml::kReal) {
            Logf("builtins:   buffer_get_size       -> %g (want exactly 64)", size.real);
            if (size.real == 64.0) passed += 2;      // the whole point of the gate
        } else {
            Logf("builtins:   buffer_get_size       -> FAILED");
        }

        gml::RValue ignored{};
        Call("buffer_delete", &ignored, &id, 1, self);
    } else {
        Logf("builtins:   buffer_create         -> FAILED (kind=%d)", buf.kind);
    }

    gml::RValue seed{};
    if (Call("random_get_seed", &seed, nullptr, 0, self) && seed.kind == gml::kReal) {
        Logf("builtins:   random_get_seed       -> %g", seed.real);
        ++passed;
    } else {
        Logf("builtins:   random_get_seed       -> FAILED");
    }

    g_abiOk = passed >= 2;
    Logf("builtins: --- phase A %s (%d/3) ---", g_abiOk ? "PASSED" : "FAILED", passed);
}

// Phase B: reflection. Needs a loaded character, so it waits for one.
void PhaseB() {
    const Handle byRef  = PlayerHandle();
    const Handle bySelf = SelfHandle(gml::PlayerInstance());

    Logf("builtins: --- self-test phase B (self=%p, ref=%s kind=%d) ---",
         bySelf.self, byRef.haveRef ? "yes" : "no", byRef.id.kind);

    char summary[256];
    bool refOk = false, selfOk = false, mutatorOk = false, restored = false;
    int  fieldCount = -1;

    // 1) Read through the instance reference instance_find handed back.
    gml::RValue vRef{};
    if (byRef.haveRef && GetVar(byRef, "x", &vRef) && vRef.kind == gml::kReal) {
        refOk = true;
        Logf("builtins:   get x by instance ref -> %.2f", vRef.real);
    } else {
        Logf("builtins:   get x by instance ref -> FAILED (haveRef=%d kind=%d)",
             byRef.haveRef ? 1 : 0, vRef.kind);
    }

    // 2) Read through -1/"self".
    gml::RValue vSelf{};
    if (bySelf.self && GetVar(bySelf, "x", &vSelf) && vSelf.kind == gml::kReal) {
        selfOk = true;
        Logf("builtins:   get x by -1 (\"self\")  -> %.2f", vSelf.real);
    } else {
        Logf("builtins:   get x by -1 (\"self\")  -> FAILED (kind=%d)", vSelf.kind);
    }

    // 3) Prove variable_instance_set is a real mutator rather than a call that
    //    merely returns cleanly: nudge x by one, read it back, put it straight
    //    back. One pixel, fully reversed.
    //
    //    Prefer the reference path: driving an instance that is NOT the running
    //    `self` is the harder case, so proving that one matters more.
    const Handle* mut      = refOk ? &byRef : (selfOk ? &bySelf : nullptr);
    const char*   mutVia   = refOk ? "ref" : "self";
    const double  original = refOk ? vRef.real : (selfOk ? vSelf.real : 0.0);

    if (mut) {
        gml::RValue nudged{};
        gml::SetReal(nudged, original + 1.0);

        if (SetVar(*mut, "x", nudged)) {
            gml::RValue back{};
            if (GetVar(*mut, "x", &back) && back.kind == gml::kReal) {
                mutatorOk = (back.real == original + 1.0);
                Logf("builtins:   set x %.2f -> %.2f via %s, read back %.2f (%s)",
                     original, original + 1.0, mutVia, back.real,
                     mutatorOk ? "MUTATOR WORKS" : "write did not take");
            } else {
                Logf("builtins:   set x via %s -> could not read back", mutVia);
            }

            // Always restore, and verify the restore rather than assuming it.
            gml::RValue restore{};
            gml::SetReal(restore, original);
            SetVar(*mut, "x", restore);

            gml::RValue check{};
            if (GetVar(*mut, "x", &check) && check.kind == gml::kReal)
                restored = (check.real == original);
            Logf("builtins:   restore x to %.2f     -> %s", original,
                 restored ? "ok" : "NOT RESTORED - the player moved one pixel");
        } else {
            Logf("builtins:   set x via %s          -> FAILED", mutVia);
        }
    } else {
        Logf("builtins:   mutator test skipped - no working read path");
    }

    // 4) Field count, plus a sample of the names, so the log shows what
    //    reflection actually sees on a live player.
    {
        const Handle& h = refOk ? byRef : bySelf;
        std::vector<std::string> names;
        fieldCount = VarNames(h, names, 12);
        if (fieldCount >= 0) {
            Logf("builtins:   o_player has %d instance variables", fieldCount);
            for (const std::string& n : names) Logf("builtins:     %s", n.c_str());
        } else {
            Logf("builtins:   variable_instance_names_count -> FAILED");
        }
    }

    std::snprintf(summary, sizeof(summary),
                  "abi=%s read_by_ref=%s read_by_self=%s mutator=%s(%s) restored=%s fields=%d",
                  g_abiOk ? "ok" : "FAIL", refOk ? "ok" : "FAIL", selfOk ? "ok" : "FAIL",
                  mutatorOk ? "ok" : "FAIL", mutVia, restored ? "ok" : "FAIL", fieldCount);
    g_report = summary;
    Logf("builtins: --- phase B done: %s ---", g_report.c_str());
}

} // namespace

bool        SelfTestPassed() { return g_abiOk; }
const char* SelfTestReport() { return g_report.c_str(); }

void SelfTest() {
    if (!g_ready && !Init()) return;

    // Borrowing the game's current instance the way gml::AbiSelfTest does:
    // builtins are handed a `self` even when they never look at it, and a null
    // one is not worth the risk.
    void* self = gml::CurrentSelf();
    if (!self) return;                       // retried next frame

    if (!g_abiDone) {
        g_abiDone = true;
        PhaseA(self);
        g_report = g_abiOk ? "abi=ok, waiting for a character" : "abi=FAIL";
    }

    // Reflection needs a loaded character; keep retrying until there is one.
    if (!g_reflDone && g_abiOk && gml::PlayerInstance()) {
        g_reflDone = true;
        PhaseB();
    }
}

} // namespace mod::builtins
