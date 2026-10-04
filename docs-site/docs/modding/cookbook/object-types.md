---
title: New object types
description: Recipes for defining a new GameMaker object from C# - its events, a parent to inherit from, collisions, spawning, and what happens on hot reload and in saves.
---

`ObjectTypes.Define` makes a real GameMaker object at runtime. It gets an object index that every
builtin accepts (`instance_create_depth`, `object_get_name`, `object_get_parent`, `asset_get_index`,
`instance_number`, collision functions), it can have a parent whose events it inherits, and its own
events run your C# handlers. A data.win modding tool would add the object to the game's files; a YYC
game has none, so Lodestone builds the object the way the runner builds its own.

Object types are a loader feature, not a per-game one: they work in any YYC game where the loader could
find and prove the runner's object machinery. Check `ObjectTypes.Available` (or let `Define` throw), and
read `ObjectTypes.Status` for the reason when they are not.

## Define an object and its events {#define}

Define it once, from `OnInitialize` or later, and chain its events. Each handler receives an
`ObjectEventCall`: the instance it runs for (`Self`), GML's `other`, and the event.

```csharp
public override void OnInitialize()
{
    _beacon = ObjectTypes.Define("o_mymod_beacon", sprite: "s_lamp", visible: true)
        .On(GameEvent.Create, e => e.Self.Set("pulse", 0))
        .On(GameEvent.Step, e => e.Self.Set("pulse", e.Self.Get("pulse").AsReal + 1))
        .On(GameEvent.Alarm(0), e => Log.Info("alarm"))
        .On(GameEvent.DrawGui, e => DrawLabel(e.Self));
}
```

Gotchas:

- The name must not be an asset the game already has. Prefix it with your mod's name.
- `Define` again with the same name returns the same object (after a hot reload, too), with its
  sprite and flags set again. The handlers of the earlier definition are dropped, and the
  `ObjectType` it returned refuses new handlers. The parent cannot change: inheritance is copied
  into the game's maps, so a new parent needs a restart of the game, and `Define` throws until then.
- `GameEvent` names the common events (`Create`, `Destroy`, `Step`, `BeginStep`, `EndStep`, `Draw`,
  `DrawGui`, `DrawBegin`, `DrawEnd`, `CleanUp`, `Alarm(n)`, `User(n)`, `Collision(obj)`, `Mouse(n)`,
  `KeyPress(key)`, `Other(n)`, the async events). `new GameEvent(type, subtype)` names any other one.
- A handler that throws faults your mod, like a throwing hook. The event then falls back to the
  parent's code (nothing, without a parent) until the mod is reloaded.

## Spawn instances {#spawn}

`ObjectType.Create(x, y, depth)` creates an instance and returns it as an `InstanceRef`. Its Create
event has run when the call returns.

```csharp
var beacon = _beacon.Create(player.Get("x").AsReal, player.Get("y").AsReal);
```

Gotchas:

- Definitions take effect in the runner at the end of the frame. `ObjectType.Create` applies them
  first. If you create instances some other way in the frame you defined the type or gave it an event,
  through `Game.CallBuiltin("instance_create_depth", ...)` or a game script, call
  `ObjectTypes.Flush()` first. The runner decides whether an instance takes part in collisions when it
  creates it, so an instance created before that never collides.
- Inside a hook or event handler, where the runner may be walking the lists being rebuilt, neither
  does anything: the definitions take effect at the end of the frame. Define a type in an earlier
  frame (`OnInitialize`) than the handler that spawns it.
- Applying definitions rebuilds the runner's per-event object lists. That takes about half a second in
  a game with 10,000 objects (Stoneshard) and a millisecond in a small one, once per frame in which
  something changed. Define your types together, at start-up, rather than one per frame. A hot reload
  that defines the same events again costs nothing.

## Inherit from a game object {#inherit}

