#pragma once

// The C ABI between the native loader and the managed runtime.
//
// One struct of function pointers, handed to CoreLoader.dll once at start-up.
// The managed side mirrors it field for field (CoreLoader/Native/CoreApi.cs), so
// the two MUST change together: fields are only ever appended, `size` tells the
// managed side how many exist, and `version` is bumped whenever a field's
// meaning changes rather than just being added.
//
// Conventions, so neither side has to guess:
//   * Strings are UTF-8, NUL-terminated. Strings returned from here are owned by
//     the loader and valid until the next call of the same function on the same
//     thread unless stated otherwise - copy them.
//   * `int` results are 1 for success and 0 for failure; the reason goes to the log.
//   * GML values travel as the runtime's own 16-byte RValue. Argument lists are
//     CONTIGUOUS arrays; the loader builds whatever layout the callee needs.
//   * Everything that touches GML (calls, variables, strings) must run on the
//     game thread - i.e. from inside a frame, GUI or hook callback.

#include <cstdint>

extern "C" {

struct CoreRValue {
    union {
        double       real;
        void*        ptr;
        std::int64_t i64;
        std::int32_t i32;
    };
    std::int32_t flags;
    std::int32_t kind;
};

enum CoreLogLevel : std::int32_t {
    kCoreLogInfo  = 0,
    kCoreLogWarn  = 1,
    kCoreLogError = 2,
};

constexpr std::int32_t kCoreApiVersion = 5;   // 2: hooks, 3: builtin_arity, 4: hook_call_original, 5: builtin_name

struct CoreApi {
    std::int32_t size;      // sizeof(CoreApi) as the loader was built
    std::int32_t version;   // kCoreApiVersion

    // ------------------------------------------------------------- loader
    void (*log)(std::int32_t level, const char* source, const char* message);
    const char* (*game_name)();          // exe file name without extension
    const char* (*game_dir)();           // folder holding the game exe
    const char* (*loader_dir)();         // folder holding CoreLoader.dll

    // ------------------------------------------------------------ symbols
    std::int32_t (*symbol_count)();
    const char*  (*symbol_name)(std::int32_t index);   // lives for the process
    void*        (*symbol_address)(std::int32_t index);
    void*        (*symbol_find)(const char* name);

    // ---------------------------------------------------------- GML bridge
    std::int32_t (*gml_ready)();         // runtime helpers resolved
    std::int32_t (*abi_proven)();        // self-test passed this session
    void*        (*current_self)();      // a live CInstance, or null

    // Scripts (gml_Script_*): `self`/`other` may be null for "the current one".
    std::int32_t (*call_script)(void* func, void* self, void* other,
                                CoreRValue* result, CoreRValue* args, std::int32_t argc);
    // Object events (gml_Object_*): two-argument convention, no result.
    std::int32_t (*call_event)(void* func, void* self, void* other);
    // GameMaker builtins by name, e.g. "instance_create_depth".
    std::int32_t (*call_builtin)(const char* name, CoreRValue* result,
                                 CoreRValue* args, std::int32_t argc,
                                 void* self, void* other);
    std::int32_t (*builtin_count)();

    // The runtime keeps a POINTER to `text`; the caller must keep it alive for
    // as long as the value may be referenced.
    std::int32_t (*set_string)(CoreRValue* value, const char* text);
    // Writes up to `capacity` bytes (always NUL-terminated when capacity > 0)
    // and returns the full length, so a short buffer can be retried.
    std::int32_t (*to_string)(const CoreRValue* value, char* buffer, std::int32_t capacity);

    // Instance variables by name, on any live CInstance.
    std::int32_t (*var_get)(void* instance, const char* name, CoreRValue* out);
    std::int32_t (*var_set)(void* instance, const char* name, const CoreRValue* value);

    // ------------------------------------------------------------------ UI
    // Dear ImGui, callable only from the managed GUI callback.
    std::int32_t (*ui_begin_tab_bar)(const char* id);
    void         (*ui_end_tab_bar)();
    std::int32_t (*ui_begin_tab_item)(const char* label);
    void         (*ui_end_tab_item)();
    void         (*ui_text)(const char* text);
    void         (*ui_text_colored)(float r, float g, float b, float a, const char* text);
    void         (*ui_text_disabled)(const char* text);
    std::int32_t (*ui_button)(const char* label);
    std::int32_t (*ui_checkbox)(const char* label, std::int32_t* value);
    std::int32_t (*ui_slider_float)(const char* label, float* value, float min, float max);
    std::int32_t (*ui_input_int)(const char* label, std::int32_t* value);
    std::int32_t (*ui_input_text)(const char* label, char* buffer, std::int32_t capacity);
    std::int32_t (*ui_collapsing_header)(const char* label);
    void         (*ui_same_line)();
    void         (*ui_separator)();
    void         (*ui_push_id)(const char* id);
    void         (*ui_pop_id)();

    // --------------------------------------------------------------- hooks
    // Detours a gml_* function (kind 0 = script, 1 = object event). One native
    // hook per target however many subscribers the managed side has; returns
    // its id, the same id again for the same target, or -1.
    std::int32_t (*hook_install)(void* target, std::int32_t kind);
    // Whether calls through the hook are routed to ManagedExports.hook_dispatch.
    std::int32_t (*hook_set_managed)(std::int32_t id, std::int32_t managed);
    std::int32_t (*hook_count)();

    // A builtin's registered argument count: -1 variadic, -2 not found.
    std::int32_t (*builtin_arity)(const char* name);

    // Inside a hook handler: run the unhooked original script once more with
    // the call's own self/other/arguments. Handlers do not see this call.
    std::int32_t (*hook_call_original)(const struct CoreHookCall* call, CoreRValue* result);

    // Builtins by index, sorted by name; 0..builtin_count()-1. Lives for the process.
    const char*  (*builtin_name)(std::int32_t index);
};

// Mirror of mod::hk::Call - what a hook callback sees.
struct CoreHookCall {
    void*         self;
    void*         other;
    CoreRValue*   result;    // null for events
    CoreRValue**  args;      // null for events
    std::int32_t  argc;
    std::int32_t  phase;     // 0 before the original, 1 after
    std::int32_t  skip;      // set during phase 0 to suppress the original
    std::int32_t  hook_id;
};

// Filled in by the managed runtime's Init. Every entry is required.
struct ManagedExports {
    std::int32_t size;
    void (*frame)();                 // once per rendered frame, game thread
    void (*gui)();                   // inside the overlay's Mods tab
    void (*shutdown)();
    void (*hook_dispatch)(CoreHookCall* call);
};

} // extern "C"

namespace mod::host {

// The loader's single instance of the table.
const CoreApi* Api();

} // namespace mod::host
