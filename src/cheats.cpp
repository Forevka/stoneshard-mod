// Cheat panels.
//
// Every button builds a plain invocation line and hands it to console::Execute,
// so the exact call is visible in the console, recorded in the log, and covered
// by the save backup. The line is also shown under each control, which matters
// because these signatures are inferred from the compiled code rather than
// documented - if one is wrong you can see why and fix it in the console.

#include "cheats.h"
#include "assets.h"
#include "console.h"
#include "gml.h"
#include "symbols.h"

#include <cctype>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "imgui.h"

namespace mod::cheats {
namespace {

std::string Fmt(const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    return buf;
}

// One row: shows the call it will make, greys out if the symbol is missing.
void CallRow(const char* label, const std::string& command, const char* symbol) {
    const bool available = sym::Find(symbol) || sym::Find(std::string("gml_Script_") + symbol);

    ImGui::BeginDisabled(!available);
    if (ImGui::Button(label)) console::Execute(command);
    ImGui::EndDisabled();

    ImGui::SameLine();
    if (available) ImGui::TextDisabled("%s", command.c_str());
    else           ImGui::TextDisabled("%s  (symbol not found)", symbol);
}

void StatsPanel() {
    static float hp     = 100.0f;
    static int   gold   = 1000;
    static char  atr[64] = "STR";
    static int   atrVal = 20;

    ImGui::SeparatorText("Vitals");
    ImGui::SetNextItemWidth(160.0f);
    ImGui::InputFloat("amount##hp", &hp);
    CallRow("Restore HP", Fmt("scr_restore_hp %g", hp), "scr_restore_hp");

    ImGui::SeparatorText("Attributes");
    ImGui::SetNextItemWidth(160.0f);
    ImGui::InputText("attribute", atr, sizeof(atr));
    ImGui::SameLine();
    ImGui::SetNextItemWidth(120.0f);
    ImGui::InputInt("value", &atrVal);
    CallRow("Set attribute", Fmt("scr_atr_set \"%s\" %d", atr, atrVal), "scr_atr_set");
    CallRow("Set attribute (simple)", Fmt("scr_atr_set_simple \"%s\" %d", atr, atrVal),
            "scr_atr_set_simple");

    ImGui::SeparatorText("Money");
    ImGui::SetNextItemWidth(160.0f);
    ImGui::InputInt("gold", &gold);
    CallRow("Add gold", Fmt("scr_gold_add %d", gold), "scr_gold_add");
}

// Capture tooling, shown for every item because it is how the remaining
// unknowns (gear construction) get resolved.
void ItemsPanel() {
    static char              filter[96] = "";
    static int               count      = 1;
    static int               category   = 0;    // 0 == "All"
    static const assets::Item* selected = nullptr;

    if (!assets::Loaded()) {
        ImGui::TextColored(ImVec4(0.95f, 0.40f, 0.40f, 1.0f),
                           "Item list unavailable: %s", assets::Status());
        return;
    }

    const auto& items = assets::Items();
    const auto& cats  = assets::Categories();

    ImGui::TextDisabled("%zu items in %zu categories, read from the game's own data.",
                        items.size(), cats.size());

    ImGui::SetNextItemWidth(200.0f);
    const char* current = (category == 0) ? "All categories"
                                          : cats[static_cast<std::size_t>(category - 1)].c_str();
    if (ImGui::BeginCombo("##cat", current)) {
        if (ImGui::Selectable("All categories", category == 0)) category = 0;
        for (std::size_t i = 0; i < cats.size(); ++i) {
            const bool sel = (category == static_cast<int>(i) + 1);
            if (ImGui::Selectable(cats[i].c_str(), sel)) category = static_cast<int>(i) + 1;
        }
        ImGui::EndCombo();
    }
    ImGui::SameLine();
    ImGui::SetNextItemWidth(-1.0f);
    ImGui::InputTextWithHint("##itemfilter", "filter by name or id...", filter, sizeof(filter));

    std::string needle = filter;
    for (char& c : needle) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));

