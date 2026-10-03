---
title: Cookbook
description: Task-oriented recipes for CoreLoader mods, each with the API to use, a snippet from a real mod in this repository, and the gotchas.
---

Each recipe starts from something you want to do ("run code when an instance is destroyed"), names the
API that does it and why, quotes a snippet from a mod that ships in this repository with a link to the
exact lines, and lists the mistakes that cost time. Where both the typed form (from the generated
[interop](../interop.md)) and the untyped form work, both are shown. The recipes assume you have read the
[concepts](../concepts.md): the game thread, value lifetime, ownership and fault isolation explain why
many of the gotchas exist. If you have not built a mod yet, start with the [first mod](../first-mod.md).

## Hooks

Run your code before or after a game script or object event. See [Hooks](hooks.md).

| I want to... | Recipe |
|---|---|
| change a script's argument before it runs | [Change a script's argument](hooks.md#change-an-argument) |
| run the original script again (more loot, repeated effect) | [Run the original script again](hooks.md#run-the-original-again) |
| cancel a script and return my own value | [Cancel a script and return your own value](hooks.md#cancel-a-script) |
| change what a script returned | [Change what a script returned](hooks.md#change-the-result) |
| run code when an instance is destroyed | [Instance destroyed](hooks.md#instance-destroyed) |
| run code when an instance is created | [Instance created](hooks.md#instance-created) |
| run code every step of an object | [Every step](hooks.md#every-step) |
| hook a user event | [Hook a user event](hooks.md#user-events) |
| look up the name of an event (`Create_0`, `Step_1`, `Draw_64`, `Other_10`) | [Event names](hooks.md#event-names) |
| hook a function I only know the name of at runtime, and unhook it | [Hook by name](hooks.md#hook-by-name) |
| run something once inside the next matching call, with a timeout | [Run once in the next call](hooks.md#next-call) |
| choose between the attribute, the typed ref and a string | [Attribute, typed or string](hooks.md#attribute-or-typed) |

## Game state

Read and change what the game keeps. See [Game state](game-state.md).

| I want to... | Recipe |
|---|---|
| read or write a global variable | [Globals](game-state.md#globals) |
| find a singleton instance and edit its variables | [Singleton instance](game-state.md#singleton) |
| loop over every instance of an object, children included | [Iterate instances](game-state.md#iterate-instances) |
| create or destroy an instance | [Spawn and destroy](game-state.md#spawn-and-destroy) |
| read a ds_map | [Read a ds_map](game-state.md#ds-map) |
| edit a ds_list that lives inside a ds_map | [Edit a ds_list](game-state.md#ds-list) |
| read arrays and structs | [Arrays and structs](game-state.md#arrays-and-structs) |
| walk the object table and its parent and child objects | [Object table](game-state.md#object-table) |
| scale a value without compounding it | [Scale without compounding](game-state.md#base-value) |

## Calling the game

Make the game do something. See [Calling the game](calling-the-game.md).

| I want to... | Recipe |
|---|---|
| call a script, typed or by name | [Call a script](calling-the-game.md#call-a-script) |
| call a builtin | [Call a builtin](calling-the-game.md#call-a-builtin) |
| run an object event directly | [Run an object event](calling-the-game.md#run-an-event) |
| look up an asset by name and play a sound | [Assets and sounds](calling-the-game.md#assets-and-sounds) |
| run code inside the game's own event | [Inside the game's event](calling-the-game.md#inside-the-event) |

## Drawing and UI

Put pixels on screen, in the game or in the overlay. See [Drawing and UI](drawing-and-ui.md).

| I want to... | Recipe |
|---|---|
| add a sprite and a sound and draw in the game's GUI layer | [Sprite and sound](drawing-and-ui.md#sprite-and-sound) |
| reskin one of the game's sprites | [Reskin a sprite](drawing-and-ui.md#reskin) |
| draw text and shapes with `draw_*` | [Draw builtins](drawing-and-ui.md#draw-builtins) |
| draw a game sprite at a position | [draw_sprite_ext](drawing-and-ui.md#draw-sprite-ext) |
| draw with the game's own UI scripts | [Game UI scripts](drawing-and-ui.md#game-ui) |
| build an overlay tab with ImGui | [Overlay tab](drawing-and-ui.md#overlay-tab) |
| keep a tab alive when it reads live data | [UI.Guarded](drawing-and-ui.md#guarded) |
| give widgets stable ids | [Stable ids](drawing-and-ui.md#stable-ids) |
| open and close UI scopes correctly | [Scopes](drawing-and-ui.md#scopes) |

## Input

Keys, the mouse and clicks. See [Input](input.md).

| I want to... | Recipe |
|---|---|
| detect a key press | [Key press](input.md#key-press) |
| read the mouse in GUI coordinates | [Mouse](input.md#mouse-gui) |
| take a click the game never sees | [Take a click](input.md#take-a-click) |
| keep keys from reaching the game while my window is open | [Swallow the keyboard](input.md#swallow-keys) |
| understand what the overlay does to my input | [The overlay and your input](input.md#overlay-input) |

## Settings and persistence

Keep things between runs. See [Settings and persistence](settings-and-persistence.md).

| I want to... | Recipe |
|---|---|
| save a setting | [Config](settings-and-persistence.md#config) |
| offer settings in the game's own menu | [ModSettings](settings-and-persistence.md#mod-settings) |
| read a file from my mod's folder | [Mod folder](settings-and-persistence.md#mod-folder) |
| keep per-character data inside the save | [Data in the save](settings-and-persistence.md#save-data) |
| survive a hot reload | [Hot reload](settings-and-persistence.md#hot-reload) |
| clean up when the mod unloads | [OnShutdown](settings-and-persistence.md#on-shutdown) |

## Robustness and testing

Fail soft, stay fast, and test without a mouse. See [Robustness and testing](robustness-and-testing.md).

| I want to... | Recipe |
|---|---|
| handle a game that is not ready (the title screen) | [Catch GmlException](robustness-and-testing.md#catch-gmlexception) |
| keep one bad callback from disabling my mod | [Guard](robustness-and-testing.md#guard) |
| keep per-frame work cheap | [Throttle OnUpdate](robustness-and-testing.md#throttle) |
| do slow work in the background and come back to the game | [Background work](robustness-and-testing.md#background-work) |
| see what a compiled script calls, and who calls it | [Inspect compiled code](robustness-and-testing.md#introspection) |
| expose commands to the test host | [Test host](robustness-and-testing.md#test-host) |
| write a regression mod for a risky area | [Regression mods](robustness-and-testing.md#regression-mods) |

Looking for the whole API instead? See the [API reference](../reference/api.md). Looking for which
script to hook in a game you do not know? See [Finding hooks](../finding-hooks.md).
