#include "body.h"

#include "builtins.h"
#include "gml.h"
#include "log.h"
#include "savebackup.h"

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <deque>

#include "imgui.h"

namespace mod::body {
namespace {

std::string g_error;

void Fail(const char* fmt, ...) {
    char buf[256];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    g_error = buf;
    Logf("[!] body: %s", buf);
}

// Strings handed to the runtime are not copied - SetString points a RefString
// at our buffer and flags it external. Keys therefore have to outlive the call
// permanently; a deque never invalidates what it already holds.
const char* Intern(const std::string& s) {
    static std::deque<std::string> pool;
    pool.push_back(s);
    return pool.back().c_str();
}

void* Player() { return gml::PlayerInstance(); }

// The game's own spelling, in the order a person reads a body.
const char* LabelFor(const std::string& key) {
    if (key == "head")  return "Head";
    if (key == "tors")  return "Torso";
    if (key == "lhand") return "Left arm";
    if (key == "rhand") return "Right arm";
    if (key == "legs")  return "Left leg";
    if (key == "rlegs") return "Right leg";
    return key.c_str();
}

int OrderOf(const std::string& key) {
    if (key == "head")  return 0;
    if (key == "tors")  return 1;
    if (key == "lhand") return 2;
    if (key == "rhand") return 3;
    if (key == "legs")  return 4;
    if (key == "rlegs") return 5;
    return 6;
}

bool PartsMap(double* mapId) {
    if (!Player()) { Fail("no player instance yet"); return false; }

    gml::RValue v{};
    if (!builtins::GetVar(builtins::PlayerHandle(), "Body_Parts_map", &v) ||
        v.kind != gml::kReal) {
        Fail("Body_Parts_map is not readable");
        return false;
    }
    *mapId = v.real;
    return true;
}

} // namespace

const char* LastError() { return g_error.c_str(); }

bool Read(std::vector<Part>* out) {
    out->clear();

    double mapId = 0.0;
    if (!PartsMap(&mapId)) return false;

    void* self = Player();

    gml::RValue mapArg{};
    gml::SetReal(mapArg, mapId);

    gml::RValue key{};
    if (!builtins::Call("ds_map_find_first", &key, &mapArg, 1, self)) {
        Fail("could not walk Body_Parts_map");
        return false;
    }

    for (int guard = 0; guard < 64; ++guard) {
        if (key.kind == gml::kUndefined || key.kind == gml::kUnset) break;

        gml::RValue args[2]{};
        args[0] = mapArg;
        args[1] = key;

        gml::RValue val{};
        if (!builtins::Call("ds_map_find_value", &val, args, 2, self)) break;

        if (val.kind == gml::kReal) {
            Part p;
            p.key       = gml::ToString(key);
            p.label     = LabelFor(p.key);
            p.condition = val.real;
            out->push_back(p);
        }

        gml::RValue next{};
        if (!builtins::Call("ds_map_find_next", &next, args, 2, self)) break;
        key = next;
    }

    // A ds_map iterates in hash order, which would shuffle the body around the
    // panel between reads. Sort into anatomical order so the layout is stable.
    std::sort(out->begin(), out->end(), [](const Part& a, const Part& b) {
        return OrderOf(a.key) < OrderOf(b.key);
    });
    return !out->empty();
}

bool SetCondition(const std::string& key, double value) {
    backup::EnsureBackupOnce();

    double mapId = 0.0;
    if (!PartsMap(&mapId)) return false;

    if (value < 0.0)   value = 0.0;
    if (value > 100.0) value = 100.0;

    gml::RValue args[3]{};
    gml::SetReal(args[0], mapId);
    if (!gml::SetString(args[1], Intern(key))) { Fail("bad part name"); return false; }
    gml::SetReal(args[2], value);

    gml::RValue r{};
    if (!builtins::Call("ds_map_replace", &r, args, 3, Player())) {
        Fail("could not write %s", key.c_str());
        return false;
    }
    Logf("body: %s = %g", key.c_str(), value);
    return true;
}

bool Heal(const std::string& key) { return SetCondition(key, 100.0); }

bool HealAll() {
    std::vector<Part> parts;
    if (!Read(&parts)) return false;

    bool all = true;
    for (const Part& p : parts) all = SetCondition(p.key, 100.0) && all;
    return all;
}

bool ActiveBuffs(std::vector<std::string>* out) {
    out->clear();

    if (!Player()) return false;

    gml::RValue v{};
    if (!builtins::GetVar(builtins::PlayerHandle(), "buffs", &v) || v.kind != gml::kReal)
        return false;

    gml::RValue listArg{};
    gml::SetReal(listArg, v.real);

    gml::RValue size{};
    if (!builtins::Call("ds_list_size", &size, &listArg, 1, Player()) ||
        size.kind != gml::kReal)
        return false;

    const int n = static_cast<int>(size.real);
    for (int i = 0; i < n && i < 64; ++i) {
        gml::RValue args[2]{};
        args[0] = listArg;
        gml::SetReal(args[1], static_cast<double>(i));

        gml::RValue e{};
        if (!builtins::Call("ds_list_find_value", &e, args, 2, Player())) break;
        out->push_back(gml::ToString(e));
    }
    return true;
}

// ---------------------------------------------------------------------- tab

void DrawBodyTab() {
    if (!gml::PlayerInstance()) {
        ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                           "Waiting for the player instance - load a character, then move a step.");
        return;
    }