    std::vector<const assets::Item*> shown;
    shown.reserve(items.size());
    for (const auto& it : items) {
        if (category != 0 && it.category != cats[static_cast<std::size_t>(category - 1)])
            continue;
        if (!needle.empty()) {
            std::string hay = it.display + " " + it.id;
            for (char& c : hay) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            if (hay.find(needle) == std::string::npos) continue;
        }
        shown.push_back(&it);
    }

    ImGui::Text("%zu shown", shown.size());

    ImGui::BeginChild("##itemlist", ImVec2(0.0f, 260.0f), true);
    ImGuiListClipper clipper;
    clipper.Begin(static_cast<int>(shown.size()));
    while (clipper.Step()) {
        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; ++i) {
            const assets::Item* it = shown[static_cast<std::size_t>(i)];
            if (ImGui::Selectable(it->display.c_str(), selected == it)) selected = it;
            ImGui::SameLine(260.0f);
            ImGui::TextDisabled("%-12s %s", it->category.c_str(), it->id.c_str());
        }
    }
    ImGui::EndChild();

    if (!selected) { ImGui::TextDisabled("Select an item above."); return; }

    ImGui::Text("Selected: %s  [%s]", selected->display.c_str(), selected->category.c_str());
    ImGui::SetNextItemWidth(130.0f);
    ImGui::InputInt("count", &count);
    if (count < 1) count = 1;
    ImGui::Spacing();

    if (selected->source == assets::Source::Object) {
        // Objects go through the game's own quest-reward flow, which does its
        // own placement. One argument: the object index.
        if (ImGui::Button("Give item", ImVec2(220.0f, 0.0f))) {
            for (int i = 0; i < count; ++i)
                console::Execute(Fmt("scr_dialogue_reward_add_item %d", selected->index));
        }
        ImGui::SameLine();
        ImGui::TextDisabled("delivered as a quest reward");
        return;
    }

    // Gear has no object to reference and cannot be built from scratch: the only
    // thing that works is replaying a real scr_weapon_loot call with the item
    // name swapped in. The recorder keeps one on hand automatically.
    // Two ways to reach the game's weapon spawner, preferred in order:
    //   1. Build the call from scratch around the player instance. The tracker
    //      follows o_player every frame and works out where x/y sit inside the
    //      CInstance, so this needs nothing to have happened first and drops the
    //      item at your feet.
    //   2. Replay a real call the game made, if one has been seen. Only used as
    //      a fallback, since it drops wherever that call was going to.
        // The instance alone is enough to spawn: with x/y left at 0 the game falls
    // back to the tile you are standing on. Calibrated coordinates just place it
    // precisely. A usable button beats a disabled one.
    double px = 0.0, py = 0.0;
    const bool  havePos    = gml::PlayerPosition(px, py);
    const bool  havePlayer = gml::PlayerInstance() != nullptr;
    const auto& rec        = gml::WeaponRecord();
    const bool  haveSample = rec.valid && rec.raw.size() >= 4 && rec.self;
    const bool  ready      = havePlayer || haveSample;

    ImGui::BeginDisabled(!ready);
    if (ImGui::Button("Give weapon", ImVec2(220.0f, 0.0f))) {
        static std::string held;              // must outlive the call
        held = selected->display;

        for (int n = 0; n < count; ++n) {
            std::vector<gml::RValue> args;
            void* self  = nullptr;
            void* other = nullptr;

            if (havePlayer) {
                args.assign(9, gml::RValue{});
                gml::SetString(args[0], held.c_str());
                gml::SetReal(args[1], px);
                gml::SetReal(args[2], py);
                gml::SetReal(args[3], 100.0);      // spawn chance -> certain
                args[4].i64   = 1;                 // matches every observed call
                args[4].flags = 0;
                args[4].kind  = gml::kInt64;
                for (std::size_t i = 5; i < args.size(); ++i) gml::SetUndefined(args[i]);
                self = other = gml::PlayerInstance();
            } else {
                args = rec.raw;
                gml::SetString(args[0], held.c_str());
                gml::SetReal(args[3], 100.0);
                self  = rec.self;
                other = rec.other;
            }

            std::vector<gml::RValue*> argv(args.size());
            for (std::size_t i = 0; i < args.size(); ++i) argv[i] = &args[i];

            void* fn = sym::Find("gml_Script_scr_weapon_loot");
            gml::RValue result{};
            console::Print(console::Line::Echo, "> spawn \"%s\" %s",
                           held.c_str(), havePos ? "at the player" : "on the player tile");
            if (fn && gml::CallAs(fn, &result, argv.data(),
                                  static_cast<int>(args.size()), self, other))
                console::Print(console::Line::Result, "spawned on the ground");
            else
                console::Print(console::Line::Error, "spawn failed%s%s",
                               gml::LastError()[0] ? ": " : "", gml::LastError());
        }
    }
    ImGui::EndDisabled();

    ImGui::SameLine();
    if (havePos)         ImGui::TextDisabled("drops at your feet - walk over it");
    else if (havePlayer) ImGui::TextDisabled("drops on your tile (still calibrating exact position)");
    else                 ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f), "locating player...");

    if (!ready) {
        ImGui::TextWrapped(
            "Weapons and armor are not objects - they are built by the game's own spawner. "
            "The mod follows the player to learn a spawn point, which takes a moment of "
            "movement after loading a save. Move a few steps and this enables itself.");
    }
}

