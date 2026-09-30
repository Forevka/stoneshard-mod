# Installing Lodestone and mods

Lodestone is a mod loader for GameMaker games, Stoneshard among them. You install it once per game,
then add the mods you want. It changes no game files, and removing it puts the game back as it was.

Windows, 64-bit only. You do not need to install .NET or anything else: Lodestone brings what it needs.

## 1. Download Lodestone

Open the [releases page](https://github.com/Forevka/stoneshard-mod/releases) and, under the newest
release, download **`Lodestone-<version>-win64.zip`**.

## 2. Find the game folder

This is the folder that holds the game's `.exe` (for Stoneshard, `StoneShard.exe`).

- **Steam:** right-click the game in your library, then **Manage → Browse local files**.
- **GOG Galaxy:** click the settings icon next to Play, then **Manage installation → Show folder**.
- **Anything else:** wherever you installed or unpacked the game.

## 3. Install Lodestone

Extract **everything** in the zip into the game folder. The easiest way is to open the zip, select
everything inside it, and drag it into the game folder.

Afterwards the game folder should look like this:

```
StoneShard.exe
version.dll        <- from the zip
Lodestone\         <- from the zip
...the game's own files
```

`version.dll` must sit **right next to the game's .exe**, not in a subfolder. Nearly every "it does
nothing" report comes down to this.

> If the game folder already has a `version.dll` (usually another mod loader), copy it somewhere safe
> first: Lodestone's replaces it.

## 4. Check that it works

Start the game as usual. Once it is running, press **INSERT**. The Lodestone window should appear
over the game. Press INSERT again to hide it.

A `Mods` folder now sits next to the game's `.exe`: Lodestone creates it on the first launch.

## 5. Add mods

Download a mod's zip from the same releases page, for example `StoneshardCheats-<version>.zip`.
Use mods from the **same release** as your Lodestone.

Each mod zip holds a `Mods` folder. Extract the zip into the game folder, just as you did with
Lodestone, and let Windows merge the `Mods` folders. Or drag the **contents** of the zip's `Mods`
folder into the game's `Mods` folder. Either way, you should end up with something like:

```
Mods\
  StoneshardCheats.dll
  FastTravel.dll
  StoneShard.Interop.dll    <- some mods bring this along; keep it
  Reliquary.dll
  Reliquary\                <- some mods bring their own pictures and sounds; keep the folder
```

If the game is running, a new mod loads within a moment, with no restart. Each mod gets its own tab
in the Lodestone window (INSERT), where its settings live. Settings are saved in `Mods\<ModName>.json`.

### Which mods?

**Stoneshard**

| Mod | What it does |
|---|---|
| StoneshardCheats | Spawn any weapon or armor at any rarity, build potions from chosen effects, edit stats, needs, conditions, psyche and body parts, and manage enemies. Your save is backed up before the first cheat. |
| StoneshardBoost | XP and loot multipliers. |
| FastTravel | Fast travel from the world map: open the map (M), press F, and click land you have visited. |
| Reliquary | Twenty-two relics that give a lot and ask for something back. |

**Any game**

| Mod | What it does |
|---|---|
| SpeedControl | Run the game faster or slower. |
| Console | A command console for poking at the game. Mostly for tinkerers and mod makers. |
| ScriptSpy | Watch the game's functions as they run. For mod makers. |

**Dwarf Eats Mountain**

| Mod | What it does |
|---|---|
| DwarfBoost | Gold income and unit damage multipliers, and a resource editor. |

A mod made for one game does nothing in another, so an extra mod is harmless.

## Updating

- **A mod:** replace its files in `Mods` with the new ones. If the game is running, the mod reloads
  on its own.
- **Lodestone:** close the game, then extract the new zip over the old files and let it overwrite
  them. Update your mods to the same release at the same time.

## Uninstalling

- **One mod:** delete its `.dll` from `Mods`, plus its folder if it has one.
- **Everything:** delete `version.dll`, the `Lodestone` folder and the `Mods` folder from the game
  folder. If you saved another `version.dll` in step 3, put it back.

## Troubleshooting

**Nothing happens when I press INSERT.**
Check that `version.dll` is right next to the game's `.exe`, and that the `Lodestone` folder is
beside it. Some laptops need **Fn + INSERT**.

**My antivirus deleted or flagged `version.dll`.**
Mod loaders work by being loaded by the game in place of a Windows file with the same name, which is
exactly what antivirus programs are suspicious of. Restore the file and add an exception for the
game folder, or check the file on [VirusTotal](https://www.virustotal.com) first if you prefer.

**A mod's tab shows an error, or the mod stopped working.**
A mod that runs into a problem is switched off on its own, and the game carries on. Updating the
game can break a mod until the mod is updated too.

**Reporting a problem.**
Attach `Lodestone\Logs\lodestone.log` from the game folder. If the game crashed, attach
`lodestone.prev.log` too: that is the log from the run before.

**Keep backups of saves you care about.** Cheat mods change live game state. StoneshardCheats backs
up your save before the first cheat, into `Lodestone\save-backups`, but a copy of your own does no harm.
