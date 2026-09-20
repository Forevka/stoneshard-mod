#pragma once

#include <string>
#include <vector>

namespace mod::character {

// Character modification.
//
// Almost none of this is reachable by writing instance variables. XP, Thirsty,
// Intoxication, Morale, Sanity and perksList are on NEITHER the player instance
// NOR any global - checked directly, all absent - so the direct route that
// worked for item stats does not exist here.
//
// What does work is driving the game's own machinery, the same way gear is
// spawned through scr_weapon_loot rather than constructed from nothing:
//
//   scr_atr / scr_atr_set   the attribute system. Verified by write-then-read:
//                           Thirsty 0 -> 40 -> 0, Intoxication 0 -> 25 -> 0,
//                           and Immunity reads back the same 50 the instance
//                           variable holds.
//   scr_get_XP              grants XP with the real level-up handling. Asking
//                           for 500 on a character whose max_xp was 250 landed
//                           at 250 and levelled them up - it is not a setter.
//   scr_buff_change(index)  WRONG - does NOT apply a status, and is disabled.
//                           The capture behind this line recorded an argument
//                           value (o_db_hunger0, 6028) but not the arity and
//                           not the `self`, and both of those were wrong: the
//                           script is a buff's own tick handler, called only
//                           from buff Alarm events, and it reads more argument
//                           slots than it was given. See ApplyCondition.
//
// Everything runs with the player as `self`. The console's current-instance
// global is whatever the game last ran, which is not reliably the player.

// ------------------------------------------------------------------ attributes
//
// Named exactly as the save's characterDataMap spells them - "Thirsty", not
// "Thirst".
struct Attr {
    const char* key;
    const char* label;
    double      soft;      // a sane upper bound for the slider, not a clamp
};

const std::vector<Attr>& Needs();        // Hunger, Thirsty, Intoxication, ...
const std::vector<Attr>& Vitals();       // HP, MP, Pain, Fatigue, Immunity

bool GetAttr(const char* key, double* out);
bool SetAttr(const char* key, double value);

// Level-up aware, so it is offered separately from SetAttr("XP", n).
bool GrantXP(double amount);

// ------------------------------------------------------------------ conditions

// Always fails, with an explanation in LastError(). Kept as a seam so the
// catalogue and the UI stay wired up while the real applying call is pinned
// down; see the comment on the definition for what went wrong.
bool ApplyCondition(int assetIndex);

// ---------------------------------------------------------------------- psyche
//
// psyData is a live ds_map on the player - Sanity, Morale, Panic, Frenzy,
// Paranoia, Anxiety, Catharsis, DeathWish, Megalomania, BlessTime - and unlike
// the needs it IS directly writable, because it is a map rather than a
// variable the save assembles from elsewhere.
struct PsyField {
    std::string key;
    double      value = 0.0;
    bool        isString = false;
    std::string str;
};

bool ReadPsyche(std::vector<PsyField>* out);
bool WritePsyche(const std::string& key, double value);

// ----------------------------------------------------------------------- locks
//
// lock_skills, lock_spells, lock_attack, lock_regen, lock_mana_regen and
// lock_turn are ds_lists on the player. They are the game's OWN mechanism for
// taking an ability away, which beats inventing one.
const std::vector<std::string>& LockLists();
int  LockCount(const char* listName);          // -1 when unreadable

const char* LastError();

// The ImGui tab. Lives here rather than in cheats.cpp because every control is
// a thin wrapper over the calls above, and splitting them would put the "which
// lever does this pull" comment a file away from the lever.
void DrawCharacterTab();

} // namespace mod::character
