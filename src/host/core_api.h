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
//   * Everything that touches GML (calls, variables, strings, value free/copy)
//     must run on the game thread - i.e. from inside a frame, GUI or hook
//     callback. From version 9 such calls fail (return 0) on any other thread.

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

constexpr std::int32_t kCoreApiVersion = 11;  // 11: object types (objtype_*)   // 10: ui round 3 (combo, selectable, disabled, clipper, is_item_deactivated_after_edit, ...), last_gml_error, instance_from_id   // 9: pick mode, tree nodes, clipboard; GML calls refused off the game thread   // 7: ui round 2, 8: memory_read   // 2: hooks, 3: builtin_arity, 4: hook_call_original, 5: builtin_name, 6: value_free/copy

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

    // Value lifetime through the runtime's own helpers. value_free drops the
    // reference a string/array/struct holds and leaves the value undefined;
    // value_copy makes dst an additional owner of src (dst must hold nothing).
    // Both return 0 when this runtime's helper was not found.
    std::int32_t (*value_free)(CoreRValue* value);
    std::int32_t (*value_copy)(CoreRValue* dst, const CoreRValue* src);

    // Detaches (0) or re-attaches (1) a hook's detour. A hook nobody listens
    // to is detached so the function runs at full speed again.
    std::int32_t (*hook_enable)(std::int32_t id, std::int32_t enabled);

    // UI, round 2 (for consoles and log views).
    // Text input with ImGuiInputTextFlags; with EnterReturnsTrue (64) it returns
    // 1 only on the frame Enter was pressed.
    std::int32_t (*ui_input_text_flags)(const char* label, char* buffer, std::int32_t capacity, std::int32_t flags);
    // A scrolling region `height` pixels tall (0 = fill, negative = leave that much below).
    std::int32_t (*ui_begin_child)(const char* id, float height, std::int32_t border);
    void         (*ui_end_child)();
    void         (*ui_set_keyboard_focus_here)();
    void         (*ui_set_scroll_here_y)(float ratio);
    std::int32_t (*ui_is_key_pressed)(std::int32_t imguiKey);
    float        (*ui_get_scroll_y)();
    float        (*ui_get_scroll_max_y)();
    // A single-line input with shell-style history: Up/Down walk `history`
    // (oldest first, `count` entries) and *cursor tracks the position (-1 =
    // editing a new line). Returns 1 on the frame Enter was pressed.
    std::int32_t (*ui_input_history)(const char* label, char* buffer, std::int32_t capacity,
                                     const char* const* history, std::int32_t count, std::int32_t* cursor);

    // Fault-safe read of game memory (code or data). Returns 1 when all
    // `bytes` were readable. For tools that inspect compiled code.
    std::int32_t (*memory_read)(const void* src, void* dst, std::int32_t bytes);

    // Pick mode (version 9): while armed, the next click outside the overlay's
    // windows is swallowed and reported once by input_pick_take - client
    // pixel position, client size, button (0 left, 1 right). Arming again or
    // disarming drops a click nobody took.
    void         (*input_pick_arm)(std::int32_t armed);
    std::int32_t (*input_pick_take)(std::int32_t* x, std::int32_t* y, std::int32_t* width,
                                    std::int32_t* height, std::int32_t* button);
    // Collapsible tree rows: pop only when tree_node returned 1.
    std::int32_t (*ui_tree_node)(const char* label);
    void         (*ui_tree_pop)();
    void         (*ui_set_clipboard)(const char* text);
    // The native function behind a builtin (null if not found): compiled code
    // calls it directly, so tools can name a call target.
    void*        (*builtin_address)(const char* name);
    // The builtin at a runner-registry index (what compiled code passes to
    // its builtin-call helper), or null. Lives for the process.
    const char*  (*builtin_name_at)(std::int32_t registryIndex);

    // UI, round 3 (version 10): what tool panels need. Scopes: end_combo only
    // when begin_combo returned 1; begin/end_disabled, push/pop_text_color and
    // clipper_begin/end always pair.
    std::int32_t (*ui_begin_combo)(const char* label, const char* preview);
    void         (*ui_end_combo)();
    // flags: ImGuiSelectableFlags (AllowOverlap = 16).
    std::int32_t (*ui_selectable)(const char* label, std::int32_t selected, std::int32_t flags);
    void         (*ui_separator_text)(const char* text);
    // format may be null for "%.3f".
    std::int32_t (*ui_input_double)(const char* label, double* value, double step, double stepFast,
                                    const char* format);
    std::int32_t (*ui_slider_int)(const char* label, std::int32_t* value, std::int32_t min,
                                  std::int32_t max, const char* format);
    void         (*ui_set_next_item_width)(float width);
    void         (*ui_begin_disabled)(std::int32_t disabled);
    void         (*ui_end_disabled)();
    std::int32_t (*ui_input_text_hint)(const char* label, const char* hint, char* buffer,
                                       std::int32_t capacity);
    void         (*ui_same_line_ex)(float offsetX, float spacing);
    void         (*ui_text_wrapped)(const char* text);
    void         (*ui_spacing)();
    std::int32_t (*ui_button_ex)(const char* label, float width, float height);
    std::int32_t (*ui_small_button)(const char* label);
    // fraction 0..1. width 0 fills the row, a negative width fills it but leaves
    // that much room on the right (ImGui's convention), a positive one is pixels.
    // overlay may be null.
    void         (*ui_progress_bar)(float fraction, float width, const char* overlay);
    void         (*ui_push_text_color)(float r, float g, float b, float a);
    void         (*ui_pop_text_color)();
    void         (*ui_set_item_tooltip)(const char* text);
    // A list clipper: begin, then step until it returns 0, drawing rows
    // [*start, *end) each time; end always. itemHeight <= 0 measures the first row.
    void         (*ui_clipper_begin)(std::int32_t count, float itemHeight);
    std::int32_t (*ui_clipper_step)(std::int32_t* start, std::int32_t* end);
    void         (*ui_clipper_end)();
    // 1 on the frame the last item (a slider or input) stopped being edited
    // after its value changed: the moment to commit an edit, once, instead of
    // writing on every frame of a drag.
    std::int32_t (*ui_is_item_deactivated_after_edit)();

    // Why the last failed call_script / call_event / call_builtin on this
    // thread failed: the GML error's message ("... (in gml_Script_x, line 12)")
    // when the runtime threw one, else the exception's type or code followed
    // by "no message recovered". Only meaningful right after a failed call;
    // empty when that call was refused before reaching the game. Never null.
    const char*  (*last_gml_error)();
    // The live CInstance for an instance id - a number, or a kind-15 instance
    // reference as instance_find returns on newer runtimes - or null when there
    // is no such active instance, or when this runtime's id lookup could not be
    // proven. With id null it is a probe, not a lookup: non-null (and not an
    // instance) once the lookup is proven, null otherwise. Game thread only.
    void*        (*instance_from_id)(const CoreRValue* id);

    // Object types (version 11): new GameMaker objects defined at runtime.
    // "available", or why not ("unavailable: ...", "not proven yet"). Never null.
    const char*  (*objtype_status)();
    // A new object named `name` (parent -1 for none), or the one already
    // defined under that name this session, re-parented if `parent` differs.
    // Its object index, or -1 (refused; the reason is logged). Game thread.
    std::int32_t (*objtype_define)(const char* name, std::int32_t parent);
    // Gives a defined object its own event (type, subtype). Returns the hook id
    // the event's calls dispatch under (hook_dispatch: phase 0, then 1; nothing
    // runs in between), the same id again for the same event, or -1. While no
    // managed handler is attached the parent's event runs instead.
    std::int32_t (*objtype_event)(std::int32_t object, std::int32_t type, std::int32_t subtype);
    // From inside a defined event's dispatch: runs the parent's event for the
    // same (type, subtype) as the call's self/other - event_inherited(). 1 if
    // one ran, 0 if there is none or it failed.
    std::int32_t (*objtype_call_inherited)(const CoreHookCall* call);
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
