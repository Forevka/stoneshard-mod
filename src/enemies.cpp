// The enemy roster, and the buttons that clear it.
//
// Reaching an enemy at all is the whole problem here. The reflection API in
// builtins.cpp reads and writes variables on any instance it is handed, but the
// game's own scripts need something it cannot produce: the CInstance* the
// runtime passes as `self`. gml.cpp gets one for the player by hooking
// o_player's Step event, so the same trick works for enemies - o_enemy's Step
// event runs once per enemy per frame, and each run hands over that enemy's
// pointer.
//
// That gives a roster for free. The set is republished whenever an instance
// repeats, which is the moment a new step cycle has begun, so it is exactly the
// enemies that were alive on the last full pass rather than a growing pile.

#include "enemies.h"

#include "builtins.h"
#include "gml.h"
#include "items.h"
#include "log.h"
#include "savebackup.h"
#include "symbols.h"

#include <windows.h>
#include <MinHook.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <string>
#include <vector>

#include "imgui.h"

namespace mod::enemies {
namespace {

constexpr const char* kObject    = "o_enemy";
constexpr const char* kStepEvent = "gml_Object_o_enemy_Step_0";

// Enough for any room the game builds; the cap only exists so a runaway spawn
// cannot grow our scratch list without bound inside a per-frame event.
constexpr std::size_t kMaxTracked = 512;

using EventFn = void (*)(void* self, void* other);

std::string g_error;

void Fail(const char* fmt, ...) {
    char buf[256];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    g_error = buf;
    Logf("[!] enemies: %s", buf);
}

// Strings handed to the runtime are not copied - gml::SetString points a
// RefString at our buffer and flags it external - so anything passed as an
// argument has to outlive the call permanently. A deque never invalidates what
// it already holds.
const char* Intern(const std::string& s) {
    static std::deque<std::string> pool;
    pool.push_back(s);
    return pool.back().c_str();
}

// Builtins are handed a `self` even when they ignore it, and a null one is not
// worth the risk.
void* Context() {
    void* p = gml::PlayerInstance();
    return p ? p : gml::CurrentSelf();
}

// ---------------------------------------------------------------- tracker

EventFn            g_stepOrig = nullptr;
std::vector<void*> g_pending;          // this step cycle, still filling
std::vector<void*> g_live;             // the last complete cycle
unsigned           g_quietFrames = 0;
unsigned long long g_stepHits    = 0;

bool Contains(const std::vector<void*>& v, void* p) {
    for (void* q : v)
        if (q == p) return true;
    return false;
}

void Drop(std::vector<void*>& v, void* p) {
    v.erase(std::remove(v.begin(), v.end(), p), v.end());
}

// Destroying an instance leaves our copy of its pointer behind until the next
// step cycle republishes, and reading variables off freed memory is exactly the
// kind of crash a cheat tool has no excuse for. So the pointer goes the moment
// we are the ones who removed it.
void Forget(void* inst) {
    Drop(g_live, inst);
    Drop(g_pending, inst);
}

// Runs inside the game's Step event, once per enemy per frame. Seeing an
// instance we already have means the pass wrapped around, which is the only
// reliable "the room has been fully walked" signal available from in here -
// counting render frames would be a guess, since a frame is not a step.
void STDMETHODCALLTYPE StepDetour(void* self, void* other) {
    if (self) {
        ++g_stepHits;
        if (Contains(g_pending, self)) {
            g_live.swap(g_pending);
            g_pending.clear();
        }
        if (g_pending.size() < kMaxTracked) g_pending.push_back(self);
        g_quietFrames = 0;
    }
    g_stepOrig(self, other);
}

// ---------------------------------------------------------------- reading

bool ReadNumber(const builtins::Handle& h, const char* name, double* out) {
    gml::RValue v{};
    if (!builtins::GetVar(h, name, &v)) return false;
    if (v.kind == gml::kReal)  { *out = v.real; return true; }
    if (v.kind == gml::kInt32) { *out = static_cast<double>(v.i32); return true; }
    if (v.kind == gml::kInt64) { *out = static_cast<double>(v.i64); return true; }
    return false;
}

std::string ReadText(const builtins::Handle& h, const char* name) {
    gml::RValue v{};
    if (!builtins::GetVar(h, name, &v)) return std::string();
    if (v.kind != gml::kString) return std::string();
    return gml::ToString(v);
}

// Field names taken from a live o_player dump (run/o_player-variables.txt):
// o_player and o_enemy are both units, so they share the unit-level fields.
// Every one is probed rather than assumed - a missing field leaves the column
// blank instead of failing the row, which is what makes this survive a patch
// that renames one of them.
void Describe(Enemy* e, double px, double py, bool havePlayer) {
    e->name = ReadText(e->handle, "name");
    e->race = ReadText(e->handle, "race");
    e->type = ReadText(e->handle, "type");

    if (e->name.empty()) e->name = e->race;
    if (e->name.empty()) e->name = e->type;
    if (e->name.empty()) e->name = "(unnamed)";

    e->haveHp = ReadNumber(e->handle, "HP", &e->hp);
    if (!ReadNumber(e->handle, "max_hp", &e->maxHp))
        ReadNumber(e->handle, "bmax_hp", &e->maxHp);
    ReadNumber(e->handle, "LVL", &e->level);

    e->havePos = ReadNumber(e->handle, "x", &e->x) && ReadNumber(e->handle, "y", &e->y);
    if (e->havePos && havePlayer) {
        const double dx = e->x - px, dy = e->y - py;
        e->dist = std::sqrt(dx * dx + dy * dy);
    }
}

std::vector<Enemy> g_roster;
int                g_reported = -1;

// The fallback roster: instance_number + instance_find, the same walk items.cpp
// does. It answers with a reference rather than a pointer, which is enough to
// read and write fields but not to run a script as the instance.
bool ListByEnumeration(std::vector<Enemy>* out) {
    void* ctx = Context();
    if (!ctx) { Fail("no instance context yet - load a save"); return false; }

    gml::RValue nameArg{};
    if (!gml::SetString(nameArg, Intern(kObject))) {
        Fail("could not build the object name string");
        return false;
    }

    gml::RValue idx{};
    if (!builtins::Call("asset_get_index", &idx, &nameArg, 1, ctx) ||
        idx.kind != gml::kReal || idx.real < 0.0) {
        Fail("unknown object '%s' (asset_get_index kind=%d)", kObject, idx.kind);
        return false;
    }

    for (int i = 0; i < g_reported; ++i) {
        gml::RValue args[2]{};
        gml::SetReal(args[0], idx.real);
        gml::SetReal(args[1], static_cast<double>(i));

        gml::RValue inst{};
        if (!builtins::Call("instance_find", &inst, args, 2, ctx)) continue;
        if (inst.kind != gml::kRef && inst.kind != gml::kObject) continue;

        Enemy e;
        e.handle.id      = inst;
        e.handle.self    = ctx;
        e.handle.haveRef = true;
        e.tracked        = false;
        out->push_back(e);
    }
    return !out->empty();
}

} // namespace

const char*               LastError() { return g_error.c_str(); }
const std::vector<Enemy>& Roster()    { return g_roster; }
int                       Reported()  { return g_reported; }
bool                      Tracking()  { return g_stepOrig != nullptr; }
unsigned long long        StepsSeen() { return g_stepHits; }

// ---------------------------------------------------------------- lifecycle

bool InstallTracker() {
    if (g_stepOrig) return true;

    void* fn = sym::Find(kStepEvent);
    if (!fn) { Logf("[!] enemy tracker: %s not found", kStepEvent); return false; }

    if (MH_CreateHook(fn, reinterpret_cast<void*>(&StepDetour),
                      reinterpret_cast<void**>(&g_stepOrig)) != MH_OK ||
        MH_EnableHook(fn) != MH_OK) {
        Logf("[!] enemy tracker: hook failed");
        g_stepOrig = nullptr;
        return false;
    }
    Logf("enemy tracker: watching %s", kStepEvent);
    return true;
}

void Tick() {
    if (!g_stepOrig) return;

    // No step events for a while means no enemies are running - a cleared room,
    // a town, or a menu. Without this the last room's occupants stay listed and
    // the Kill buttons point at pointers the game has already freed.
    if (++g_quietFrames > 90) {
        if (!g_live.empty() || !g_pending.empty() || !g_roster.empty()) {
            g_live.clear();
            g_pending.clear();
            g_roster.clear();
        }
        g_quietFrames = 91;                    // stop the counter running away
    }
}

// ---------------------------------------------------------------- roster

bool Refresh() {
    g_roster.clear();
    g_error.clear();

    void* ctx = Context();
    if (!ctx) { Fail("no instance context yet - load a save"); return false; }

    // Ask the game how many there are first: it is one call, it is the honest
    // answer, and it is what the tracker's count gets compared against.
    g_reported = items::InstanceCount(kObject);

    // The tracked set is only as fresh as the last step cycle, and an enemy that
    // has just died stops stepping - so its pointer can outlive the instance by
    // a frame or two. instance_number is the game's own answer to "what is
    // still here", so it decides whether the pointers are worth using:
    //
    //   reported 0            - the room is clear, whatever we are holding is stale
    //   tracked  > reported   - at least one of ours is a corpse, do not touch any
    //   tracked <= reported   - ours are a subset of what is alive, safe to use
    //
    // Falling back to instance_find costs the ability to run scripts as the
    // enemy, but references are validated by the runtime and cannot dangle.
    if (g_reported == 0) {
        g_live.clear();
        g_pending.clear();
    }

    const bool trackedUsable = !g_live.empty() && g_reported > 0 &&
                               static_cast<int>(g_live.size()) <= g_reported;

    std::vector<Enemy> built;
    if (trackedUsable) {
        built.reserve(g_live.size());
        for (void* inst : g_live) {
            Enemy e;
            e.inst    = inst;
            e.handle  = builtins::SelfHandle(inst);
            e.tracked = true;
            built.push_back(e);
        }
    } else if (g_reported > 0) {
        ListByEnumeration(&built);
    }

    if (built.empty()) {
        if (g_reported == 0) g_error.clear();      // an empty room is not a failure
        return g_reported == 0;
    }

    double px = 0.0, py = 0.0;
    const bool havePlayer = gml::PlayerPosition(px, py);
    for (Enemy& e : built) Describe(&e, px, py, havePlayer);

    // Nearest first: the one you care about is the one about to hit you. Rows
    // with no known distance sink to the bottom rather than sorting as zero.
    std::stable_sort(built.begin(), built.end(), [](const Enemy& a, const Enemy& b) {
        if (a.dist < 0.0) return false;
        if (b.dist < 0.0) return true;
        return a.dist < b.dist;
    });

    g_roster.swap(built);
    return true;
}

// ---------------------------------------------------------------- actions
//
// Only one, deliberately. Writing an enemy's HP - as a kill, or partway to
// wound it - went through scr_atr_set run as that enemy, and it did not take:
// nothing died and nothing was hurt. The direct-variable fallback moved the
// number without the game reacting to it, which is a worse outcome than no
// button, because the row then reads a lie. Both are gone until the damage path
// is understood the way the buff path now is.

bool Remove(const Enemy& e) {
    backup::EnsureBackupOnce();

    gml::RValue r{};
    bool ok = false;

    // With a real instance pointer, instance_destroy takes no argument at all -
    // it destroys `self`, which is exactly the enemy meant. The reference form
    // is only needed for a listed row, where there is no pointer to be `self`
    // with.
    if (e.tracked) {
        ok = builtins::Call("instance_destroy", &r, nullptr, 0, e.inst);
    } else {
        gml::RValue arg = e.handle.id;
        ok = builtins::Call("instance_destroy", &r, &arg, 1, Context());
    }

    if (!ok) { Fail("instance_destroy failed on %s", e.name.c_str()); return false; }
    if (e.inst) Forget(e.inst);
    Logf("enemies: removed %s", e.name.c_str());
    return true;
}

std::vector<std::string> Probe(const Enemy& e, int limit) {
    std::vector<std::string> lines;

    char head[192];
    std::snprintf(head, sizeof(head), "=== %s (%s, inst=%p) ===",
                  e.name.c_str(), e.tracked ? "tracked" : "listed", e.inst);
    lines.push_back(head);

    int total = -1;
    for (const std::string& l : items::DumpVars(e.handle, limit, &total))
        lines.push_back(l);

    char tail[96];
    std::snprintf(tail, sizeof(tail), "    %d instance variables in total", total);
    lines.push_back(tail);
    return lines;
}

// ---------------------------------------------------------------------- tab

namespace {

int   g_selected     = -1;
bool  g_autoRefresh  = true;
int   g_refreshEvery = 15;                  // frames
int   g_sinceRefresh = 0;
char  g_filter[64]   = "";

std::string Lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

bool Matches(const Enemy& e) {
    if (!g_filter[0]) return true;
    const std::string needle = Lower(g_filter);
    return Lower(e.name).find(needle) != std::string::npos ||
           Lower(e.race).find(needle) != std::string::npos ||
           Lower(e.type).find(needle) != std::string::npos;
}

} // namespace

void DrawEnemiesTab() {
    ImGui::TextWrapped(
        "Everything hostile standing in the room with you. Enemies are all instances of one "
        "object, so this is a walk over live instances rather than a list of monster names - "
        "which is why it needs no table to maintain and cannot go out of date.");

    if (!builtins::Ready()) {
        ImGui::TextColored(ImVec4(0.95f, 0.40f, 0.40f, 1.0f),
                           "Reflection unavailable: %s", builtins::Status());
        return;
    }

    ImGui::Spacing();
    if (ImGui::Button("Refresh", ImVec2(110.0f, 0.0f))) Refresh();
    ImGui::SameLine();
    ImGui::Checkbox("auto", &g_autoRefresh);
    ImGui::SameLine();
    ImGui::SetNextItemWidth(150.0f);
    ImGui::SliderInt("##rate", &g_refreshEvery, 5, 120, "every %d frames");
    ImGui::SameLine();
    ImGui::SetNextItemWidth(-1.0f);
    ImGui::InputTextWithHint("##filter", "filter by name...", g_filter, sizeof(g_filter));

    // Reading a dozen fields per enemy is cheap but not free, and the roster
    // only changes as fast as the game's turns do.
    if (g_autoRefresh && ++g_sinceRefresh >= g_refreshEvery) {
        g_sinceRefresh = 0;
        Refresh();
    }

    const std::vector<Enemy>& rows = Roster();

    // The two counts sit side by side on purpose: the tracker only sees enemies
    // that take a step, so a gap between them is real information about what is
    // standing there rather than something to smooth over.
    const char* source = (!rows.empty() && rows.front().tracked) ? "o_enemy Step hook"
                                                                 : "instance_find";
    ImGui::Text("listed %zu", rows.size());
    ImGui::SameLine();
    if (Reported() < 0) ImGui::TextDisabled("| the game could not be asked | source: %s", source);
    else                ImGui::TextDisabled("| the game reports %d | source: %s", Reported(), source);

    // A counter rather than a claim: it only moves when the game really runs
    // o_enemy's Step event through our detour.
    ImGui::TextDisabled("enemy steps seen: %llu", StepsSeen());

    if (rows.empty()) {
        ImGui::Spacing();
        ImGui::TextDisabled(Reported() == 0 ? "Nothing hostile here."
                                            : "Nothing listed yet - hit Refresh.");
    }

    ImGui::Spacing();
    ImGui::BeginChild("##roster", ImVec2(0.0f, 230.0f), true);
    for (int i = 0; i < static_cast<int>(rows.size()); ++i) {
        const Enemy& e = rows[static_cast<std::size_t>(i)];
        if (!Matches(e)) continue;

        ImGui::PushID(i);

        char hp[64] = "hp ?";
        if (e.haveHp) {
            if (e.maxHp > 0.0) std::snprintf(hp, sizeof(hp), "%.0f/%.0f hp", e.hp, e.maxHp);
            else               std::snprintf(hp, sizeof(hp), "%.0f hp", e.hp);
        }
        char lvl[24] = "";
        if (e.level >= 0.0) std::snprintf(lvl, sizeof(lvl), "  lvl %.0f", e.level);
        char dist[32] = "";
        if (e.dist >= 0.0) std::snprintf(dist, sizeof(dist), "  %.0f px", e.dist);

        char label[288];
        std::snprintf(label, sizeof(label), "%-22s %s%s%s%s",
                      e.name.c_str(), hp, lvl, dist, e.tracked ? "" : "  [listed]");

        // AllowOverlap matters: the row's buttons are drawn on top of this
        // Selectable, which spans the full width. Without it the Selectable wins
        // the hit test and swallows every click meant for Remove or Vars - the
        // row just highlights and nothing happens.
        if (ImGui::Selectable(label, g_selected == i, ImGuiSelectableFlags_AllowOverlap))
            g_selected = i;

        ImGui::SameLine(ImGui::GetContentRegionAvail().x - 118.0f);
        if (ImGui::SmallButton("Remove")) { Remove(e); Refresh(); ImGui::PopID(); break; }
        ImGui::SameLine();
        if (ImGui::SmallButton("Vars"))
            for (const std::string& l : Probe(e, 300)) Logf("%s", l.c_str());

        ImGui::PopID();
    }
    ImGui::EndChild();

    if (g_selected >= 0 && g_selected < static_cast<int>(Roster().size())) {
        const Enemy& e = Roster()[static_cast<std::size_t>(g_selected)];
        ImGui::SeparatorText(e.name.c_str());
        if (e.havePos)
            ImGui::TextDisabled("race %s   type %s   at (%.0f, %.0f)   %s",
                                e.race.empty() ? "?" : e.race.c_str(),
                                e.type.empty() ? "?" : e.type.c_str(),
                                e.x, e.y, e.tracked ? "tracked" : "listed");
        else
            ImGui::TextDisabled("race %s   type %s   position unreadable   %s",
                                e.race.empty() ? "?" : e.race.c_str(),
                                e.type.empty() ? "?" : e.type.c_str(),
                                e.tracked ? "tracked" : "listed");

    }

    ImGui::Spacing();
    ImGui::TextWrapped(
        "Remove destroys the instance: the drop still happens, because that lives in the "
        "Destroy event, but nothing on the damage path does. Vars dumps every instance "
        "variable to the log, which is how the field names above were settled and how they "
        "get re-checked after a patch.");
    ImGui::TextWrapped(
        "Those are the only two actions on purpose. Writing an enemy's HP - to kill it, or "
        "partway to wound it - went through the game's own attribute setter run as that "
        "enemy, and it did not take: nothing died and nothing was hurt. Writing the variable "
        "directly moved the number without the game reacting, which is worse than no button, "
        "because then the row reads a lie. The kill-everything variants could not be aimed "
        "either, so one misclick emptied the room.");

    if (!Tracking())
        ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                           "Step hook not installed - rows are listed, not tracked, so they "
                           "cannot be used as a script's own instance.");

    if (LastError()[0])
        ImGui::TextColored(ImVec4(0.95f, 0.45f, 0.45f, 1.0f), "%s", LastError());
}

} // namespace mod::enemies
