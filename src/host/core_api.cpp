#include "host/core_api.h"

#include "builtins.h"
#include "gml.h"
#include "hookengine.h"
#include "log.h"
#include "overlay.h"
#include "symbols.h"

#include <windows.h>
#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstring>
#include <filesystem>
#include <string>
#include <unordered_set>
#include <vector>

#include "imgui.h"

namespace mod::host {
namespace {

static_assert(sizeof(CoreRValue) == sizeof(gml::RValue), "RValue layouts must match");
static_assert(offsetof(CoreRValue, kind) == 0xC, "RValue.kind lives at +0xC");

gml::RValue*       Gml(CoreRValue* v)       { return reinterpret_cast<gml::RValue*>(v); }
const gml::RValue* Gml(const CoreRValue* v) { return reinterpret_cast<const gml::RValue*>(v); }

std::string Utf8(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                                      nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                        out.data(), n, nullptr, nullptr);
    return out;
}

std::filesystem::path ModulePath(HMODULE m) {
    wchar_t buf[MAX_PATH * 2];
    const DWORD n = GetModuleFileNameW(m, buf, static_cast<DWORD>(std::size(buf)));
    return std::filesystem::path(std::wstring(buf, n));
}

HMODULE ThisModule() {
    HMODULE m = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&ThisModule), &m);
    return m;
}

// Resolved once; these never change for the life of the process.
struct Paths {
    std::string gameName, gameDir, loaderDir;
    Paths() {
        const auto exe = ModulePath(nullptr);
        gameName  = Utf8(exe.stem().wstring());
        gameDir   = Utf8(exe.parent_path().wstring());
        loaderDir = Utf8((ModulePath(ThisModule()).parent_path() / L"CoreLoader").wstring());
    }
};
const Paths& P() { static Paths p; return p; }

// ------------------------------------------------------------------ loader

void ApiLog(std::int32_t level, const char* source, const char* message) {
    const char* tag = level >= kCoreLogWarn ? "[!] " : "";
    Logf("%s[%s] %s", tag, source ? source : "managed", message ? message : "");
}

const char* ApiGameName()  { return P().gameName.c_str(); }
const char* ApiGameDir()   { return P().gameDir.c_str(); }
const char* ApiLoaderDir() { return P().loaderDir.c_str(); }

// ----------------------------------------------------------------- symbols

std::int32_t ApiSymbolCount() { return static_cast<std::int32_t>(sym::All().size()); }

const char* ApiSymbolName(std::int32_t i) {
    const auto& all = sym::All();
    return (i >= 0 && static_cast<std::size_t>(i) < all.size()) ? all[i].name : nullptr;
}

void* ApiSymbolAddress(std::int32_t i) {
    const auto& all = sym::All();
    return (i >= 0 && static_cast<std::size_t>(i) < all.size()) ? all[i].func : nullptr;
}

void* ApiSymbolFind(const char* name) { return name ? sym::Find(name) : nullptr; }

// -------------------------------------------------------------- GML bridge

// The runtime is single-threaded: a GML call, a string or a reference count
// touched from another thread races the game and corrupts it silently. The
// managed side already refuses; this also covers native plugins and any path
// that slips past it. Logged a few times, then quietly.
bool GameThreadOnly(const char* what) {
    if (gml::OnGameThread()) return true;
    static std::atomic<int> reported{0};
    if (reported.fetch_add(1) < 8)
        Logf("[!] core api: %s called off the game thread (thread %lu); refused", what,
             static_cast<unsigned long>(GetCurrentThreadId()));
    return false;
}

std::int32_t ApiGmlReady()   { return gml::Ready() ? 1 : 0; }
std::int32_t ApiAbiProven()   { return gml::AbiProven() ? 1 : 0; }
void*        ApiCurrentSelf() { return gml::CurrentSelf(); }

