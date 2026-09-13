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
#include "body.h"
#include "character.h"
#include "items.h"
#include "potions.h"

#include <array>
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

    // Rarity is argument 4 of scr_weapon_loot. Anything above Common makes the
    // GAME roll the bonus stats itself - which is a truer enchanted item than
    // typing stat keys in by hand, and it fills Curse/Suffix/Colour correctly.
    static int rarity = items::kCommon;
    ImGui::SetNextItemWidth(180.0f);
    if (ImGui::BeginCombo("rarity", items::RarityName(rarity))) {
        for (int r = items::kCommon; r <= items::kTreasure; ++r)
            if (ImGui::Selectable(items::RarityName(r), rarity == r)) rarity = r;
        ImGui::EndCombo();
    }
    ImGui::SameLine();
    ImGui::TextDisabled("the game rolls the bonus stats above Common");

    ImGui::BeginDisabled(!ready);
    if (ImGui::Button("Give weapon", ImVec2(220.0f, 0.0f))) {
        for (int n = 0; n < count; ++n) {
            console::Print(console::Line::Echo, "> spawn \"%s\" %s",
                           selected->display.c_str(),
                           havePos ? "at the player" : "on the player tile");
            if (items::SpawnGear(selected->display, 48.0, 0.0, rarity))
                console::Print(console::Line::Result, "spawned on the ground");
            else
                console::Print(console::Line::Error, "spawn failed: %s", items::LastError());
        }
    }

    ImGui::SameLine();
    if (ImGui::Button("To inventory", ImVec2(140.0f, 0.0f))) {
        for (int i = 0; i < count; ++i) {
            console::Print(console::Line::Echo, "> scr_inventory_add_weapon \"%s\" %s",
                           selected->display.c_str(), items::RarityName(rarity));
            if (!items::AddWeaponToInventory(selected->display, rarity))
                console::Print(console::Line::Error, "failed: %s", items::LastError());
        }
    }

    ImGui::SameLine();

    // The experiment behind the planned stat editor: spawn one, then read back
    // every instance variable the game put on it. Which carrier holds the
    // rolled stats - and what the fields are called - is not yet established,
    // and guessing it would be the wrong way to find out.
    if (ImGui::Button("Probe stats", ImVec2(140.0f, 0.0f))) {
        console::Print(console::Line::Echo, "> itemprobe \"%s\"", selected->display.c_str());
        for (const std::string& l : items::Probe(selected->display, 0))
            console::Print(console::Line::Result, "%s", l.c_str());
    }

    ImGui::EndDisabled();

    ImGui::SameLine();
    if (havePos)         ImGui::TextDisabled("drops at your feet - walk over it");
    else if (havePlayer) ImGui::TextDisabled("drops on your tile (still calibrating exact position)");
    else                 ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f), "locating player...");

    // ------------------------------------------------------------ constructor
    //
    // An item is one ds_map keyed by the CSV column names, carrying only the
    // stats it actually has - so ADDING a key is what the game does to make a
    // Unique, and it is what makes an enchantment here. The template comes from
    // a real spawned item rather than from re-reading the CSV, so the field set
    // and the base values are the game's own and cannot drift out of step.
    ImGui::Spacing();
    if (ImGui::CollapsingHeader("Constructor - pick the stats before spawning")) {
        static std::vector<items::Field>       edit;
        static std::vector<std::array<char, 96>> textBuf;
        static std::string                     editFor;
        static int                             addChoice = 0;

        auto rebuildBuffers = [&] {
            textBuf.assign(edit.size(), {});
            for (std::size_t i = 0; i < edit.size(); ++i)
                std::snprintf(textBuf[i].data(), textBuf[i].size(), "%s", edit[i].str.c_str());
        };

        ImGui::BeginDisabled(!ready);
        if (ImGui::Button("Load template", ImVec2(160.0f, 0.0f))) {
            if (items::LoadTemplate(selected->display, rarity)) {
                edit    = items::Template();
                editFor = selected->display;
                rebuildBuffers();
                console::Print(console::Line::Result, "template: %s, %zu editable field(s)",
                               editFor.c_str(), edit.size());
            } else {
                editFor.clear();
                edit.clear();
                console::Print(console::Line::Error, "template failed: %s", items::LastError());
            }
        }
        ImGui::EndDisabled();
        ImGui::SameLine();
        ImGui::TextDisabled("spawns one, reads it, removes it again");

        if (editFor != selected->display || edit.empty()) {
            ImGui::TextDisabled("Load a template for \"%s\" to edit its stats.",
                                selected->display.c_str());
        } else {
            // Weapons and armor have different stat vocabularies; the item says
            // which it is rather than us inferring it from the category text.
            bool isArmor = false;
            for (const auto& f : edit)
                if (f.key == "Metatype" && f.isString) isArmor = (f.str == "Armor");
            const auto& vocab = isArmor ? assets::ArmorStats() : assets::WeaponStats();

            ImGui::Text("%s - %zu field(s)", editFor.c_str(), edit.size());

            ImGui::BeginChild("##fields", ImVec2(0.0f, 240.0f), true);
            for (std::size_t i = 0; i < edit.size(); ++i) {
                ImGui::PushID(static_cast<int>(i));
                ImGui::SetNextItemWidth(180.0f);
                if (edit[i].isString) {
                    if (ImGui::InputText(edit[i].key.c_str(), textBuf[i].data(), textBuf[i].size()))
                        edit[i].str = textBuf[i].data();
                } else {
                    ImGui::InputDouble(edit[i].key.c_str(), &edit[i].num, 1.0, 10.0, "%.2f");
                }
                ImGui::PopID();
            }
            ImGui::EndChild();

            // Only stats the item does NOT already carry: adding one of these
            // is the enchantment.
            std::vector<const std::string*> addable;
            for (const std::string& stat : vocab) {
                bool present = false;
                for (const auto& f : edit) if (f.key == stat) { present = true; break; }
                if (!present) addable.push_back(&stat);
            }

            if (!addable.empty()) {
                if (addChoice >= static_cast<int>(addable.size())) addChoice = 0;
                ImGui::SetNextItemWidth(220.0f);
                if (ImGui::BeginCombo("##addstat", addable[addChoice]->c_str())) {
                    for (std::size_t i = 0; i < addable.size(); ++i)
                        if (ImGui::Selectable(addable[i]->c_str(), addChoice == static_cast<int>(i)))
                            addChoice = static_cast<int>(i);
                    ImGui::EndCombo();
                }
                ImGui::SameLine();
                if (ImGui::Button("Add stat")) {
                    items::Field f;
                    f.key = *addable[addChoice];
                    edit.push_back(f);
                    rebuildBuffers();
                }
                ImGui::SameLine();
                ImGui::TextDisabled("%zu more available", addable.size());
            }

            ImGui::Spacing();
            ImGui::BeginDisabled(!ready);
            if (ImGui::Button("Spawn configured", ImVec2(220.0f, 0.0f))) {
                console::Print(console::Line::Echo, "> build \"%s\" with %zu field(s)",
                               editFor.c_str(), edit.size());
                if (items::SpawnConfigured(editFor, edit, rarity))
                    console::Print(console::Line::Result, "built - it is on the ground at your feet");
                else
                    console::Print(console::Line::Error, "build failed: %s", items::LastError());
            }
            ImGui::EndDisabled();
            ImGui::SameLine();
            if (ImGui::Button("Reset to base")) { edit = items::Template(); rebuildBuffers(); }
        }
    }

    if (!ready) {
        ImGui::TextWrapped(
            "Weapons and armor are not objects - they are built by the game's own spawner. "
            "The mod follows the player to learn a spawn point, which takes a moment of "
            "movement after loading a save. Move a few steps and this enables itself.");
    }
}

// The scr_console_* panels lived here: godmode, nodeathmode, killall, time
// change and the rest. They are GONE rather than merely disabled.
//
// Their compiled bodies in this release contain only a prologue and a return -
// there is no implementation behind them - and in-game testing confirmed they
// did nothing at all. They were kept for a while on the theory that the real
// dispatcher might reach the logic another way. It never did, and meanwhile
// the Character tab grew working equivalents through scr_atr_set and
// scr_buff_change. Two tabs of buttons that provably do nothing are worse than
// no tabs at all: they make the whole panel look unreliable.

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
        // Its own tab rather than a section of Items, because a potion is not
        // an entry in that catalogue and never can be - see potions.h.
        if (ImGui::BeginTabItem("Potions"))  { potions::DrawPotionsTab(); ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Character")){ character::DrawCharacterTab(); ImGui::EndTabItem(); }
        if (ImGui::BeginTabItem("Body"))     { body::DrawBodyTab();           ImGui::EndTabItem(); }
        ImGui::EndTabBar();
    }
}

} // namespace mod::cheats
