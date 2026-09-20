#include "character.h"

#include "builtins.h"
#include "gml.h"
#include "log.h"
#include "savebackup.h"
#include "assets.h"
#include "console.h"
#include "symbols.h"

#include "imgui.h"

#include <cstdarg>
#include <cstdio>
#include <deque>

namespace mod::character {
namespace {

std::string g_error;

void Fail(const char* fmt, ...) {
    char buf[256];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    g_error = buf;
    Logf("[!] character: %s", buf);
}

// Strings passed to the runtime are not copied - gml::SetString points a
// RefString at our buffer and marks it external. Attribute names therefore have
// to outlive the call permanently; a deque never invalidates what it holds.
const char* Intern(const std::string& s) {
    static std::deque<std::string> pool;
    pool.push_back(s);
    return pool.back().c_str();
}

// Every one of these has to run AS the player. The console's current-instance
// global is whatever the game last executed, which is not reliably the player,
// and scr_atr resolves its attribute against `self`.
void* Player() { return gml::PlayerInstance(); }

bool CallScript(const char* symbol, gml::RValue* result,
                gml::RValue* args, int argc) {
    void* self = Player();
    if (!self) { Fail("no player instance yet"); return false; }

    void* fn = sym::Find(std::string("gml_Script_") + symbol);
    if (!fn) { Fail("%s not found", symbol); return false; }

    std::vector<gml::RValue*> argv(static_cast<std::size_t>(argc));
    for (int i = 0; i < argc; ++i) argv[static_cast<std::size_t>(i)] = &args[i];

    if (!gml::CallAs(fn, result, argv.data(), argc, self, self)) {
        Fail("%s failed%s%s", symbol, gml::LastError()[0] ? ": " : "", gml::LastError());
        return false;
    }
    return true;
}

const std::vector<Attr> kNeeds = {
    { "Hunger",       "Hunger",       100.0 },
    { "Thirsty",      "Thirst",       100.0 },
    { "Intoxication", "Intoxication", 100.0 },
    { "Immunity",     "Immunity",     100.0 },
    { "Fatigue",      "Fatigue",      100.0 },
    { "Pain",         "Pain",         100.0 },
};

const std::vector<Attr> kVitals = {
    { "HP",     "Health",  500.0 },
    { "MP",     "Mana",    500.0 },
    { "XP",     "XP",     5000.0 },
    { "LVL",    "Level",     30.0 },
};

const std::vector<std::string> kLocks = {
    "lock_skills", "lock_spells", "lock_attack",
    "lock_regen",  "lock_mana_regen", "lock_turn", "lock_items",
};

} // namespace

const char* LastError() { return g_error.c_str(); }

const std::vector<Attr>&        Needs()     { return kNeeds; }
const std::vector<Attr>&        Vitals()    { return kVitals; }
const std::vector<std::string>& LockLists() { return kLocks; }

// ------------------------------------------------------------------ attributes

bool GetAttr(const char* key, double* out) {
    gml::RValue arg{};
    if (!gml::SetString(arg, Intern(key))) { Fail("bad attribute name"); return false; }

    gml::RValue r{};
    if (!CallScript("scr_atr", &r, &arg, 1)) return false;
    if (r.kind != gml::kReal) { Fail("scr_atr(%s) gave kind=%d", key, r.kind); return false; }

    *out = r.real;
    return true;
}

bool SetAttr(const char* key, double value) {
    backup::EnsureBackupOnce();

    gml::RValue args[2]{};
    if (!gml::SetString(args[0], Intern(key))) { Fail("bad attribute name"); return false; }
    gml::SetReal(args[1], value);

    gml::RValue r{};
    if (!CallScript("scr_atr_set", &r, args, 2)) return false;

    Logf("character: %s = %g", key, value);
    return true;
}

bool GrantXP(double amount) {
    backup::EnsureBackupOnce();

    gml::RValue arg{};
    gml::SetReal(arg, amount);

    gml::RValue r{};
    if (!CallScript("scr_get_XP", &r, &arg, 1)) return false;

    Logf("character: granted %g XP", amount);
    return true;
}

// ------------------------------------------------------------------ conditions

// DISABLED - this called the wrong script, as the wrong instance.
//
// scr_buff_change does not apply a status. Every one of its four call sites in
// the exe is a buff object's own Alarm event (c_buff_Alarm_1 twice,
// o_b_deflect_Alarm_1, o_b_residual_charge_Alarm_1), so it runs AS a live buff
// instance and ticks that buff through scr_modifer_duration_change. Running it
// as the player points buff-tick logic at a character.
//
// It is also variadic: it converts argument_count at +0x1b7 (cvtsi2sd xmm0,
// r9d), branches on it at +0x64e (cmp ebx, 2), and dereferences args[2], args[3]
// and args[4] on some paths - past the end of the one-element array this passed.
//
// Either defect on its own corrupts the character, which is what reached us as
// "every status effect instantly kills me, even the beneficial ones". Under
// this failure which object you picked barely matters, which is exactly the
// shape of that report.
//
// The applying path is scr_player_buff_buffer -> scr_buff_buffer_add (7 call
// sites, all from the player/enemy buffer scripts). Its signature is NOT
// established, and inferring one from a single observation is what produced
// this bug in the first place - scr_weapon_loot and scr_buff_param went the
// same way. So nothing is called until a real call has been recorded.
bool ApplyCondition(int /*assetIndex*/) {
    Fail("disabled: scr_buff_change ticks an existing buff, it does not apply "
         "one - the real signature has not been captured yet");
    return false;
}

// ---------------------------------------------------------------------- psyche

namespace {

bool PsyMap(double* mapId) {
    void* self = Player();
    if (!self) { Fail("no player instance yet"); return false; }

    gml::RValue v{};
    if (!builtins::GetVar(builtins::PlayerHandle(), "psyData", &v) || v.kind != gml::kReal) {
        Fail("psyData is not readable");
        return false;
    }
    *mapId = v.real;
    return true;
}

} // namespace

bool ReadPsyche(std::vector<PsyField>* out) {
    out->clear();

    double mapId = 0.0;
    if (!PsyMap(&mapId)) return false;

    gml::RValue mapArg{};
    gml::SetReal(mapArg, mapId);

    void* self = Player();

    gml::RValue key{};
    if (!builtins::Call("ds_map_find_first", &key, &mapArg, 1, self)) {
        Fail("could not walk psyData");
        return false;
    }

    for (int guard = 0; guard < 256; ++guard) {
        if (key.kind == gml::kUndefined || key.kind == gml::kUnset) break;

        gml::RValue args[2]{};
        args[0] = mapArg;
        args[1] = key;

        gml::RValue val{};
        if (!builtins::Call("ds_map_find_value", &val, args, 2, self)) break;

        PsyField f;
        f.key = gml::ToString(key);
        if (val.kind == gml::kReal) {
            f.value = val.real;
            out->push_back(f);
        } else if (val.kind == gml::kString) {
            f.isString = true;
            f.str      = gml::ToString(val);
            out->push_back(f);
        }

        gml::RValue next{};
        if (!builtins::Call("ds_map_find_next", &next, args, 2, self)) break;
        key = next;
    }
    return !out->empty();
}

bool WritePsyche(const std::string& key, double value) {
    backup::EnsureBackupOnce();

    double mapId = 0.0;
    if (!PsyMap(&mapId)) return false;

    gml::RValue args[3]{};
    gml::SetReal(args[0], mapId);
    if (!gml::SetString(args[1], Intern(key))) { Fail("bad psyche key"); return false; }
    gml::SetReal(args[2], value);

    gml::RValue r{};
    if (!builtins::Call("ds_map_replace", &r, args, 3, Player())) {
        Fail("could not write psyData.%s", key.c_str());
        return false;
    }
    Logf("character: psyData.%s = %g", key.c_str(), value);
    return true;
}

// ----------------------------------------------------------------------- locks

int LockCount(const char* listName) {
    gml::RValue v{};
    if (!builtins::GetVar(builtins::PlayerHandle(), listName, &v) || v.kind != gml::kReal)
        return -1;

    gml::RValue arg{};
    gml::SetReal(arg, v.real);

    gml::RValue r{};
    if (!builtins::Call("ds_list_size", &r, &arg, 1, Player()) || r.kind != gml::kReal)
        return -1;
    return static_cast<int>(r.real);
}

// -------------------------------------------------------------------- the tab

void DrawCharacterTab() {
    static char   condFilter[96] = "";
    static int    condChoice     = -1;
    static double xpAmount       = 100.0;

    if (!Player()) {
        ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                           "Waiting for the player instance - load a character, then move a step.");
        return;
    }

