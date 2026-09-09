#pragma once

namespace mod::remote {

// Headless control for the debugger.
//
// The overlay's controls are ImGui widgets drawn inside the game, which makes
// them unreachable to anything but a human at the keyboard. This watches for a
// one-line command file and executes it on the game thread, so traces,
// breakpoints and memory dumps can be driven from a script or another process
// without stealing focus or the mouse.
//
// Write a command into <data dir>/debug-cmd.txt; it is consumed and deleted.
// The data dir is per process (see paths.h), so two game instances can be
// driven independently.
//
//   trace <ms> [filter]      record a burst
//   bp <symbol>              arm a breakpoint (trap-and-report)
//   bpoff                    clear the breakpoint
//   dump [player|<hex>] [n]  hex/typed dump of instance memory to the log
//   call <symbol> [args...]  run a game SCRIPT, same syntax as the console
//   callb <builtin> [args..] run a GameMaker BUILTIN - a separate table and a
//                            different ABI, so it cannot go through `call`
//   getvar <name>            read a player instance variable by name
//   setvar <name> <value>    write one
//   names [limit]            list the player's instance variables
//   status                   write a state summary to the log
void Poll();

} // namespace mod::remote
