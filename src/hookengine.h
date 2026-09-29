#pragma once

#include "gml.h"

#include <cstdint>

namespace mod::hk {

// A generic detour engine for compiled GML.
//
// MinHook needs a distinct detour function per target and gives it no context,
// which would force a fixed number of template-instantiated slots. Here each
// hooked target gets a small thunk emitted at runtime that
// carries a pointer to its own record into ONE shared dispatcher, so the number
// of hooks is bounded only by the thunk arena.
//
//   scripts: RValue* f(self, other, result, argc, args**)   -> thunk builds a
//            frame and appends the record as a 6th argument; its unwind data is
//            registered, so an exception unwinding through a hooked frame still
//            walks the stack correctly.
//   events:  void f(self, other)                            -> thunk loads the
//            record into r8 (the unused 3rd argument) and tail-jumps.

enum class Kind : std::int32_t { Script = 0, Event = 1 };

enum Phase : std::int32_t { kBefore = 0, kAfter = 1 };

// What a hook callback sees. Shared with the managed side (CoreHookCall), so the
// layout is fixed: append only.
struct Call {
    void*         self;
    void*         other;
    gml::RValue*  result;    // null for events
    gml::RValue** args;      // null for events
    std::int32_t  argc;      // 0 for events
    std::int32_t  phase;     // Phase
    std::int32_t  skip;      // set in kBefore to suppress the original
    std::int32_t  hookId;
};

// Receives every call of every hook that has `managed` set.
using ManagedDispatch = void (*)(Call* call);
void SetManagedDispatch(ManagedDispatch fn);

// Hooks the gml_* function at `target`. Idempotent: hooking the same target
// again returns the same id. -1 on failure (reason in the log).
int  Install(void* target, Kind kind);

// Routes the hook's calls to the managed dispatcher (on) or not (off). A hook
// with no managed subscribers still observes `self` but costs no transition.
bool SetManaged(int id, bool managed);

// Detaches the detour. The thunk stays allocated: a call already inside it
// must be able to finish.
bool Disable(int id);
bool Enable(int id);

int  Count();

// Runs the ORIGINAL (unhooked) script of `call` once more with the same
// self/other/arguments, writing into `result`. Nothing hooked runs again, so a
// handler can repeat a call without re-entering itself. Scripts only.
bool CallOriginal(const Call* call, gml::RValue* result);

// The loader's own use: when the runtime has no current-self global (2024+),
// watch a spread of Step events so a live instance is always known.
void InstallSelfObservers(int maxEvents);

// How many hook dispatches are active on this thread (0 outside game code).
int DispatchDepth();

} // namespace mod::hk
