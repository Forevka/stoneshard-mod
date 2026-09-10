#pragma once

#include <cstddef>
#include <string>
#include <vector>

namespace mod::rewrite {

// Argument-rewriting hooks: intercept a script, change one of its arguments,
// then let the real function run.
//
// Every detour in this mod so far only OBSERVES - the tracer and the capture
// read arguments and pass them straight through, and the breakpoint's only
// intervention is to suppress the call entirely. This is the middle ground:
// the original still runs, but with a number changed on the way in.
//
// That is enough to retune game rules that are expressed as a numeric argument
// - a drop count, a chance, a multiplier - without patching any code. It is
// also the first thing here that changes gameplay from inside a detour, so the
// blast radius is larger than logging: the guard rails below are not optional
// decoration.
//
//   * Only NUMBERS are ever touched. A string or an instance reference is left
//     alone even if it is the selected index - rewriting a pointer would hand
//     the game a bogus address and crash it.
//   * The index is bounds-checked against the live argc, per call. Two call
//     sites of the same script can pass different argument counts.
//   * Observe-only until you say otherwise, so you can watch real values go by
//     and pick the right argument instead of guessing at it.
//
// Scripts only. Object events (gml_Object_*) use a two-argument convention with
// no argument array to rewrite, so they are rejected rather than silently
// doing nothing.

struct Rule {
    std::string symbol;         // gml_Script_scr_loot
    int         argIndex = -1;  // <0 = observe only
    double      factor   = 1.0; // multiplied into the argument
    double      addend   = 0.0; // then added
    double      minValue = 0.0; // result is clamped into [minValue, maxValue]
    double      maxValue = 9999.0;
    bool        roundToInt = true;   // counts have to stay whole
    bool        enabled  = false;    // rewriting armed

    // How many times to run the ORIGINAL call.
    //
    // Some game rules have no numeric argument to scale. scr_loot places one
    // item per call - there is no count parameter - so "more loot" means
    // calling it more often, and "less" means sometimes not calling it at all.
    //
    // Fractions work the way you would want: 2.5 runs it twice and a third
    // time half the time; 0.5 runs it half the time. 1.0 is untouched.
    double      repeat = 1.0;
};

struct Status {
    Rule        rule;
    unsigned    hits = 0;
    unsigned    changed = 0;
    unsigned    repeated = 0;
    unsigned    suppressed = 0;
    std::string lastSeen;       // the arguments as they arrived
};

// Hooks `symbol` if it is not hooked already, or updates the existing rule.
bool Install(const Rule& rule);

void Remove(const std::string& symbol);
void RemoveAll();

const std::vector<Status>& Hooks();
const char* LastError();

// Capacity is fixed because each hook needs its own distinct detour function,
// and those are template instantiations rather than generated code.
std::size_t Capacity();

void DrawRewriteTab();

} // namespace mod::rewrite
