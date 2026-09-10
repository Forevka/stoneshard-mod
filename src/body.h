#pragma once

#include <string>
#include <vector>

namespace mod::body {

// Body part condition.
//
// Stoneshard tracks six parts independently, each a 0-100 condition value. The
// save calls the structure `Body`; live it is a ds_map on the player called
// Body_Parts_map, keyed exactly the same way:
//
//     head   tors   lhand   rhand   legs   rlegs
//
// Those keys line up one-for-one with the o_db_bleed_* objects (bleed_head,
// bleed_tors, bleed_lhand, ...), which is a good sign the naming is the game's
// own rather than a coincidence of the save format.
//
// Healing writes the map directly with ds_map_replace, the same mechanism that
// already works for psyData. That is deliberate: scr_bodyPartsConditionChange
// exists and takes two arguments, but what those two arguments MEAN has not
// been established, and calling a script with guessed arguments is how
// scr_skill_open and scr_buff_param were made to fault earlier.

struct Part {
    std::string key;        // head
    std::string label;      // Head
    double      condition = 0.0;
};

// Reads Body_Parts_map. Empty when the player is not loaded or the map is not
// readable - never a fabricated set of full-health parts, since a healing UI
// showing invented values would be worse than showing nothing.
bool Read(std::vector<Part>* out);

bool SetCondition(const std::string& key, double value);
bool Heal(const std::string& key);      // to 100
bool HealAll();

// The player's active statuses, read from the `buffs` ds_list. Wounds and
// bleeds live here rather than in the condition map, so a part can read 100
// and still be bleeding.
bool ActiveBuffs(std::vector<std::string>* out);

const char* LastError();

void DrawBodyTab();

} // namespace mod::body
