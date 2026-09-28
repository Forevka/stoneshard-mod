#include "host/core_api.h"

#include "builtins.h"
#include "gml.h"
#include "hookengine.h"
#include "log.h"
#include "symbols.h"

#include <windows.h>
#include <algorithm>
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

std::int32_t ApiGmlReady()    { return gml::Ready() ? 1 : 0; }
std::int32_t ApiAbiProven()   { return gml::AbiProven() ? 1 : 0; }
void*        ApiCurrentSelf() { return gml::CurrentSelf(); }

std::int32_t ApiCallScript(void* func, void* self, void* other, CoreRValue* result,
                           CoreRValue* args, std::int32_t argc) {
    if (!func || !result || argc < 0 || (argc > 0 && !args)) return 0;

    // Scripts take an array of POINTERS; the managed side hands a flat array.
    std::vector<gml::RValue*> ptrs(static_cast<std::size_t>(argc));
    for (std::int32_t i = 0; i < argc; ++i) ptrs[i] = Gml(&args[i]);

    gml::RValue** argv = argc ? ptrs.data() : nullptr;
    const bool ok = (self || other)
        ? gml::CallAs(func, Gml(result), argv, argc, self, other)
        : gml::Call(func, Gml(result), argv, argc);
    return ok ? 1 : 0;
}

std::int32_t ApiCallEvent(void* func, void* self, void* other) {
    if (!func || !self) return 0;
    return gml::CallEvent(func, self, other) ? 1 : 0;
}

std::int32_t ApiCallBuiltin(const char* name, CoreRValue* result, CoreRValue* args,
                            std::int32_t argc, void* self, void* other) {
    if (!name || !result || argc < 0 || (argc > 0 && !args)) return 0;
    // Builtins read `self` even when they ignore it; borrow a live instance.
    if (!self) self = gml::CurrentSelf();
    return builtins::Call(name, Gml(result), Gml(args), argc, self, other) ? 1 : 0;
}

std::int32_t ApiBuiltinCount() { return static_cast<std::int32_t>(builtins::Count()); }

std::int32_t ApiSetString(CoreRValue* value, const char* text) {
    if (!value || !text) return 0;
    return gml::SetString(*Gml(value), text) ? 1 : 0;
}

std::int32_t ApiToString(const CoreRValue* value, char* buffer, std::int32_t capacity) {
    if (!value) return 0;
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
    if (!instance || !name || !out) return 0;
    return builtins::GetVar(builtins::SelfHandle(instance), InternName(name), Gml(out)) ? 1 : 0;
}

std::int32_t ApiVarSet(void* instance, const char* name, const CoreRValue* value) {
    if (!instance || !name || !value) return 0;
    return builtins::SetVar(builtins::SelfHandle(instance), InternName(name), *Gml(value)) ? 1 : 0;
}

// ------------------------------------------------------------------- hooks

static_assert(sizeof(CoreHookCall) == sizeof(hk::Call), "hook call layouts must match");
static_assert(offsetof(CoreHookCall, skip) == offsetof(hk::Call, skip), "hook call layouts must match");
static_assert(offsetof(CoreHookCall, hook_id) == offsetof(hk::Call, hookId), "hook call layouts must match");

std::int32_t ApiHookInstall(void* target, std::int32_t kind) {
    if (kind != 0 && kind != 1) return -1;
    return hk::Install(target, kind == 0 ? hk::Kind::Script : hk::Kind::Event);
}

std::int32_t ApiHookSetManaged(std::int32_t id, std::int32_t managed) {
    return hk::SetManaged(id, managed != 0) ? 1 : 0;
}

std::int32_t ApiHookCount() { return hk::Count(); }

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
    return a;
}

} // namespace

const CoreApi* Api() {
    static const CoreApi api = Build();
    return &api;
}

} // namespace mod::host
