---
title: Object types
description: How the loader defines new GameMaker objects at runtime in a YYC game - which runner pieces it finds, how it proves them, and how C# events, inheritance and collisions are wired into the runner's own structures.
---

A mod can define a new GameMaker object from C# (`ObjectTypes.Define`, see the
[cookbook](../modding/cookbook/object-types.md)). In a VM game a modding tool adds the object to
data.win and recompiles; a YYC game has no such file, and its objects are structs the runner builds
in memory at start-up. The loader builds new ones the same way, into the same structures, from pieces
of the runner it locates by pattern and then proves against every object the game already has.

The native side is `src/objtypes.cpp`; the managed API is `managed/CoreLoader/ObjectTypes.cs`.

## What an object is to the runner {#model}

- **The object hash**: a bucketed hash from object index to `CObjectGM*`, the same shape as the
  instance id map. Every `object_*` builtin starts with an inlined lookup into it.
- **`CObjectGM`** (0x98 bytes in every runtime seen so far): name at +0x00, parent object pointer at
  +0x08, a children map at +0x10, the **event map** at +0x18, flags/sprite/depth/parent index/mask
  from +0x80, and its own index at +0x94.
- **The event map**: a robin-hood `CHashMap` from `(event type << 32) | subtype` to `CEvent*`.
  Inheritance is flattened into it: a child's map also holds its parent's `CEvent`s for every event
  the child does not define, which is how an inherited event runs with no lookup up the chain.