    ImGui::TextWrapped(
        "Each body part carries its own 0-100 condition. Read live from the player's "
        "Body_Parts_map every frame, so this is the game's current state rather than a "
        "cached copy.");
    ImGui::Spacing();

    std::vector<Part> parts;
    if (!Read(&parts)) {
        ImGui::TextColored(ImVec4(0.95f, 0.45f, 0.45f, 1.0f), "%s", LastError());
        return;
    }

    for (const Part& p : parts) {
        ImGui::PushID(p.key.c_str());

        // Colour by severity, so a glance finds the broken limb without
        // reading six numbers.
        const float f = static_cast<float>(p.condition) / 100.0f;
        const ImVec4 col = f >= 0.75f ? ImVec4(0.45f, 0.80f, 0.45f, 1.0f)
                         : f >= 0.40f ? ImVec4(0.90f, 0.80f, 0.35f, 1.0f)
                                      : ImVec4(0.92f, 0.45f, 0.40f, 1.0f);

        ImGui::PushStyleColor(ImGuiCol_PlotHistogram, col);
        char overlay[32];
        std::snprintf(overlay, sizeof(overlay), "%.0f / 100", p.condition);
        ImGui::ProgressBar(f, ImVec2(220.0f, 0.0f), overlay);
        ImGui::PopStyleColor();

        ImGui::SameLine();
        ImGui::Text("%-10s", p.label.c_str());

        ImGui::SameLine();
        ImGui::BeginDisabled(p.condition >= 100.0);
        if (ImGui::Button("Heal")) Heal(p.key);
        ImGui::EndDisabled();

        // Injuring one part on purpose is genuinely useful for testing the
        // very system this panel exists to inspect.
        ImGui::SameLine();
        if (ImGui::SmallButton("-25")) SetCondition(p.key, p.condition - 25.0);

        ImGui::PopID();
    }

    ImGui::Spacing();
    if (ImGui::Button("Heal everything", ImVec2(200.0f, 0.0f))) HealAll();
    ImGui::SameLine();
    ImGui::TextDisabled("all six parts to 100");

    if (LastError()[0])
        ImGui::TextColored(ImVec4(0.95f, 0.45f, 0.45f, 1.0f), "%s", LastError());

    // ---- wounds -----------------------------------------------------------
    ImGui::Spacing();
    ImGui::SeparatorText("Active statuses");
    ImGui::TextWrapped(
        "Wounds and bleeding are NOT part of the condition value - they are entries in "
        "the player's buffs list, so a limb can read 100 and still be bleeding. Listed "
        "read-only: removing one needs a call whose arguments are not established yet, "
        "and guessing them is how two earlier scripts were made to fault.");

    std::vector<std::string> buffs;
    if (!ActiveBuffs(&buffs)) {
        ImGui::TextDisabled("buffs list not readable");
    } else if (buffs.empty()) {
        ImGui::TextDisabled("none - no wounds, bleeds or effects active");
    } else {
        for (const std::string& b : buffs) ImGui::BulletText("%s", b.c_str());
    }
}

} // namespace mod::body
