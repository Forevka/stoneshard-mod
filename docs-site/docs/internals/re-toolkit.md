---
title: How we found what to hook
description: The method behind every pattern in the loader, the offline tools in tools/re that apply it to a game's exe, and a worked example of finding a hook point.
---

Everything the loader knows about a game it worked out from the game's own code: where the GML
functions are, where the builtins are registered, which runtime helper copies a value. None of it
is a hard-coded address. This page describes the method, the offline toolkit that applies it to an
exe on disk, and how the same questions get answered on a running game.

## The method {#method}

Every discovery in `src/` follows the same four rules. They are why one `version.dll` runs on games
built years apart without a per-game table.

1. **Vote across many functions.** One code site can be unusual. A helper that thousands of compiled
   functions call in the same way cannot be. The [builtin registrar](./builtins.md) wins a vote among
   eleven unrelated names; the runtime helpers in the [runtime bridge](./runtime-bridge.md) are
   tallied across thousands of `gml_Script_*` functions.
2. **Validate the winner structurally.** A vote finds the most common target, not necessarily the right
   one. The winner must then look right: the builtin rows must give printable names, code pointers and
   sane arities; a string setter must write the string kind into the value.
3. **Prove it by behaviour.** Before anything depends on a helper, the loader calls it on something it
   controls and checks the result: `buffer_get_size` must return exactly the 64 bytes just created,
   and the value-lifetime check copies and frees a probe string with canaries around it.
4. **Fail closed.** If any step is unsure, the feature is off and the log says why. A missing feature
   is a bug report; a guessed address is a crash, or a corrupted save.

Anchoring on names and instruction shapes is also what makes the loader survive game updates: an
update moves code, but it does not rename `buffer_create` or change how the compiler loads a string.

## The offline toolkit {#toolkit}

`tools/re/` holds small Python scripts that answer the same questions statically, from the exe and
`data.win` on disk. They are for research: finding a pattern, checking a hypothesis, or seeing who
calls what before you start the game. They are game-agnostic and need Python 3 with `numpy` (and
`capstone` for disassembly).

