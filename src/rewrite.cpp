#include "rewrite.h"

#include "gml.h"
#include "hookengine.h"
#include "log.h"
#include "symbols.h"

#include <windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "imgui.h"

namespace mod::rewrite {
namespace {

// Rules run as native handlers on the shared hook engine, which carries each
// slot's context itself - so the old fixed set of template detours (and their
// 12-hook limit, and their clash with mods hooking the same script) is gone.
// The slot array only bounds how many rules the tab manages at once.
constexpr std::size_t kSlots = 64;

struct Slot {
    void*             target = nullptr;
    int               hookId = -1;
    std::string       symbol;

    // Read from the game thread inside the detour, written from the UI (also
    // the game thread, since the overlay draws in the Present hook). Atomic
    // anyway: a detour firing from another thread must never see a torn value.
    std::atomic<int>    argIndex{-1};
    std::atomic<double> factor{1.0};
    std::atomic<double> addend{0.0};
    std::atomic<double> minValue{0.0};
    std::atomic<double> maxValue{9999.0};
    std::atomic<bool>   roundToInt{true};
    std::atomic<double> repeat{1.0};
    std::atomic<bool>   enabled{false};
    std::atomic<bool>   live{false};

    std::atomic<unsigned> hits{0};
    std::atomic<unsigned> changed{0};
    std::atomic<unsigned> repeated{0};
    std::atomic<unsigned> suppressed{0};

