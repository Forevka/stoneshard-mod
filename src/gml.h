#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace mod::gml {

// GameMaker RValue kinds, read off the compiled code.
enum Kind : std::int32_t {
    kReal      = 0,
    kString    = 1,
    kArray     = 2,
    kPtr       = 3,
    kUndefined = 5,
    kObject    = 6,
    kInt32     = 7,
    kInt64     = 10,
    kBool      = 13,
    kRef       = 15,   // reference to an instance/struct - cannot be built from scratch
    kUnset     = 0x00FFFFFF,
};

// 16 bytes: value at +0, flags at +8, kind at +0xC. Confirmed from the
// compiled locals (`mov qword [x],0` / `mov dword [x+0xC],0xffffff`) and from
// YYSetString writing kind 1 at +0xC.
struct RValue {
    union {
        double         real;
        void*          ptr;
        std::int64_t   i64;
        std::int32_t   i32;
    };
    std::int32_t flags;
    std::int32_t kind;
};
static_assert(sizeof(RValue) == 16, "RValue must be 16 bytes");

// What YYSetString builds. `chars` is stored WITHOUT copying; the high bit of
// `length` marks the buffer as external, so our own storage must outlive the call.
struct RefString {
    const char*  chars;
    std::int32_t refCount;
    std::int32_t length;   // | 0x80000000 when chars is external
};

// Locates the runtime helpers by scanning many resolved functions for a shared
// code pattern and taking the consensus target. No addresses are hardcoded, so
// this survives a game update that relocates everything.
bool        Init();
bool        Ready();
const char* Status();

// Text of the last GML runtime error, when the game rejected a call rather
// than faulting. Empty if nothing was recovered.
const char* LastError();

// Value lifetime, through the runtime's own helpers (located at Init). A
// string, array or struct value holds a reference; FreeValue drops it and
// leaves v undefined, CopyValue makes dst an additional owner of src's value.
// Both return false when the helper could not be found in this runtime.
bool        FreeValue(RValue& v);
bool        CopyValue(RValue& dst, const RValue& src);
bool        CanFreeValues();
bool        CanCopyValues();

// Managed code on this thread's stack (Frame, Gui, a managed hook handler).
// A GML exception must never unwind through .NET frames - that kills the
// process - so while one is present every call into the game catches it.
struct ManagedScope {
    ManagedScope();
    ~ManagedScope();
    ManagedScope(const ManagedScope&) = delete;
    ManagedScope& operator=(const ManagedScope&) = delete;
};
bool InManagedCode();

// The game's main thread, as first seen at Present. OnGameThread() is true
// only on it, and on no thread before it is known.
void        NoteGameThread();
bool        OnGameThread();

// Proves the located free/copy helpers behave as such, on a probe string.
// Game thread, once; until it passes, FreeValue/CopyValue refuse.
void        VerifyValueLifetime();

void        SetReal(RValue& v, double value);
void        SetUndefined(RValue& v);
bool        SetString(RValue& v, const char* text);

// Characters that live for the rest of the process, deduplicated. The runtime
// keeps a POINTER to a string's characters (a variable set from it, a script
// that stores its argument), so anything handed to SetString must come from
// here or from static storage - never from a local buffer. Thread-safe.
const char* Intern(const std::string& text);
std::string ToString(const RValue& v);

// Calls a YYC script:
//   RValue* f(CInstance* self, CInstance* other, RValue* result, int argc, RValue** args)
// `self`/`other` default to the game's current-instance global when null.
bool Call(void* func, RValue* result, RValue** args, int argc);

// Same, but with an explicit instance context.
bool CallAs(void* func, RValue* result, RValue** args, int argc, void* self, void* other);

// Object events compile to a SMALLER signature than scripts:
//     void Event(CInstance* self, CInstance* other)
// Calling one with the 5-argument script signature corrupts the stack, so
// anything named gml_Object_* must go through here instead.
bool CallEvent(void* func, void* self, void* other);
bool IsEventSymbol(const std::string& symbol);
bool CallByName(const std::string& symbol, RValue* result, RValue** args, int argc);

// Proves the ABI end to end by calling zero-argument *_help scripts, which just
// return a string. MUST be invoked from the game's own thread (the Present
// hook), never from our init thread. Runs once; safe to call every frame.
void AbiSelfTest();
bool AbiProven();

// Bounded, fault-tolerant read of game memory, for walking a CInstance
// without risking a fault on a short allocation.
bool  ReadMemory(const void* src, void* dst, int bytes);

// Whatever instance the game last ran code as. Null until the game has run
// some GML, which is why anything calling into the runtime waits for it.
// Builtins need a plausible `self` even when they never look at it.
void* CurrentSelf();

// Records an instance the game was just seen running code as. The hook engine
// calls this on every hooked call; on runtimes without a current-self global
// (2024+) it is the only source CurrentSelf() has.
void NoteSelf(void* self);

// Forgets the observed instance. Called once per frame after the loader's own
// work, so what CurrentSelf() hands out was seen running during the frame in
// progress - never an instance destroyed by a room change long ago.
void ClearObservedSelf();

// Whether this runtime exposes a current-self global (older runtimes do).
bool HasSelfGlobal();

} // namespace mod::gml
