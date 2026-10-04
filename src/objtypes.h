#pragma once

#include "hookengine.h"

#include <cstdint>

namespace mod::objtypes {

// New GameMaker objects, defined at runtime.
//
// A YYC game has no data.win code to recompile: its objects are CObjectGM
// structs the runner builds at start-up, in a hash keyed by object index. Each
// holds a map from (event type << 32 | subtype) to a CEvent, whose CCode points
// at the compiled gml_Object_<name>_<Event>_<n> function through a
// {name, function} row. Inheritance is flattened: a child's map also holds its
// parent's CEvents for every event it does not define itself. Per-event lists
// of the objects that have an event (what drives Step, Draw, Alarm ...) are
// rebuilt from those maps by one runner function.
//
// A defined object is made the way the runner makes its own internal object
// (the inlined Object_Add that names "__YYInternalObject__"), and an event
// implemented in C# is a CEvent whose CCode row points at a hook-engine thunk.
// Everything is located by pattern and proven against the game's own objects
// before use (Verify); anywhere that fails, the feature is unavailable.

// Game thread, every frame: locates and proves the runtime pieces once the
// builtin registry is up. Settles once, proven or refused.
void        Verify();
bool        Ready();
// "available", or why not ("unavailable: ...", "not proven yet").
const char* Status();

// Game thread, every frame: rebuilds the runner's per-event object lists if a
// definition changed since the last frame.
void Flush();

// A new object named `name` with parent object `parent` (-1 for none), or the
// one an earlier Define already made under that name (re-parented if asked).
// Returns its object index, -1 on failure (reason logged). A name the game
// already uses for one of its own assets is refused.
int  Define(const char* name, int parent);

// Gives an object made by Define its own event (type, subtype) and returns the
// hook id whose calls are that event; the same id again for the same event.
// The event replaces what the object inherited for it. -1 on failure.
int  DefineEvent(int object, int type, int subtype);

// Runs, as `self`/`other`, the event the parent of the defined event's object
// has for the same (type, subtype) - GameMaker's event_inherited(). False when
// there is none or it failed. Game thread.
bool CallInherited(int hookId, void* self, void* other);

// Whether `object` was made by Define.
bool IsDefined(int object);

} // namespace mod::objtypes