    // Only touched under g_reportLock, and only for display.
    std::string lastSeen;
};

std::array<Slot, kSlots> g_slots;
CRITICAL_SECTION         g_reportLock;
bool                     g_lockReady = false;
std::string              g_error;

void EnsureLock() {
    if (!g_lockReady) { InitializeCriticalSection(&g_reportLock); g_lockReady = true; }
}

void Fail(const char* fmt, ...) {
    char buf[256];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    g_error = buf;
    Logf("[!] rewrite: %s", buf);
}

bool IsNumber(const gml::RValue& v) {
    return v.kind == gml::kReal || v.kind == gml::kInt32 || v.kind == gml::kInt64;
}

double NumberOf(const gml::RValue& v) {
    switch (v.kind) {
    case gml::kReal:  return v.real;
    case gml::kInt32: return static_cast<double>(v.i32);
    case gml::kInt64: return static_cast<double>(v.i64);
    default:          return 0.0;
    }
}

// Writes back in the SAME representation the argument arrived in. Turning an
// int64 argument into a real (or the reverse) would be a quiet type change the
// callee never agreed to.
void StoreNumber(gml::RValue& v, double value) {
    switch (v.kind) {
    case gml::kInt32: v.i32 = static_cast<std::int32_t>(value); break;
    case gml::kInt64: v.i64 = static_cast<std::int64_t>(value); break;
    default:          v.real = value; break;
    }
}

void RecordSeen(Slot& s, int argc, gml::RValue** args) {
    // Snapshotting every call would be pure overhead; the display only ever
    // shows the most recent one, and only the first few arguments fit.
    char buf[512];
    int  n = std::snprintf(buf, sizeof(buf), "argc=%d ", argc);
    for (int i = 0; i < argc && i < 8 && n > 0 && n < static_cast<int>(sizeof(buf)) - 40; ++i) {
        if (!args[i]) { n += std::snprintf(buf + n, sizeof(buf) - n, "[%d]<null> ", i); continue; }

        if (IsNumber(*args[i])) {
            n += std::snprintf(buf + n, sizeof(buf) - n, "[%d]=%g ", i, NumberOf(*args[i]));
        } else if (args[i]->kind == gml::kString) {
            // Printing the CONTENT matters: the loot tables express counts and
            // rarity as range strings ("1,5", "common, uncommon"), so a bare
            // "kind1" hides exactly the argument worth finding.
            std::string v = gml::ToString(*args[i]);
            if (v.size() > 22) v.resize(22);
            n += std::snprintf(buf + n, sizeof(buf) - n, "[%d]=\"%s\" ", i, v.c_str());
        } else if (args[i]->kind == gml::kBool) {
            n += std::snprintf(buf + n, sizeof(buf) - n, "[%d]=%s ",
                               i, args[i]->i32 ? "true" : "false");
        } else {
            n += std::snprintf(buf + n, sizeof(buf) - n, "[%d]kind%d ", i, args[i]->kind);
        }
    }

    EnsureLock();
    EnterCriticalSection(&g_reportLock);
    s.lastSeen.assign(buf);
    LeaveCriticalSection(&g_reportLock);
}

// The whole point of the module, kept deliberately small and total: every path
// out of here either leaves the argument untouched or writes a finite number
// back into the slot it came from.
void Apply(Slot& s, int argc, gml::RValue** args) {
    s.hits.fetch_add(1, std::memory_order_relaxed);
    RecordSeen(s, argc, args);

    if (!s.enabled.load(std::memory_order_relaxed)) return;

    const int idx = s.argIndex.load(std::memory_order_relaxed);
    if (idx < 0 || idx >= argc) return;          // argc varies between call sites
    if (!args || !args[idx]) return;
    if (!IsNumber(*args[idx])) return;           // never rewrite a string or a ref

    const double in  = NumberOf(*args[idx]);
    double       out = in * s.factor.load(std::memory_order_relaxed)
                          + s.addend.load(std::memory_order_relaxed);

    if (!std::isfinite(out)) return;             // a NaN handed to the game is a crash

    out = std::clamp(out, s.minValue.load(std::memory_order_relaxed),
                          s.maxValue.load(std::memory_order_relaxed));
    if (s.roundToInt.load(std::memory_order_relaxed)) out = std::floor(out + 0.5);

    if (out == in) return;

    StoreNumber(*args[idx], out);
    s.changed.fetch_add(1, std::memory_order_relaxed);
}

// A fractional repeat needs a coin flip. The game's own RNG is not used on
// purpose: drawing from it would consume rolls the game expects to make itself
// and could shift world generation in ways nobody asked for.
double NextRandom() {
    static std::atomic<std::uint64_t> state{0x9E3779B97F4A7C15ull};
    std::uint64_t x = state.fetch_add(0x9E3779B97F4A7C15ull, std::memory_order_relaxed);
    x ^= x >> 30; x *= 0xBF58476D1CE4E5B9ull;
    x ^= x >> 27; x *= 0x94D049BB133111EBull;
    x ^= x >> 31;
    return static_cast<double>(x >> 11) / 9007199254740992.0;   // [0,1)
}

// Runs the original 0..N times. Anything past a handful is almost certainly a
// typo rather than an intent, and a runaway here would spew items until the
// game died.
int RepeatCount(double repeat) {
    if (repeat <= 0.0) return 0;

    constexpr int kMaxRepeats = 20;
    if (repeat > kMaxRepeats) repeat = kMaxRepeats;

    const double whole = std::floor(repeat);
    int          n     = static_cast<int>(whole);
    if (NextRandom() < (repeat - whole)) ++n;         // the fractional part
    return n;
}

// Extra runs decided in Before, carried out in After. Hooked calls nest, so a
// per-thread stack pairs each After with its Before - keyed by the call record,
// because a GML exception can unwind a call whose After then never runs: any
// entries above the matching one belong to such abandoned calls and are dropped.
struct PendingRuns { const hk::Call* call; int extra; };
thread_local std::vector<PendingRuns> g_extraRuns;

void RuleBefore(hk::Call* c, void* ctx) {
    Slot& s = *static_cast<Slot*>(ctx);
    if (!s.live.load(std::memory_order_acquire)) { g_extraRuns.push_back({c, 0}); return; }

    Apply(s, c->argc, c->args);

    // Repeat only applies when armed; observing must never change how many
    // times the game's own call actually happens.
    const double repeat = s.enabled.load(std::memory_order_relaxed)
                              ? s.repeat.load(std::memory_order_relaxed) : 1.0;
    if (repeat == 1.0) { g_extraRuns.push_back({c, 0}); return; }

    const int times = RepeatCount(repeat);
    if (times == 0) {
        // Suppressed this time round - hand back a well-formed "nothing".
        c->skip = 1;
        if (c->result) { c->result->ptr = nullptr; c->result->flags = 0; c->result->kind = gml::kUndefined; }
        s.suppressed.fetch_add(1, std::memory_order_relaxed);
        g_extraRuns.push_back({c, 0});
        return;
    }
    g_extraRuns.push_back({c, times - 1});
}

void RuleAfter(hk::Call* c, void* ctx) {
    Slot& s = *static_cast<Slot*>(ctx);
    // Pop down to this call's own entry; anything above it was pushed by a
    // nested call that unwound before its After could run.
    int extra = 0;
    bool found = false;
    while (!g_extraRuns.empty()) {
        const PendingRuns top = g_extraRuns.back();
        g_extraRuns.pop_back();
        if (top.call == c) { extra = top.extra; found = true; break; }
    }
    if (!found || extra <= 0 || c->skip) return;

    // The unhooked original, so the repeats neither re-enter this rule nor
    // any other subscriber of the same script. Each repeat writes the result
    // slot, so what is already there is released first.
    for (int i = 0; i < extra; ++i) {
        if (c->result) gml::FreeValue(*c->result);
        hk::CallOriginal(c, c->result);
    }
    s.repeated.fetch_add(1, std::memory_order_relaxed);
}

int FindByTarget(void* target) {
    for (std::size_t i = 0; i < kSlots; ++i)
        if (g_slots[i].target == target) return static_cast<int>(i);
    return -1;
}

int FindSlot(const std::string& symbol) {
    for (std::size_t i = 0; i < kSlots; ++i)
        if (g_slots[i].target && g_slots[i].symbol == symbol) return static_cast<int>(i);
    return -1;
}

int FreeSlot() {
    for (std::size_t i = 0; i < kSlots; ++i)
        if (!g_slots[i].target) return static_cast<int>(i);
    return -1;
}

void ApplyRuleTo(Slot& s, const Rule& r) {
    s.argIndex.store(r.argIndex);
    s.factor.store(r.factor);
    s.addend.store(r.addend);
    s.minValue.store(r.minValue);
    s.maxValue.store(r.maxValue);
    s.roundToInt.store(r.roundToInt);
    s.repeat.store(r.repeat);
    s.enabled.store(r.enabled);
}

} // namespace

const char* LastError() { return g_error.c_str(); }
std::size_t Capacity()  { return kSlots; }

bool Install(const Rule& rule) {
    g_error.clear();

    std::string symbol = rule.symbol;
    if (symbol.empty()) { Fail("no symbol given"); return false; }

    // Events take (self, other) with no argument array, so there is nothing to
    // rewrite and the script signature would corrupt the stack.
    if (gml::IsEventSymbol(symbol)) {
        Fail("%s is an object event - it has no arguments to rewrite", symbol.c_str());
        return false;
    }

    // Resolve FIRST, then look for an existing hook.
    //
    // Getting this order wrong was not cosmetic: a slot installed as
    // "scr_loot" is stored under the resolved "gml_Script_scr_loot", so a
    // later call using the SHORT name found no slot, tried to hook the same
    // function a second time, and MinHook refused with ALREADY_CREATED. The
    // preset buttons therefore did nothing at all whenever the script was
    // already being observed - which was the documented way to use them.
    void* fn = sym::Find(symbol);
    if (!fn) {
        symbol = "gml_Script_" + rule.symbol;
        fn     = sym::Find(symbol);
    }
    if (!fn) { Fail("unknown symbol '%s'", rule.symbol.c_str()); return false; }

    // Match on the target ADDRESS, not the name, so the same function reached
    // by either spelling is always the same hook.
    if (const int existing = FindByTarget(fn); existing >= 0) {
        ApplyRuleTo(g_slots[static_cast<std::size_t>(existing)], rule);
        Logf("rewrite: updated %s (arg %d x%g, repeat x%g, %s)",
             symbol.c_str(), rule.argIndex, rule.factor, rule.repeat,
             rule.enabled ? "ARMED" : "observing");
        return true;
    }

    const int slot = FreeSlot();
    if (slot < 0) { Fail("all %zu hook slots are in use", kSlots); return false; }

    Slot& s = g_slots[static_cast<std::size_t>(slot)];
    s.target = fn;
    s.symbol = symbol;
    s.hits.store(0);
    s.changed.store(0);
    ApplyRuleTo(s, rule);

    // Publish the slot before the handlers can run, so the first call already
    // sees a complete rule.
    s.live.store(true, std::memory_order_release);

    s.hookId = hk::AddNative(fn, hk::Kind::Script, &RuleBefore, &RuleAfter, &s);
    if (s.hookId < 0) {
        s.live.store(false, std::memory_order_release);
        s.target = nullptr;
        Fail("could not hook %s", symbol.c_str());
        return false;
    }

    Logf("rewrite: hooked %s in slot %d", symbol.c_str(), slot);
    return true;
}

void Remove(const std::string& symbol) {
    const int slot = FindSlot(symbol);
    if (slot < 0) return;

    Slot& s = g_slots[static_cast<std::size_t>(slot)];
    s.enabled.store(false);
    s.live.store(false, std::memory_order_release);

    hk::RemoveNative(s.hookId, &RuleBefore, &RuleAfter, &s);

    s.target = nullptr;
    s.hookId = -1;
    Logf("rewrite: removed %s", symbol.c_str());
    s.symbol.clear();
}

void RemoveAll() {
    for (std::size_t i = 0; i < kSlots; ++i)
        if (g_slots[i].target) Remove(g_slots[i].symbol);
}

const std::vector<Status>& Hooks() {
    static std::vector<Status> out;
    out.clear();

    EnsureLock();
    for (std::size_t i = 0; i < kSlots; ++i) {
        Slot& s = g_slots[i];
        if (!s.target) continue;

        Status st;
        st.rule.symbol     = s.symbol;
        st.rule.argIndex   = s.argIndex.load();
        st.rule.factor     = s.factor.load();
        st.rule.addend     = s.addend.load();
        st.rule.minValue   = s.minValue.load();
        st.rule.maxValue   = s.maxValue.load();
        st.rule.roundToInt = s.roundToInt.load();
        st.rule.repeat     = s.repeat.load();
        st.rule.enabled    = s.enabled.load();
        st.hits            = s.hits.load();
        st.changed         = s.changed.load();
        st.repeated        = s.repeated.load();
        st.suppressed      = s.suppressed.load();

        EnterCriticalSection(&g_reportLock);
        st.lastSeen = s.lastSeen;
        LeaveCriticalSection(&g_reportLock);

        out.push_back(std::move(st));
    }
    return out;
}

// ---------------------------------------------------------------------- tab

void DrawRewriteTab() {
    ImGui::TextWrapped(
        "Intercept a script, change one numeric argument, then let the real function run. "
        "This is the only thing here that changes gameplay from inside a detour, so it "
        "starts in observe-only mode: watch the arguments real calls pass, then arm the "
        "one you actually want.");
    ImGui::Spacing();

    static char symbol[128] = "scr_loot";
    ImGui::SetNextItemWidth(320.0f);
    ImGui::InputText("script", symbol, sizeof(symbol));
    ImGui::SameLine();
    if (ImGui::Button("Observe", ImVec2(120.0f, 0.0f))) {
        Rule r;
        r.symbol  = symbol;
        r.enabled = false;              // watch first, always
        if (!Install(r))
            ImGui::OpenPopup("##rewriteerr");
    }
    ImGui::SameLine();
    ImGui::TextDisabled("%zu of %zu slots used", Hooks().size(), Capacity());

    if (LastError()[0])
        ImGui::TextColored(ImVec4(0.95f, 0.45f, 0.45f, 1.0f), "%s", LastError());

    ImGui::Spacing();
    ImGui::SeparatorText("Hooks");

    const auto& hooks = Hooks();
    if (hooks.empty()) {
        ImGui::TextDisabled("Nothing hooked. Type a script name and press Observe.");
        return;
    }

    for (const Status& st : hooks) {
        ImGui::PushID(st.rule.symbol.c_str());
        ImGui::SeparatorText(st.rule.symbol.c_str());
        if (st.rule.enabled && st.rule.repeat != 1.0)
            ImGui::TextColored(ImVec4(0.55f, 0.90f, 0.55f, 1.0f),
                               "ARMED - running the original x%.2f", st.rule.repeat);

        ImGui::Text("hits %u    args rewritten %u    repeated %u    suppressed %u",
                    st.hits, st.changed, st.repeated, st.suppressed);
        ImGui::TextWrapped("last call: %s",
                           st.lastSeen.empty() ? "(not called yet)" : st.lastSeen.c_str());

        Rule r = st.rule;
        bool dirty = false;

        // The multiplier most rules actually want. scr_loot has no count
        // argument, so this is what "more loot" means for it.
        ImGui::SetNextItemWidth(160.0f);
        if (ImGui::InputDouble("run x times", &r.repeat, 0.25, 1.0, "%.2f")) dirty = true;
        ImGui::SameLine();
        if (r.repeat < 1.0)      ImGui::TextDisabled("sometimes skipped");
        else if (r.repeat > 1.0) ImGui::TextDisabled("called more often");
        else                     ImGui::TextDisabled("unchanged");

        ImGui::SetNextItemWidth(120.0f);
        if (ImGui::InputInt("arg index", &r.argIndex)) dirty = true;
        ImGui::SameLine();
        ImGui::TextDisabled("-1 = observe only");

        ImGui::SetNextItemWidth(140.0f);
        if (ImGui::InputDouble("x factor", &r.factor, 0.1, 1.0, "%.2f")) dirty = true;
        ImGui::SameLine();
        ImGui::SetNextItemWidth(140.0f);
        if (ImGui::InputDouble("+ add", &r.addend, 1.0, 10.0, "%.2f")) dirty = true;

        ImGui::SetNextItemWidth(120.0f);
        if (ImGui::InputDouble("min", &r.minValue, 1.0, 10.0, "%.0f")) dirty = true;
        ImGui::SameLine();
        ImGui::SetNextItemWidth(120.0f);
        if (ImGui::InputDouble("max", &r.maxValue, 1.0, 10.0, "%.0f")) dirty = true;

        if (ImGui::Checkbox("whole numbers", &r.roundToInt)) dirty = true;
        ImGui::SameLine();
        if (ImGui::Checkbox("ARMED", &r.enabled)) dirty = true;
        ImGui::SameLine();
        if (r.enabled)
            ImGui::TextColored(ImVec4(0.95f, 0.65f, 0.35f, 1.0f), "rewriting live calls");
        else
            ImGui::TextDisabled("observing only");

        if (ImGui::Button("Remove")) { Remove(st.rule.symbol); ImGui::PopID(); break; }

        if (dirty) Install(r);
        ImGui::PopID();
    }
}

} // namespace mod::rewrite