void ConsoleCommandsNotice() {
    ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(0.95f, 0.80f, 0.35f, 1.0f));
    ImGui::TextWrapped(
        "CONFIRMED NOT WORKING. These call the game's built-in scr_console_* commands, "
        "whose compiled bodies in this release build contain only a prologue and a return - "
        "there is no implementation to run. In-game testing confirmed they do nothing.");
    ImGui::PopStyleColor();
    ImGui::TextWrapped(
        "They are left here because they run harmlessly and because the real command "
        "dispatcher (NeoConsole) may still reach the logic another way - that is the next "
        "thing to chase. The working cheats live in the Stats and Items tabs.");
    ImGui::Spacing();
}

void TogglesPanel() {
    ConsoleCommandsNotice();

    static const char* toggles[] = {
        "scr_console_godmode", "scr_console_nodeathmode", "scr_console_nocd",
        "scr_console_nopain",  "scr_console_allskills",   "scr_console_nomobs",
    };
    for (const char* t : toggles)
        CallRow(t + std::strlen("scr_console_"), t, t);
}

void WorldPanel() {
    static int hour = 12;

    ConsoleCommandsNotice();

    ImGui::SeparatorText("Time");
    ImGui::SetNextItemWidth(160.0f);
    ImGui::SliderInt("hour", &hour, 0, 23);
    CallRow("Set time", Fmt("scr_console_time_change %d", hour), "scr_console_time_change");

    ImGui::SeparatorText("World");
    CallRow("Kill all", "scr_console_killall", "scr_console_killall");
    CallRow("Reveal fog", "scr_console_FOG_visible 1", "scr_console_FOG_visible");
    CallRow("Weather", "scr_console_weather_switch", "scr_console_weather_switch");
    CallRow("Debug map", "scr_console_debugmap", "scr_console_debugmap");
}

} // namespace

void DrawCheatsTab() {
    if (!gml::Ready() || !gml::AbiProven()) {
        ImGui::TextColored(ImVec4(0.95f, 0.40f, 0.40f, 1.0f),
                           "Disabled: the GML bridge is not proven in this session.");
        ImGui::TextWrapped("Status: %s", gml::Status());
        return;
    }

    if (ImGui::BeginTabBar("##cheattabs")) {
        if (ImGui::BeginTabItem("Stats"))    { StatsPanel();   ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Items"))    { ItemsPanel();   ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Toggles"))  { TogglesPanel(); ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("World"))    { WorldPanel();   ImGui::EndTabItem(); }
        ImGui::EndTabBar();
    }
}

} // namespace mod::cheats
