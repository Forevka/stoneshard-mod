#pragma once

#include "builtins.h"

#include <string>
#include <vector>

namespace mod::items {

// Gear construction.
//
// Weapons and armor are not objects in the asset sense - they are rows in two
// CSV tables embedded in the exe (83 columns for weapons, 77 for armor), and
// the game builds an instance from a row on demand. assets.cpp already reads
// those rows; this is the runtime half.
//
// At runtime a dropped weapon IS an instance - o_weapon_loot on the ground,
// o_inv_weapon_slot once carried - so its rolled stats are reachable through
// the same reflection that builtins.cpp proves on the player.
//
// What is NOT yet established is which of those two instances actually holds
// the stat fields, and under what names. Probe() answers that empirically
// rather than by guessing, and everything else here waits on its result.

// Spawns gear by its CSV display name ("Militia Falchion") through the game's
// own scr_weapon_loot, at an offset from the player. Factored out of the Items
// tab so the probe drives the exact same path the cheat button does.
// Rarity, as the game numbers it. Read off a live item's own instance
// variables (Common=1 ... Treasure=7) and confirmed against ModShardLauncher,
// whose loot module calls
//     scr_weapon_loot(name, x, y, 100, rarity)
// so the FIFTH argument is rarity - not the magic constant this mod used to
// pass. Sending 1 is why every constructed item came out Common with an empty
// Curse array: we were asking for Common every single time.
enum Rarity {
    kCommon = 1, kUncommon = 2, kRare = 3, kEpic = 4,
    kCursed = 5, kUnique = 6, kTreasure = 7,
};
const char* RarityName(int rarity);

// `created` receives the spawned instance when non-null.
//
// scr_weapon_loot RETURNS the instance it made - ModShardLauncher relies on
// that, wrapping the call in `with (...)` to set Duration on the result. So the
// spawned item can simply be taken from the return value instead of being
// hunted down afterwards by diffing instance references.
bool SpawnGear(const std::string& displayName, double dx, double dy,
               int rarity = kCommon, builtins::Handle* created = nullptr);

// Straight into the inventory, skipping the drop-and-walk-over step:
//     scr_inventory_add_weapon(name, rarity)
bool AddWeaponToInventory(const std::string& displayName, int rarity);

// (The old SpawnGearArgs experiment is gone: it existed to hunt for whichever
// trailing argument drove the rarity roll, and the answer turned out to be
// argument 4, which SpawnGear now takes directly.)

// Resolves an object name to a live instance near the player, addressed by the
// reference the runtime hands back (kind 15) rather than a decoded id.
// `dist` is how far that instance sits from the player, which is the only
// evidence available that it is the one just spawned.
bool NearestInstance(const std::string& objectName, builtins::Handle* out, double* dist);

// Addresses instance `nth` of an object by the reference the runtime returns.
// The character state the save calls XP, Thirsty, Intoxication and perksList
// is on none of the obvious carriers - not the player instance, not a global -
// so finding it means being able to look inside an arbitrary object.
bool FirstInstance(const std::string& objectName, builtins::Handle* out);

// How many instances of an object exist right now; -1 if the question could
// not be asked. Used to confirm a spawn actually produced something before
// attributing the nearest instance to it.
int InstanceCount(const std::string& objectName);

// Every instance variable on `h`, formatted "name = value (kind=N)". Empty
// when reflection failed; `total` receives the count the game reports, which
// can exceed `limit`.
std::vector<std::string> DumpVars(const builtins::Handle& h, int limit, int* total);

// The experiment: spawn `displayName`, then dump the variables of both
// candidate carriers - the ground drop and the inventory slot - so the field
// names and their values can be read straight out of the log.
//
// Returns the report as lines, so the caller decides where they go.
std::vector<std::string> Probe(const std::string& displayName, int limit);

// Walks every o_inv_slot the player actually carries and reports each item's
// `data` map. Spawned gear rolls plain - no rarity, no curses - so real
// enchanted items are the only place the Curse entry format can be read from,
// and the ones already in the character's inventory are the supply.
std::vector<std::string> ScanInventory(int limit);

// ------------------------------------------------------------- constructor
//
// An item is one ds_map called `data`, keyed by the CSV column names, holding
// ONLY the stats that item actually has. So a "good enchantment" is not a
// separate mechanism at all - it is an extra key. Adding "Lifesteal": 10 to a
// sword that had none is exactly what the game does for a Unique.
//
// Values are written with the game's own ds_map_replace, never by touching
// memory, so anything the game can represent we can set - including values no
// roll table would ever produce.

struct Field {
    std::string key;
    bool        isString = false;
    double      num      = 0.0;
    std::string str;
};

// Spawns one throwaway of `displayName`, reads its `data` map, then destroys
// it. The result is a template with the game's own field set and real base
// values, which beats reconstructing it from the CSV: it cannot drift, and it
// works for weapons and armor without either being special-cased.
bool LoadTemplate(const std::string& displayName, int rarity = kCommon);

bool                      HaveTemplate();
const std::vector<Field>& Template();
const std::string&        TemplateName();

// Spawns the item, then writes every field over its `data` map. Fields absent
// from the template are added; that is how a stat becomes an enchantment.
bool SpawnConfigured(const std::string& displayName, const std::vector<Field>& fields,
                     int rarity = kCommon);

const char* LastError();

} // namespace mod::items