    // Read live rather than cached. The game moves these constantly, and a
    // stale slider would quietly write an old value back over a newer one.
    auto attrRow = [](const Attr& a) {
        double value = 0.0;
        if (!GetAttr(a.key, &value)) { ImGui::TextDisabled("%s: unreadable", a.label); return; }

        ImGui::PushID(a.key);
        float f = static_cast<float>(value);
        ImGui::SetNextItemWidth(220.0f);
        if (ImGui::SliderFloat(a.label, &f, 0.0f, static_cast<float>(a.soft), "%.0f"))
            SetAttr(a.key, static_cast<double>(f));
        ImGui::SameLine();
        ImGui::TextDisabled("%s", a.key);
        ImGui::PopID();
    };

    ImGui::SeparatorText("Needs");
    for (const Attr& a : Needs()) attrRow(a);

    ImGui::SeparatorText("Vitals and progression");
    for (const Attr& a : Vitals()) attrRow(a);

    // scr_get_XP is NOT a setter - it runs the real level-up path, so asking
    // for more than the threshold levels you up instead of overshooting. That
    // is why it sits apart from the raw XP slider.
    ImGui::SetNextItemWidth(160.0f);
    ImGui::InputDouble("##xpamt", &xpAmount, 10.0, 100.0, "%.0f");
    ImGui::SameLine();
    if (ImGui::Button("Grant XP", ImVec2(140.0f, 0.0f))) {
        const bool ok = GrantXP(xpAmount);
        console::Print(ok ? console::Line::Result : console::Line::Error,
                       "scr_get_XP %g -> %s", xpAmount, ok ? "ok" : LastError());
    }
    ImGui::SameLine();
    ImGui::TextDisabled("levels you up if it crosses the threshold");