- **`CEvent`**: `{CCode* code; int owner}`, where `owner` is the index of the object that defines the
  event (an ancestor's for an inherited entry). The 2024 runtime keeps some `CEvent`s with no code.
- **`CCode`**: among other fields, a pointer to the function's registration row,
  `{const char* name; void* function; ...}` - the same `gml_Object_<obj>_<Event>_<n>` rows the
  [symbol scan](./gml-functions.md) reads. Running an event calls that row's function with
  `(self, other)`.
- **Per-event object lists**: which objects have a Step, a Draw, an Alarm... event. They drive the
  frame's event dispatch, and a runner function (`Create_Object_Lists`) rebuilds them from the event
  maps.
- **Collisions**: the frame's collision pass asks, for two overlapping instances, whether one's
  object has the event `(Collision, other's exact object index)`. An event written against a parent
  is therefore filed once per descendant of that parent.

## Finding the pieces {#discovery}

All of it is anchored on code that exists in every runtime seen, never on an address:

| Piece | Anchor |
|---|---|
| Object hash | `object_get_parent` (found in the [builtin registry](./builtins.md)): its first rip-relative qword load followed by a `movsxd r, [r+8]`, the inlined bucket lookup |
| Allocator, `CObjectGM` size and constructor, object count, hash insert | The runner always creates one object of its own, `__YYInternalObject__<n>`. Its creation is inlined beside the name: `mov ecx, <size>; call <operator new>`, then `mov edx, [rip+<count>]` and `call <constructor>`, and after the name `mov edx, [rip+<count>]; mov rcx, [rip+<hash>]; call <insert>`. Every site that loads the name and decodes as a creation must agree (Stoneshard has two: `Object_Add` behind the old `object_add` builtin, and the loader's copy) |
| Event map header size | The constructor allocating the event map: `mov ecx, <bytes>; call <operator new>` followed by `mov dword [rax], 8`. 0x20 in Stoneshard; 0x28 in the 2024 runtime, which adds the longest probe and a second grow threshold |
| `Create_Object_Lists` | Its head (`mov esi, 8; lea rax, [rip+lists]; mov ecx, esi; xorps xmm0, xmm0`) in a function that also reads the object count and the object hash. Exactly one function must match |

Anything that does not decode, or decodes ambiguously, makes the feature unavailable for the
session (`ObjectTypes.Status` says which step). Nothing is written to the game before the proof below
passes.

## Proving them {#proof}

On the game thread, once the [builtin registry](./builtins.md) is ready and a live instance is known,
`objtypes::Verify` walks every object index in the hash and checks:

- each object's index field is its index, its name is a readable string, and its parent pointer is the
  object the parent index names;
- each event map is a sane power-of-two table whose stored hashes match the hash function
  (`(key * 0x9E3779B97F4A7C55 >> 32) + 1`, masked to 31 bits);
- each event the object owns has a `CCode` whose row is a `gml_Object_<this object>_...` symbol whose
  function is that symbol's address, and the event's key matches the symbol's suffix (`Step_2` is
  type 3, subtype 2). The row's offset in `CCode` must be the same for every event;
- each inherited event is owned by an ancestor, and a sample of children hold every event of their
  parent (the flattening);
- for 64 objects spread over the table, `object_get_name` and `object_get_parent` agree with the
  struct.

Stoneshard proves in about 0.2 s (9,664 objects, 23,935 events of their own, 202,786 inherited).
One mismatch anywhere refuses the feature. The reads go through a cache of readable memory regions,
and each copy is still fault-guarded.

## Defining an object {#define}

`objtypes::Define(name, parent)` repeats the runner's own creation: `operator new` of the object size,
the constructor with the next index, the name copied into the runner's heap, the hash insert, and the
object count increased. Then the builtins are asked for the object's name and parent, as a check.

With a parent, the loader links it the way the runner's load does: parent index and pointer, and the
parent's event map entries copied into the new one. It then files the new object into collisions:
every object (the game's and defined ones) that has a Collision event against one of its ancestors
gets the same `CEvent` under the new index, the nearest ancestor's winning, so a game spell that hits
`o_unit` hits a defined child of `o_enemy`. Because inheritance is copied this way into maps all
over the game, a defined object's parent is fixed for the session: a `Define` that names another
parent is refused.

The runner's children map is not updated; nothing the loader or the tests depend on reads it.

## Events implemented in C# {#events}

`objtypes::DefineEvent(object, type, subtype)` gives a defined object its own event:

1. The [hook engine](./hook-engine.md#defined-events) makes a **defined** hook: an event thunk with
   no detour, whose dispatch has no original to run.
2. A registration row `{"gml_Object_<name>_<Event>_<n>", thunk, ...}` is made for it.
3. A `CCode` is cloned from one of the game's own (the template the proof found), with its row
   pointer, and any name pointer, replaced by ours. A `CEvent` points at it, owned by the object.
4. The `CEvent` goes into the object's event map, replacing what it inherited for that key, and down
   into defined objects that inherit from it and do not define the event themselves. A Collision
   event is also filed under every descendant of its target.
5. The per-event lists are marked for a rebuild.

The managed side subscribes to the hook id like any hook (`Hooks.AddDefined`), so the call reaches
the defining mod's handler through the ordinary [hook dispatch](./hook-engine.md#dispatch), with
the same fault handling and ownership. While no handler is attached (the mod unloaded, faulted, or has
not attached one yet) the thunk runs a fallback instead: `CallInherited`, which looks up the parent's
event map for the same key and calls that event's function. A defined object therefore falls back to
its parent's behaviour, or does nothing without a parent.

The event maps are written with the loader's own robin-hood insert, which raises the 2024
runtime's longest-probe field the way the runtime's own inlined insert does (it never lowers it).
Only a map that looks as the proof found them (a power-of-two size, a grow threshold below it) is
written to, and an insert gives up rather than probe past the table's size. Growing a map allocates the new elements from
the runner's allocator; the old block is left allocated.

## Rebuilding the lists {#rebuild}

`objtypes::Flush` calls `Create_Object_Lists` once at the end of a frame in which a definition
changed, and on demand (`ObjectTypes.Flush`, which `ObjectType.Create` calls) - except while a
hooked event is being dispatched, when the runner may be walking those lists. The runner files an
instance for collisions as it creates it, from the state those lists leave: an instance of a defined
type created before the rebuild never collides. The rebuild costs about 0.5 s in Stoneshard (10,000
objects) and a millisecond in small games, so mods define their types together.

## Limits {#limits}

- Objects cannot be deleted; a defined object stays for the session, with its thunks and its parent.
- An event filed under several keys (a Collision event under every descendant of its target) knows
  only the key it was declared for, so `CallInherited` runs the parent's event for that one.
- A game whose title screen runs no code where the loader can borrow an instance waits in "not proven
  yet" until it does (Zero Stress King, TheSpikeCross).
- Physics: a defined object is not a physics object, so in a physics room it gets no collision events
  (GameMaker's rule for non-physics instances).
- Saving is the game's business: a game that saves instances by object name would write a defined
  object's name. Stoneshard's room saver honours `roomEntityIsSavable = false`.

## Tested on {#tested}

`managed/Tests/ObjectTypeProbe` (see [shipped mods](../modding/reference/shipped-mods.md#regression-mods))
passes in Stoneshard, the Dwarf Eats Mountain demo (2024.14), The King is Watching and Slime Trader,
including a hot reload. Zero Stress King and TheSpikeCross stay "not proven yet" on their title
screens. A child of Stoneshard's `o_enemy` set up like the training dummy runs the enemy's Create and
Step, is listed among the enemies by StoneshardHarness (which walks the game's own unit lists), and 55 of
the game's spell and projectile objects collide with it.
