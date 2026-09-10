# Stoneshard Mod

A native debugging and cheat mod for [Stoneshard](https://store.steampowered.com/app/625960/Stoneshard/),
loaded as a `version.dll` proxy and driven from a Dear ImGui overlay (**INSERT** toggles it). It gives you an
item constructor that reads a real spawned item's stat map and lets you edit every field or add new
ones before spawning it at any rarity; a character panel for hunger, thirst, intoxication, pain,
fatigue, immunity, XP and the full psyche system, plus all 323 of the game's status effects; a body
part panel showing each limb's condition with per-part heal buttons; a configurable loot multiplier
(0.25x–7x) covering both enemy drops and containers; adjustable game speed so you aren't watching
walk animations; a save importer that moves characters between machines and re-signs their
checksums; and a developer layer with a script tracer, breakpoints, an instance inspector, a live
console over all 34,167 game scripts, and argument-rewriting hooks. Nothing is hardcoded — every
function, object and asset is resolved by name at runtime, so a game patch that moves addresses
costs nothing.

**Build** requires CMake 3.21+, a C++17 MSVC toolchain and Git (ImGui, MinHook and zlib are fetched
automatically). Point it at your install and build:
`cmake -B build -DSTONESHARD_DIR="C:/path/to/Stoneshard" && cmake --build build`. **Install** by
copying `build/version.dll` next to `StoneShard.exe`, or run `cmake --build build --target deploy`;
use `deploy-live` instead to stage a build while the game is running (it takes effect on the next
launch). To uninstall, delete `version.dll`. The mod writes its log, config and save backups to its
own folder and never into the game directory, and it takes a full backup of your saves before the
first cheat of each session — but it modifies live game state, so treat it as you would any cheat
tool and keep your own backups of characters you care about.
