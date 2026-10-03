# Console

An in-game console and object inspector for **any** YYC GameMaker game. It lives in the Lodestone
overlay (**INSERT**) as the *Console* tab, with *Inspector*, *Objects* and *Globals* tabs beside it.

## What it does

- **Evaluates GML-style expressions** against the running game:
  - builtins and scripts: `instance_number(o_enemy)`, `scr_some_script(1, "a")` (the `gml_Script_`
    prefix is optional);
  - globals and instance variables, read or write: `global.gold = 500`, `oSys.gold *= 2`,
    `o_enemy[3].hp = 1`;
  - numbers, strings and `+ - * / % == < && || !`; bare names resolve as assets, and `ans` is the
    last result.
- **Commands:**

  | Command | What it does |
  |---|---|
  | `find <text>` | scripts, events and builtins containing the text |
  | `objects [filter]` | objects with live instances (all matching objects with a filter) |
  | `vars <obj>[n]` | every variable of an instance |
  | `globals [filter]` | global variables and their values |
  | `where <text>` | live instances with a variable named like the text |
  | `hook <script>` / `unhook <script\|all>` / `hooks` | print each call's arguments and result |
  | `inspect` / `inspect <obj> [n]` | pick an instance by clicking it in the game, or name one |
  | `dump [<obj> [n]]` | write the inspected instance to `Mods/Console/dumps` |
  | `code <function>` / `callers <function>` | what a script or event calls and the strings it uses; who calls it |
  | `frozen` / `unfreeze all` | variables held by a freeze |

- **Inspector:** the clicked instance's object and parents, every variable (edit with any expression,
  freeze, expand arrays and structs), and read-only code.
- **Objects:** every object with its live instance count, and its instances' variables.
- **Globals:** list, edit and freeze global variables.

Up and Down in the input line walk the history, which is kept between sessions.

## Settings (`Mods\Console.json`)

| Key | Default | |
|---|---|---|
| `history` | empty | the input history; written by the console itself |

## Test host

`console <line>` runs a console line and answers its output (see the [test host reference](https://lodestone.forevka.dev/modding/reference/test-host)).