std::int32_t ApiCallScript(void* func, void* self, void* other, CoreRValue* result,
                           CoreRValue* args, std::int32_t argc) {
    if (!func || !result || argc < 0 || (argc > 0 && !args)) return 0;
    if (!GameThreadOnly("call_script")) return 0;

    // A null self means "whatever the game last ran as" - never the debug
    // console's captured context, which may be long gone.
    if (!self) self = gml::CurrentSelf();
    if (!other) other = self;
    if (!self) {
        Logf("[!] core api: call_script with no instance to run as; refused");
        return 0;
    }

    // The script gets its own copies of the arguments: GML may assign to an
    // argument slot, which releases what was there - and the caller's values
    // belong to the caller (the managed pool frees them at the end of the
    // frame). Scripts take an array of POINTERS; the managed side hands a flat one.
    const bool copies = gml::CanCopyValues() && gml::CanFreeValues();
    std::vector<gml::RValue> own(static_cast<std::size_t>(argc));
    std::vector<gml::RValue*> ptrs(static_cast<std::size_t>(argc));
    for (std::int32_t i = 0; i < argc; ++i) {
        if (!copies || !gml::CopyValue(own[i], *Gml(&args[i]))) own[i] = *Gml(&args[i]);
        ptrs[i] = &own[i];
    }

    gml::RValue** argv = argc ? ptrs.data() : nullptr;
    const bool ok = gml::CallAs(func, Gml(result), argv, argc, self, other);
    // Whatever the slots hold now is ours to release, as a compiled caller would.
    if (copies) for (auto& v : own) gml::FreeValue(v);
    return ok ? 1 : 0;
}

std::int32_t ApiCallEvent(void* func, void* self, void* other) {
    if (!func || !self || !GameThreadOnly("call_event")) return 0;
    return gml::CallEvent(func, self, other) ? 1 : 0;
}

std::int32_t ApiCallBuiltin(const char* name, CoreRValue* result, CoreRValue* args,
                            std::int32_t argc, void* self, void* other) {
    if (!name || !result || argc < 0 || (argc > 0 && !args)) return 0;
    if (!GameThreadOnly("call_builtin")) return 0;
    // Builtins read `self` even when they ignore it; borrow a live instance.
    if (!self) self = gml::CurrentSelf();
    return builtins::Call(name, Gml(result), Gml(args), argc, self, other) ? 1 : 0;
}

std::int32_t ApiBuiltinCount() { return static_cast<std::int32_t>(builtins::Count()); }

std::int32_t ApiSetString(CoreRValue* value, const char* text) {
    if (!value || !text || !GameThreadOnly("set_string")) return 0;
    return gml::SetString(*Gml(value), text) ? 1 : 0;
}

std::int32_t ApiToString(const CoreRValue* value, char* buffer, std::int32_t capacity) {
    if (!value || !GameThreadOnly("to_string")) return 0;
    const std::string s = gml::ToString(*Gml(value));
    if (buffer && capacity > 0) {
        const std::size_t n = std::min<std::size_t>(s.size(), static_cast<std::size_t>(capacity) - 1);
        std::memcpy(buffer, s.data(), n);
        buffer[n] = '\0';
    }
    return static_cast<std::int32_t>(s.size());
}

// The variable name reaches the runtime as a GML string, and the runtime keeps
// a POINTER to its characters - a newly created variable may hold on to it for
// good. The caller's buffer only lives for the call, so names are interned here
// for the life of the process. There are only ever as many as a game's mods use.
const char* InternName(const char* name) {
    static std::unordered_set<std::string> names;
    return names.emplace(name).first->c_str();
}

std::int32_t ApiVarGet(void* instance, const char* name, CoreRValue* out) {
    if (!instance || !name || !out || !GameThreadOnly("var_get")) return 0;
    return builtins::GetVar(builtins::SelfHandle(instance), InternName(name), Gml(out)) ? 1 : 0;
}

std::int32_t ApiVarSet(void* instance, const char* name, const CoreRValue* value) {
    if (!instance || !name || !value || !GameThreadOnly("var_set")) return 0;
    return builtins::SetVar(builtins::SelfHandle(instance), InternName(name), *Gml(value)) ? 1 : 0;
}

// ------------------------------------------------------------------- hooks

static_assert(sizeof(CoreHookCall) == sizeof(hk::Call), "hook call layouts must match");
static_assert(offsetof(CoreHookCall, skip) == offsetof(hk::Call, skip), "hook call layouts must match");
static_assert(offsetof(CoreHookCall, hook_id) == offsetof(hk::Call, hookId), "hook call layouts must match");

