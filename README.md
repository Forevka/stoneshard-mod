# Lodestone (CoreLoader)

**Lodestone** is the player-facing name of CoreLoader: releases, the install folder
(`<game>\Lodestone\`), the log and the overlay carry it. The code, the `CoreLoader.dll` assembly and
the mod API keep the CoreLoader name.

**Playing?** [INSTALL.md](INSTALL.md) walks through installing Lodestone and mods from a release.

A mod loader for **any YYC-compiled GameMaker game**. A `version.dll` proxy finds the game's
compiled GML and the runtime's helpers by pattern, hosts .NET and loads C# mods, with a Dear ImGui
overlay (**INSERT** toggles it). Nothing is hardcoded: every function, object and asset is resolved
by name at runtime, so a patch that moves addresses around costs nothing. It works on six YYC games
so far (see [Tested on](#tested-on)), and it generates a typed interop project from the running game.

Writing mods, the API and the shipped mods: **[managed/README.md](managed/README.md)**.

## Tested on

CoreLoader works only with **YYC** builds, where the game's GML is compiled to native code in the
exe. A **VM** build keeps its GML as bytecode in `data.win` (a non-empty `CODE` chunk), so there is
nothing for the loader to find. In a VM game the loader stands down: it says so in its log and the
game runs as usual. Mods still load, but they cannot touch the game.

| Game | Store | Build | GML functions | Loader | Console mod | Notes |
|---|---|---|---|---|---|---|
| Stoneshard (Early Access) | Steam | YYC, older runtime | 34,167 | ✅ | ✅ | The main target. The Stoneshard mods and the full `smoke-stoneshard.ps1` suite run here |
| Dwarf Eats Mountain Demo | Steam | YYC, runtime 2024.14 | 4,968 | ✅ | ✅ | The second reference game, on the newer runtime |
| The Spike Cross | Steam | YYC | 48,206 | ✅ | ✅ | Steam relaunches it (see below) |
| The King is Watching 1.3.6 | GOG | YYC | 14,511 | ✅ | ✅ | Rebuilds its swap chain at startup; fixed in the overlay |
| Zero Stress King | GOG | YYC | 1,833 | ✅ | ✅ | |
| Slime Trader | Steam | YYC | 1,478 | ✅ | ✅ | Steam relaunches it (see below) |
| Void War | GOG | **VM** | 0 | ⛔ not supported | Loads, no game access | Detected as "not a YYC game"; the game runs unaffected |

"✅" means the loader starts, proves its GML bridge by a live call, and generates interop. The overlay
draws, and `tools\smoke-generic.ps1` passes all its checks (29) in that game. It checks builtin and
script calls, globals, instance variables, and object and event lookup. It also checks the Console
mod's expressions and commands, hooking and unhooking a live event, and that game errors are
reported without disabling a mod. Tested with CoreLoader 0.4.0 plus the unreleased changes, on
2026-09-29.

To try another game, install CoreLoader with the Console mod and run the generic smoke test:

```powershell
tools\run-game.ps1 -GameDir "<game folder>" -Deploy -Mods Console -CleanMods -TestHost
tools\smoke-generic.ps1 -GameDir "<game folder>"
```

Steam relaunches some games through `steam.exe`, which drops the `CORELOADER_TEST` variable.
`-TestHost` also writes a `Lodestone\testhost.enable` marker, which covers those games.

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

The game locks `version.dll` and the runtime while it runs. `-Live` installs anyway: each locked
file is renamed to `*.old` (or `*.<timestamp>.old` while an earlier one is still locked) and the new
one takes its place, to be picked up on the next launch. Leftover `*.old` files are deleted on the
next deploy that can. The runtime is copied before `version.dll`, so a failed deploy never pairs a
new `version.dll` with an old runtime; without `-Live`, a running game stops the deploy before it
copies anything. `-Live` only defers the native and runtime files: mods copied into `Mods\` are
hot-reloaded at once by the runtime already running, whatever its version.
The loader writes its log to `<game>\Lodestone\Logs`; set `CORELOADER_DATA_DIR` to move it.
An install from before the rename (a `CoreLoader\` folder) is moved to `Lodestone\` by the next
deploy, generated interop and logs included.

### Releases

`.github/workflows/release.yml` builds everything on a pushed tag `v<Version>` (it must match
`<Version>` in `managed/CoreLoader/CoreLoader.csproj`) and creates a **draft** GitHub release with
`tools\package-release.ps1 -BundleRuntime`:

- `Lodestone-<version>-win64.zip`: `version.dll`, `Lodestone\` with a private .NET runtime, and a
  README.txt for players. It extracts straight into the game folder.
- `<Mod>-<version>.zip` for each mod: `Mods\<Mod>.dll`, its dependencies and its content.

CI has no game, so mods written against a generated interop (FastTravel, Reliquary) are skipped
there. Add them from a machine with the game, then publish the draft:

```powershell
dotnet build managed\CoreLoader.sln -c Release
tools\package-release.ps1 -Mods FastTravel,Reliquary -NoLoader -Upload v<version>
```

`tools\re\` holds the static reverse-engineering scripts used to study a game's exe offline
(xref indexes, callers and callees, `data.win` assets, the builtin table, a Ghidra export). Point
them at a game with `--exe "<game>\Game.exe"` or the `RE_GAME_EXE` variable; they need Python with
`numpy` and `capstone`. Their derived caches (xref indexes, the script, object and builtin tables) go
to `tools\re\cache\<exe name>\`, one folder per game, or to `RELIB_CACHE` if it is set.

To uninstall, delete `version.dll` and the `Lodestone` folder from the game directory. The cheat
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
