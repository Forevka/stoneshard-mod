#pragma once

namespace mod::gamespeed {

// Game speed, for getting somewhere without watching the walk cycle.
//
// The cost in a turn-based game is wall-clock time spent on animation: every
// step between cells is interpolated over a fixed number of frames, so walking
// across a map is mostly waiting. GameMaker runs game logic at a fixed steps-
// per-second, which game_set_speed controls, so raising it makes each animation
// finish proportionally sooner.
//
// This is deliberately the whole game rather than just the walk animation.
// Nothing found so far exposes the step interpolation on its own, and a single
// engine-level knob is both honest about what it does and impossible to leave
// the game in a strange half-state - unlike reaching into one animation timer
// and hoping everything that reads it agrees.
//
// The trade is that combat, UI and every other timer speed up too. For getting
// to a location to debug something, that is usually the point.

bool Ready();

// The game's own steps-per-second, captured the first time it is read and
// never overwritten - so "Reset" always has something true to return to,
// even after the value has been changed repeatedly.
double Baseline();

// What the engine reports right now.
double Current();

bool Set(double stepsPerSecond);

// Re-assert the chosen speed when the game changes it back.
//
// The game sets its own speed (room transitions, cutscenes, menus), which would
// silently undo the setting and leave the slider lying. With hold on, the value
// is re-applied whenever the engine has drifted away from it.
bool Hold();
void SetHold(bool on);

// Called once per frame from the overlay, on the game thread.
void Tick();

void DrawSpeedTab();

} // namespace mod::gamespeed
