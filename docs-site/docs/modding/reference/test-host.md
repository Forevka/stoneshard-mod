---
title: Test host
description: Drive a running game from a script over a named pipe, add your own commands, and use the Stoneshard harness to play without screenshots.
---

For automated testing, the loader can take commands over a named pipe: a script (or an agent) drives
the game and reads its state back without clicking through the overlay.

:::danger
The test host is for development only. It is off unless the game starts with the environment variable
`CORELOADER_TEST=1` or the marker file `Lodestone	esthost.enable` exists, and the log says which way it went (`test host ON` or `test host off`). The pipe,
`\\.\pipe\coreloader-<pid>`, admits only the user running the game, but anything running as that user
can then call any script and write any variable. Never set the variable or leave the marker file in place for normal play.
:::

## Turning it on

- **`CORELOADER_TEST=1`** in the game's environment.
- **A file `Lodestone\testhost.enable`** in the game folder. Steam relaunches some games through
  `steam.exe`, and the environment variable does not survive that; the file turns the host on too.
  `tools\run-game.ps1 -TestHost` writes it, and a launch without `-TestHost`, or `-Stop`, removes it.

`TestHost.Enabled` is true when either is present. The pipe's name is also written to
`Lodestone\Logs\testhost.pipe`.

## Launching and talking to the game

```powershell
tools\run-game.ps1 -Game Stoneshard -TestHost          # launches with CORELOADER_TEST=1
tools\run-game.ps1 -GameDir "D:\Games\Other" -TestHost # any other game, by folder
tools\smoke-generic.ps1 -GameDir "D:\Games\Other"      # core + Console checks for any YYC game
tools\coreloader.ps1 -Game Stoneshard status           # one command, result printed as JSON
tools\coreloader.ps1 -Game Stoneshard call scr_atr STR
. tools\coreloader.ps1 -Game Dwarf                     # or, from a script:
Invoke-CoreLoader builtin string_upper "abc"           #   returns the result, throws on failure
Wait-CoreLoader { (Invoke-CoreLoader object-count o_enemy) -gt 0 } -TimeoutSec 30
```


### `tools\run-game.ps1`

Stops a running instance of the game, optionally deploys Lodestone and mods, launches the game, and
waits for this run's log to report `mod(s) loaded`. Parameters:

| Parameter | Does |
|---|---|
| `-Game Stoneshard\|Dwarf` or `-GameDir <folder>` (`-Exe <name>` if the folder holds several exes) | Which game |
| `-TestHost` | Launch with `CORELOADER_TEST=1` and write `testhost.enable`; also waits for the `test host ON` line |
| `-Deploy`, `-Mods A,B`, `-CleanMods`, `-Configuration` | Deploy first (`tools\deploy-coreloader.ps1`); `-CleanMods` removes other mod dlls |
| `-WaitFor <regex>` | Also wait for a matching log line |
| `-TimeoutSec <n>` | Default 120; exit code 1 on timeout |
| `-Stop` | Only stop the game |

The log counts as this run's once it names the pid of a live instance of the game, so a leftover log
from the previous run, or a relaunch through Steam, cannot fool it. Mods start only once the game has
loaded its assets (Stoneshard waits about 16 s), so wait for `starting mods` or a line of your own mod.

### `tools\coreloader.ps1`

As a command, `tools\coreloader.ps1 [-Game Stoneshard|Dwarf | -GameDir <folder>] <cmd> [args...]`
prints the result as JSON and exits:

| Exit code | Meaning |
|---|---|
| 0 | The command succeeded |
| 1 | The command failed (`ok:false`); the error is printed |
| 2 | The game cannot be reached |
| 3 | The game did not answer in time; the request is dropped by the game unrun |

Arguments that read as numbers, `true`, `false` or `null` are sent as such; wrap one in double quotes
(`'"123"'`) to send it as a string. `-As current` runs a `call` or `builtin` as the instance the game
last ran, `-As <id>` as that instance. `-TimeoutSec` defaults to 30.

Dot-sourced, it defines two functions:

- `Invoke-CoreLoader <cmd> [args...]` returns the result and throws on `ok:false`. Arguments keep
  their PowerShell types.
- `Wait-CoreLoader { condition }` polls until the block returns something truthy.

`list-commands` lists every command with its help, including the ones mods register.

## Protocol

One JSON object per line each way:

```json
{"id":1,"cmd":"call","args":["scr_foo",1,"a"]}
```

is answered with `{"id":1,"ok":true,"result":...}` or `{"id":1,"ok":false,"error":"..."}`.

- **Arguments** are numbers, strings, booleans or null.
- **Results** are numbers, strings, booleans, null (undefined) and arrays. An instance or asset
  reference becomes its id as a number, which can be passed back. Any other value becomes its GML
  string.
- **Commands run on the game thread at the start of a frame**, never on the pipe's thread. Nothing
  blocks the frame: `wait-frames n` answers n frames later, and a longer wait is polled from the client
  (`Wait-CoreLoader`).
- **A request may carry `"timeout"`** (seconds, 1 to 600, default 30) next to `"as"`. A request the game
  thread has not started by a second before its timeout is dropped unrun and answered as expired, so a
  client should wait a little past the timeout it sends (`tools\coreloader.ps1` waits two seconds more).
- **Until mods have started**, `call`, `builtin`, `global-get`, `global-set`, `instance-get`,
  `instance-set` and `object-count` are refused with `the game is still loading its assets (status.modsStarted is false)`; poll `status` first.

## Built-in commands