Give a `parent`. Every event the type does not implement runs the parent's code, and the runner counts
the type as the parent wherever the game asks: `instance_number(parent)`, `instance_find`,
`instance_deactivate_object` and the other functions that walk a parent's instances the way `with`
does, `object_is_ancestor`, collision events and collision functions against the parent. An event you do
implement replaces the parent's, as in GameMaker; call `CallInherited()` to run the parent's too
(GML's `event_inherited()`).

```csharp
// A unit the game's own code treats as an enemy (Stoneshard).
_ghost = ObjectTypes.Define("o_mymod_ghost", parent: "o_enemy", sprite: "s_dummy")
    .On(GameEvent.Create, e =>
    {
        e.CallInherited();                         // o_enemy's Create first
        e.Self.Set("ai_is_on", false);
        e.Self.Set("roomEntityIsSavable", false);  // see Saving below
    })
    .On(GameEvent.Step, e =>
    {
        e.CallInherited();
        FollowRemotePlayer(e.Self);
    });
```

Gotchas:

- Sprites are not inherited (as in GameMaker): give the type its own `sprite` or `mask` if it should
  collide.
- Being a child of a game object makes the game's code run for your instances, and that code may
  expect variables its own children set up. Read what the game's children of that parent do (the
  interop and the Console's object inspector help) and set the same things in your Create.
- A type of yours can be the parent of another type of yours. An event added to the parent later
  reaches the children that do not implement it themselves.

## Collide {#collide}

`GameEvent.Collision(obj)` runs while an instance of the type overlaps an instance of `obj` or of any
object that inherits from it. `Other` is the instance collided with.

```csharp
ObjectTypes.Define("o_mymod_trap", sprite: "s_trap")
    .On(GameEvent.Collision(GmlObject.Find("o_player")!.Value), e => Spring(e.Self, e.Other));
```

Collisions work the other way too: game objects whose collision events name a parent of your type (a
spell that hits `o_unit`, say) collide with your instances.

`CallInherited()` in a collision handler runs the parent's collision event for the object the event
names (`Collision(o_unit)`), even when the instance collided with is of a child of it.

Gotchas:

- In a room with a physics world, GameMaker reports collisions through the physics world, and only for
  physics instances. A defined type is not a physics object, so its collision events do not run in such
  rooms; `place_meeting`, `instance_place` and `collision_*` still work.
- Only instances created after the definitions took effect collide (see [Spawn](#spawn)).

## Hot reload, unload and faults {#lifetime}

A type belongs to the mod that defined it.

| What happens | Its handlers | Its instances |
|---|---|---|
| The mod unloads or hot-reloads | dropped | destroyed (Destroy events do not run; Clean Up events run the parent's code) |
| The mod faults | dropped; events fall back to the parent's code | kept until the mod is reloaded or unloaded |
| The game exits | `OnShutdown` runs | left to the game |

The object itself stays for the rest of the session: GameMaker cannot delete an object. A reloaded
mod that defines the same name gets the same object index back, so ids kept elsewhere stay valid.

## Saving {#saving}

A game that saves its rooms by walking the instances may write yours into the save, by object name.
Load that save in a session where your mod is not installed (or before it has defined the type) and
the name means nothing. Keep your instances out of saves:

- Stoneshard's room saver skips an instance whose `roomEntityIsSavable` is false. Set it in Create
  (and, if the game's code can set it back, in Step), as the example in [Inherit](#inherit) does.
- In other games, find how the save code chooses instances (a parent, a flag, a list) and stay out of
  it, or destroy your instances before the game saves.

## When it is not available {#availability}

`ObjectTypes.Status` is `"available"`, or says why not:

- `not proven yet (...)`: the loader proves its findings against the game's own objects once the game
  runs code. Wait, or try again on a later frame. A game that runs no code at the frame boundary on its
  title screen stays here until it does.
- `unavailable: ...`: a piece of the runner's object machinery was not found, or did not match what
  the game's objects show. Nothing was changed in the game, and defining objects is off for the
  session. The loader log has the detail.

See [Object types](../../internals/object-types.md) in the loader internals for how it works.
