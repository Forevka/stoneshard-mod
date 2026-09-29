# CoreLoader

A mod loader for **any YYC-compiled GameMaker game**. A `version.dll` proxy finds the game's
compiled GML and the runtime's helpers by pattern, hosts .NET and loads C# mods, with a Dear ImGui
overlay (**INSERT** toggles it). Nothing is hardcoded: every function, object and asset is resolved
by name at runtime, so a patch that moves addresses around costs nothing. It is verified on
Stoneshard and Dwarf Eats Mountain, and it generates a typed interop project from the running game.

Writing mods, the API and the shipped mods: **[managed/README.md](managed/README.md)**.

CoreLoader began as a native cheat mod for Stoneshard. Those tools are now C# mods
(`StoneshardCheats`, `StoneshardBoost`, `SpeedControl`), and `version.dll` holds no game-specific code.

## Stoneshard mods

**Items** (StoneshardCheats)

- Catalogue of 1,705 items across 94 categories, read from the game's own object table and the
  weapon/armor tables embedded in the exe — no hand-maintained lists.
- Spawn any weapon or armor at any rarity (Common through Treasure). Above Common the *game* rolls
  the bonus stats, so you get genuinely enchanted gear rather than a renamed common one.
- **Constructor** — load a real item as a template, then edit every stat it has or add any of the
  83 weapon / 77 armor stats it doesn't. Negative values work, so drawbacks are available too.

**Potions** (StoneshardCheats)

Potions are the one family the catalogue cannot list, because there is no "Potion of Healing"
object in the game: there is a single potion object, and every potion is an instance of it
carrying a list of effects, with the name assembled from them at display time.

- All **34 effects**, read from the game's own localisation table.
- Tick any combination of effects and get a potion carrying exactly those, including combinations
  no roll table produces. The effect list is written inside the bottle's own event and the game's
  `scr_potion_set_param` derives the name, colour and quality from it.

**Character and body** (StoneshardCheats)

- Hunger, thirst, intoxication, immunity, fatigue and pain as live sliders; HP, MP, XP and level,
  with XP granted through the game's real level-up path.
- Every status effect (buffs and debuffs) applied by name — stun, bleeding, poison, blessing and the rest.
- Psyche: sanity, morale, panic and the rest of the psyche map.
- All six body parts with their own 0–100 condition, heal and damage buttons, and the active wounds.

**Enemies** (StoneshardCheats)

Every enemy is an instance of one object, `o_enemy`; what makes a wolf a wolf is the variables it
carries. The roster is a walk over live instances, nearest first, with name, race, HP, level and
distance, plus each enemy's variables. **Remove** destroys an instance (its drop still happens).
Killing or wounding through HP was dropped on purpose: the game's own setter did not take, and
writing the variable directly moved the number without the game reacting.

**Saves** (StoneshardCheats)

Import characters from another machine into free slots. Every save file is signed with an MD5
salted by its own folder path, so each file is re-signed for its new slot rather than copied.
The save folder is backed up once per session before the first cheat.

**World**

- **Loot multiplier** and **XP multiplier** (StoneshardBoost).
- **Game speed** (SpeedControl, works in any game).

## Build and install

Requires Windows, CMake 3.21+, a C++17 MSVC toolchain, the .NET 10 SDK and Git (ImGui and MinHook
are fetched automatically).

```powershell
cmake -B build ; cmake --build build --config Release        # build\version.dll
dotnet build managed\CoreLoader.sln -c Release                 # runtime and mods
tools\deploy-coreloader.ps1 -GameDir "<game folder>" -Mods Console,StoneshardCheats
```

To uninstall, delete `version.dll` and the `CoreLoader` folder from the game directory. The cheat
mods modify live game state: keep your own backups of characters you care about.

## Credits

Two existing projects saved this one a great deal of guesswork, and each is worth using in its own
right:

- **[stoneshard-editor](https://github.com/MikaBuchholz/stoneshard-editor)** by MikaBuchholz
  ([live version](https://mikabuchholz.github.io/stoneshard-editor/)) — a browser-based save editor
  that runs entirely locally. Its `app/src/codec/save.ts` is where the save checksum scheme came
  from: `md5(json + "stOne!" + <folder path> + "!shArd")`. Knowing the salt embeds the folder path
  is the reason the save importer re-signs files instead of just copying them. The scheme is
  re-verified against live save files by `tools/checkcksum.py` rather than taken on trust.
- **[ModShardLauncher](https://github.com/ModShardTeam/ModShardLauncher)** by ModShardTeam — the
  established Stoneshard modding framework, which patches `data.win` statically via UndertaleModLib.
  Its loot module supplied several vanilla call signatures this project had wrong or unknown, most
  importantly that argument 4 of `scr_weapon_loot` is the item's **rarity** and that the function
  returns the instance it creates. MSL and CoreLoader are not alternatives to each other: MSL edits
  GML in `data.win`, which requires a build that ships a `CODE` chunk, whereas CoreLoader hooks the
  compiled functions of a YYC build at runtime.

Bundled at build time: [Dear ImGui](https://github.com/ocornut/imgui) (MIT) and
[MinHook](https://github.com/TsudaKageyu/minhook) (BSD-2-Clause). Stoneshard is a game by
[Ink Stains Games](https://store.steampowered.com/app/625960/Stoneshard/); this project is
unaffiliated with them.

## Licence

MIT — see [LICENSE](LICENSE). The bundled dependencies keep their own permissive licences, both
MIT-compatible; they are fetched at build time rather than vendored, so no third-party source is
redistributed here.