    // ---- conditions -------------------------------------------------------
    ImGui::SeparatorText("Conditions");
    const auto& conds = assets::Conditions();
    if (conds.empty()) {
        ImGui::TextDisabled("No condition catalogue (object table unreadable).");
    } else {
        // The catalogue is still worth showing - it is read from the object
        // table and is correct. It is only applying one that is broken.
        ImGui::TextColored(ImVec4(0.95f, 0.45f, 0.45f, 1.0f),
                           "Applying a status is DISABLED - it corrupted the character.");
        ImGui::TextWrapped(
            "scr_buff_change turned out to be the buff's own tick handler, not the call "
            "that applies one: every site that calls it in the game is a buff object's "
            "Alarm event, so it expects to run as a buff instance rather than as you. "
            "It also reads argument_count and can index past the single argument this "
            "passed it. That is why every effect killed the character, beneficial ones "
            "included - the object you picked barely entered into it.");
        ImGui::TextDisabled("%zu statuses listed, read from the object table.",
                            conds.size());
        ImGui::SetNextItemWidth(-1.0f);
        ImGui::InputTextWithHint("##condfilter", "filter conditions...",
                                 condFilter, sizeof(condFilter));

        std::string needle = condFilter;
        for (char& c : needle) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));

        ImGui::BeginChild("##condlist", ImVec2(0.0f, 200.0f), true);
        for (std::size_t i = 0; i < conds.size(); ++i) {
            if (!needle.empty()) {
                std::string hay = conds[i].display + " " + conds[i].name;
                for (char& c : hay)
                    c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
                if (hay.find(needle) == std::string::npos) continue;
            }
            // Buffs and debuffs go through the same call, so colour is the only
            // clue about which one you are about to inflict on yourself.
            ImGui::PushStyleColor(ImGuiCol_Text,
                                  conds[i].positive ? ImVec4(0.55f, 0.90f, 0.55f, 1.0f)
                                                    : ImVec4(0.95f, 0.65f, 0.55f, 1.0f));
            if (ImGui::Selectable(conds[i].display.c_str(), condChoice == static_cast<int>(i)))
                condChoice = static_cast<int>(i);
            ImGui::PopStyleColor();
            ImGui::SameLine(250.0f);
            ImGui::TextDisabled("%s", conds[i].name.c_str());
        }
        ImGui::EndChild();

        ImGui::BeginDisabled(true);
        ImGui::Button("Apply condition", ImVec2(200.0f, 0.0f));
        ImGui::EndDisabled();
        ImGui::SameLine();
        ImGui::TextDisabled("re-enabled once a real apply has been recorded");
    }

    // ---- psyche -----------------------------------------------------------
    // psyData IS a live ds_map on the player, so unlike the needs these are
    // written directly instead of through a script.
    if (ImGui::CollapsingHeader("Psyche - Sanity, Morale, Panic, Bless...")) {
        static std::vector<PsyField> psy;
        if (ImGui::Button("Read psyData", ImVec2(160.0f, 0.0f)))
            if (!ReadPsyche(&psy))
                console::Print(console::Line::Error, "psyData: %s", LastError());
        ImGui::SameLine();
        ImGui::TextDisabled("%zu field(s)", psy.size());

        ImGui::BeginChild("##psy", ImVec2(0.0f, 220.0f), true);
        for (std::size_t i = 0; i < psy.size(); ++i) {
            if (psy[i].isString) {
                ImGui::TextDisabled("%s = %s", psy[i].key.c_str(), psy[i].str.c_str());
                continue;
            }
            ImGui::PushID(static_cast<int>(i));
            ImGui::SetNextItemWidth(180.0f);
            if (ImGui::InputDouble(psy[i].key.c_str(), &psy[i].value, 1.0, 10.0, "%.2f"))
                WritePsyche(psy[i].key, psy[i].value);
            ImGui::PopID();
        }
        ImGui::EndChild();
    }

    // ---- locks ------------------------------------------------------------
    if (ImGui::CollapsingHeader("Ability locks")) {
        ImGui::TextWrapped(
            "The game's own way of taking abilities away: ds_lists on the player. "
            "Read-only for now - what a locked entry actually looks like is not "
            "established, and writing a guess into these would break the character.");
        for (const std::string& l : LockLists()) {
            const int n = LockCount(l.c_str());
            if (n < 0) ImGui::TextDisabled("   %-18s unreadable", l.c_str());
            else       ImGui::Text("   %-18s %d entr%s", l.c_str(), n, n == 1 ? "y" : "ies");
        }
    }
}

} // namespace mod::character