| Command | Does |
|---|---|
| `ping`, `status`, `mods`, `log [n]` | Liveness, game and bridge state, frame count, each mod's state and fault (`status` also lists `notLoaded`: mods skipped as being for another game, or refused for not declaring one), the last n log lines |
| `reload <mod\|all>` | Reloads a mod, as the Loader tab does |
| `call <script> [args]`, `builtin <name> [args]` | Calls a script or builtin. `"as":"current"` runs it as the instance the game last ran; `"as":<instance id>` runs it as that instance (needs `Game.CanResolveInstances`) |
| `global-get <name>`, `global-set <name> <value>` | Global variables (set answers the value read back) |
| `instance-get <object> <n> <var>`, `instance-set <object> <n> <var> <value>` | Instance variables of an object's n-th live instance, or of an instance id (`<id> <var>`) |
| `object-count <object>` | Live instances, children included |
| `wait-frames [n]`, `list-commands` | Wait n frames; every command with its help, including mods' |

## Registering your own commands

A mod adds its own commands, usually only when the host is on:

```csharp
public override void OnInitialize()
{
    if (TestHost.Enabled)
        TestHost.Register("mymod.gold", args => { Globals.Set("gold", args[0].GetDouble()); return Globals.Get("gold"); },
            "mymod.gold <n>: sets the gold, answers it read back");
}
```


`TestHost.Register(string name, Func<IReadOnlyList<JsonElement>, object?> handler, string help = "")`:

- The handler gets the request's arguments as `System.Text.Json.JsonElement`s (`args[0].GetDouble()`,
  `GetString()`, `GetInt32()`), and returns null, a bool, a number, a string, an `RValue`, an
  `InstanceRef`, a collection or dictionary of those, or any object, whose public properties are
  serialised (Fast Travel's `ft.state` returns an anonymous object).
- A name that is a built-in command throws `ArgumentException`. When the host is off, `Register`
  returns without registering anything.
- **A command belongs to the mod that registered it** and goes when the mod unloads or hot-reloads. A
  hot-reloaded copy is constructed before the old one is unloaded, and its command replaces the old
  copy's.
- **A handler that throws answers `ok:false` with the message and does not fault the mod**: a test
  feeds commands bad input on purpose. A faulted mod's commands answer with its fault until it is
  reloaded.
- Do not capture a `HookCall` or `Instance` in the handler ([CL0002](analyzers.md#cl0002)).

The Console mod adds `console <line>`, ScriptSpy adds `spy.*`, and StoneshardCheats adds `cheats.*`
(see `list-commands`). The [Fast Travel walkthrough](../walkthroughs/fast-travel.md#test-host) shows a
mod's own `ft.*` commands.

## Smoke tests

| Script | Checks |
|---|---|
| `tools\smoke-generic.ps1 -GameDir <folder>` | The core commands and the Console mod's, against any YYC game. It knows no object, script or variable name; it finds them in the running game with the console's `objects`, `vars` and `find` |
| `tools\smoke-dwarf.ps1` | Core + Console in Dwarf Eats Mountain |
| `tools\smoke-stoneshard.ps1` | Every `cheats.*` command, with read-backs. Needs a loaded save; never let the game save afterwards |

Each prints `PASS`/`FAIL` per check and exits 1 on any FAIL. They need the game running with the test
host on (`tools\run-game.ps1 ... -TestHost`).

## Playing Stoneshard: the harness

For Stoneshard, the **StoneshardHarness** mod goes further than reading state: it plays the game over
the test host without screenshots. It describes what the player sees as JSON with desktop-pixel
positions, and acts through the game's own scripts and events. A real mouse click is the fallback,
used for one action only (picking things up off the ground).

```powershell
tools\run-game.ps1 -Game Stoneshard -TestHost -Deploy -Mods Console,ScriptSpy,StoneshardHarness -CleanMods
tools\stoneshard.ps1 state            # room, turn, open windows, the player's vitals, gold
tools\stoneshard.ps1 enemies          # what can be fought, nearest first
tools\stoneshard.ps1 attack 401593 20 # clicks it once per turn until it dies (or 20 turns)
tools\stoneshard.ps1 -Json objects    # the raw JSON the mod answers
```

<small>Source: [managed/Mods/StoneshardHarness/README.md](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardHarness/README.md#L10-L14)</small>

Three ways in, all the same commands:

- **`tools\stoneshard.ps1`** prints compact tables (`-Json` for the raw answer). For an action it waits
  until the game has settled and prints what happened.
- **`tools\stoneshard_harness.py`** is the same in Python: a module, and a command that prints JSON.
- **`hx.*` commands** straight through `tools\coreloader.ps1`, for example
  `tools\coreloader.ps1 -Game Stoneshard hx.state`.

The commands, in groups:

- **Looking:** `hx.state`, `hx.player`, `hx.enemies`, `hx.npcs`, `hx.objects`, `hx.inventory`,
  `hx.log`, `hx.dialogue`, `hx.buttons`, `hx.screen`.
- **Acting:** `hx.move`, `hx.goto`, `hx.attack`, `hx.interact`, `hx.use`, `hx.actions`, `hx.wait`,
  `hx.say`, `hx.press`, `hx.key`, `hx.click`, `hx.close`.

Each action answers `{seq}` at once and runs over the next frames; `hx.result [seq]` answers
`done:false` until the game has settled, then what happened (turns taken, HP, MP, gold and cell before
and after, the target's HP, new log lines, open windows). One action runs at a time; `hx.cancel` stops
tracking one.

Test on a **new Adventure character** (title, Play, New Game, Adventure). Never press Continue, which
loads the player's latest real character; and back up `%LOCALAPPDATA%\StoneShard` with
`tools\game-saves.ps1 backup -Game Stoneshard` first. Other mods add their own setup commands
(StoneshardTrials: `tr.*`).

The full command reference, what each action runs, the screen-mapping facts and the known gaps are in
the harness's own README:
[managed/Mods/StoneshardHarness/README.md](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardHarness/README.md).
