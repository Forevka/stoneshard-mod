# Script Spy

A modding tool for **any** YYC GameMaker game: hook a script or object event by name and watch its
calls live, with the arguments going in, the result coming out, and the object each call ran as.
This is how the argument meanings for the other mods were found.

## Use

- In the overlay (**INSERT**), *Script Spy* tab: type a function name (`scr_get_XP`,
  `gml_Object_o_player_Step_0`...) and press *Watch*, or search with *find* and click a result.
  Each watch keeps its recent calls (they are also logged) and can be paused, cleared or unhooked.
- Over the test host, a watch can also track a **variable of self**, read before and after each
  call, to see what a script changes.
- **Watches at startup:** list one symbol per line in `ScriptSpy.txt` next to the dll, for functions
  that only run while the game loads.

## Test host

| Command | What it does |
|---|---|
| `spy.watch <function> [variable]` | starts watching (and reading that variable of self around each call) |
| `spy.read [function]` | every watch (or one) with its call count and recent calls |
| `spy.clear` | forgets the recorded calls |
| `spy.unwatch <function\|all>` | stops watching |
