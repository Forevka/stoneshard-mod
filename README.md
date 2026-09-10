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

## Credits

Two existing projects saved this one a great deal of guesswork, and each is worth using in its own
right:

- **[stoneshard-editor](https://github.com/MikaBuchholz/stoneshard-editor)** by MikaBuchholz
  ([live version](https://mikabuchholz.github.io/stoneshard-editor/)) — a browser-based save editor
  that runs entirely locally. Its `app/src/codec/save.ts` is where the save checksum scheme came
  from: `md5(json + "stOne!" + <folder path> + "!shArd")`. Knowing the salt embeds the folder path
  is the reason the save importer here re-signs files instead of just copying them. The scheme is
  re-verified against live save files by `tools/checkcksum.py` rather than taken on trust.
- **[ModShardLauncher](https://github.com/ModShardTeam/ModShardLauncher)** by ModShardTeam — the
  established Stoneshard modding framework, which patches `data.win` statically via UndertaleModLib.
  Its loot module supplied several vanilla call signatures this mod had wrong or unknown, most
  importantly that argument 4 of `scr_weapon_loot` is the item's **rarity** and that the function
  returns the instance it creates. It also pointed at the real loot hook sites (`o_unit_Destroy_0`,
  `c_container_Other_10`, `o_chest_p_Alarm_1`). Note that MSL and this mod are not alternatives to
  each other: MSL edits GML in `data.win`, which requires a build that ships a `CODE` chunk, whereas
  this hooks the compiled functions of a YYC build at runtime.

Bundled at build time: [Dear ImGui](https://github.com/ocornut/imgui) (MIT),
[MinHook](https://github.com/TsudaKageyu/minhook) (BSD-2-Clause) and
[zlib](https://github.com/madler/zlib) (zlib licence). Stoneshard is a game by
[Ink Stains Games](https://store.steampowered.com/app/625960/Stoneshard/); this project is
unaffiliated with them.

## Licence

MIT — see [LICENSE](LICENSE). The three bundled dependencies keep their own permissive licences,
all of which are MIT-compatible; they are fetched at build time rather than vendored, so no
third-party source is redistributed here.
