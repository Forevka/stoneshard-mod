#pragma once

#include "builtins.h"

#include <string>
#include <vector>

namespace mod::enemies {

// The enemies standing in the room you are in, and a way to remove them.
//
// Every enemy in the game is an instance of ONE object, o_enemy - there is no
// o_enemy_wolf or o_enemy_bandit. What makes a wolf a wolf is the instance
// variables it carries (race, type, name, HP, LVL), which is the same shape the
// item and potion systems have: one object, many instances, the identity in the
// fields. So a roster is a walk over the live instances rather than a list of
// object names to look up.
//
// Two ways to reach those instances, and the difference matters:
//
//   tracked  - the mod hooks o_enemy's Step event, so every enemy that takes a
//              step hands us its CInstance* directly. That pointer is what the
//              game itself passes as `self`, which means the game's OWN scripts
//              can be run AS that enemy - the same trick character.cpp uses to
//              drive scr_atr_set on the player.
//   listed   - instance_number / instance_find, which answer with a reference
//              (kind 15) rather than a pointer. Good enough to read and write
//              variables through reflection, not enough to run a script as the
//              instance.
//
// The tracker is preferred and the enumeration is the fallback, so the feature
// still works if the Step hook cannot be installed. Which one a row came from is
// shown in the tab rather than hidden, because it decides what the Kill button
// is able to do.

struct Enemy {
    void*            inst = nullptr;   // CInstance*, only when `tracked`
    builtins::Handle handle;           // how its variables are read and written
    bool             tracked = false;

    std::string name;                  // "name", falling back to race/type
    std::string race;
    std::string type;

    double x = 0.0, y = 0.0;
    double dist   = -1.0;              // pixels from the player, -1 when unknown
    double hp     = -1.0;
    double maxHp  = -1.0;
    double level  = -1.0;
    bool   havePos = false;
    bool   haveHp  = false;
};

// Hooks o_enemy's Step event. Safe to call more than once; needs MinHook, so it
// must run after InstallHooks().
bool InstallTracker();
bool Tracking();

// How many times o_enemy's Step event has run through our detour. Evidence the
// hook is live, rather than a claim that it is.
unsigned long long StepsSeen();

// Retires the tracked set when the step events stop arriving - leaving a room
// otherwise leaves its dead occupants listed forever. Called once per frame.
void Tick();

// Rebuilds the roster from the live room. Costs a handful of reflection calls
// per enemy, so the tab throttles it rather than running it every frame.
bool Refresh();

const std::vector<Enemy>& Roster();

// What the GAME says is in the room, through instance_number. Kept beside the
// roster size as a cross-check: the two disagreeing is worth seeing, not
// hiding, because it means the tracker is missing instances.
int Reported();

// Sets HP to 0 through the game's own attribute setter, run as that enemy -
// the same lever the game pulls when damage lands, so the death that follows is
// the game's own, with whatever it normally awards.
//
// Falls back to writing the HP variable directly for a listed (untracked) row,
// which sets the number but cannot run the game's reaction to it.
bool Kill(const Enemy& e);

// instance_destroy. Removes the enemy outright rather than defeating it: the
// Destroy event still runs (which is where the drop comes from) but nothing
// that normally happens on the damage path does.
bool Remove(const Enemy& e);

// Weaken rather than remove.
bool SetHP(const Enemy& e, double hp);

// The game's own console command, scr_console_killall, called with no
// arguments. It is a `with` loop over every enemy, so it cannot be aimed - but
// it is the game's genuine kill-everything path.
bool KillAllVanilla();

// Kill() over every row currently listed. Reports how many succeeded.
int KillAllListed();

// Every instance variable on one enemy, for finding out what a field is
// actually called. The names read here were taken from a live player dump;
// this is how they get checked against a live enemy.
std::vector<std::string> Probe(const Enemy& e, int limit);

const char* LastError();

void DrawEnemiesTab();

} // namespace mod::enemies
