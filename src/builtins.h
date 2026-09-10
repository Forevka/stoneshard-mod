#pragma once

#include "gml.h"

#include <cstddef>
#include <string>
#include <vector>

namespace mod::builtins {

// GameMaker's ~2,500 built-in functions (network_*, buffer_*, variable_instance_*,
// instance_*, ds_map_* ...) are NOT in the gml_* script table - they are direct-called
// and registered at runtime into a heap array. Reaching them unlocks the whole GML
// standard library, including reflection, which is how instance fields become readable
// by NAME instead of by guessed memory offset.
//
// Resolution is anchored on builtin name strings, never addresses, so it survives a
// game update the same way the script resolver does.
bool        Init();          // lazy; safe to call repeatedly
bool        Ready();
const char* Status();
std::size_t Count();

struct Builtin {
    void* fn   = nullptr;
    int   argc = 0;          // -1 == variadic
};

// Exact name, e.g. "variable_instance_get".
Builtin Find(const std::string& name);

// Builtins use a DIFFERENT ABI from YYC scripts:
//     void TRoutine(RValue* result, CInstance* self, CInstance* other,
//                   int argc, RValue* args)
// The result is the FIRST parameter and `args` is a CONTIGUOUS array, not an array of
// pointers. Passing the script-style layout dereferences argument values as pointers
// and crashes, so this must never route through gml::Call.
bool Call(const std::string& name, gml::RValue* result,
          gml::RValue* args, int argc, void* self, void* other = nullptr);

// Reads an instance variable by name via the reflection API. `self` supplies the
// instance context; GML's -1 ("self") selects it.
bool GetInstanceVar(void* instance, const char* name, gml::RValue* out);

// ---------------------------------------------------------------- reflection
//
// The reflection API takes a GML instance handle as its first argument.
//
// Two forms work, and which one you need depends on the target:
//   - real -1 ("self"), which only means anything when `self` is also supplied.
//     Verified working through a TRoutine on 2026-09-09.
//   - an instance reference. `instance_find` returns kind 15 (kRef) in this
//     runtime, NOT a number - assuming a numeric id is what made the first
//     attempt fail. The reference is passed straight back through, never
//     decoded.
//
// The handle carries whichever it has plus the CInstance* for `self`, so one
// call site works for the player and for any other instance.
struct Handle {
    gml::RValue id{};            // set to real -1 by PlayerHandle when no ref resolved
    void*       self = nullptr;  // CInstance* passed as the TRoutine's `self`
    bool        haveRef = false; // true when `id` is a real instance reference
};

// The local player. The object index is cached; the reference is re-resolved
// per call rather than held, since the mod does not manage its lifetime.
Handle PlayerHandle();

// Builds a "self" handle for an arbitrary instance: real -1 plus that
// CInstance*. This is the path that is known to work.
Handle SelfHandle(void* instance);

bool GetVar(const Handle& h, const char* name, gml::RValue* out);
bool SetVar(const Handle& h, const char* name, const gml::RValue& value);

// Total variable count (-1 on failure). Fills `out` with up to `limit` names
// (all of them when limit <= 0). The names come back through the game's own
// array_get rather than by decoding a GML array's memory layout, which was
// never established.
int VarNames(const Handle& h, std::vector<std::string>& out, int limit);

// The ABI gate plus the reflection probes that everything reading or writing
// instance fields depends on. MUST run on the game thread. Runs once per
// phase; safe to call every frame.
void        SelfTest();
bool        SelfTestPassed();
const char* SelfTestReport();

} // namespace mod::builtins
