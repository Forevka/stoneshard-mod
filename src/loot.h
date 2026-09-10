#pragma once

namespace mod::loot {

// Loot volume, as a single multiplier.
//
// scr_loot places exactly ONE item per call and takes no count argument - the
// signature is
//
//     scr_loot(lootObject, x, y, chance, bool, entityType, entityTag, presetTag)
//
// read off a live call rather than guessed. So loot volume is not a number to
// scale, it is how many times the game calls that function. This runs the
// original more often (or sometimes not at all) through the argument-rewriting
// hook, which is what "x5 loot" actually means here.
//
// Both routes go through it: o_enemy_Destroy_0 calls scr_loot on death, and
// container events call it on opening. Drops resolve at that moment, so a
// change applies to the next thing you kill or open - not to what is already
// lying on the floor.

// 1.0 is vanilla. Below 1 the call is sometimes skipped; above 1 it repeats.
// Fractions are honoured: 2.5 runs it twice and a third time half the time.
double Multiplier();

// Installs, updates or (at exactly 1.0) disarms the hook.
bool SetMultiplier(double multiplier);

bool Active();
void Disable();

// Live evidence that it is doing something, rather than a setting you have to
// take on trust.
unsigned Calls();
unsigned Repeated();
unsigned Suppressed();

const char* LastError();

void DrawLootTab();

} // namespace mod::loot
