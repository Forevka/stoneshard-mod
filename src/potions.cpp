// Potion construction: write the effect list, let the game derive the rest.
//
// The design argument is in potions.h. The short version: a potion is its
// `atrdlist` - a plain ds_list of effect tags - plus everything the game
// derives from that list. This writes the list directly and calls
// scr_potion_set_param to do the deriving, from inside the bottle's own alarm,
// which is the only context where the potion scripts run.

#include "potions.h"

#include "assets.h"
#include "builtins.h"
#include "console.h"
#include "gml.h"
#include "log.h"
#include "savebackup.h"
#include "symbols.h"

#include <windows.h>
#include <MinHook.h>
#include <cstdarg>
#include <cstdio>
#include <deque>

#include "imgui.h"

namespace mod::potions {
namespace {

// The one potion object. Its asset index is looked up in the catalogue at every
// use, never compiled in.
constexpr const char* kBottleObject = "o_inv_bottle";

std::string g_error;

void Fail(const char* fmt, ...) {
    char buf[256];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    g_error = buf;
    Logf("[!] potions: %s", buf);
}

std::string Line(const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    return buf;
}

// ------------------------------------------------------------------ recorder

using EventFn = void(STDMETHODCALLTYPE*)(void* self, void* other);

EventFn g_alarmOrig = nullptr;
void*   g_bottle    = nullptr;

// The armed request, and the outcome of the last one.
//
// A potion is BUILT, not searched for. Four sessions of evidence got here:
//
//   * scr_roll_potion from the Present hook throws 0xE06D7363 - it only
//     survives inside its own event frame, so the work happens in the detour
//     below.
//   * called there on an already-rolled bottle it changes nothing, and neither
//     blanking `Name` nor emptying `atrdlist` lifts that. A potion cannot be
//     re-rolled.
//   * taking fresh bottles until one rolls the wanted effect DOES work, but it
//     can only ever produce combinations the game itself rolls, and disposing
//     of the misses needs an instance_destroy this mod cannot aim safely: with
//     argc = 0 the instance travels in `self`, and builtins.h is explicit that
//     the addressed instance belongs in args[0]. Destroying "whatever the
//     runtime thinks is current" from the Present hook ate the kept potion.
//   * scr_potion_set_param - the function that turns an effect list into a
//     finished potion, writing Name and Colour - takes argc = 0 and works on
//     `self`, confirmed by capturing a real call. There are no argument
//     semantics to guess.
//
// So: take one bottle, let the game roll it normally, then write the effects
// you asked for into `atrdlist` and have the game re-derive the rest. One
// bottle, no churn, and any combination of effects rather than only the ones
// the roll tables happen to produce.
bool                     g_armed = false;
std::vector<std::string> g_wantTags;

struct Outcome {
    bool        done    = false;   // a run finished and the UI has not shown it
    bool        ok      = false;
    std::string message;
};
Outcome g_outcome;

// Forward declarations - these live below but the detour needs them.
bool MapJson(double id, std::string* out);
bool BottleMap(void* bottle, double* mapId);
bool SetEffects(double mapId, const std::vector<std::string>& tags);

void Finish(bool ok, const std::string& message) {
    g_armed   = false;
    g_outcome = {true, ok, message};
    Logf("potions: %s", message.c_str());
}

// Rewrites the freshly rolled bottle into the potion that was asked for.
//
// Runs inside the alarm, which is the only place the potion scripts work.
// gml::CallAs contains the runtime's exceptions itself, so a rejected call is
// reported rather than fatal.
void BuildInContext(void* self, void* other) {
    double mapId = 0.0;
    if (!BottleMap(self, &mapId)) { Finish(false, "the bottle has no data map"); return; }

    if (!SetEffects(mapId, g_wantTags)) {
        Finish(false, "could not write the effect list");
        return;
    }

    void* fn = sym::Find("gml_Script_scr_potion_set_param");
    if (!fn) { Finish(false, "scr_potion_set_param not found"); return; }

    // argc = 0, self = the bottle: exactly how scr_roll_potion calls it, read
    // off a captured call rather than assumed.
    gml::RValue result{};
    if (!gml::CallAs(fn, &result, nullptr, 0, self, other)) {
        Finish(false, "scr_potion_set_param rejected the call - the effect list is "
                      "written but the name and colour are stale");
        return;
    }

    std::string json;
    if (BottleMap(self, &mapId) && MapJson(mapId, &json)) {
        std::string name;
        const std::string key = "\"Name\": \"";
        const std::size_t at = json.find(key);
        if (at != std::string::npos) {
            const std::size_t from = at + key.size();
            const std::size_t to   = json.find('"', from);
            if (to != std::string::npos) name = json.substr(from, to - from);
        }
        Finish(true, name.empty() ? "built it" : ("built " + name));
        return;
    }
    Finish(true, "built it, but the result could not be read back");
}

void STDMETHODCALLTYPE BottleAlarmDetour(void* self, void* other) {
    // The game's own roll, untouched and always first. It is what makes the
    // bottle a valid potion in the first place; the build only rewrites it.
    g_alarmOrig(self, other);

    if (!self) return;
    g_bottle = self;
    if (!g_armed) return;

    BuildInContext(self, other);
}

// Builtins want some live instance as `self` even when they ignore it.
void* Context() {
    void* p = gml::PlayerInstance();
    return p ? p : gml::CurrentSelf();
}

// json_encode answers a null string for a stale or non-map id rather than
// faulting, which is what makes "try it and see" safe on a recycled handle.
bool MapJson(double id, std::string* out) {
    gml::RValue arg{};
    gml::SetReal(arg, id);

    gml::RValue r{};
    if (!builtins::Call("json_encode", &r, &arg, 1, Context()) || r.kind != gml::kString)
        return false;

    *out = gml::ToString(r);
    return !out->empty() && *out != "<null string>";
}

// Strings handed to the runtime are NOT copied - gml::SetString points a
// RefString at our buffer and flags it external, and the game keeps that
// pointer. Every tag written into a potion therefore has to outlive the call
// permanently. A deque never invalidates what it already holds.
const char* Intern(const std::string& s) {
    static std::deque<std::string> pool;
    pool.push_back(s);
    return pool.back().c_str();
}

// Replaces the potion's effect list with `tags`.
//
// `atrdlist` is a plain ds_list of tag strings - [ "good_pt_rage" ] - so it is
// edited IN PLACE through ds_list_clear and ds_list_add. The raw id from
// ds_map_find_value is never written back with ds_map_replace, which is what
// items.cpp warns destroys the nesting.
bool SetEffects(double mapId, const std::vector<std::string>& tags) {
    gml::RValue find[2]{};
    gml::SetReal(find[0], mapId);
    if (!gml::SetString(find[1], "atrdlist")) return false;

    gml::RValue listVal{};
    if (!builtins::Call("ds_map_find_value", &listVal, find, 2, Context()) ||
        listVal.kind != gml::kReal)
        return false;

    gml::RValue clearArg{};
    gml::SetReal(clearArg, listVal.real);
    gml::RValue r{};
    if (!builtins::Call("ds_list_clear", &r, &clearArg, 1, Context())) return false;

    for (const std::string& tag : tags) {
        gml::RValue add[2]{};
        gml::SetReal(add[0], listVal.real);
        if (!gml::SetString(add[1], Intern(tag))) return false;
        if (!builtins::Call("ds_list_add", &r, add, 2, Context())) return false;
    }
    return true;
}

// The recorded bottle's `data` map id.
bool BottleMap(void* bottle, double* mapId) {
    gml::RValue d{};
    if (!builtins::GetInstanceVar(bottle, "data", &d) || d.kind != gml::kReal) return false;
    *mapId = d.real;
    return true;
}

} // namespace

const char* LastError() { return g_error.c_str(); }

bool InstallRecorder() {
    if (g_alarmOrig) return true;

    // The whole event body is one call to scr_roll_potion, so this fires once
    // per potion the game creates - not on every step.
    void* fn = sym::Find("gml_Object_o_inv_bottle_Alarm_0");
    if (!fn) {
        Fail("o_inv_bottle Alarm 0 not found");
        return false;
    }

    if (MH_CreateHook(fn, reinterpret_cast<void*>(&BottleAlarmDetour),
                      reinterpret_cast<void**>(&g_alarmOrig)) != MH_OK ||
        MH_EnableHook(fn) != MH_OK) {
        Fail("bottle recorder hook failed");
        g_alarmOrig = nullptr;
        return false;
    }
    Logf("potions: watching o_inv_bottle Alarm 0");
    return true;
}

bool RecorderReady() { return g_alarmOrig != nullptr; }

// --------------------------------------------------------------------- giving

// Internal: the only way a bottle is handed out is as part of BuildPotion.
namespace {

bool GiveBottle(int count) {
    backup::EnsureBackupOnce();
    g_error.clear();

    if (!assets::Loaded()) { Fail("the item catalogue did not load"); return false; }

    // The object's own asset index, from the catalogue rather than a constant.
    int index = -1;
    for (const assets::Item& it : assets::Items())
        if (it.source == assets::Source::Object && it.id == kBottleObject) {
            index = it.index;
            break;
        }
    if (index < 0) { Fail("%s is not in the object table", kBottleObject); return false; }

    for (int i = 0; i < count; ++i)
        console::Execute(Line("scr_dialogue_reward_add_item %d", index));
    return true;
}

} // namespace

// ------------------------------------------------------------------- building

bool BuildPotion(const std::vector<std::string>& tags) {
    backup::EnsureBackupOnce();
    g_error.clear();

    if (tags.empty())  { Fail("no effects chosen"); return false; }
    if (!g_alarmOrig)  { Fail("the bottle recorder is not installed"); return false; }

    // Arm first, take the bottle second: the alarm fires on the next step, and
    // the detour does the building then - inside the event frame, the only
    // place the potion scripts run.
    g_wantTags = tags;
    g_armed    = true;
    g_outcome  = {};

    if (!GiveBottle(1)) { g_armed = false; return false; }
    return true;
}

bool Pending() { return g_armed; }

bool TakeOutcome(bool* ok, std::string* message) {
    if (!g_outcome.done) return false;
    if (ok)      *ok      = g_outcome.ok;
    if (message) *message = g_outcome.message;
    g_outcome.done = false;
    return true;
}

// ------------------------------------------------------------------------ UI

void DrawPotionsTab() {
    ImGui::TextWrapped(
        "Potions are the one thing the Items tab cannot list. There is no \"Potion of "
        "Healing\" object in the game - there is one potion object, o_inv_bottle, and every "
        "potion is an instance of it carrying a rolled set of effects. The name you read in "
        "the inventory is assembled from those effects, so searching the catalogue for a "
        "potion by name will never find one.");
    ImGui::Spacing();
    ImGui::TextWrapped(
        "So building one means taking a bottle, letting the game roll it, then replacing "
        "its effect list with the one you picked and having the game re-derive the name, "
        "colour and the rest. Any combination works - including ones no roll table "
        "produces.");

    ImGui::Separator();

    if (!RecorderReady()) {
        ImGui::TextColored(ImVec4(0.95f, 0.40f, 0.40f, 1.0f),
                           "Bottle recorder not installed: %s", LastError());
        return;
    }

    const auto& effects = assets::PotionEffects();
    if (effects.empty()) {
        ImGui::TextColored(ImVec4(0.95f, 0.40f, 0.40f, 1.0f),
                           "No effect table found in the game image.");
        return;
    }

    // Which effects the potion will carry. A set rather than one choice: the
    // whole point of building instead of rolling is combinations the roll
    // tables never produce.
    static std::vector<bool> picked(effects.size(), false);
    static bool              lastOk = false;
    static std::string       lastMessage;

    if (picked.size() != effects.size()) picked.assign(effects.size(), false);

    std::vector<std::string> chosen;
    for (std::size_t i = 0; i < effects.size(); ++i)
        if (picked[i]) chosen.push_back(effects[i].tag);

    ImGui::Text("%zu effect(s) selected", chosen.size());
    ImGui::SameLine();
    if (ImGui::SmallButton("clear")) picked.assign(effects.size(), false);

    ImGui::BeginChild("##effects", ImVec2(0.0f, 220.0f), true);
    for (std::size_t i = 0; i < effects.size(); ++i) {
        bool on = picked[i];
        ImGui::PushID(static_cast<int>(i));
        ImGui::PushStyleColor(ImGuiCol_Text,
                              effects[i].positive ? ImVec4(0.55f, 0.90f, 0.55f, 1.0f)
                                                  : ImVec4(0.95f, 0.60f, 0.55f, 1.0f));
        if (ImGui::Checkbox(effects[i].display.c_str(), &on)) picked[i] = on;
        ImGui::PopStyleColor();
        ImGui::SameLine(220.0f);
        ImGui::TextDisabled("%s", effects[i].tag.c_str());
        ImGui::PopID();
    }
    ImGui::EndChild();

    ImGui::BeginDisabled(Pending() || chosen.empty());
    if (ImGui::Button("Build this potion", ImVec2(220.0f, 0.0f))) {
        console::Print(console::Line::Echo, "> build a potion with %zu effect(s)",
                       chosen.size());
        for (const std::string& t : chosen)
            console::Print(console::Line::Echo, "    %s", t.c_str());
        if (!BuildPotion(chosen))
            console::Print(console::Line::Error, "failed: %s", LastError());
    }
    ImGui::EndDisabled();
    ImGui::SameLine();
    ImGui::TextDisabled("takes one bottle and rewrites it");

    if (Pending())
        ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f), "building...");

    // Collected once, so the result is reported on the frame it arrives rather
    // than every frame after it.
    {
        std::string message;
        if (TakeOutcome(&lastOk, &message)) {
            lastMessage = message;
            console::Print(lastOk ? console::Line::Result : console::Line::Error,
                           "%s", message.c_str());
        }
    }

    if (!lastMessage.empty())
        ImGui::TextColored(lastOk ? ImVec4(0.45f, 0.90f, 0.45f, 1.0f)
                                  : ImVec4(0.95f, 0.40f, 0.40f, 1.0f),
                           "%s", lastMessage.c_str());

}

} // namespace mod::potions