// Only the exact start of a known gml_* function, hooked with the calling
// convention its name implies. A mid-function address would have MinHook
// patch into the middle of code; the wrong kind makes the thunk read garbage
// registers as result/args - and a handler write through them.
std::int32_t ApiHookInstall(void* target, std::int32_t kind) {
    if (kind != 0 && kind != 1) return -1;
    const char* name = sym::OwnerOf(target);
    if (!name || sym::Find(name) != target) {
        Logf("[!] core api: hook_install(%p) is not the start of a gml_* function; refused", target);
        return -1;
    }
    // Scripts take (self, other, result, argc, args); object events, room
    // creation code and 2.3+ global-script initialisers take (self, other).
    const bool script = std::strncmp(name, "gml_Script_", 11) == 0;
    const bool event  = std::strncmp(name, "gml_Object_", 11) == 0 || std::strncmp(name, "gml_RoomCC_", 11) == 0 ||
                        std::strncmp(name, "gml_GlobalScript_", 17) == 0;
    if ((kind == 0 && !script) || (kind == 1 && !event)) {
        Logf("[!] core api: hook_install(%s) as %s does not match its calling convention; refused",
             name, kind == 0 ? "a script" : "an event");
        return -1;
    }
    return hk::Install(target, kind == 0 ? hk::Kind::Script : hk::Kind::Event);
}

std::int32_t ApiHookSetManaged(std::int32_t id, std::int32_t managed) {
    return hk::SetManaged(id, managed != 0) ? 1 : 0;
}

std::int32_t ApiHookCount() { return hk::Count(); }

std::int32_t ApiHookCallOriginal(const CoreHookCall* call, CoreRValue* result) {
    if (!call || !result || !GameThreadOnly("hook_call_original")) return 0;
    return hk::CallOriginal(reinterpret_cast<const hk::Call*>(call), Gml(result)) ? 1 : 0;
}

std::int32_t ApiMemoryRead(const void* src, void* dst, std::int32_t bytes) {
    return src && dst && bytes > 0 && gml::ReadMemory(src, dst, bytes) ? 1 : 0;
}

std::int32_t ApiHookEnable(std::int32_t id, std::int32_t enabled) {
    return (enabled ? hk::Enable(id) : hk::Disable(id)) ? 1 : 0;
}

std::int32_t ApiValueFree(CoreRValue* value) {
    return value && GameThreadOnly("value_free") && gml::FreeValue(*Gml(value)) ? 1 : 0;
}

std::int32_t ApiValueCopy(CoreRValue* dst, const CoreRValue* src) {
    return dst && src && GameThreadOnly("value_copy") && gml::CopyValue(*Gml(dst), *Gml(src)) ? 1 : 0;
}

const char* ApiBuiltinName(std::int32_t index) {
    if (!GameThreadOnly("builtin_name")) return nullptr;
    const auto& names = builtins::Names();
    return (index >= 0 && static_cast<std::size_t>(index) < names.size()) ? names[index] : nullptr;
}

std::int32_t ApiBuiltinArity(const char* name) {
    if (!name || !GameThreadOnly("builtin_arity")) return -2;
    const builtins::Builtin b = builtins::Find(name);
    return b.fn ? b.argc : -2;
}

// ---------------------------------------------------------------------- UI

std::int32_t UiBeginTabBar(const char* id)     { return ImGui::BeginTabBar(id) ? 1 : 0; }
void         UiEndTabBar()                     { ImGui::EndTabBar(); }
std::int32_t UiBeginTabItem(const char* label) { return ImGui::BeginTabItem(label) ? 1 : 0; }
void         UiEndTabItem()                    { ImGui::EndTabItem(); }
void         UiText(const char* t)             { ImGui::TextUnformatted(t ? t : ""); }
void         UiTextDisabled(const char* t)     { ImGui::TextDisabled("%s", t ? t : ""); }
void UiTextColored(float r, float g, float b, float a, const char* t) {
    ImGui::TextColored(ImVec4(r, g, b, a), "%s", t ? t : "");
}
std::int32_t UiButton(const char* label) { return ImGui::Button(label) ? 1 : 0; }
std::int32_t UiCheckbox(const char* label, std::int32_t* value) {
    if (!value) return 0;
    bool b = *value != 0;
    const bool changed = ImGui::Checkbox(label, &b);
    *value = b ? 1 : 0;
    return changed ? 1 : 0;
}
std::int32_t UiSliderFloat(const char* label, float* v, float lo, float hi) {
    return v && ImGui::SliderFloat(label, v, lo, hi) ? 1 : 0;
}
std::int32_t UiInputInt(const char* label, std::int32_t* v) {
    return v && ImGui::InputInt(label, v) ? 1 : 0;
}
std::int32_t UiInputText(const char* label, char* buf, std::int32_t cap) {
    return buf && cap > 0 && ImGui::InputText(label, buf, static_cast<std::size_t>(cap)) ? 1 : 0;
}
std::int32_t UiCollapsingHeader(const char* label) { return ImGui::CollapsingHeader(label) ? 1 : 0; }
void UiSameLine()             { ImGui::SameLine(); }
void UiSeparator()            { ImGui::Separator(); }
void UiPushId(const char* id) { ImGui::PushID(id ? id : ""); }
void UiPopId()                { ImGui::PopID(); }

