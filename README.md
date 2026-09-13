# Stoneshard Mod

A native debugging and cheat mod for [Stoneshard](https://store.steampowered.com/app/625960/Stoneshard/),
loaded as a `version.dll` proxy and driven from a Dear ImGui overlay (**INSERT** toggles it).
Nothing is hardcoded — every function, object and asset is resolved by name at runtime against the
34,167 compiled scripts and ~2,530 GameMaker builtins in the game, so a patch that moves addresses
around costs nothing.

## Features

**Items**

- Catalogue of 1,705 items across 94 categories, read from the game's own object table and the
  weapon/armor tables embedded in the exe — no hand-maintained lists.
- Spawn any weapon or armor at any rarity (Common through Treasure). Above Common the *game* rolls
  the bonus stats, so you get genuinely enchanted gear rather than a renamed common one.
- Drop at your feet or place straight into your inventory.
- **Constructor** — load a real item as a template, then edit every stat it has or add any of the
  83 weapon / 77 armor stats it doesn't. Negative values work, so drawbacks are available too.
  Edits survive pickup, equipping and saving.

**Potions**

Potions are the one family the catalogue cannot list, because there is no "Potion of Healing"
object in the game: there is a single potion object, and every potion is an instance of it
carrying a list of effects, with the name assembled from them at display time.

- All **34 effects** — Healing, Restoration, Stoneskin, Antivenom, Rage and the rest — read from
  the game's own localisation table rather than typed out here.
- **Constructor** — tick any combination of effects and get a potion carrying exactly those,
  including combinations no roll table produces. The effect list is written directly and the
  game's own `scr_potion_set_param` derives the name, colour and quality from it, so the result
  is a potion the game built rather than an imitation of one.

**Character**

- Hunger, thirst, intoxication, immunity, fatigue and pain as live sliders.
- HP, MP, XP and level; XP can be granted through the game's real level-up path.
- All **323 status effects** (169 debuffs, 154 buffs) applied by name — stun, bleeding, poison,
  blessing, drunk, coma and the rest.
- Psyche system: sanity, morale, panic, frenzy, paranoia, anxiety, catharsis and more.
- **Body parts** — all six limbs with their own 0–100 condition, a heal button each, and a damage
  button for testing. Active wounds are listed alongside (they're separate from condition).

**World**

- **Loot multiplier**, 0.25x to 7x, over both enemy drops and dungeon containers, with live counters
  showing it actually firing.
- **Game speed**, 0.25x to 8x, so crossing a map isn't spent watching walk animations. Re-applies
  itself when the game resets it.
- **Save importer** — pick a save folder from another machine, see which characters it holds, and
  import them into free slots. Checksums are re-signed for their new paths, which plain copying
  can't do.

**Developer tools**

- Console over all 34,167 game scripts, with symbol search.
- Script tracer (~9,260 functions through a single hook), instance inspector, and breakpoints that
  report arguments, instance context and a resolved call stack without halting the game.
- **Argument-rewriting hooks** — intercept any script, watch the arguments real calls pass, then
  change one or run the call more or fewer times. This is what the loot multiplier is built on.
- Headless control: write a line to `debug-cmd.txt` and drive any of the above from a script.
- Automatic save backup before the first cheat of each session.

## Build and install

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
