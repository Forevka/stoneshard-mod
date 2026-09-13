#pragma once

#include <string>
#include <vector>

namespace mod::potions {

// Potion construction.
//
// Potions are the one item family the Items tab genuinely cannot list. There is
// no o_inv_potion_healing object to spawn - there is exactly one potion object,
// o_inv_bottle, and every potion in the game is an instance of it whose `data`
// map carries an effect list. "Potion of Healing" is not an item name at all:
// it is assembled at display time from the tag good_pt_healing plus a quality
// tier. So the catalogue shows a single entry called "Bottle", and no amount of
// searching it will ever turn up a named potion.
//
// The game builds one like this:
//
//     o_inv_bottle Create  -> sets alarm[0]
//     o_inv_bottle Alarm 0 -> scr_roll_potion   (the whole event; nothing else)
//     scr_roll_potion      -> rolls the effects, then calls
//                             scr_potion_set_param, which derives everything
//                             else from them - Name, Colour, quality
//
// A potion is therefore its effect list, `atrdlist`, plus whatever the game
// derives from it. And `atrdlist` is a plain ds_list of tag strings, so it can
// be written directly: ds_list_clear, then ds_list_add per tag. The derived
// half is not invented here - scr_potion_set_param is asked to do it, exactly
// as the game does.
//
// Two facts make that safe, and both were established by observation rather
// than assumption:
//
//   * scr_potion_set_param takes argc = 0 and works on the bottle as `self`,
//     read off a captured call. There are no argument semantics to guess.
//   * neither it nor scr_roll_potion survives being called from the Present
//     hook - the runtime throws 0xE06D7363. They only work inside their own
//     event frame, which is why the build happens in the alarm detour below and
//     not in the UI callback.
//
// What this does NOT do is re-roll a potion. That was tried at length: called
// on an already-rolled bottle scr_roll_potion changes nothing, and blanking the
// bottle first either does not lift that (Name) or makes the call throw
// (emptying atrdlist). Building the potion you want outright turned out to be
// both simpler and more capable - it reaches combinations no roll table
// produces.

// ------------------------------------------------------------------ recorder
//
// The potion scripts need a CInstance* for the bottle, and an instance
// reference (kind 15) is not one. The same answer as everywhere else in this
// mod: let the game hand it over. Hooking the bottle's own Alarm 0 gives `self`
// at the exact moment the game finishes rolling a potion - a real instance
// pointer, with no layout assumptions behind it, in the one context where the
// potion scripts run.
bool InstallRecorder();
bool RecorderReady();

// ------------------------------------------------------------------ building

// Builds a potion carrying exactly `tags` and hands it to you.
//
// Takes one bottle, lets the game roll it normally, then replaces its effect
// list with yours and has scr_potion_set_param re-derive the name, colour and
// the rest. That script takes argc = 0 and works on the bottle as `self` -
// confirmed by capturing a real call, not inferred - so there is nothing about
// its arguments left to guess.
//
// Any combination works, including ones no roll table produces. The build
// happens in the alarm detour on the next step, so the result arrives a frame
// later through TakeOutcome.
bool BuildPotion(const std::vector<std::string>& tags);

// True between arming and the alarm firing - normally a single frame.
bool Pending();

// Collects the result of the last served request exactly once, so the UI can
// poll without re-reporting. False when there is nothing new.
bool TakeOutcome(bool* ok, std::string* message);

const char* LastError();

void DrawPotionsTab();

} // namespace mod::potions
