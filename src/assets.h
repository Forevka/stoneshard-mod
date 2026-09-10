#pragma once

#include <string>
#include <vector>

namespace mod::assets {

enum class Source { Object, Csv };

struct Item {
    std::string id;        // what gets passed to the game
    std::string display;   // human-readable label
    std::string category;  // e.g. "Sword", "Head", "Food"
    int         index = -1;  // GameMaker asset index (objects only)
    Source      source = Source::Object;
};

// Builds the item catalogue from two places, both read live so a game patch is
// picked up automatically:
//
//   * data.win OBJT  - the o_inv_* objects (food, books, consumables...).
//     Their category comes from the object's parent, which is how the game
//     itself groups them.
//   * the exe's .rdata - the embedded weapons/armor CSV rows, which carry an
//     explicit category column. Gear is data-driven in Stoneshard, so there are
//     no weapon or armor objects to find.
bool Load();

bool        Loaded();
const char* Status();

// The gear tables' column names, taken from the two header rows embedded
// alongside the data ("name;Tier;id;Slot;Subtype;..." for weapons, 83 columns;
// "name;Tier;id;Slot;class;..." for armor, 77).
//
// These are not just documentation: an item's live `data` ds_map is keyed by
// these exact names, so the header doubles as the set of stats a constructed
// item may legally carry. Read from the exe like everything else, so a patch
// that adds a stat column adds it here too.
// A status the game can put on a unit.
//
// scr_buff_change takes an ASSET INDEX - captured from the game applying
// o_db_hunger0 (6028) after eating - so the catalogue is just the object table
// filtered to the two families the game uses for statuses:
//     o_db_*  debuffs  (stun, poison, bleeding, coma, pain, drunk, ...)
//     o_b_*   buffs    (bless, adrenaline, carnage, stances, ...)
struct Condition {
    std::string name;      // o_db_stun
    std::string display;   // Stun
    int         index = -1;
    bool        positive = false;
};

const std::vector<Condition>& Conditions();

const std::vector<std::string>& WeaponStats();
const std::vector<std::string>& ArmorStats();

const std::vector<Item>&        Items();
const std::vector<std::string>& Categories();   // sorted, no duplicates

} // namespace mod::assets
