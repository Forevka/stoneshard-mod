#include "objtypes.h"

#include "builtins.h"
#include "gml.h"
#include "log.h"
#include "symbols.h"

#include <windows.h>
#include <cctype>
#include <cstring>
#include <deque>
#include <string>
#include <unordered_map>
#include <vector>

namespace mod::objtypes {
namespace {

// ---- layout ------------------------------------------------------------------
//
// YYC's CObjectGM, CHashMap and CEvent as both test runtimes lay them out.
// None of this is trusted until Prove() has checked it against every object the
// game has; a runtime where any of it differs is refused, never guessed at.

constexpr std::size_t kObjName        = 0x00;   // char*, the runner's heap
constexpr std::size_t kObjParent      = 0x08;   // CObjectGM* of the parent
constexpr std::size_t kObjEvents      = 0x18;   // CHashMap<int64, CEvent*>*
constexpr std::size_t kObjParentIndex = 0x8C;   // int, -1 for none
constexpr std::size_t kObjId          = 0x94;   // int, its own index

// CHashMap: open addressing with robin-hood probing over 24-byte elements.
// The 2024 runtime appends the longest probe seen and a second grow threshold
// (its header is 0x28 bytes); the older one stops at the delete callback.
struct MapHeader {
    std::int32_t size;       // power of two
    std::int32_t used;
    std::int32_t mask;       // size - 1
    std::int32_t grow;       // grows past this many entries
    struct Elem* elems;
    void*        deleteValue;
    std::int32_t maxProbe;   // only with a 0x28-byte header
    std::int32_t grow2;      // only with a 0x28-byte header
};
struct Elem {
    void*         value;
    std::int64_t  key;       // event type << 32 | subtype
    std::uint32_t hash;      // 0 = empty
    std::uint32_t pad;
};
static_assert(sizeof(Elem) == 24, "CHashMap element is 24 bytes");

// CEvent: the code, and the index of the object that defines the event (an
// ancestor's for an inherited entry).
struct EventRec {
    void*        code;
    std::int32_t owner;
    std::int32_t pad;
};

// A YYC function registration row - what sym::Scan reads out of .data.
struct Row {
    const char* name;
    void*       fn;
    void*       extra;
};

// How much of a CCode is copied from a template for a defined event. The 2024
// runtime allocates 0xB8; anything past the real end is never read by it.
constexpr std::size_t kCodeBytes = 0x100;

std::uint32_t HashOf(std::int64_t key) {
    const std::uint64_t h = static_cast<std::uint64_t>(key) * 0x9E3779B97F4A7C55ull;
    return (static_cast<std::uint32_t>(h >> 32) + 1) & 0x7FFFFFFF;
}

// ---- state -------------------------------------------------------------------

using AllocFn   = void* (*)(std::size_t);
using CtorFn    = void* (*)(void* object, int index);
using InsertFn  = void  (*)(void* hash, int key, void* object);
using RebuildFn = void  (*)();

struct Pieces {
    std::uintptr_t hashGlobal  = 0;   // CHash<CObjectGM>* (by index)
    std::uintptr_t countGlobal = 0;   // int, the number of object indices in use
    AllocFn        alloc       = nullptr;
    CtorFn         ctor        = nullptr;
    InsertFn       insert      = nullptr;
    RebuildFn      rebuild     = nullptr;
    std::size_t    objectBytes = 0;
    std::size_t    mapBytes    = 0;
    std::size_t    rowOffset   = 0;   // CCode -> Row*
    const void*    templateCode = nullptr;
    const Row*     templateRow  = nullptr;
};
Pieces g;

enum class State { Pending, Proven, Failed };
State       g_state  = State::Pending;
std::string g_status = "not proven yet";
bool        g_dirty  = false;

struct DefinedEvent {
    int   hookId = -1;
    void* event  = nullptr;   // our CEvent
};
struct Defined {
    std::string name;
    void*       object = nullptr;
    int         index  = -1;
    int         parent = -1;
    std::unordered_map<std::int64_t, DefinedEvent> events;
};
std::unordered_map<int, Defined>         g_defined;   // by object index
std::unordered_map<std::string, int>     g_byName;
struct HookTarget { int object; std::int64_t key; };
std::unordered_map<int, HookTarget>      g_byHook;
std::deque<Row>                          g_rows;      // stable: CCodes point at them

void Fail(const std::string& why) {
    g_state  = State::Failed;
    g_status = "unavailable: " + why;
    Logf("[!] objtypes: %s", g_status.c_str());
}

// ---- memory ------------------------------------------------------------------

// The proof reads a few hundred thousand small fields, nearly all inside a
// handful of heap regions: asking VirtualQuery for each one (what
// gml::ReadMemory does) took the 10k-object game most of a minute. Regions
// already found plainly readable are remembered here; the copy itself stays
// fault-guarded, so a region freed meanwhile still cannot take the game down.
struct Region { std::uintptr_t lo, hi; };
Region g_regions[64];
int    g_regionCount = 0, g_regionNext = 0;

bool RegionReadable(std::uintptr_t p, std::size_t n) {
    for (int i = 0; i < g_regionCount; ++i)
        if (p >= g_regions[i].lo && p + n <= g_regions[i].hi) return true;
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(reinterpret_cast<const void*>(p), &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) return false;
    if (!(mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ |
                         PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
        return false;
    const auto lo = reinterpret_cast<std::uintptr_t>(mbi.BaseAddress);
    const Region r{lo, lo + mbi.RegionSize};
    g_regions[g_regionNext] = r;
    g_regionNext = (g_regionNext + 1) % 64;
    if (g_regionCount < 64) ++g_regionCount;
    // A read straddling two regions: the next one must be readable too.
    return p + n <= r.hi || RegionReadable(r.hi, p + n - r.hi);
}

bool CopyGuarded(const void* src, void* dst, std::size_t n) {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool Read(std::uintptr_t p, void* out, std::size_t n) {
    if (p < 0x10000 || n == 0 || p + n < p) return false;
    return RegionReadable(p, n) && CopyGuarded(reinterpret_cast<const void*>(p), out, n);
}
template <class T> bool Read(std::uintptr_t p, T& out) { return Read(p, &out, sizeof(T)); }

// A NUL-terminated string of printable characters at p, up to `cap` bytes.
bool ReadString(std::uintptr_t p, std::string& out, std::size_t cap = 256) {
    out.clear();
    char buf[64];
    for (std::size_t done = 0; done < cap; done += sizeof(buf)) {
        if (!Read(p + done, buf, sizeof(buf))) {
            // The tail of a string near a page end: byte by byte.
            for (std::size_t i = 0; i < sizeof(buf); ++i) {
                char c;
                if (!Read(p + done + i, c)) return false;
                if (!c) return !out.empty();
                if (static_cast<unsigned char>(c) < 0x20) return false;
                out.push_back(c);
            }
            continue;
        }
        for (char c : buf) {
            if (!c) return !out.empty();
            if (static_cast<unsigned char>(c) < 0x20) return false;
            out.push_back(c);
        }
    }
    return false;
}

// ---- the runner's calls, fault-guarded ---------------------------------------
// (separate functions: __try cannot share a frame with destructors)

void* GuardedAlloc(std::size_t n) {
    __try { return g.alloc(n); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
bool GuardedCtor(void* object, int index) {
    __try { g.ctor(object, index); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool GuardedInsert(void* hash, int key, void* object) {
    __try { g.insert(hash, key, object); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool GuardedRebuild() {
    __try { g.rebuild(); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// ---- code patterns -----------------------------------------------------------

bool InText(std::uintptr_t p) { return sym::TextRange().contains(p); }
bool InData(std::uintptr_t p) { return sym::DataRange().contains(p); }

std::int32_t Disp(std::uintptr_t at) {
    std::int32_t d;
    std::memcpy(&d, reinterpret_cast<const void*>(at), 4);
    return d;
}

// `call rel32` at p: its target.
bool CallAt(std::uintptr_t p, std::uintptr_t& target) {
    if (*reinterpret_cast<const unsigned char*>(p) != 0xE8) return false;
    target = p + 5 + static_cast<std::intptr_t>(Disp(p + 1));
    return InText(target);
}

// `mov edx, [rip+d32]` (8B 15) at p.
bool MovEdxRip(std::uintptr_t p, std::uintptr_t& target) {
    const auto* b = reinterpret_cast<const unsigned char*>(p);
    if (b[0] != 0x8B || b[1] != 0x15) return false;
    target = p + 6 + static_cast<std::intptr_t>(Disp(p + 2));
    return true;
}

// `mov rcx, [rip+d32]` (48 8B 0D) at p.
bool MovRcxRip(std::uintptr_t p, std::uintptr_t& target) {
    const auto* b = reinterpret_cast<const unsigned char*>(p);
    if (b[0] != 0x48 || b[1] != 0x8B || b[2] != 0x0D) return false;
    target = p + 7 + static_cast<std::intptr_t>(Disp(p + 3));
    return true;
}

// Within [from, to): is there an instruction matching `match` whose operand is `want`?
template <class F>
bool HasOperand(std::uintptr_t from, std::uintptr_t to, F match, std::uintptr_t want) {
    for (std::uintptr_t p = from; p < to; ++p) {
        std::uintptr_t t = 0;
        if (match(p, t) && t == want) return true;
    }
    return false;
}

std::vector<std::uintptr_t> RdataStrings(const char* text) {
    std::vector<std::uintptr_t> out;
    const auto rd = sym::RdataRange();
    const std::size_t n = std::strlen(text);
    for (std::uintptr_t p = rd.lo + 1; p + n + 1 < rd.hi; ++p) {
        const char* s = reinterpret_cast<const char*>(p);
        if (s[0] == text[0] && s[-1] == '\0' && std::memcmp(s, text, n) == 0 && s[n] == '\0')
            out.push_back(p);
    }
    return out;
}

// `lea r64, [rip+d32]` sites loading `target`.
std::vector<std::uintptr_t> LeasTo(std::uintptr_t target) {
    std::vector<std::uintptr_t> out;
    const auto tx = sym::TextRange();
    for (std::uintptr_t p = tx.lo; p + 7 < tx.hi; ++p) {
        const auto* b = reinterpret_cast<const unsigned char*>(p);
        if ((b[0] & 0xFB) != 0x48 || b[1] != 0x8D || (b[2] & 0xC7) != 0x05) continue;
        if (p + 7 + static_cast<std::intptr_t>(Disp(p + 3)) == target) out.push_back(p);
    }
    return out;
}

// The object hash, from object_get_parent: the first rip-relative qword load
// followed by `movsxd r, dword [r+8]` (the bucket mask) - the inlined lookup.
std::uintptr_t HashFromObjectGetParent() {
    const auto fn = reinterpret_cast<std::uintptr_t>(builtins::Find("object_get_parent").fn);
    if (!fn) return 0;
    for (std::uintptr_t p = fn; p < fn + 0x80; ++p) {
        const auto* b = reinterpret_cast<const unsigned char*>(p);
        if ((b[0] & 0xF8) != 0x48 || b[1] != 0x8B || (b[2] & 0xC7) != 0x05) continue;
        const std::uintptr_t target = p + 7 + static_cast<std::intptr_t>(Disp(p + 3));
        if (!InData(target)) continue;
        for (std::uintptr_t q = p + 7; q < p + 7 + 12; ++q) {
            const auto* c = reinterpret_cast<const unsigned char*>(q);
            if ((c[0] & 0xF0) == 0x40 && c[1] == 0x63 && (c[2] & 0xC0) == 0x40 && c[3] == 0x08) return target;
        }
    }
    return 0;
}

// The runner's own object creation, inlined where it makes its internal object:
//   mov ecx, <sizeof CObjectGM> ; call <operator new>
//   mov edx, [rip+<count>] ... ; call <CObjectGM ctor>
//   lea r, [rip+"__YYInternalObject__"] ... (the name)
//   mov edx, [rip+<count>] ; mov rcx, [rip+<hash>] ... ; call <hash insert>
bool DecodeCreation(std::uintptr_t lea, Pieces& out, std::string& why) {
    std::uintptr_t newCall = 0;
    for (std::uintptr_t p = lea - 10; p > lea - 0xC0; --p) {
        const auto* b = reinterpret_cast<const unsigned char*>(p);
        std::uintptr_t t = 0;
        if (b[0] == 0xB9 && CallAt(p + 5, t)) {
            const std::uint32_t size = static_cast<std::uint32_t>(Disp(p + 1));
            if (size < 0x80 || size > 0x400) continue;
            out.objectBytes = size;
            out.alloc = reinterpret_cast<AllocFn>(t);
            newCall = p + 5;
            break;
        }
    }
    if (!newCall) { why = "no `mov ecx, size; call new` before the internal object's name"; return false; }

    for (std::uintptr_t p = newCall + 5; p < lea; ++p) {
        std::uintptr_t t = 0;
        if (!CallAt(p, t)) continue;
        for (std::uintptr_t q = p - 16; q < p; ++q) {
            std::uintptr_t count = 0;
            if (MovEdxRip(q, count) && InData(count)) {
                out.ctor = reinterpret_cast<CtorFn>(t);
                out.countGlobal = count;
                break;
            }
        }
        if (out.ctor) break;
    }
    if (!out.ctor) { why = "no constructor call after the allocation"; return false; }

    for (std::uintptr_t p = lea + 7; p < lea + 0xC0; ++p) {
        std::uintptr_t t = 0;
        if (!CallAt(p, t)) continue;
        if (HasOperand(p - 24, p, MovRcxRip, out.hashGlobal) && HasOperand(p - 24, p, MovEdxRip, out.countGlobal)) {
            out.insert = reinterpret_cast<InsertFn>(t);
            break;
        }
    }
    if (!out.insert) { why = "no hash insert of the new object (loading the object hash and count)"; return false; }
    return true;
}

// The CHashMap header size, from the constructor making the event map:
//   mov ecx, <header bytes> ; call <operator new> ... mov dword [rax], 8
std::size_t MapHeaderBytes() {
    const auto ctor = reinterpret_cast<std::uintptr_t>(g.ctor);
    for (std::uintptr_t p = ctor; p < ctor + 0x300; ++p) {
        const auto* b = reinterpret_cast<const unsigned char*>(p);
        std::uintptr_t t = 0;
        if (b[0] != 0xB9 || !CallAt(p + 5, t) || t != reinterpret_cast<std::uintptr_t>(g.alloc)) continue;
        static const unsigned char kInitialSize[] = {0xC7, 0x00, 0x08, 0x00, 0x00, 0x00};
        for (std::uintptr_t q = p + 10; q < p + 10 + 24; ++q)
            if (std::memcmp(reinterpret_cast<const void*>(q), kInitialSize, sizeof(kInitialSize)) == 0)
                return static_cast<std::uint32_t>(Disp(p + 1));
    }
    return 0;
}

// Create_Object_Lists: clears the per-event list counters (8 rounds of 0x80
// bytes) and walks every object index asking each for its events.
//   mov esi, 8 ; lea rax, [rip+lists] ; mov ecx, esi ; xorps xmm0, xmm0
// It must also read the object count and the object hash.
std::uintptr_t FindRebuild(std::string& why) {
    static const unsigned char kHead[] = {0xBE, 0x08, 0x00, 0x00, 0x00, 0x48, 0x8D, 0x05};
    static const unsigned char kTail[] = {0x8B, 0xCE, 0x0F, 0x57, 0xC0};
    const auto tx = sym::TextRange();
    std::vector<std::uintptr_t> found;
    for (std::uintptr_t p = tx.lo; p + 32 < tx.hi; ++p) {
        if (std::memcmp(reinterpret_cast<const void*>(p), kHead, sizeof(kHead)) != 0) continue;
        if (std::memcmp(reinterpret_cast<const void*>(p + 12), kTail, sizeof(kTail)) != 0) continue;
        DWORD64 image = 0;
        const RUNTIME_FUNCTION* rf = RtlLookupFunctionEntry(p, &image, nullptr);
        if (!rf) continue;
        const std::uintptr_t begin = image + rf->BeginAddress, end = image + rf->EndAddress;
        if (p - begin > 0x30) continue;
        bool readsCount = false, readsHash = false;
        for (std::uintptr_t q = begin; q + 8 < end; ++q) {
            const auto* b = reinterpret_cast<const unsigned char*>(q);
            // cmp dword [rip+count], 1
            if (b[0] == 0x83 && b[1] == 0x3D && b[6] == 0x01 && q + 7 + static_cast<std::intptr_t>(Disp(q + 2)) == g.countGlobal)
                readsCount = true;
            // mov rdx, [rip+hash]
            if (b[0] == 0x48 && b[1] == 0x8B && b[2] == 0x15 && q + 7 + static_cast<std::intptr_t>(Disp(q + 3)) == g.hashGlobal)
                readsHash = true;
        }
        if (readsCount && readsHash) found.push_back(begin);
    }
    if (found.size() != 1) {
        why = "the event-list rebuild matched " + std::to_string(found.size()) + " functions (want 1)";
        return 0;
    }
    return found[0];
}

bool Scan(std::string& why) {
    g.hashGlobal = HashFromObjectGetParent();
    if (!g.hashGlobal) { why = "object hash not found in object_get_parent"; return false; }

    const auto strs = RdataStrings("__YYInternalObject__");
    if (strs.size() != 1) { why = "\"__YYInternalObject__\" found " + std::to_string(strs.size()) + " times"; return false; }
    // The name is also read where the runner looks its internal objects up by
    // prefix, and runtimes that still have object_add make the object in two
    // places (Object_Add, and the loader's inlined copy). Sites that do not
    // decode as a creation are passed over; every one that does must name the
    // same pieces.
    const auto leas = LeasTo(strs[0]);
    if (leas.empty() || leas.size() > 4) { why = "the internal object's name is loaded at " + std::to_string(leas.size()) + " sites"; return false; }
    int creations = 0;
    std::string firstWhy;
    for (std::uintptr_t lea : leas) {
        Pieces p;
        p.hashGlobal = g.hashGlobal;
        std::string siteWhy;
        if (!DecodeCreation(lea, p, siteWhy)) {
            if (firstWhy.empty()) firstWhy = siteWhy;
            continue;
        }
        if (creations++ == 0) { g = p; continue; }
        if (p.alloc != g.alloc || p.ctor != g.ctor || p.insert != g.insert || p.countGlobal != g.countGlobal ||
            p.objectBytes != g.objectBytes) {
            why = "the sites that make the internal object disagree";
            return false;
        }
    }
    if (!creations) { why = firstWhy; return false; }

    g.mapBytes = MapHeaderBytes();
    if (g.mapBytes != 0x20 && g.mapBytes != 0x28) {
        why = "event map header of unknown size " + std::to_string(g.mapBytes);
        return false;
    }
    const std::uintptr_t rebuild = FindRebuild(why);
    if (!rebuild) return false;
    g.rebuild = reinterpret_cast<RebuildFn>(rebuild);

    Logf("objtypes: hash %p, count %p, new %p (object %#zx bytes), ctor %p, insert %p, rebuild %p, map header %#zx",
         reinterpret_cast<void*>(g.hashGlobal), reinterpret_cast<void*>(g.countGlobal),
         reinterpret_cast<void*>(g.alloc), g.objectBytes, reinterpret_cast<void*>(g.ctor),
         reinterpret_cast<void*>(g.insert), reinterpret_cast<void*>(g.rebuild), g.mapBytes);
    return true;
}

// ---- the object hash -----------------------------------------------------------

int ObjectCount() {
    std::int32_t n = 0;
    return Read(g.countGlobal, n) ? n : 0;
}

void* Lookup(int index) {
    std::uintptr_t hash = 0, buckets = 0;
    std::int32_t mask = 0;
    if (!Read(g.hashGlobal, hash) || !Read(hash, buckets) || !Read(hash + 8, mask)) return nullptr;
    const auto umask = static_cast<std::uint32_t>(mask);
    if (!buckets || mask <= 0 || (umask & (umask + 1)) != 0) return nullptr;
    std::uintptr_t node = 0;
    if (!Read(buckets + static_cast<std::uintptr_t>(index & mask) * 16, node)) return nullptr;
    struct Node { std::uintptr_t prev, next; std::int32_t key, pad; void* value; };
    for (int hops = 0; node && hops < 4096; ++hops) {
        Node n{};
        if (!Read(node, n)) return nullptr;
        if (n.key == index) return n.value;
        node = n.next;
    }
    return nullptr;
}

template <class T> T Field(void* object, std::size_t off) {
    T v{};
    Read(reinterpret_cast<std::uintptr_t>(object) + off, v);
    return v;
}
MapHeader* EventsOf(void* object) { return Field<MapHeader*>(object, kObjEvents); }

// ---- event maps --------------------------------------------------------------

bool MapLooksSane(const MapHeader* m) {
    MapHeader h{};
    if (!Read(reinterpret_cast<std::uintptr_t>(m), &h, 0x20)) return false;
    const auto size = static_cast<std::uint32_t>(h.size);
    if (!(h.size > 0 && h.size <= (1 << 20) && (size & (size - 1)) == 0 && h.mask == h.size - 1 &&
          h.used >= 0 && h.used <= h.size && h.elems))
        return false;
    // The elements are walked directly afterwards: both ends must be there.
    Elem probe{};
    const auto elems = reinterpret_cast<std::uintptr_t>(h.elems);
    return Read(elems, probe) && Read(elems + (size - 1) * sizeof(Elem), probe);
}

// Probe distance of the entry at `pos` from where its hash wants it.
std::int32_t DistanceAt(const MapHeader* m, std::int32_t pos) {
    return (pos - static_cast<std::int32_t>(m->elems[pos].hash & m->mask) + m->size) & m->mask;
}

Elem* MapFind(MapHeader* m, std::int64_t key) {
    if (!m || !m->elems) return nullptr;
    const std::uint32_t h = HashOf(key);
    std::int32_t pos = static_cast<std::int32_t>(h & m->mask);
    for (std::int32_t dist = 0; dist < m->size; ++dist) {
        Elem& e = m->elems[pos];
        if (e.hash == 0) return nullptr;
        if (e.hash == h && e.key == key) return &e;
        if (static_cast<std::int32_t>(e.hash) > 0 && DistanceAt(m, pos) < dist) return nullptr;
        pos = (pos + 1) & m->mask;
    }
    return nullptr;
}

bool HasProbeFields() { return g.mapBytes >= 0x28; }

// Robin-hood placement of an entry known to be absent; no growing.
void MapPlace(MapHeader* m, std::int64_t key, void* value) {
    Elem cur{value, key, HashOf(key), 0};
    std::int32_t pos = static_cast<std::int32_t>(cur.hash & m->mask);
    std::int32_t dist = 0, probes = 0;
    for (;;) {
        Elem& e = m->elems[pos];
        if (e.hash == 0) {
            e = cur;
            break;
        }
        const std::int32_t theirs = DistanceAt(m, pos);
        if (theirs < dist) {
            std::swap(e, cur);
            dist = theirs;
        }
        pos = (pos + 1) & m->mask;
        ++dist;
        ++probes;
    }
    ++m->used;
    if (HasProbeFields() && probes > m->maxProbe) m->maxProbe = probes;
}

// Doubles the table into a block from the runner's own allocator. The old
// elements are left allocated: nothing else points at them, and a few hundred
// bytes per defined object is not worth trusting a located free for.
bool MapGrow(MapHeader* m) {
    const std::int32_t newSize = m->size * 2;
    auto* fresh = static_cast<Elem*>(GuardedAlloc(static_cast<std::size_t>(newSize) * sizeof(Elem)));
    if (!fresh) return false;
    std::memset(fresh, 0, static_cast<std::size_t>(newSize) * sizeof(Elem));
    Elem* old = m->elems;
    const std::int32_t oldSize = m->size;
    m->elems = fresh;
    m->size  = newSize;
    m->mask  = newSize - 1;
    m->grow  = m->grow * 2;
    m->used  = 0;
    if (HasProbeFields()) { m->grow2 = m->grow2 * 2; m->maxProbe = 0; }
    for (std::int32_t i = 0; i < oldSize; ++i)
        if (old[i].hash != 0 && static_cast<std::int32_t>(old[i].hash) > 0) MapPlace(m, old[i].key, old[i].value);
    return true;
}

// Inserts or replaces.
bool MapPut(MapHeader* m, std::int64_t key, void* value) {
    if (Elem* e = MapFind(m, key)) { e->value = value; return true; }
    while (m->used + 1 > m->grow)
        if (!MapGrow(m)) return false;
    MapPlace(m, key, value);
    return true;
}

// Backward-shift deletion: leaves the table as if the entry was never there.
void MapErase(MapHeader* m, Elem* e) {
    std::int32_t pos = static_cast<std::int32_t>(e - m->elems);
    m->elems[pos].hash = 0;
    for (;;) {
        const std::int32_t next = (pos + 1) & m->mask;
        Elem& n = m->elems[next];
        if (n.hash == 0 || DistanceAt(m, next) == 0) break;
        m->elems[pos] = n;
        n.hash = 0;
        pos = next;
    }
    --m->used;
}

template <class F> void ForEachEntry(MapHeader* m, F f) {
    for (std::int32_t i = 0; i < m->size; ++i) {
        Elem& e = m->elems[i];
        if (e.hash != 0 && static_cast<std::int32_t>(e.hash) > 0) f(e);
    }
}

// ---- the proof ---------------------------------------------------------------

const char* const kEventNames[] = {"Create", "Destroy", "Alarm", "Step", "Collision", "Keyboard", "Mouse",
                                   "Other", "Draw", "KeyPress", "KeyRelease", "Trigger", "CleanUp",
                                   "Gesture", "PreCreate"};
constexpr int kEventTypes = static_cast<int>(sizeof(kEventNames) / sizeof(kEventNames[0]));

// "Step_2" -> type 3, subtype 2. Collision subtypes name objects: subtype -1.
bool ParseEventSuffix(const std::string& s, int& type, int& sub) {
    for (int t = 0; t < kEventTypes; ++t) {
        const std::size_t n = std::strlen(kEventNames[t]);
        if (s.size() <= n + 1 || s.compare(0, n, kEventNames[t]) != 0 || s[n] != '_') continue;
        type = t;
        const std::string rest = s.substr(n + 1);
        if (t == 4) { sub = -1; return true; }
        if (rest.empty() || rest.find_first_not_of("0123456789") != std::string::npos) return false;
        sub = std::stoi(rest);
        return true;
    }
    return false;
}

bool BuiltinString(const char* name, int arg, std::string& out) {
    gml::RValue a{}, r{};
    gml::SetReal(a, arg);
    if (!builtins::Call(name, &r, &a, 1, gml::CurrentSelf())) return false;
    out = gml::ToString(r);
    gml::FreeValue(r);
    return true;
}

bool BuiltinNumber(const char* name, int arg, double& out) {
    gml::RValue a{}, r{};
    gml::SetReal(a, arg);
    if (!builtins::Call(name, &r, &a, 1, gml::CurrentSelf())) return false;
    const std::int32_t kind = r.kind & 0x00FFFFFF;
    if (kind == gml::kReal || kind == gml::kBool) out = r.real;
    else if (kind == gml::kInt32) out = r.i32;
    else if (kind == gml::kInt64 || kind == gml::kRef) out = static_cast<std::int32_t>(r.i64 & 0xFFFFFFFF);
    else return false;
    return true;
}

bool Prove(std::string& why) {
    const int count = ObjectCount();
    if (count <= 0 || count > (1 << 20)) { why = "implausible object count " + std::to_string(count); return false; }

    int objects = 0, withParent = 0, ownEvents = 0, inherited = 0, keysChecked = 0, codeless = 0;
    int bad = 0;
    std::string firstBad;
    auto mismatch = [&](const std::string& what) {
        if (bad++ == 0) firstBad = what;
    };
    std::unordered_map<std::size_t, int> rowVotes;

    for (int i = 0; i < count; ++i) {
        void* o = Lookup(i);
        if (!o) continue;
        ++objects;
        const auto id = Field<std::int32_t>(o, kObjId);
        if (id != i) { mismatch("object " + std::to_string(i) + " has id " + std::to_string(id)); continue; }
        std::string name;
        if (!ReadString(Field<std::uintptr_t>(o, kObjName), name)) { mismatch("object " + std::to_string(i) + " has no name"); continue; }
        const auto parent = Field<std::int32_t>(o, kObjParentIndex);
        if (parent >= 0) {
            ++withParent;
            if (Field<void*>(o, kObjParent) != Lookup(parent)) mismatch(name + "'s parent pointer");
        }

        MapHeader* m = EventsOf(o);
        if (!MapLooksSane(m)) { mismatch(name + "'s event map"); continue; }
        const std::string prefix = "gml_Object_" + name + "_";
        ForEachEntry(m, [&](Elem& e) {
            if (e.hash != HashOf(e.key)) { mismatch(name + ": an event hash"); return; }
            EventRec ev{};
            if (!Read(reinterpret_cast<std::uintptr_t>(e.value), ev)) { mismatch(name + ": a CEvent"); return; }
            // The 2024 runtime keeps events whose code was compiled away: no
            // CCode, and its own list rebuild skips them.
            if (!ev.code) { ++codeless; return; }
            if (ev.owner != i) {
                // Inherited: the owner must be an ancestor.
                int a = parent, hops = 0;
                while (a >= 0 && a != ev.owner && hops++ < 64) {
                    void* ao = Lookup(a);
                    a = ao ? Field<std::int32_t>(ao, kObjParentIndex) : -1;
                }
                if (a != ev.owner) mismatch(name + ": an event owned by a non-ancestor");
                else ++inherited;
                return;
            }
            ++ownEvents;
            // The row: the qword in the CCode that points at {name, function}
            // of a gml_Object_<this object>_ symbol. Searched for until one
            // offset has a clear lead, then only that offset is tried (an
            // event without it there still counts against the proof).
            const auto code = reinterpret_cast<std::uintptr_t>(ev.code);
            std::size_t lead = 0;
            for (const auto& [off, n] : rowVotes)
                if (n >= 64) lead = off;
            for (std::size_t off = lead ? lead : 8; off < (lead ? lead + 8 : kCodeBytes); off += 8) {
                std::uintptr_t rowp = 0;
                Row row{};
                std::string rowName;
                if (!Read(code + off, rowp) || !Read(rowp, row) || !ReadString(reinterpret_cast<std::uintptr_t>(row.name), rowName))
                    continue;
                if (rowName.compare(0, prefix.size(), prefix) != 0 || sym::Find(rowName) != row.fn) continue;
                ++rowVotes[off];
                if (!g.templateCode) {
                    g.templateCode = ev.code;
                    g.templateRow  = reinterpret_cast<const Row*>(rowp);
                }
                int type = -1, sub = -1;
                if (ParseEventSuffix(rowName.substr(prefix.size()), type, sub)) {
                    ++keysChecked;
                    if ((e.key >> 32) != type || (sub >= 0 && static_cast<std::int32_t>(e.key & 0xFFFFFFFF) != sub))
                        mismatch(rowName + " filed under another event");
                }
                break;
            }
        });
        // Flattened inheritance: every event of the parent is in the child's map.
        if (parent >= 0 && withParent <= 400) {
            if (MapHeader* pm = EventsOf(Lookup(parent)); pm && MapLooksSane(pm))
                ForEachEntry(pm, [&](Elem& pe) {
                    if (!MapFind(m, pe.key)) mismatch(name + " lacks an event of its parent");
                });
        }
    }

    // The builtins' word on a spread of objects.
    int sampled = 0;
    for (int i = 0; i < count; i += (count / 64) + 1) {
        void* o = Lookup(i);
        if (!o) continue;
        std::string viaBuiltin, viaStruct;
        double parent = 0;
        if (!BuiltinString("object_get_name", i, viaBuiltin) || !BuiltinNumber("object_get_parent", i, parent)) continue;
        ReadString(Field<std::uintptr_t>(o, kObjName), viaStruct);
        if (viaBuiltin != viaStruct) mismatch("object_get_name(" + std::to_string(i) + ") is " + viaBuiltin);
        if (static_cast<int>(parent) != Field<std::int32_t>(o, kObjParentIndex) &&
            !(parent < 0 && Field<std::int32_t>(o, kObjParentIndex) < 0))
            mismatch("object_get_parent(" + std::to_string(i) + ")");
        ++sampled;
    }

    std::size_t rowOffset = 0;
    int rowBest = 0;
    for (const auto& [off, n] : rowVotes)
        if (n > rowBest) { rowBest = n; rowOffset = off; }

    char summary[320];
    std::snprintf(summary, sizeof(summary),
                  "%d objects (%d with a parent), %d own events (%d keys named, %d without code), %d inherited, "
                  "%d sampled by builtin; row at CCode+%#zx (%d of %d)",
                  objects, withParent, ownEvents, keysChecked, codeless, inherited, sampled, rowOffset, rowBest, ownEvents);
    Logf("objtypes: proof: %s", summary);

    if (bad) { why = std::to_string(bad) + " mismatch(es), first: " + firstBad; return false; }
    if (objects < 2 || sampled < 2) { why = "too few objects to prove anything"; return false; }
    if (ownEvents < 1 || rowBest != ownEvents || rowVotes.size() != 1) {
        why = "the CCode's function row is not where every event has it";
        return false;
    }
    g.rowOffset = rowOffset;
    return true;
}

// ---- defining ----------------------------------------------------------------

int AssetIndex(const char* name) {
    gml::RValue a{}, r{};
    if (!gml::SetString(a, gml::Intern(name))) return -1;
    if (!builtins::Call("asset_get_index", &r, &a, 1, gml::CurrentSelf())) return -1;
    double v = -1;
    const std::int32_t kind = r.kind & 0x00FFFFFF;
    if (kind == gml::kReal) v = r.real;
    else if (kind == gml::kRef || kind == gml::kInt64) v = static_cast<std::int32_t>(r.i64 & 0xFFFFFFFF);
    else if (kind == gml::kInt32) v = r.i32;
    return static_cast<int>(v);
}

bool ValidName(const char* name) {
    if (!name || !*name || std::strlen(name) > 200) return false;
    if (!(std::isalpha(static_cast<unsigned char>(name[0])) || name[0] == '_')) return false;
    for (const char* c = name; *c; ++c)
        if (!(std::isalnum(static_cast<unsigned char>(*c)) || *c == '_')) return false;
    return true;
}

// Copies the parent's events into the child's map, for every event the child
// does not define itself.
bool InheritEvents(Defined& d, void* parentObject) {
    MapHeader* pm = EventsOf(parentObject);
    MapHeader* m  = EventsOf(d.object);
    bool ok = true;
    ForEachEntry(pm, [&](Elem& pe) {
        if (d.events.count(pe.key)) return;
        ok = MapPut(m, pe.key, pe.value) && ok;
    });
    return ok;
}

// The object's parent becomes `parent` (-1 for none): its inherited entries
// are dropped and the new parent's copied in.
bool Link(Defined& d, int parent) {
    MapHeader* m = EventsOf(d.object);
    std::vector<std::int64_t> drop;
    ForEachEntry(m, [&](Elem& e) {
        if (!d.events.count(e.key)) drop.push_back(e.key);
    });
    for (std::int64_t key : drop)
        if (Elem* e = MapFind(m, key)) MapErase(m, e);

    auto* base = static_cast<unsigned char*>(d.object);
    void* parentObject = parent >= 0 ? Lookup(parent) : nullptr;
    *reinterpret_cast<std::int32_t*>(base + kObjParentIndex) = parent;
    *reinterpret_cast<void**>(base + kObjParent) = parentObject;
    d.parent = parent;
    return !parentObject || InheritEvents(d, parentObject);
}

void Fallback(void* self, void* other, int hookId) { CallInherited(hookId, self, other); }

// Hands `key`'s event of `object` down to the defined objects below it that
// do not define it themselves.
void PropagateDown(int object, std::int64_t key, void* event) {
    for (auto& [index, child] : g_defined) {
        if (child.parent != object || child.events.count(key)) continue;
        MapPut(EventsOf(child.object), key, event);
        PropagateDown(index, key, event);
    }
}

} // namespace

void Verify() {
    if (g_state != State::Pending) return;
    if (!gml::Ready() || !builtins::Ready() || !gml::CurrentSelf()) return;

    std::string why;
    if (!Scan(why)) { Fail(why); return; }
    if (!Prove(why)) { Fail(why); return; }
    g_state  = State::Proven;
    g_status = "available";
    Logf("objtypes: object types available (%d objects)", ObjectCount());
}

bool        Ready()  { return g_state == State::Proven; }
const char* Status() { return g_status.c_str(); }

void Flush() {
    if (!g_dirty || !Ready()) return;
    g_dirty = false;
    LARGE_INTEGER t0, t1, f;
    QueryPerformanceCounter(&t0);
    const bool ok = GuardedRebuild();
    QueryPerformanceCounter(&t1);
    QueryPerformanceFrequency(&f);
    static int logged = 0;
    if (!ok) Logf("[!] objtypes: rebuilding the event lists faulted");
    else if (logged++ < 3)
        Logf("objtypes: event lists rebuilt in %.2f ms", 1000.0 * static_cast<double>(t1.QuadPart - t0.QuadPart) / static_cast<double>(f.QuadPart));
}

bool IsDefined(int object) { return g_defined.count(object) != 0; }

int Define(const char* name, int parent) {
    if (!Ready()) { Logf("[!] objtypes: Define refused: %s", g_status.c_str()); return -1; }
    if (!ValidName(name)) { Logf("[!] objtypes: \"%s\" is not a valid object name", name ? name : ""); return -1; }
    if (parent >= 0 && !Lookup(parent)) { Logf("[!] objtypes: %s: parent %d does not exist", name, parent); return -1; }

    if (auto it = g_byName.find(name); it != g_byName.end()) {
        Defined& d = g_defined[it->second];
        if (d.parent != parent) {
            for (int a = parent, hops = 0; a >= 0 && hops < 64; ++hops) {
                if (a == d.index) { Logf("[!] objtypes: %s cannot be its own ancestor", name); return -1; }
                void* ao = Lookup(a);
                a = ao ? Field<std::int32_t>(ao, kObjParentIndex) : -1;
            }
            if (!Link(d, parent)) Logf("[!] objtypes: %s: re-parenting left events behind", name);
            g_dirty = true;
            Logf("objtypes: %s (%d) re-parented to %d", name, d.index, parent);
        }
        return d.index;
    }
    if (AssetIndex(name) >= 0) { Logf("[!] objtypes: the game already has an asset named %s", name); return -1; }

    const int index = ObjectCount();
    void* object = GuardedAlloc(g.objectBytes);
    if (!object || !GuardedCtor(object, index)) { Logf("[!] objtypes: %s: creating the object faulted", name); return -1; }
    const std::size_t len = std::strlen(name);
    auto* heapName = static_cast<char*>(GuardedAlloc(len + 1));
    if (!heapName) return -1;
    std::memcpy(heapName, name, len + 1);
    *reinterpret_cast<char**>(static_cast<unsigned char*>(object) + kObjName) = heapName;

    if (Field<std::int32_t>(object, kObjId) != index || !MapLooksSane(EventsOf(object))) {
        Logf("[!] objtypes: %s: the constructed object does not look as proven", name);
        return -1;
    }
    std::uintptr_t hash = 0;
    if (!Read(g.hashGlobal, hash) || !GuardedInsert(reinterpret_cast<void*>(hash), index, object)) {
        Logf("[!] objtypes: %s: inserting the object faulted", name);
        return -1;
    }
    *reinterpret_cast<std::int32_t*>(g.countGlobal) = index + 1;

    Defined& d = g_defined[index];
    d.name   = name;
    d.object = object;
    d.index  = index;
    g_byName[d.name] = index;
    if (parent >= 0 && !Link(d, parent)) Logf("[!] objtypes: %s: inheriting the parent's events failed", name);
    g_dirty = true;

    // The runner's own view of it.
    std::string viaName;
    double viaParent = -2;
    BuiltinString("object_get_name", index, viaName);
    BuiltinNumber("object_get_parent", index, viaParent);
    Logf("objtypes: defined %s = %d (parent %d); runtime sees \"%s\", parent %d, asset_get_index %d",
         name, index, parent, viaName.c_str(), static_cast<int>(viaParent), AssetIndex(name));
    return index;
}

int DefineEvent(int object, int type, int subtype) {
    auto it = g_defined.find(object);
    if (!Ready() || it == g_defined.end()) return -1;
    if (type < 0 || type >= kEventTypes || subtype < 0) {
        Logf("[!] objtypes: no event type %d / subtype %d", type, subtype);
        return -1;
    }
    Defined& d = it->second;
    const std::int64_t key = (static_cast<std::int64_t>(type) << 32) | static_cast<std::uint32_t>(subtype);
    if (auto e = d.events.find(key); e != d.events.end()) return e->second.hookId;

    const int hookId = hk::InstallDefined(&Fallback);
    void* fn = hookId >= 0 ? hk::DefinedFunction(hookId) : nullptr;
    if (!fn) return -1;

    const std::string symbol = "gml_Object_" + d.name + "_" + kEventNames[type] + "_" + std::to_string(subtype);
    Row& row = g_rows.emplace_back(Row{gml::Intern(symbol), fn, g.templateRow->extra});

    auto* code = static_cast<unsigned char*>(GuardedAlloc(kCodeBytes));
    auto* event = static_cast<EventRec*>(GuardedAlloc(sizeof(EventRec)));
    if (!code || !event) return -1;
    std::memset(code, 0, kCodeBytes);
    // Copied in shrinking pieces: the template's block may end before kCodeBytes.
    std::size_t copied = 0;
    for (std::size_t n = kCodeBytes; n >= 0x40 && !copied; n -= 0x20)
        if (gml::ReadMemory(g.templateCode, code, static_cast<int>(n))) copied = n;
    if (copied <= g.rowOffset) { Logf("[!] objtypes: could not copy a template CCode"); return -1; }
    for (std::size_t off = 0; off + 8 <= copied; off += 8) {
        auto* slot = reinterpret_cast<const char**>(code + off);
        if (*slot == g.templateRow->name) *slot = row.name;
    }
    *reinterpret_cast<Row**>(code + g.rowOffset) = &row;
    event->code  = code;
    event->owner = object;
    event->pad   = 0;

    if (!MapPut(EventsOf(d.object), key, event)) { Logf("[!] objtypes: %s: growing the event map failed", symbol.c_str()); return -1; }
    d.events[key] = DefinedEvent{hookId, event};
    g_byHook[hookId] = HookTarget{object, key};
    PropagateDown(object, key, event);
    g_dirty = true;
    Logf("objtypes: %s -> hook #%d", symbol.c_str(), hookId);
    return hookId;
}

bool CallInherited(int hookId, void* self, void* other) {
    auto t = g_byHook.find(hookId);
    if (t == g_byHook.end()) return false;
    auto d = g_defined.find(t->second.object);
    if (d == g_defined.end() || d->second.parent < 0) return false;
    void* parent = Lookup(d->second.parent);
    MapHeader* pm = parent ? EventsOf(parent) : nullptr;
    Elem* e = pm ? MapFind(pm, t->second.key) : nullptr;
    EventRec ev{};
    std::uintptr_t rowp = 0;
    Row row{};
    if (!e || !Read(reinterpret_cast<std::uintptr_t>(e->value), ev) ||
        !Read(reinterpret_cast<std::uintptr_t>(ev.code) + g.rowOffset, rowp) || !Read(rowp, row) || !row.fn)
        return false;
    return gml::CallEvent(row.fn, self, other);
}

} // namespace mod::objtypes