| Tool | What it does |
|---|---|
| `gamepath.py` | Picks the game under study (`--exe` or `RE_GAME_EXE`) and its per-game cache folder. Imported by every other tool. |
| `relib.py` | The library: PE sections, address/offset conversion, C strings, the [`gml_*` symbol table](./gml-functions.md#offline-twin), `owner(va)`, `lea`/call/data xrefs, `disasm` via Capstone. |
| `idx.py` | Whole-`.text` indexes of every `lea reg,[rip+d32]`, `call rel32` and `jmp rel32` with their targets, built with numpy and cached as `.npy`. Makes "who loads this string" or "who calls this" instant. |
| `pdata.py` | Function bounds for every function, including unnamed runner C++, from the `.pdata` exception table. Labels addresses as `gml_...+0x12` or `sub_XXXX+0x12`. |
| `who.py <symbol>...` | Call sites of a GML function, grouped by calling function. |
| `callees.py <symbol>...` | What a GML function calls, in address order, plus the strings it loads. |
| `strings_in.py <symbol>...` | The distinct string literals a function references. |
| `builtins.py [registrar]` | Every builtin registration, decoded statically (see [builtins](./builtins.md#offline-twin)). Writes `builtin_table.json`. |
| `datawin.py [object]...` | Reads `data.win` chunk headers and writes `objt_indexed.json`: object names by asset index, slots kept aligned. |
| `objfind.py <name>...` | Object name to asset index (exact or substring), and index to name. |
| `assetref.py <object>...` | Counts the functions that embed an object's asset index. |
| `whorefs.py <object>...` | Names those functions, with a site count each. |
| `ghidra/ExportGml.java` | Ghidra headless script: names every function and decompiles the game logic to one `.c` file each. |

### Pointing the tools at a game {#game-selection}

Every tool takes `--exe <path to the game's exe>` anywhere on its command line, or reads
`RE_GAME_EXE`. `data.win` is expected beside the exe.

```powershell
$env:RE_GAME_EXE = 'D:\Games\Stoneshard\StoneShard.exe'
python tools\re\who.py gml_Script_scr_player_move
python tools\re\callees.py gml_Object_o_enemy_Mouse_4
python tools\re\datawin.py                       # once per game, before objfind/assetref/whorefs
python tools\re\whorefs.py o_floor_target
python tools\re\pdata.py --exe "D:\Games\Other\Other.exe"
```

### The cache {#cache}

The derived files (`lea_idx.npy`, `call_idx.npy`, `jmp_idx.npy`, `script_table.json`,
`objt_indexed.json`, `builtin_table.json`) are addresses and indices into one exe and one `data.win`.
They live in `tools/re/cache/<exe name without extension>/`, so a second game never reuses the first
one's tables. `RELIB_CACHE` overrides the folder. The first run of an indexing tool on a large exe
takes a while; later runs load the cache. The rules are in the docstring of
[tools/re/gamepath.py, lines 1-13](https://github.com/Forevka/stoneshard-mod/blob/main/tools/re/gamepath.py#L1-L13).

:::warning
After a game update, delete that game's cache folder. The tools do not check whether the cached tables
still match the exe.
:::

## Asset references are numbers {#asset-references}

In GML source, `instance_exists(o_player)` names an object. YYC compiles that name into the object's
numeric asset index, so there is no `"o_player"` string at the call site to search for. Finding the
code that mentions an object takes two steps:

1. `datawin.py` reads the `OBJT` chunk of `data.win` and lists object names by index, keeping empty
   slots so the indices stay aligned with what the compiled code uses.
2. `assetref.py` searches `.text` for that index as a 32-bit immediate, keeping only hits right after
   an immediate-bearing opcode (`mov r32, imm32`, `push imm32`, `cmp eax, imm32`), plus `.rdata`
   doubles of the same value loaded with a rip-relative `lea`. `whorefs.py` names the functions those
   hits fall in.

The strict opcode test matters: a small index such as 12 occurs as raw bytes almost everywhere.

```python
YYC compiles an asset reference like `o_player` into its numeric index, so this
is a proxy for 'how many places in the game's GML mention this object'.
```

<small>Source: [tools/re/assetref.py](https://github.com/Forevka/stoneshard-mod/blob/main/tools/re/assetref.py#L4-L5)</small>

## Ghidra export {#ghidra}

For reading a function's logic rather than its call graph, `tools/re/ghidra/ExportGml.java` runs in
Ghidra's headless analyser. It names every builtin `bi_<name>` (so decompiled GML shows
`bi_instance_create` at call sites), names every `gml_*` function from the symbol table, then
decompiles each `gml_Script_*` and `gml_Object_*` function to `<name>.c`, with an `_index.tsv` of
addresses beside them.

```java
// Headless:
//   analyzeHeadless <projDir> <projName> -process <Game>.exe -readOnly -noanalysis
//     -scriptPath tools/re/ghidra -postScript ExportGml.java <symtab.json> <builtins.json> <outDir>
//
// symtab.json   {"gml_Script_foo": "0x140001000", ...}   (relib.table())
// builtins.json tools/re/builtin_table.json               ({"name": {"func": "0x..."}})
// Files already present in outDir are skipped, so an interrupted run resumes.
// Optional 4th argument: per-function decompile timeout in seconds (default 120);
// delete the "// decompile failed" files and rerun with a larger one.
// Optional 5th argument "seq": decompile one function at a time instead of in parallel.
```

<small>Source: [tools/re/ghidra/ExportGml.java](https://github.com/Forevka/stoneshard-mod/blob/main/tools/re/ghidra/ExportGml.java#L4-L13)</small>

`seq` exists because the largest event handlers each want several GB in the decompiler, and running
them side by side exhausts memory. A timed-out decompiler process is replaced with a fresh one.

:::note
The export is a research aid for loader work. Mods are written against the
[generated interop](../modding/interop.md) and checked on the running game, which stays correct when
the game updates; a decompile is a snapshot of one build.
:::

## On the running game {#live-tools}

Most questions a mod author has are faster to answer live, because the loader already has the symbol
table, the builtin registry and a code scanner in the process:

- The Console's `code <symbol>` lists what a function calls and the strings it loads; `callers <symbol>`
  lists who calls it. These are the in-game counterparts of `callees.py` and `who.py`.
- ScriptSpy (`spy.watch`, `spy.read`) records which scripts and events actually run, with their
  arguments, while you do something in the game.

[Finding hooks](../modding/finding-hooks.md) walks through both.

## Case study: replaying a click {#case-study}

The StoneshardHarness mod lets a test drive Stoneshard without a mouse: walk, attack, open doors. The
question was which game code a click runs, so the harness could run that code itself.

**Watch it happen.** With ScriptSpy watching candidate scripts and events, each action was done once
with a real click (on the floor, on a unit, on a door), and the spy was read after each. That showed that a floor
click ends in `scr_player_move(x, y)` with the cell's room coordinates, run as the player, and that a
click on a unit runs the unit's Left Pressed event (`Mouse_4`, for example `o_enemy_Mouse_4`), which
picks the tile and calls `scr_player_move` as that unit.

**Cross-check the call graph.** `who.py` confirms the picture from the exe: the callers of
`scr_player_move` are mouse events, keyboard control, the player's AI and delayed moves.

```
=== gml_Script_scr_player_move  0x141805180  12 call sites, 9 distinct callers ===
      3x  gml_Object_o_floor_target_Mouse_53
      2x  gml_Object_o_player_AI_Step_0
      1x  gml_Script_scr_keyboard_control
      1x  gml_Script_scr_one_path_move
      1x  gml_Object_o_enemy_Mouse_4
      1x  gml_Object_o_loot_Mouse_4
      1x  gml_Object_o_NPC_Other_23
      1x  gml_Object_o_delayed_move_Alarm_0
      1x  gml_Object_o_delayed_move_grid_Alarm_0
```

`o_floor_target` is the floor cursor, and `Mouse_53` is its global left-press event. That event reads
the real mouse position, so it cannot be replayed as it is.

**Replay the parts that can be replayed.** For an object such as a door, the click is the object's own
`Mouse_4` plus the cursor's part, and the spy showed the cursor's part on a real door click:
`target_id` is set to the object, then `scr_player_move` runs as the cursor with the tile beside it;
the game walks the player there and, on arrival, runs the object's User Event 0. The harness makes
exactly those calls and leaves the rest to the game.

```csharp
// A click on an object is the object's Mouse_4 plus the floor cursor's
// own click (o_floor_target, whose click event reads the real mouse, so it
// cannot be run as it is). Watched on a real door click, the cursor's part
// is: target_id = the object, then scr_player_move(the tile beside it) run
// as the cursor; the game walks the player there and, arriving, runs the
// object's User Event 0 - for a door its way out (alarm 7 changes room).
// Those are the calls made here; the rest is the game's.
```

<small>Source: [managed/Mods/StoneshardHarness/Act.cs](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardHarness/Act.cs#L151-L157)</small>

**Prove it, and stop where the proof stops.** Running an event by guess can stop the game with a GML
"Code Error": it happened three times while the harness was built, including the cursor's user event
on a corpse. So the cursor's route is used only for doors and containers, where a watched click proved
it, and anything else is only walked up to. That is the same "prove by behaviour, fail closed" rule the
native code follows. The harness's
[README](https://github.com/Forevka/stoneshard-mod/blob/main/managed/Mods/StoneshardHarness/README.md)
lists the other facts found this way.

## Credits {#credits}

Two existing Stoneshard projects saved this one a great deal of guesswork:

- **[ModShardLauncher](https://github.com/ModShardTeam/ModShardLauncher)** by ModShardTeam, the
  established Stoneshard modding framework, which patches `data.win` statically through
  UndertaleModLib. Its loot module supplied several vanilla call signatures this project had wrong or
  unknown, most importantly that argument 4 of `scr_weapon_loot` is the item's rarity and that the
  function returns the instance it creates. MSL edits GML in `data.win`, which needs a build that ships
  a `CODE` chunk; CoreLoader hooks the compiled functions of a YYC build at run time. They are not
  alternatives to each other.
- **[stoneshard-editor](https://github.com/MikaBuchholz/stoneshard-editor)** by MikaBuchholz, a
  browser-based save editor that runs entirely locally. Its save codec is where the save checksum
  scheme came from, which `tools/checkcksum.py` re-verifies against live save files.
