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
//   speed [mult|reset]       game speed, as a multiple of the game's own
//   capture <symbol>         detour a script and record the game's own call
//   capture                  show what was recorded
//   weaponrec                show the recorded real scr_weapon_loot call
//   spawnrare "<name>" <1-7> spawn gear at a rarity the GAME rolls stats for
//   giveweapon "<name>" [r]  same, but straight into the inventory
//   objvars <obj> [limit]    list one instance's variables and values
//   objget <obj> <var>       read a variable on any object
//   objset <obj> <var> <val> write one
//   itemtemplate "<name>"    read a gear item's editable stat fields
//   itembuild "<name>" K=V   build one with those stats set or ADDED
//   itemscan [limit]         dump the `data` map of every carried item
//   itemprobe <gear name>    spawn gear and dump the instance variables of
//                            both carriers, to find where its stats live
//   status                   write a state summary to the log
void Poll();

} // namespace mod::remote
