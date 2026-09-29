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

// Every resolved builtin name, sorted. The pointers stay valid for the life of
// the process (they point at the map's own keys, which are never erased once
// resolution succeeds).
const std::vector<const char*>& Names();

// The builtin at a position in the runner's own registry. YYC code calls many
// builtins through a helper that takes this index (loaded from a global the
// runner fills at startup), not the function's address. Null if out of range.
const char* NameAt(int registryIndex);

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
// The handle carries the id argument plus the CInstance* for `self`.
struct Handle {
    gml::RValue id{};            // real -1 ("self") from SelfHandle
    void*       self = nullptr;  // CInstance* passed as the TRoutine's `self`
};

// Builds a "self" handle for an arbitrary instance: real -1 plus that
// CInstance*. This is the path that is known to work.
Handle SelfHandle(void* instance);

bool GetVar(const Handle& h, const char* name, gml::RValue* out);
bool SetVar(const Handle& h, const char* name, const gml::RValue& value);

// A struct member by name (variable_struct_get). `self` is only what the
// builtin is handed as its instance. False if the member is missing.
bool StructGet(const gml::RValue& structValue, const char* name, gml::RValue* out, void* self);

// The ABI gate that everything calling builtins depends on. MUST run on the
// game thread. Runs once; safe to call every frame.
void        SelfTest();
bool        SelfTestPassed();
const char* SelfTestReport();

} // namespace mod::builtins
