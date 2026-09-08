// On-demand script tracer.
//
// Every compiled script calls one shared helper on entry (`mov ecx,<id>` then
// `call <helper>`). That target has ~9,260 call sites, so detouring it once
// observes almost every script in the game - as opposed to hooking 34,167
// functions individually, which would be unusable.
//
// The helper's `ecx` id does not map onto a script index, so the caller is
// identified a different way: _ReturnAddress() lands inside the calling
// function, and sym::OwnerOf turns that into a name.
//
// Cost discipline, because this sits on a very hot path:
//   * the hook is installed on Start and removed on Stop - idle costs nothing
//   * the detour only appends to a pre-allocated ring; no allocation, no I/O
//   * a worker thread drains the ring to disk
//   * call stacks are opt-in

#include "tracer.h"
#include "log.h"
#include "gml.h"
#include "symbols.h"

#include <windows.h>
#include <intrin.h>
#include <MinHook.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <thread>
#include <unordered_map>

namespace mod::tracer {
namespace {

// The helper takes its id in ecx. Four integer parameters are declared so that
// whatever the game had in rcx/rdx/r8/r9 is forwarded untouched - a narrower
// signature would silently drop arguments the real function may use.
using HelperFn = std::uintptr_t (*)(std::uintptr_t, std::uintptr_t,
                                    std::uintptr_t, std::uintptr_t);

HelperFn    g_original = nullptr;
void*       g_helper   = nullptr;
bool        g_ready    = false;
std::string g_status   = "not initialised";

// ------------------------------------------------------------------ records

enum class Kind : unsigned char { Call, PlayerStep, Key };

struct Record {
    const char*    name;      // points into the game's .rdata; stable
    long long      qpc;
    Kind           kind;
    unsigned char  depth;
    double         x, y;
    int            key;
};

constexpr std::size_t kRingSize = 1u << 18;   // 262144 records, ~12 MB
Record*                     g_ring = nullptr;
std::atomic<unsigned long long> g_head{0};    // producer
std::atomic<unsigned long long> g_tail{0};    // consumer
std::atomic<unsigned long long> g_dropped{0};

std::atomic<bool> g_recording{false};
std::atomic<bool> g_draining{false};
std::thread       g_worker;

Options     g_opts;
std::string g_file;
long long   g_qpcFreq  = 1;
long long   g_qpcStart = 0;
DWORD       g_stopAt   = 0;

long long Now() {
    LARGE_INTEGER v;
    QueryPerformanceCounter(&v);
    return v.QuadPart;
}

// Producer side. Must stay allocation-free and lock-free.
inline void Push(const Record& r) {
    const unsigned long long head = g_head.load(std::memory_order_relaxed);
    const unsigned long long tail = g_tail.load(std::memory_order_acquire);
    if (head - tail >= kRingSize) {          // full: drop rather than stall the game
        g_dropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    g_ring[head & (kRingSize - 1)] = r;
    g_head.store(head + 1, std::memory_order_release);
}

std::uintptr_t HelperDetour(std::uintptr_t a, std::uintptr_t b,
                            std::uintptr_t c, std::uintptr_t d) {
    if (g_recording.load(std::memory_order_relaxed)) {
        const char* name = sym::OwnerOf(_ReturnAddress());
        if (name) {
            bool keep = true;
            if (g_opts.filter[0]) keep = std::strstr(name, g_opts.filter) != nullptr;
            if (keep) {
                Record r{};
                r.name  = name;
                r.qpc   = Now();
                r.kind  = Kind::Call;
                r.depth = 0;
                if (g_opts.stacks) {
                    void* frames[24];
                    const USHORT n = RtlCaptureStackBackTrace(1, 24, frames, nullptr);
                    r.depth = static_cast<unsigned char>(n);
                }
                Push(r);
            }
        }
    }
    return g_original(a, b, c, d);
}

// ------------------------------------------------------------ consensus scan

bool Readable(std::uintptr_t p, std::size_t n) {
    return sym::TextRange().contains(p) && sym::TextRange().contains(p + n);
}

// Prologues look like:  8B 0D <rel32>   (mov ecx,[rip+d32])
//                       E8 <rel32>      (call helper)
void TallyHelper(std::uintptr_t fn, std::unordered_map<std::uintptr_t, int>& votes) {
    for (std::size_t i = 0; i + 12 < 0x100; ++i) {
        const std::uintptr_t at = fn + i;
        if (!Readable(at, 12)) return;
        const auto* b = reinterpret_cast<const unsigned char*>(at);
        if (!(b[0] == 0x8B && b[1] == 0x0D)) continue;

        for (std::size_t j = 6; j < 14 && Readable(at + j, 5); ++j) {
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

// --------------------------------------------------------------- file output

void WriteHeader(std::FILE* f) {
    std::fprintf(f, "# Stoneshard script trace\n");
    std::fprintf(f, "# helper=%p  filter=\"%s\"  stacks=%s  cap=%dms\n",
                 g_helper, g_opts.filter, g_opts.stacks ? "on" : "off", g_opts.durationMs);
    std::fprintf(f, "# columns: ms  kind  detail\n");
}

void Drain(std::FILE* f) {
    unsigned long long tail = g_tail.load(std::memory_order_relaxed);
    const unsigned long long head = g_head.load(std::memory_order_acquire);
    for (; tail < head; ++tail) {
        const Record& r = g_ring[tail & (kRingSize - 1)];
        const double ms = static_cast<double>(r.qpc - g_qpcStart) * 1000.0 /
                          static_cast<double>(g_qpcFreq);
        switch (r.kind) {
        case Kind::Call:
            if (r.depth) std::fprintf(f, "%10.3f  call   %s  (depth %u)\n", ms, r.name, r.depth);
            else         std::fprintf(f, "%10.3f  call   %s\n", ms, r.name);
            break;
        case Kind::PlayerStep:
            std::fprintf(f, "%10.3f  player x=%.2f y=%.2f\n", ms, r.x, r.y);
            break;
        case Kind::Key:
            std::fprintf(f, "%10.3f  key    vk=0x%02X\n", ms, r.key);
            break;
        }
    }
    g_tail.store(tail, std::memory_order_release);
}

void WorkerMain() {
    std::FILE* f = std::fopen(g_file.c_str(), "w");
    if (!f) {
        Logf("[!] tracer: could not open %s", g_file.c_str());
        g_draining.store(false);
        return;
    }
    WriteHeader(f);
    while (g_draining.load(std::memory_order_acquire)) {
        Drain(f);
        std::fflush(f);
        Sleep(15);
    }
    Drain(f);                                  // final sweep after the stop
    std::fprintf(f, "# recorded=%llu dropped=%llu\n", Recorded(), Dropped());
    std::fclose(f);
    Logf("tracer: wrote %s (%llu records, %llu dropped)",
         g_file.c_str(), Recorded(), Dropped());
}

std::string Timestamp() {
    SYSTEMTIME st{};
    GetLocalTime(&st);
    char b[32];
    std::snprintf(b, sizeof(b), "%04u%02u%02u-%02u%02u%02u",
                  st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return b;
}

} // namespace

bool        Ready()         { return g_ready; }
const char* Status()        { return g_status.c_str(); }
void*       HelperAddress() { return g_helper; }
bool        Recording()     { return g_recording.load(std::memory_order_relaxed); }
unsigned long long Recorded() { return g_head.load(std::memory_order_relaxed); }
unsigned long long Dropped()  { return g_dropped.load(std::memory_order_relaxed); }
const std::string& LastFile() { return g_file; }

int RemainingMs() {
    if (!Recording()) return 0;
    const DWORD now = GetTickCount();
    return (g_stopAt > now) ? static_cast<int>(g_stopAt - now) : 0;
}

bool Init() {
    if (g_ready) return true;
    if (!sym::Healthy()) { g_status = "symbol resolver unhealthy"; return false; }

    std::unordered_map<std::uintptr_t, int> votes;
    std::size_t sampled = 0;
    for (const sym::Entry& e : sym::All()) {
        if (sampled >= 4000) break;
        if (std::strncmp(e.name, "gml_Script_", 11) != 0) continue;
        ++sampled;
        TallyHelper(reinterpret_cast<std::uintptr_t>(e.func), votes);
    }

    std::uintptr_t best = 0, second = 0;
    int bestN = 0, secondN = 0;
    for (const auto& [addr, n] : votes) {
        if (n > bestN) { second = best; secondN = bestN; best = addr; bestN = n; }
        else if (n > secondN) { second = addr; secondN = n; }
    }
    Logf("tracer: prologue helper consensus -> %p (%d votes; runner-up %p %d) from %zu functions",
         reinterpret_cast<void*>(best), bestN, reinterpret_cast<void*>(second), secondN, sampled);

    // A weak margin means the pattern changed; hooking a hot unrelated function
    // would be far worse than not tracing at all.
    if (!best || bestN < 200 || bestN < secondN * 3) {
        g_status = "prologue helper consensus too weak - tracing disabled";
        Logf("[!] tracer: %s", g_status.c_str());
        return false;
    }

    g_helper = reinterpret_cast<void*>(best);
    g_ring   = static_cast<Record*>(::operator new(sizeof(Record) * kRingSize));

    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_qpcFreq = f.QuadPart ? f.QuadPart : 1;

    g_ready  = true;
    g_status = "ready";
    return true;
}

bool StartRecording(const Options& opts) {
    if (!g_ready || Recording()) return false;

    g_opts = opts;
    g_head.store(0);
    g_tail.store(0);
    g_dropped.store(0);
    g_qpcStart = Now();
    g_stopAt   = GetTickCount() + static_cast<DWORD>(opts.durationMs);
    g_file     = std::string(MOD_DATA_DIR) + "\\trace-" + Timestamp() + ".log";

    if (MH_CreateHook(g_helper, reinterpret_cast<void*>(&HelperDetour),
                      reinterpret_cast<void**>(&g_original)) != MH_OK ||
        MH_EnableHook(g_helper) != MH_OK) {
        Logf("[!] tracer: hook failed");
        g_original = nullptr;
        return false;
    }

    g_draining.store(true);
    g_worker = std::thread(&WorkerMain);
    g_recording.store(true);
    Logf("tracer: recording for %d ms -> %s", opts.durationMs, g_file.c_str());
    return true;
}

void StopRecording() {
    if (!Recording()) return;
    g_recording.store(false);

    MH_DisableHook(g_helper);
    MH_RemoveHook(g_helper);
    g_original = nullptr;

    g_draining.store(false);
    if (g_worker.joinable()) g_worker.join();
}

void Tick() {
    if (Recording() && GetTickCount() >= g_stopAt) StopRecording();
}

void NotePlayerStep(double x, double y, bool havePos) {
    if (!Recording()) return;
    Record r{};
    r.kind = Kind::PlayerStep;
    r.qpc  = Now();
    r.x    = havePos ? x : 0.0;
    r.y    = havePos ? y : 0.0;
    Push(r);
}

void NoteKey(int virtualKey) {
    if (!Recording()) return;
    Record r{};
    r.kind = Kind::Key;
    r.qpc  = Now();
    r.key  = virtualKey;
    Push(r);
}

// ---------------------------------------------------------------- breakpoints

namespace {

using BpScriptFn = gml::RValue* (*)(void*, void*, gml::RValue*, int, gml::RValue**);
using BpEventFn  = void (*)(void*, void*);

BreakpointInfo g_bp;
void*          g_bpFn        = nullptr;
bool           g_bpIsEvent   = false;
BpScriptFn     g_bpScriptOrig = nullptr;
BpEventFn      g_bpEventOrig  = nullptr;

// A resolved call stack is the most useful half of a breakpoint: it says who
// asked for this, which is exactly what static analysis kept failing to answer.
std::string CaptureStack() {
    void*  frames[24];
    const USHORT n = RtlCaptureStackBackTrace(2, 24, frames, nullptr);
    std::string out;
    int shown = 0;
    for (USHORT i = 0; i < n && shown < 10; ++i) {
        const char* name = sym::OwnerOf(frames[i]);
        if (!name) continue;
        out += "\n      <- ";
        out += name;
        ++shown;
    }
    return out.empty() ? std::string("\n      <- <no resolvable frames>") : out;
}

void ReportHit(void* self, void* other, int argc, gml::RValue** args) {
    ++g_bp.hits;

    char head[256];
    std::snprintf(head, sizeof(head), "%s  hit #%u  self=%p other=%p argc=%d",
                  g_bp.symbol.c_str(), g_bp.hits, self, other, argc);
    std::string report = head;

    if (args) {
        for (int i = 0; i < argc && i < 12; ++i) {
            char line[220];
            if (args[i])
                std::snprintf(line, sizeof(line), "\n    arg[%d] kind=%d %s",
                              i, args[i]->kind, gml::ToString(*args[i]).c_str());
            else
                std::snprintf(line, sizeof(line), "\n    arg[%d] <null>", i);
            report += line;
        }
    }
    report += CaptureStack();

    g_bp.lastReport = report;
    Logf("breakpoint: %s", report.c_str());

    if (g_bp.hitLimit > 0 && static_cast<int>(g_bp.hits) >= g_bp.hitLimit) {
        Logf("breakpoint: hit limit reached, clearing %s", g_bp.symbol.c_str());
        ClearBreakpoint();
    }
}

gml::RValue* BpScriptDetour(void* self, void* other, gml::RValue* result,
                            int argc, gml::RValue** args) {
    ReportHit(self, other, argc, args);
    if (g_bp.skipOriginal) {
        if (result) { result->ptr = nullptr; result->flags = 0; result->kind = gml::kUndefined; }
        return result;
    }
    return g_bpScriptOrig(self, other, result, argc, args);
}

void BpEventDetour(void* self, void* other) {
    ReportHit(self, other, 0, nullptr);
    if (g_bp.skipOriginal) return;
    g_bpEventOrig(self, other);
}

} // namespace

const BreakpointInfo& Breakpoint() { return g_bp; }

void ClearBreakpoint() {
    if (!g_bpFn) return;
    MH_DisableHook(g_bpFn);
    MH_RemoveHook(g_bpFn);
    g_bpFn         = nullptr;
    g_bpScriptOrig = nullptr;
    g_bpEventOrig  = nullptr;
    g_bp.active    = false;
}

bool SetBreakpoint(const std::string& symbol, bool skipOriginal, int hitLimit) {
    ClearBreakpoint();
    if (symbol.empty()) return false;

    std::string resolved = symbol;
    void* fn = sym::Find(resolved);
    if (!fn) {
        resolved = "gml_Script_" + symbol;
        fn = sym::Find(resolved);
    }
    if (!fn) { Logf("[!] breakpoint: symbol not found: %s", symbol.c_str()); return false; }

    g_bp = BreakpointInfo{};
    g_bp.symbol       = resolved;
    g_bp.skipOriginal = skipOriginal;
    g_bp.hitLimit     = hitLimit;
    g_bpIsEvent       = gml::IsEventSymbol(resolved);

    void*  detour = g_bpIsEvent ? reinterpret_cast<void*>(&BpEventDetour)
                                : reinterpret_cast<void*>(&BpScriptDetour);
    void** orig   = g_bpIsEvent ? reinterpret_cast<void**>(&g_bpEventOrig)
                                : reinterpret_cast<void**>(&g_bpScriptOrig);

    if (MH_CreateHook(fn, detour, orig) != MH_OK || MH_EnableHook(fn) != MH_OK) {
        Logf("[!] breakpoint: hook failed for %s", resolved.c_str());
        g_bpScriptOrig = nullptr;
        g_bpEventOrig  = nullptr;
        return false;
    }

    g_bpFn      = fn;
    g_bp.active = true;
    Logf("breakpoint: armed on %s at %p (%s%s)", resolved.c_str(), fn,
         g_bpIsEvent ? "event" : "script", skipOriginal ? ", SKIPPING original" : "");
    return true;
}

} // namespace mod::tracer
