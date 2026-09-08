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

const std::vector<Item>&        Items();
const std::vector<std::string>& Categories();   // sorted, no duplicates

} // namespace mod::assets
