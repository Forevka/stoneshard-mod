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

// Applying a status is not a script call at all - it is creating an instance.
//
// The previous version called scr_buff_change(index) as the player and killed
// the character outright. That script is a buff's own tick handler: all four of
// its call sites in the exe are buff Alarm events, so it expects to run AS a
// live buff, and it reads more argument slots than it was given. Neither fact
// was knowable from the single capture the signature came from.
//
// Breakpointing gml_Object_c_buff_Create_0 while drinking a potion showed what
// really happens: three instances appear, each running the inherited Create
// chain (o_condition_debuff : o_debuff : c_buff). No applying script is on the
// stack, because instance creation goes through a runtime builtin and the
// engine dispatches Create itself.
//
// Reading one of those live buffs back, then creating one by hand and diffing
// the two, established the division of labour exactly: the Create chain fills
// in everything structural on its own - type, stack, stage, LVL, source, and an
// allocated (empty) data map - and leaves precisely three fields for whoever is
// applying it.
//
//     owner      the unit the buff belongs to, as an instance reference
//     target     that unit's OBJECT index
//     duration   ticks remaining; a buff created with 0 never retires
//
// Set those and the game takes it from there: scr_atr_calc runs every step and
// every turn, and scr_player_buff_buffer recomputes the attribute buffer from
// whatever buffs are live.
//
// KNOWN LIMIT: `data`, the ds_map of stat modifiers, is written by whoever
// applies the buff - the potion code computes its own rolled values - and there
// is no shared call that fills it (scr_buff_param only READS it, once per
// attribute, during recalculation). Statuses whose effect lives in their own
// events - stun, bleeding, poison, coma, most of o_db_* - do not need it.
// Pure stat-modifier buffs will apply with no magnitude until the numbers come
// from somewhere real. Inventing them here is how the last bug happened.
bool ApplyCondition(int assetIndex, double duration) {
    backup::EnsureBackupOnce();
    g_error.clear();

    void* self = Player();
    if (!self) { Fail("no player instance yet"); return false; }

    // A buff is an instance and has to be born somewhere; the player's own
    // position keeps it with the character it belongs to.
    double px = 0.0, py = 0.0;
    if (!gml::PlayerPosition(px, py)) {
        Fail("player position not known yet - take a step and try again");
        return false;
    }

    gml::RValue create[4]{};
    gml::SetReal(create[0], px);
    gml::SetReal(create[1], py);
    gml::SetReal(create[2], 0.0);                                  // depth
    gml::SetReal(create[3], static_cast<double>(assetIndex));

    gml::RValue inst{};
    if (!builtins::Call("instance_create_depth", &inst, create, 4, self) ||
        inst.kind == gml::kUndefined || inst.kind == gml::kUnset) {
        Fail("instance_create_depth failed for asset index %d", assetIndex);
        return false;
    }

    // Address the new buff by the reference the runtime just handed back; the
    // CInstance* for `self` stays the player, which is only the call context.
    builtins::Handle buff{};
    buff.id      = inst;
    buff.self    = self;
    buff.haveRef = true;

    const builtins::Handle player = builtins::PlayerHandle();
    if (!player.haveRef) {
        Fail("no instance reference for the player - cannot set the buff's owner");
        return false;
    }

    // `target` is an object index. Resolve it by name rather than pinning the
    // 5376 this happened to read, so a patch that renumbers objects costs
    // nothing - the same rule the rest of the mod follows.
    gml::RValue objIndex{};
    {
        static const char kPlayerObject[] = "o_player";            // must outlive the call
        gml::RValue nameArg{};
        if (!gml::SetString(nameArg, kPlayerObject) ||
            !builtins::Call("asset_get_index", &objIndex, &nameArg, 1, self) ||
            objIndex.kind != gml::kReal) {
            Fail("asset_get_index(\"o_player\") failed");
            return false;
        }
    }

    if (duration < 1.0) duration = 1.0;
    gml::RValue durValue{};
    gml::SetReal(durValue, duration);

    const bool ok = builtins::SetVar(buff, "owner", player.id) &&
                    builtins::SetVar(buff, "target", objIndex) &&
                    builtins::SetVar(buff, "duration", durValue);
    if (!ok) {
        Fail("created the buff but could not write owner/target/duration");
        return false;
    }

    Logf("character: applied condition index %d for %g (owner=%p target=%.0f)",
         assetIndex, duration, self, objIndex.real);
    return true;
}

// Whether the player's own buffs list actually holds `count` entries now.
//
// Reported rather than assumed: the buff was created and pointed at the player,
// but nothing has yet proved the player's `buffs` ds_list is what the game reads
// back, or that a buff adds itself to it once it has an owner. Showing the count
// next to the attempt is how the Enemies tab handles the same doubt - a
// disagreement is information, not something to paper over.
int ActiveConditionCount() {
    gml::RValue list{};
    if (!builtins::GetVar(builtins::PlayerHandle(), "buffs", &list) ||
        list.kind != gml::kReal)
        return -1;

    gml::RValue arg{};
    gml::SetReal(arg, list.real);

    gml::RValue size{};
    if (!builtins::Call("ds_list_size", &size, &arg, 1, Player()) ||
        size.kind != gml::kReal)
        return -1;
    return static_cast<int>(size.real);
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
        ImGui::TextWrapped(
            "A status is an instance, not a script call. The status object is created and "
            "given the three fields its own Create chain leaves blank - owner, target and "
            "duration - and the game's recalculation, which runs every step and every "
            "turn, picks it up from there.");
        ImGui::TextDisabled(
            "%zu statuses, read from the object table. Behavioural ones (stun, bleeding, "
            "poison) work; pure stat buffs apply with no magnitude - see the note below.",
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

        static float condDuration = 200.0f;
        ImGui::SetNextItemWidth(220.0f);
        ImGui::SliderFloat("duration", &condDuration, 1.0f, 2000.0f, "%.0f ticks");
        ImGui::SameLine();
        ImGui::TextDisabled("a potion's buff read 199");

        ImGui::BeginDisabled(condChoice < 0);
        if (ImGui::Button("Apply condition", ImVec2(200.0f, 0.0f))) {
            const auto& c = conds[static_cast<std::size_t>(condChoice)];

            // Count before and after: the apply can report success because the
            // instance was created and written, while the game still declines to
            // take it up. Showing both numbers makes that visible instead of
            // leaving a button that lies.
            const int before = ActiveConditionCount();
            const bool ok    = ApplyCondition(c.index, condDuration);
            const int after  = ActiveConditionCount();

            if (!ok) {
                console::Print(console::Line::Error, "%s (%s) -> %s",
                               c.name.c_str(), c.display.c_str(), LastError());
            } else if (before >= 0 && after >= 0) {
                console::Print(after > before ? console::Line::Result : console::Line::Error,
                               "%s (%s) -> created; player's buffs list %d -> %d%s",
                               c.name.c_str(), c.display.c_str(), before, after,
                               after > before ? "" : "  (the game did not take it up)");
            } else {
                console::Print(console::Line::Result,
                               "%s (%s) -> created; buffs list not readable",
                               c.name.c_str(), c.display.c_str());
            }
        }
        ImGui::EndDisabled();
        ImGui::SameLine();
        ImGui::TextDisabled("creates the instance and points it at you");

        ImGui::TextDisabled(
            "Note: `data`, the stat-modifier map, is filled by whoever applies a buff and "
            "there is no shared call that does it, so a pure stat buff lands with no "
            "magnitude. Making numbers up here is what broke this last time.");
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
