#pragma once

#include <cstddef>
#include <string>

namespace mod::tracer {

// Resolves the shared prologue helper that virtually every compiled script
// calls on entry. Hooking that one function traces ~9,260 functions at once,
// which is why this does not need 34,167 individual hooks.
bool        Init();
bool        Ready();
const char* Status();
void*       HelperAddress();

struct Options {
    int  durationMs = 3000;   // hard cap, so a burst cannot run away
    bool stacks     = false;  // call stacks are far more expensive per record
    char filter[64] = "";     // substring; empty records everything
};

// The hook is installed on start and removed on stop, so an idle session costs
// nothing at all.
bool StartRecording(const Options& opts);
void StopRecording();
bool Recording();

unsigned long long Recorded();
unsigned long long Dropped();
int                RemainingMs();
const std::string& LastFile();

// Called once per frame from the overlay to enforce the duration cap.
void Tick();

// ---------------------------------------------------------------- breakpoints
//
// Deliberately trap-and-report rather than halt. The overlay renders from the
// Present hook on the GAME'S OWN THREAD, so blocking inside a detour would
// freeze the renderer and the UI with it - a hang, not a pause. Instead a hit
// records arguments, self/other and a resolved call stack, which answers the
// same questions safely.
struct BreakpointInfo {
    std::string symbol;
    bool        active       = false;
    bool        skipOriginal = false;   // dangerous: suppresses the real call
    int         hitLimit     = 20;
    unsigned    hits         = 0;
    std::string lastReport;
};

bool                  SetBreakpoint(const std::string& symbol, bool skipOriginal, int hitLimit);
void                  ClearBreakpoint();
const BreakpointInfo& Breakpoint();

// Player activity, fed from the hooks that already exist elsewhere.
void NotePlayerStep(double x, double y, bool havePos);
void NoteKey(int virtualKey);

} // namespace mod::tracer
