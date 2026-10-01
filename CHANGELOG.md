# Changelog

All notable changes to CoreLoader are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is 0.x, a minor
bump may break the mod API or the native CoreApi table; each version says so under **Changed**.

## [Unreleased]

### Changed
- **Breaking:** **the loader is now called Lodestone for players.** It installs to `<game>\Lodestone\` (was
  `CoreLoader\`), logs to `Lodestone\Logs\lodestone.log`, and its overlay window, log lines and the
  shipped mods' author read "Lodestone". The assembly, namespace, API, template and `CORELOADER_*`
  variables keep the CoreLoader name. `tools\deploy-coreloader.ps1` moves an existing `CoreLoader\`
  folder to `Lodestone\`, and interop-based projects now look for `<game>\Lodestone\Interop\`.
  A mod made from the template before this has `<CoreLoaderDir>$(GameDir)\CoreLoader</CoreLoaderDir>`
  in its csproj: change it to `$(GameDir)\Lodestone`, or it no longer finds CoreLoader.dll.

### Added
- Release packaging: `tools\package-release.ps1` packs the loader (optionally with a private .NET
  runtime) and one zip per mod, each extracting straight into a game folder, and can upload them to a
  GitHub release. `.github/workflows/release.yml` builds and tests everything on a `v*` tag and
  creates a draft release; interop-based mods are added from a machine with the game.
- `INSTALL.md`: a player's guide to installing Lodestone and mods from a release, updating,
  uninstalling and troubleshooting.
- **Reliquary** (Stoneshard): six artifacts that ask for something back, one per family of the
  Stoneshard Reliquary design - Stavebound Ember, Gorgoneion, Wolf's Heart, Copper Ring of Faith,
  Grafted Hand of the Hanged Man, Pilgrim's Millstone. Written only against the generated interop.
  Relics are vanilla carrier items tagged in their saved `data` map, with placeholder icons; they work
  from the bag (the ring when worn), show a live status line in the game's own tooltip, report in the
  game's action log, and activate by hovering one and pressing a key (`activateKey`, default U).
  Test-host commands `reliq.*`.
  - The other seventeen: Faceless Mirror, Split Quiver, Cinder Rosary, Echoing Bell, Debtor's Knot,
    Weeping Candle, Pallbearer's Coin, Vessel of Borrowed Years, Reliquary of Saint Mardun, Usurer's
    Scale, Sundered Gate, Censer of the Drowned Choir, Lodestone Idol, Surveyor's Chain, Iron Lung,
    Oath-Stone of the Deep Road, Sated Worm. Relics can install their own hooks (`Relic.Install`),
    on scripts or object events.
  - Pallbearer's Coin and the Vessel of Borrowed Years save you from bleeding out too, not only from
    killing hits (`Relic.OnPlayerDying`, run as `scr_pure_damage` returns).
  - The Censer of the Drowned Choir is not registered for now: neither of the game's statuses it used
    stops abilities, and one of them crashed the game when it expired.
- **FastTravel** (Stoneshard): fast travel from the world map. The map's own controls bar gains a
  "[F] - Fast Travel" entry (click it, or press `toggleKey`); while the mode is on a banner in the
  game's style says what a click on the hovered cell would do, and clicking land you have visited, or
  a cell next to one, travels there the way a border crossing does. A blocked arrival is moved to the
  nearest free cell joined to the room's edges; travel starts only from the open world (not inside a building or dungeon) and is refused with enemies nearby. Test-host commands `ft.*`.
- **StoneshardTrials** (Stoneshard, proof of concept): a roguelike loop in the spirit of BG3's
  *Trials of Tav*. A new Adventure's Osbrook tavern is the hub, with a banner saying to leave it. Its
  street door opens onto a random crypt, catacombs or bastion whose boss still lives (tier 1 at first,
  one tier more every two trials), entered the way the dungeon's own entrance enters it. Killing the
  boss or named miniboss puts a Trial Ticket in the bag, and using it returns you to the tavern for
  the next trial. Settings `enabled`, `level`; test-host commands `tr.*`.
- **TavernGames** (Stoneshard): dice and card games against tavern NPCs, for crowns. Talk to an
  innkeeper, a drunk, a sellsword or anyone friendly inside a tavern and their conversation offers a
  line asking for a game ("Fancy a game? Dice, or cards?") just above the goodbye; it opens a table drawn with the game's own
  board, buttons, colour text and sounds (a play key, `playKey`, can be set as well; off by default).
  Pick **Poker Dice** (five dice, a round of betting, one reroll) or **Twenty-One** (the NPC deals from
  a deck on the table - cards slide out and turn over - and draws to a total set by its temperament)
  or **Thimblerig** (find the ball under the shuffled cups: a five-level ladder, each won level offering
  the next with more cups - three up to six - more and faster swaps, two pairs at once near the top,
  and a stake of x1, x2, x3, x5 then x8), **Arm Wrestling** (a tug-of-war won with a timing check: press
  Space while the cursor is in a zone as wide as your STR against theirs; it costs real Fatigue) or a
  **Drinking Contest** (mug for mug until someone falls: you down each by stopping a marker that sways
  more the drunker your character really is; every mug is the game's own Drunkenness, with its
  confusion, vomiting and sleep, and deep in it you may pass out on the spot) and a stake. Opponents are cautious, steady or reckless by trade, and their
  purses run dry and refill over turns. The stake is held by the table from the moment it is committed,
  so walking away folds and a loss is never dodged; a round dropped by a room change or hot reload
  hands it back. Games plug into a small framework (`Table`, `MiniGame`, `Session`); a game can offer ways to carry on
  after a round at a stake of its own (`MiniGame.Continuations`). Test-host commands
  `tg.*`.
- **Interop:** an `InstanceVars` class with GameMaker's built-in instance variables (`x`, `y`, `id`,
  `object_index`, `sprite_index`, ...), which the variable harvest never sees; every object's `Vars`
  class repeats them (objects never seen live have no `Vars`; use `InstanceVars`). The interop stamp now carries a format number, so installed interops
  regenerate when the generated code changes shape.
- **Analyzers:** instance fields of a `ref struct` no longer raise CL0001/CL0002 - it cannot outlive the call that made it. Static fields of one still do.
- `ScriptRef.CallAs(InstanceRef self, args)`: run a stub script as an instance held by id.
- **ScriptSpy** over the test host: `spy.watch <function> [variable]`, `spy.read`, `spy.clear`,
  `spy.unwatch`. Rows name the object the call ran as, and can track a variable of self before and
  after the call (which script actually changes HP, say).
- `deploy-coreloader.ps1` also installs a mod's project dependencies from its `deps.json`, such as a
  generated `<Game>.Interop.dll`.
- **UI round 3:** `UI.BeginCombo`/`EndCombo`, `UI.Combo`, `UI.Selectable` (optionally overlap-friendly),
  `UI.SeparatorText`, `UI.InputDouble`/`InputFloat`, `UI.SliderInt`, `UI.SetNextItemWidth`,
  `UI.BeginDisabled`/`EndDisabled`, `UI.InputTextWithHint`, `UI.SameLine(offsetX, spacing)`,
  `UI.TextWrapped`, `UI.Spacing`, sized `UI.Button`, `UI.SmallButton`, `UI.ProgressBar`,
  `UI.PushTextColor`/`PopTextColor`, `UI.Tooltip`, `UI.Clipped` for long lists, and
  `UI.ItemDeactivatedAfterEdit` for committing an edit once, when the user lets go of the widget.
  `UI.ProgressBar` fills the row at width 0 and leaves room on the right at a negative width.
  - Combos, disabled blocks, text colours and clippers are tracked scopes: a mod that throws inside
    one is unwound like any other scope.
- `WidgetProbe` test mod.
- **`DsMap` / `DsList`:** ds_map and ds_list access by id through the game's own builtins (`Exists`,
  `Count`, `Get`/`Set`/`Has`/`Remove`, `Entries()`, `ToJson()`; `At`, `Add`, `Insert`, `RemoveAt`,
  `Clear`, `Items()`).
- **`Hooks.NextBefore` / `Hooks.NextAfter`:** run code once, inside the next call of a script or event
  that a predicate accepts, with a timeout checked every frame and an optional timeout handler. The
  request belongs to the calling mod; `Dispose()` cancels it. The timeout handler always runs between
  frames, never inside the game's hooked call.
- **`ObjectTable`:** the object table (index, name, parent), built once over frames within a small
  time budget (`Start`, `Ready`, `Progress`, `Status`, `Complete`). `GmlObject.All()` now uses it, so
  only the first call walks the asset indices.
  - `GmlObject.Parent`, `Ancestors()`, `IsA(name)`, `Children()` and `GmlObject.FromIndex(index)`.
- **GML error messages.** A script, event or builtin the game rejects now fails with the runtime's own
  error text: `GmlException` reads `call to X failed: <message> (in <script>, line N)` instead of
  "see the loader log", and the log names the script and the message. The loader decodes the C++
  exception's RTTI (`YYGMLException` in both known runtimes) and reads the thrown error struct's
  `message`, `script` and `line`; other exceptions are reported by type or code. CoreApi
  `last_gml_error`.
- **Instances by id.** `InstanceRef.Resolve()` gives the live `Instance` for an id or instance
  reference, `Game.CallScriptAs(InstanceRef, name, args)` and `InstanceRef.CallScript` run a script as
  it, and `Game.CanResolveInstances` says whether this runtime supports it. The runtime's id table is
  found by pattern and proven on the live game (the current instance's own id must resolve to it, and
  bogus ids to nothing); until then, or if the proof fails, `Resolve` returns null. CoreApi
  `instance_from_id`.
- **`StoneshardCheats` mod:** the native Stoneshard cheat tabs, ported to C#. Tabs: Stats, Items, Potions,
  Character, Body, Enemies and Saves.
  - Character scripts run as the player. The player instance comes from a hook on `o_player`'s Step event,
    and is dropped half a second after that event stops running.
  - Potions are rewritten inside `o_inv_bottle`'s Alarm 0, the only place the potion scripts work. A build
    that sees no bottle within 2 s gives up, rather than rewriting the next bottle the game makes.
  - The item catalogue is read from the live object table and from the game exe's own CSV rows.
  - The save folder is backed up once per session, to `<game>\Lodestone\save-backups`, before the first cheat.
- **Mod analyzer (`CoreLoader.Analyzers`):** compile-time warnings for value-lifetime mistakes. CL0001 is
  an `RValue` kept in a field or auto-property. CL0002 is an `Instance` or `HookCall` kept in one.
  A lambda that is stored, queued or registered as a callback (`Hooks.Before`/`After`/`NextBefore`/
  `NextAfter`, `TestHost.Register`, `GameDraw.OnGui`, `Game.RunOnGameThread`) and captures an `RValue`
  (CL0001) or an `Instance` or `HookCall` (CL0002) is reported the same way. CL0003 is `Values.Free` on
  a hook argument or result.
  - Repo mods, tests and examples get it through their `Directory.Build.props`. `deploy-coreloader.ps1`
    installs it to `<game>\Lodestone\Analyzers\`, where template mods pick it up. The game never loads it.
- `tools\game-saves.ps1`: backs up, restores and hash-verifies a game's save folder around a test run.
  Backups go to `.omc\save-backups` in the repo (or `CORELOADER_BACKUP_ROOT`). Restore puts each
  backed-up character folder back exactly, and refuses (unless `-Force`) a folder without the backup
  marker, or one with no character folders while the save folder has some.
- `tools\run-game.ps1`: restarts a test game (optionally deploying first) and waits for its mods to load
  and for an optional log pattern.
- **Test host (development only):** with `CORELOADER_TEST=1`, the loader serves a named pipe
  (`coreloader-<pid>`, current user only; the name is written to `Lodestone\Logs\testhost.pipe`) that
  takes line-delimited JSON commands and runs them on the game thread. It replaces the removed remote
  command file.
  - Clients connecting over the network are refused (.NET does not create the pipe with
    `PIPE_REJECT_REMOTE_CLIENTS`, so each client is checked on connect). Request lines are capped at 1M
    characters.
  - A request not started by a second before its `timeout` is dropped unrun; the client waits two
    seconds past the timeout, so it never reports a command that ran as dropped.
  - Results show an `Instance` as its hex pointer and a `GmlObject`'s parent by name.
  - `TestHost.Register` does nothing when the host is off.
  - Built-in commands: `ping`, `status`, `log`, `mods`, `reload`, `call`, `builtin`, `global-get`/`-set`,
    `instance-get`/`-set`, `object-count`, `wait-frames`, `list-commands`.
  - `TestHost.Register(name, handler, help)` lets a mod add commands; they belong to the mod and go when it
    unloads. A handler that throws answers `ok:false` and does not fault the mod. `TestHost.Enabled` says
    whether the session runs the host.
  - The Console mod registers `console <line>`. StoneshardCheats registers `cheats.*`, one command per cheat,
    each calling the same method as its button and answering with the value read back.
  - `tools\coreloader.ps1` is the client (CLI, or dot-sourced for `Invoke-CoreLoader` / `Wait-CoreLoader`).
    `tools\smoke-stoneshard.ps1` and `tools\smoke-dwarf.ps1` check the commands against a running game.
    `tools\run-game.ps1 -TestHost` starts the game with the host on.
  - A `Lodestone\testhost.enable` file also turns the host on, for games Steam relaunches through
    `steam.exe` (the variable does not survive that). `run-game.ps1 -TestHost` writes it; a launch without
    `-TestHost`, or `-Stop`, removes it.
  - `tools\smoke-generic.ps1 -GameDir <game>` checks the core commands and the Console mod against any YYC
    game. It knows no names from any game and finds its objects, variables and events in the running one.
- **Tested on four more YYC games:** Zero Stress King, The King is Watching, The Spike Cross and Slime
  Trader. The generic smoke test passes in all four.
- **"Tested on" section in `README.md`.** It lists each game tested so far, with its store, build
  type (YYC or VM), GML function count, and whether the loader and the Console mod work. It also says
  how to test another game.

### Changed
- **Breaking:** CoreApi version 10 (UI round 3, `last_gml_error`, `instance_from_id`). The managed
  runtime requires this exact version.
- The overlay's top-level tabs are Mods, Symbols and Status. Symbols and Status used to sit under Debug.
- `StoneshardCheats` uses `DsMap`/`DsList`, `Hooks.NextAfter` and `ObjectTable` instead of its own copies.
  The potion hook is only installed while a build is armed.
- StoneshardCheats: an action's result lines now appear under its echo in the panel.
- Native GML calls with no explicit self use the current self only. The self captured by the remote
  command file is gone.
- The data-dir environment variable is `CORELOADER_DATA_DIR`. `SSMOD_DATA_DIR` is still read as a
  fallback for this release.
- The native ABI self-test is the string round-trip in every game; the Stoneshard script probes are gone.
  The builtins self-test logs "self-test", not "phase A".
- The CMake project and target are `coreloader`. The output is still `build\version.dll`.
- `tools/re` takes the game exe from `--exe` or `RE_GAME_EXE` (then `STONESHARD_DIR`) instead of
  assuming Stoneshard. Its caches go to `tools/re/cache/<exe name>/` (or `RELIB_CACHE`), so one game's
  tables are never read for another.
- **Console:** freezes live in one shared list (globals and any number of instances) and survive a
  change of the Inspector's selection. New Objects tab (object browser with live counts, instance
  paging, and a search of every live instance for a variable name) and Globals tab (edit and freeze),
  and the `where`, `frozen` and `unfreeze all` commands.
- The interop generator and the variable harvest share the cached `ObjectTable`: one object scan per
  session, spread over frames. The table starts as soon as the runtime asks for it once the game's
  assets are loaded, even before mods start, and interop waits for it without spending its wait while
  the table cannot progress. The scan is only finished in one frame as a fallback (the table still not
  done after ~10 s of work, or a minute overall), or when a mod calls `GmlObject.All`/`Children` or
  `ObjectTable.Complete` before it is ready.
- **Console:** `where` answers "object table not ready" until the table is built, instead of scanning
  in one frame. The Objects tab drops its selection when a pick or `inspect` selects another instance.
  Selecting an instance resets the variable filter unless a search set it. A frozen global the game
  removed is listed as inactive. A variable search skips instances and variables the game refuses to
  read.
- StoneshardCheats resolves the player by id where the runtime's lookup is proven, so cheats keep
  working while the game is paused; the o_player Step hook remains the fallback.
- `deploy-coreloader.ps1 -Live` installs into a running game: locked files are renamed to `*.old` (a
  timestamped `*.old` while an earlier one is still locked), and the new build is used from the next
  launch. Mods it copies are hot-reloaded at once, as before. The runtime is installed before
  `version.dll`, and without `-Live` a running game stops the deploy before anything is copied.

### Removed
- **The native Stoneshard tools.** `version.dll` no longer contains any game-specific code.
  - The Cheats, Enemies, Loot, Speed, Saves and Console tabs are replaced by the `StoneshardCheats`,
    `StoneshardBoost` and `SpeedControl` mods, and by the C# Console mod and its Inspector.
  - The script tracer, the breakpoints, the argument-rewriting hooks panel, and the native inspector are
    removed without a replacement.
  - The remote command file (`CORELOADER_REMOTE`, `debug-cmd.txt`) is removed.
  - The player tracker, the weapon recorder, and self-test phase B (which needed the player) are removed.
  - zlib is no longer a build dependency.
- The `InstanceInspector` and `GlobalsEditor` mods: their features are in the Console mod.
- The `ReflectionProbe`, `VarProbeMod` and `CoexistProbe` test mods.
- The CMake `deploy` and `deploy-live` targets, the `STONESHARD_DIR` option and `tools/deploy.ps1`
  (replaced by `deploy-coreloader.ps1 -Live`).
- Dead native code: the native hook-subscriber API, `gml::Call`/`CallByName`/`IsEventSymbol`,
  `sym::FindScript` and `builtins::SelfTestPassed`.
- `tools/savepeek.py` and 20 one-off or duplicate reverse-engineering scripts in `tools/re`.

### Fixed
- **Games that rebuild their swap chain no longer fail to start.** The overlay kept its back-buffer view
  across frames, which held the game's first swap chain alive after the game released it. DXGI then
  refused the replacement for the same window (`CreateSwapChain ... E_ACCESSDENIED`), and The King is
  Watching stopped at an error box. The view now lives for one frame only. If the new swap chain is on a
  new device, the overlay moves to that device.
- **VM-compiled games are recognised.** When no YYC code is found and `data.win` holds bytecode, the
  loader says the game is not YYC, instead of suggesting that the table shape changed. Mods start at
  once instead of after the 30 s asset wait. Interop generation and game drawing stay off instead of
  logging failures. The game itself runs as before.
- **StoneshardCheats, Items → "To inventory"** works. It never did, natively either: `scr_inventory_add_weapon`
  takes the inventory as its self, not the player (the game calls it inside `with (o_inventory)`). It now
  runs as the `o_inventory` instance itself (not a child object's instance), with the player as other,
  as `with (o_inventory)` does. The recovered GML error ("invalid with reference" in
  `scr_inventory_get_containers`) is what pointed at it.
- A GML call that failed with a thrown error no longer leaks the thrown value: the guard that catches it
  skips its destructor, so the loader releases it once after reading the message.
- The test host's `cheats.potion` answered the wrong build number when the bottle's alarm ran during the
  give itself, so a client waiting for the result timed out.

## [0.4.0] - 2026-09-29

### Added
- **Runtime content:** `Content.AddSprite` / `ReplaceSprite` (PNG, JPEG or GIF) and `Content.AddSound` (OGG).
  Everything is owned by the mod that added it and released on unload.
  - A replaced sprite gets its original image back.
  - A released sprite is emptied rather than deleted, and its slot is reused.
- **`GameDraw.OnGui`:** draws into the game's own GUI layer each frame.
  - It uses the game's Draw GUI event on a live object, and moves to another object when the room changes.
  - Draw state is restored after mod handlers run.
- **Console Inspector tab:** click any instance in the game to inspect it.
  - It shows the object, its parents and every variable.
  - Variables can be edited with GML expressions, frozen, and expanded as trees.
  - A selection can be dumped to a file or the clipboard.
  - A read-only code view lists each event's calls (scripts, events and builtins) and strings, with a callers search.
- New console commands: `inspect`, `dump`, `code` and `callers`.
- `CoreLoader.Input`: pick mode. The next click outside the overlay is taken, and the game never sees it.
- `CoreLoader.Code`: `Describe` and `FindCallers`. Builtins are named even where compiled code calls
  them through the runner's helper by registry index.
- `UI.TreeNode` / `TreePop`, `UI.SetClipboard`, `UI.InputTextEnter`, `UI.Guarded`.
- `ContentDemo` mod and `StructProbe` test mod.
- Visual Studio support for interop-based projects:
  - `<InteropGame>` finds a game's generated interop automatically, using the per-machine
    `CoreLoader.user.props` or `CORELOADER_GAME_DIRS`.
  - `tools\setup-dev.ps1` writes that file and a `CoreLoader.Dev.sln` that includes the interop projects.
- Game-thread record, and refusal of off-thread GML calls in every native API entry point.
- Logs rotate to `coreloader.prev.log`; identical repeated lines are rate-limited.

### Changed
- **Breaking:** CoreApi version 9. It adds pick mode, tree nodes, the clipboard, `builtin_address` and
  `builtin_name_at`. The managed runtime requires this exact version.
- **Breaking:** `Code.FindCallers` takes a millisecond budget instead of a function count.
- Mods start only once the game has loaded its assets (Stoneshard: ~16 s). Nothing a mod
  registers can take a slot the game is about to fill.
- A script with mod hooks on it is called with private copies of its arguments. `SetArg` changes only
  what the original and later handlers see, so a multiplier can no longer compound on constant call sites.
- Hot reload loads the new build first. If it cannot load yet, the running copy stays and the load is retried.
- `hook_install` accepts only the exact start of a `gml_*` function, with the calling convention its name
  implies. `gml_GlobalScript_*` and `gml_RoomCC_*` hook as events.
- The remote command file is read only when `CORELOADER_REMOTE=1`.
- Interop generation collects game data a few milliseconds per frame, and writes files on a worker,
  so the game no longer freezes.

### Fixed
- **Leaks:**
  - Each native variable access leaked one string.
  - Legacy tools handed the game buffers that were later freed.
- **Hang or crash at process exit:** teardown no longer runs under the loader lock.
- **Handling errors from the game:**
  - GML exceptions reach the game's own `try/catch` when only native frames are in between.
  - After-phase handlers run when the original throws.
  - Stack overflows reset the guard page.
  - Dispatch depth is capped at 256.
- **Registrations surviving unload:** hooks and draw handlers registered in a mod's constructor or from
  a Task outlived the mod.
- **Values:**
  - `Values.Free` of a pooled value freed it twice.
  - Struct copies no longer call the runtime's GC helpers outside game code, and kept structs are rooted.
- **Self-tests:**
  - The value self-test proves copy and free on plain values too.
  - The self-test no longer frees its own characters.
  - `COPY_RValue__Post` runtimes (Dwarf Eats Mountain) are supported.
- **Console and Inspector cannot disable themselves:** bad hex literals, nesting over 64 levels,
  `obj[expr]` evaluated twice, and handler errors are all caught.
- **Input:** pick mode swallows only its own button release; the Insert key ignores auto-repeat;
  key and button releases always reach the game.
- **Rendering:**
  - The overlay restores the game's render targets.
  - Mods keep ticking if the overlay cannot start.
  - Nested Present calls are skipped.
- **Instance and memory safety:**
  - The Stoneshard player pointer is forgotten when o_player stops stepping.
  - `memory_read` skips guard pages.
  - The current-self global is validated as an instance.
- Save rewrite is atomic; unreadable mod settings are kept as `.bad`; and more from the two engine reviews.

## [0.3.0] - 2026-09-29

### Added
- Hot reload: mods load from memory into collectible contexts, and a rebuilt dll is swapped in between frames.
- Shared hooks: native and managed users share one detour per function, and `HookCall.CallOriginal` is available.
- Value ownership: a per-frame autorelease pool, `Values.Keep` / `Free` / `Copy`, and a value-lifetime self-test.
- Universal in-game **Console** mod: GML-style expressions, `find`, `objects`, `vars`, `globals`, `hook`.
- Typed interop: script argument counts read from compiled code (`ScriptRef1..8`), and
  `Objects.<obj>.Vars.<name>` harvested from live instances.

### Changed
- **Breaking:** CoreApi version 8 (UI round 2, `memory_read`, value free/copy, hook enable).

## [0.2.0] - 2026-09-28

### Added
- Support for 2024 GameMaker runtimes: static string initialisers, 24-byte builtin rows,
  hook-observed self, and typed asset references.
- The shared thunk hook engine, and `[HookBefore]` / `[HookAfter]` attributes.
- Interop generation: `<Game>.Interop` with scripts, objects and events, builtins, assets, and `codemap.json`.
- Mods:
  - tools: ScriptSpy, GlobalsEditor, InstanceInspector, SpeedControl;
  - game-specific: DwarfBoost, StoneshardBoost.
- The `coreloader-mod` project template, and `tools\deploy-coreloader.ps1`.

### Changed
- **Breaking:** the loader is branded generically (CoreLoader); logs and data live under `<game>\CoreLoader\`.

## [0.1.0] - 2026-09-28

### Added
- .NET 10 hosted inside the game through `version.dll`. C# mods are loaded from `Mods\`, with
  isolated UI faults and a versioned C ABI (`CoreApi`).
- The health check accepts any YYC game, not only Stoneshard.

## Before CoreLoader

The native Stoneshard debugging and cheat mod: items, potions, character, body, enemies, loot,
game speed, saves, console and tracer. Its tools are still in `src/` and appear only in Stoneshard;
see `README.md`. A port to a C# mod is planned.

[Unreleased]: https://github.com/Forevka/stoneshard-mod/compare/0b69328...HEAD
[0.4.0]: https://github.com/Forevka/stoneshard-mod/compare/a3a13b0...0b69328
[0.3.0]: https://github.com/Forevka/stoneshard-mod/compare/a78bc5e...a3a13b0
[0.2.0]: https://github.com/Forevka/stoneshard-mod/compare/857b7ba...a78bc5e
[0.1.0]: https://github.com/Forevka/stoneshard-mod/compare/877c421...857b7ba
