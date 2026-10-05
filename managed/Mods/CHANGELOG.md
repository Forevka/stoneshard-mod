# Changelog: shipped mods

Changes to the mods that ship with Lodestone (`managed/Mods/`): StoneshardTrials, TavernGames,
FastTravel, Reliquary, ModMenu, StoneshardCheats, StoneshardHarness, Console, ScriptSpy, ContentDemo and
the rest. The loader itself (the native `version.dll`, the CoreLoader API, tools and test mods) has its
own changelog: [`CHANGELOG.md`](../../CHANGELOG.md). Versions are the Lodestone releases the mods ship in;
the format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **StoneshardTrials: the trial's dungeon keeps the player.** Its way out to the surface is refused while
  the trial is open; taken again within half a minute it gives the trial up and returns to the tavern
  (no pay, no cards). Once the master is dead the way out leads back to the tavern, where the trial
  is paid. Every run also starts with 3 ability points and 3 attribute points, given after the intro.
- **StoneshardHarness**, a development mod that lets a script or an agent play Stoneshard over the
  test host without screenshots. `hx.state`, `hx.player`, `hx.enemies`, `hx.npcs`, `hx.objects`,
  `hx.inventory`, `hx.log`, `hx.dialogue` and `hx.buttons` describe what the player sees, with
  desktop-pixel positions; `hx.move`, `hx.goto`, `hx.attack`, `hx.interact`, `hx.use`, `hx.wait`,
  `hx.say`, `hx.press` and `hx.key` act through the game's own scripts and events, and `hx.result`
  reports what each did (turns, HP before and after, the target's fate, new log lines). `hx.click` is
  the mouse fallback, refused unless the game is in the foreground. Clients: `tools\stoneshard.ps1`
  (compact tables) and `tools\stoneshard_harness.py`. See `managed/Mods/StoneshardHarness/README.md`.
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
  street door opens onto a crypt, catacombs or bastion whose boss still lives, entered the way the
  dungeon's own entrance enters it. Its danger tier comes from the character: power is half its
  level tier and half the average tier (plus rarity) of its five best worn items, pressure grows a
  quarter tier per trial up to one, and the result is rounded up past a 0.4 fraction; the nearest
  untouched dungeon is taken. A run is finite: once every dungeon's master is dead the run is
  complete and the world map opens again. Killing the
  trial dungeon's boss or named miniboss puts one Trial Ticket in the bag; using it returns you to
  the tavern for the next trial, and the innkeeper pays crowns for it (150 for tier 1, 100 more per
  tier, a tenth more per trial behind you). During the trials the world map stays shut (key, HUD
  button and paper maps), so travel mods cannot skip them, and experience comes only from kills,
  scaled by a setting. Progress is per character and part of its save (a player attribute), so
  loading an older save brings back that save's trial; a readable copy goes to
  `Mods\StoneshardTrials\characters\`. While enabled it applies to every save, not only new ones.
  Endless runs (setting `runMode`) remake a won single-floor dungeon at the wanted tier when no
  untouched one fits: its saved floors are forgotten and its tier, recommended levels and master
  (from the game's own tables) rewritten. A `difficulty` setting (Easy/Normal/Hard/Brutal) shifts
  the wanted tier (what it and Blood Money add stops 1.5 past the character's level tier), and a trial whose danger runs past its dungeon's tier (beyond the 0.4 the rounding
  allows) adds copies of the dungeon's toughest enemies and, a tier past, an elite master with one of
  the game's unused boss prefixes (Persistent, Powerful, Resistant, Nimble, Leeching, Watchful):
  half again its health plus the prefix's stats, as lasting statuses the game saves. Past tier 5 an
  endless run makes every enemy stronger with each tier-5 win, and a remade dungeon's master gets a
  name fitting its new kind (never as a tier 3 crypt or a
  tier 3-5 bastion, whose generation fails). A master that falls by another hand pays half. Payout crowns that a full bag drops are announced. The game autosaves on
  arriving in a trial and when one is settled in the tavern (setting `autosave`). A trial
  left by the dungeon's stairs is settled when the player walks back into the tavern: a won one is
  paid, any other given up. A ticket that does not fit a full bag comes once there is room, without
  the game dropping spare maps. The banner sits below the character's status effects.
  After each won trial the tavern offers three **cards** (a window in the game's style): take one
  or turn them down. 36 rewards (crowns, points, attributes, lasting statuses and stat swaps, a
  foreign perk, potions given identified, treatises, gear, experience, and run effects such as a
  fourth card, harder trials for double pay, or richer traders), and strong ones come with one of
  17 costs (a tithe, a debuff for some trials, a lost piece of gear, a closed skill tree, an
  attribute); numbers grow with the trial's tier, and the run keeps the boons, re-applying what the
  game does not save. Cards can be rare or legendary (a reward a tier or two higher; a card whose numbers do not grow
  with the tier is never rare), and a hand can be rerolled for crowns. A short animated lore intro (six illustrated slides) plays the first
  time a character stands in the tavern. From the first win, three real
  **traders** (a smith, a merchant with ready-made potions, and a jeweller) stand in the tavern and
  sell, for crowns only, a stock made for the last trial's tier after trials 1, 3, 5... The
  innkeeper heals and treats wounds for crowns, and a chest serves as a stash. Cards that act on the trials ahead (Second Look,
  Blood Money, Merchant's Favour) are not offered on the win that ends a finite run. Test-host
  commands `tr.offer`, `tr.deal`, `tr.take`, `tr.discard`, `tr.reroll`, `tr.traders`, `tr.intro`.
  Settings `enabled`, `xpScale`, `goldScale`, `difficulty`, `runMode`, `autosave` (also in the MODS window);
  test-host commands `tr.*`.
- **ModMenu** (Stoneshard): a **MODS** entry in the pause menu that opens a window, drawn with the
  game's own board, buttons and text, for every setting registered through `ModSettings`. Test-host
  commands `mm.*`.
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
- **ScriptSpy** over the test host: `spy.watch <function> [variable]`, `spy.read`, `spy.clear`,
  `spy.unwatch`. Rows name the object the call ran as, and can track a variable of self before and
  after the call (which script actually changes HP, say).
- **`StoneshardCheats` mod:** the native Stoneshard cheat tabs, ported to C#. Tabs: Stats, Items, Potions,
  Character, Body, Enemies and Saves.
  - Character scripts run as the player. The player instance comes from a hook on `o_player`'s Step event,
    and is dropped half a second after that event stops running.
  - Potions are rewritten inside `o_inv_bottle`'s Alarm 0, the only place the potion scripts work. A build
    that sees no bottle within 2 s gives up, rather than rewriting the next bottle the game makes.
  - The item catalogue is read from the live object table and from the game exe's own CSV rows.
  - The save folder is backed up once per session, to `<game>\Lodestone\save-backups`, before the first cheat.

### Changed

- `StoneshardCheats` uses `DsMap`/`DsList`, `Hooks.NextAfter` and `ObjectTable` instead of its own copies.
  The potion hook is only installed while a build is armed.
- StoneshardCheats: an action's result lines now appear under its echo in the panel.
- **Console:** freezes live in one shared list (globals and any number of instances) and survive a
  change of the Inspector's selection. New Objects tab (object browser with live counts, instance
  paging, and a search of every live instance for a variable name) and Globals tab (edit and freeze),
  and the `where`, `frozen` and `unfreeze all` commands.
- **Console:** `where` answers "object table not ready" until the table is built, instead of scanning
  in one frame. The Objects tab drops its selection when a pick or `inspect` selects another instance.
  Selecting an instance resets the variable filter unless a search set it. A frozen global the game
  removed is listed as inactive. A variable search skips instances and variables the game refuses to
  read.
- StoneshardCheats resolves the player by id where the runtime's lookup is proven, so cheats keep
  working while the game is paused; the o_player Step hook remains the fallback.

### Removed

- The `InstanceInspector` and `GlobalsEditor` mods: their features are in the Console mod.

### Fixed

- **StoneshardCheats, Items → "To inventory"** works. It never did, natively either: `scr_inventory_add_weapon`
  takes the inventory as its self, not the player (the game calls it inside `with (o_inventory)`). It now
  runs as the `o_inventory` instance itself (not a child object's instance), with the player as other,
  as `with (o_inventory)` does. The recovered GML error ("invalid with reference" in
  `scr_inventory_get_containers`) is what pointed at it.
- The test host's `cheats.potion` answered the wrong build number when the bottle's alarm ran during the
  give itself, so a client waiting for the result timed out.

## [0.4.0] - 2026-09-29

### Added

- **Console Inspector tab:** click any instance in the game to inspect it.
  - It shows the object, its parents and every variable.
  - Variables can be edited with GML expressions, frozen, and expanded as trees.
  - A selection can be dumped to a file or the clipboard.
  - A read-only code view lists each event's calls (scripts, events and builtins) and strings, with a callers search.
- New console commands: `inspect`, `dump`, `code` and `callers`.
- `ContentDemo` mod: the runtime content and `GameDraw` example.

### Fixed

- **Console and Inspector cannot disable themselves:** bad hex literals, nesting over 64 levels,
  `obj[expr]` evaluated twice, and handler errors are all caught.

## [0.3.0] - 2026-09-29

### Added

- Universal in-game **Console** mod: GML-style expressions, `find`, `objects`, `vars`, `globals`, `hook`.

## [0.2.0] - 2026-09-28

### Added

- Mods:
  - tools: ScriptSpy, GlobalsEditor, InstanceInspector, SpeedControl;
  - game-specific: DwarfBoost, StoneshardBoost.

[Unreleased]: https://github.com/Forevka/stoneshard-mod/compare/0b69328...HEAD
[0.4.0]: https://github.com/Forevka/stoneshard-mod/compare/a3a13b0...0b69328
[0.3.0]: https://github.com/Forevka/stoneshard-mod/compare/a78bc5e...a3a13b0
[0.2.0]: https://github.com/Forevka/stoneshard-mod/compare/857b7ba...a78bc5e