std::int32_t UiInputTextFlags(const char* label, char* buf, std::int32_t cap, std::int32_t flags) {
    return buf && cap > 0 && ImGui::InputText(label, buf, static_cast<std::size_t>(cap),
                                              static_cast<ImGuiInputTextFlags>(flags)) ? 1 : 0;
}
std::int32_t UiBeginChild(const char* id, float height, std::int32_t border) {
    return ImGui::BeginChild(id ? id : "##child", ImVec2(0.0f, height),
                             border ? ImGuiChildFlags_Borders : ImGuiChildFlags_None,
                             ImGuiWindowFlags_HorizontalScrollbar) ? 1 : 0;
}
void  UiEndChild()                  { ImGui::EndChild(); }
void  UiSetKeyboardFocusHere()      { ImGui::SetKeyboardFocusHere(); }
void  UiSetScrollHereY(float ratio) { ImGui::SetScrollHereY(ratio); }
std::int32_t UiIsKeyPressed(std::int32_t key) {
    if (key < ImGuiKey_NamedKey_BEGIN || key >= ImGuiKey_NamedKey_END) return 0;
    return ImGui::IsKeyPressed(static_cast<ImGuiKey>(key)) ? 1 : 0;
}
float UiGetScrollY()    { return ImGui::GetScrollY(); }
float UiGetScrollMaxY() { return ImGui::GetScrollMaxY(); }

struct HistoryState {
    const char* const* items;
    std::int32_t       count;
    std::int32_t*      cursor;
};

int HistoryCallback(ImGuiInputTextCallbackData* data) {
    auto* st = static_cast<HistoryState*>(data->UserData);
    if (data->EventFlag != ImGuiInputTextFlags_CallbackHistory || !st || st->count <= 0) return 0;

    std::int32_t cur = *st->cursor;
    if (cur >= st->count) cur = -1;   // the caller's history shrank since last frame
    if (data->EventKey == ImGuiKey_UpArrow) {
        cur = cur < 0 ? st->count - 1 : (cur > 0 ? cur - 1 : 0);
    } else if (data->EventKey == ImGuiKey_DownArrow) {
        if (cur < 0) return 0;
        cur = cur + 1 < st->count ? cur + 1 : -1;
    }
    *st->cursor = cur;
    data->DeleteChars(0, data->BufTextLen);
    if (cur >= 0 && st->items[cur]) data->InsertChars(0, st->items[cur]);
    return 0;
}

void ApiInputPickArm(std::int32_t armed) { OverlaySetPick(armed != 0); }

std::int32_t ApiInputPickTake(std::int32_t* x, std::int32_t* y, std::int32_t* w, std::int32_t* h,
                              std::int32_t* button) {
    int px = 0, py = 0, pw = 0, ph = 0, pb = 0;
    if (!OverlayTakePick(&px, &py, &pw, &ph, &pb)) return 0;
    if (x) *x = px;
    if (y) *y = py;
    if (w) *w = pw;
    if (h) *h = ph;
    if (button) *button = pb;
    return 1;
}

std::int32_t UiTreeNode(const char* label) { return ImGui::TreeNode(label ? label : "") ? 1 : 0; }
void UiTreePop()                           { ImGui::TreePop(); }
void UiSetClipboard(const char* text)      { ImGui::SetClipboardText(text ? text : ""); }

// The registry resolves lazily on first use and is rebuilt on the game thread;
// lookups from anywhere else would race that.
void* ApiBuiltinAddress(const char* name) {
    return name && GameThreadOnly("builtin_address") ? builtins::Find(name).fn : nullptr;
}
const char* ApiBuiltinNameAt(std::int32_t index) {
    return GameThreadOnly("builtin_name_at") ? builtins::NameAt(index) : nullptr;
}

