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

// The game's main thread, as first seen at Present. OnGameThread() is true on
// it - and on any thread until it is known, so start-up is not blocked.
void        NoteGameThread();
bool        OnGameThread();

// Proves the located free/copy helpers behave as such, on a probe string.
// Game thread, once; until it passes, FreeValue/CopyValue refuse.
void        VerifyValueLifetime();

void        SetReal(RValue& v, double value);
void        SetUndefined(RValue& v);
bool        SetString(RValue& v, const char* text);
std::string ToString(const RValue& v);

// Calls a YYC script:
//   RValue* f(CInstance* self, CInstance* other, RValue* result, int argc, RValue** args)
// `self`/`other` default to the game's current-instance global when null.
bool Call(void* func, RValue* result, RValue** args, int argc);

// Same, but with an explicit instance context - used to replay a captured call.
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

// ---------------------------------------------------------------- observation
//
// Rather than infer a script's signature from disassembly, detour it and let the
// GAME call it: that records the exact self/other instances, argument count and
// argument values it really uses. Replaying those removes all the guesswork.

struct Capture {
    bool                     valid = false;
    void*                    self  = nullptr;
    void*                    other = nullptr;
    int                      argc  = 0;
    std::vector<std::string> args;      // "kind=N value" per argument, for display
    std::vector<RValue>      raw;       // the exact values, for replay
    std::string              symbol;
    std::string              caller;   // function that invoked it
    unsigned                 hits = 0;
};

// A dedicated, always-on recorder for scr_weapon_loot. Gear can only be spawned
// by replaying a genuine call (the spawn point and instance cannot be
// synthesised), so the mod keeps the most recent one on hand and the UI needs no
// capture ritual. Independent of the user-driven capture above.
// Tracks the player instance every frame and works out where x/y live inside a
// CInstance by watching which doubles change as the player moves. With those we
// can build a weapon-spawn call from scratch - no enemy spawn needed - and drop
// the item at the player's feet rather than at some recorded NPC.
bool  InstallPlayerTracker();
void* PlayerInstance();
bool  PlayerPosition(double& x, double& y);

// Bounded, fault-tolerant read of game memory. Used by the inspector to walk a
// CInstance without risking a fault on a short allocation.
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

bool           InstallWeaponRecorder();
const Capture& WeaponRecord();

bool           InstallCapture(const std::string& symbol);
const Capture& LastCapture();

// When a capture exists, use its self/other for our own calls instead of the
// borrowed current-instance global.
void SetUseCapturedContext(bool on);
bool UseCapturedContext();

} // namespace mod::gml