std::int32_t UiInputHistory(const char* label, char* buf, std::int32_t cap,
                            const char* const* history, std::int32_t count, std::int32_t* cursor) {
    if (!buf || cap <= 0 || !cursor) return 0;
    if (*cursor >= count || *cursor < -1) *cursor = -1;
    HistoryState st{history, history ? count : 0, cursor};
    const bool enter = ImGui::InputText(label, buf, static_cast<std::size_t>(cap),
                                        ImGuiInputTextFlags_EnterReturnsTrue |
                                        ImGuiInputTextFlags_CallbackHistory,
                                        &HistoryCallback, &st);
    // Keep the cursor in the line after Enter within the SAME frame: waiting a
    // frame lets fast typing fall through to the game in between.
    if (enter) ImGui::SetKeyboardFocusHere(-1);
    return enter ? 1 : 0;
}

CoreApi Build() {
    CoreApi a{};
    a.size    = sizeof(CoreApi);
    a.version = kCoreApiVersion;

    a.log        = &ApiLog;
    a.game_name  = &ApiGameName;
    a.game_dir   = &ApiGameDir;
    a.loader_dir = &ApiLoaderDir;

    a.symbol_count   = &ApiSymbolCount;
    a.symbol_name    = &ApiSymbolName;
    a.symbol_address = &ApiSymbolAddress;
    a.symbol_find    = &ApiSymbolFind;

    a.gml_ready     = &ApiGmlReady;
    a.abi_proven    = &ApiAbiProven;
    a.current_self  = &ApiCurrentSelf;
    a.call_script   = &ApiCallScript;
    a.call_event    = &ApiCallEvent;
    a.call_builtin  = &ApiCallBuiltin;
    a.builtin_count = &ApiBuiltinCount;
    a.set_string    = &ApiSetString;
    a.to_string     = &ApiToString;
    a.var_get       = &ApiVarGet;
    a.var_set       = &ApiVarSet;

    a.ui_begin_tab_bar     = &UiBeginTabBar;
    a.ui_end_tab_bar       = &UiEndTabBar;
    a.ui_begin_tab_item    = &UiBeginTabItem;
    a.ui_end_tab_item      = &UiEndTabItem;
    a.ui_text              = &UiText;
    a.ui_text_colored      = &UiTextColored;
    a.ui_text_disabled     = &UiTextDisabled;
    a.ui_button            = &UiButton;
    a.ui_checkbox          = &UiCheckbox;
    a.ui_slider_float      = &UiSliderFloat;
    a.ui_input_int         = &UiInputInt;
    a.ui_input_text        = &UiInputText;
    a.ui_collapsing_header = &UiCollapsingHeader;
    a.ui_same_line         = &UiSameLine;
    a.ui_separator         = &UiSeparator;
    a.ui_push_id           = &UiPushId;
    a.ui_pop_id            = &UiPopId;

    a.hook_install     = &ApiHookInstall;
    a.hook_set_managed = &ApiHookSetManaged;
    a.hook_count       = &ApiHookCount;
    a.builtin_arity    = &ApiBuiltinArity;
    a.hook_call_original = &ApiHookCallOriginal;
    a.builtin_name       = &ApiBuiltinName;
    a.value_free         = &ApiValueFree;
    a.value_copy         = &ApiValueCopy;
    a.hook_enable        = &ApiHookEnable;

    a.ui_input_text_flags        = &UiInputTextFlags;
    a.ui_begin_child             = &UiBeginChild;
    a.ui_end_child               = &UiEndChild;
    a.ui_set_keyboard_focus_here = &UiSetKeyboardFocusHere;
    a.ui_set_scroll_here_y       = &UiSetScrollHereY;
    a.ui_is_key_pressed          = &UiIsKeyPressed;
    a.ui_get_scroll_y            = &UiGetScrollY;
    a.ui_get_scroll_max_y        = &UiGetScrollMaxY;
    a.ui_input_history           = &UiInputHistory;
    a.memory_read                = &ApiMemoryRead;
    a.input_pick_arm             = &ApiInputPickArm;
    a.input_pick_take            = &ApiInputPickTake;
    a.ui_tree_node               = &UiTreeNode;
    a.ui_tree_pop                = &UiTreePop;
    a.ui_set_clipboard           = &UiSetClipboard;
    a.builtin_address            = &ApiBuiltinAddress;
    a.builtin_name_at            = &ApiBuiltinNameAt;
    return a;
}

} // namespace

const CoreApi* Api() {
    static const CoreApi api = Build();
    return &api;
}

} // namespace mod::host
