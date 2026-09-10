#include "gamespeed.h"

#include "builtins.h"
#include "gml.h"
#include "log.h"

#include <cmath>
#include <cstdio>

#include "imgui.h"

namespace mod::gamespeed {
namespace {

// GameMaker's gamespeed_fps. It is a compile-time constant, so it appears
// nowhere in the binary as a string - 0 is the documented value and the only
// one that makes sense for a steps-per-second figure.
constexpr double kGamespeedFps = 0.0;

double g_baseline = 0.0;      // the game's own default, captured once
double g_target   = 0.0;      // what the user asked for; 0 = untouched
bool   g_hold     = true;
bool   g_warned   = false;

// Builtins want a `self` even when they ignore it.
void* Context() {
    void* p = gml::PlayerInstance();
    return p ? p : gml::CurrentSelf();
}

bool ReadSpeed(double* out) {
    if (!builtins::Ready()) return false;

    void* self = Context();
    if (!self) return false;

    gml::RValue arg{};
    gml::SetReal(arg, kGamespeedFps);

    gml::RValue r{};
    if (!builtins::Call("game_get_speed", &r, &arg, 1, self) || r.kind != gml::kReal)
        return false;
    if (r.real < 1.0 || r.real > 10000.0) return false;      // implausible; ignore

    *out = r.real;
    return true;
}

} // namespace

bool   Ready()    { return builtins::Ready() && Context() != nullptr; }
bool   Hold()     { return g_hold; }
void   SetHold(bool on) { g_hold = on; }
double Baseline() { return g_baseline; }

double Current() {
    double v = 0.0;
    if (!ReadSpeed(&v)) return 0.0;

    // First honest reading becomes the baseline, so Reset has a real value to
    // go back to rather than a guessed 60.
    if (g_baseline <= 0.0) {
        g_baseline = v;
        Logf("gamespeed: baseline is %g steps/sec", v);
    }
    return v;
}

bool Set(double stepsPerSecond) {
    if (!builtins::Ready()) return false;

    void* self = Context();
    if (!self) return false;

    if (stepsPerSecond < 1.0)    stepsPerSecond = 1.0;
    if (stepsPerSecond > 1000.0) stepsPerSecond = 1000.0;

    gml::RValue args[2]{};
    gml::SetReal(args[0], stepsPerSecond);
    gml::SetReal(args[1], kGamespeedFps);

    gml::RValue r{};
    if (!builtins::Call("game_set_speed", &r, args, 2, self)) {
        if (!g_warned) { Logf("[!] gamespeed: game_set_speed failed"); g_warned = true; }
        return false;
    }

    g_target = stepsPerSecond;
    return true;
}

void Tick() {
    if (!g_hold || g_target <= 0.0) return;
    if (!builtins::Ready()) return;

    // Only re-assert when the engine has actually drifted. Calling every frame
    // would be wasteful and would bury a genuine failure in noise.
    double now = 0.0;
    if (!ReadSpeed(&now)) return;
    if (std::fabs(now - g_target) < 0.5) return;

    Logf("gamespeed: game moved to %g, re-applying %g", now, g_target);
    Set(g_target);
}

void DrawSpeedTab() {
    if (!Ready()) {
        ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                           "Waiting for the game - load a character first.");
        return;
    }

    const double now  = Current();
    const double base = Baseline() > 0.0 ? Baseline() : now;
    if (now <= 0.0) { ImGui::TextDisabled("Could not read the game speed."); return; }

    ImGui::TextWrapped(
        "Walking between cells is animated over a fixed number of frames, so crossing a "
        "map is mostly waiting. This raises how many steps per second the game runs, "
        "which makes every animation finish proportionally sooner.");
    ImGui::Spacing();

    ImGui::Text("Baseline : %.0f steps/sec", base);
    ImGui::Text("Now      : %.0f steps/sec   (%.2fx)", now, now / base);

    ImGui::Spacing();
    ImGui::SeparatorText("Multiplier");

    static float mult = 1.0f;
    // Track the game when something else changed it, so the slider never lies.
    if (std::fabs(static_cast<double>(mult) - now / base) > 0.02)
        mult = static_cast<float>(now / base);

    ImGui::SetNextItemWidth(280.0f);
    if (ImGui::SliderFloat("##mult", &mult, 0.25f, 8.0f, "%.2fx", ImGuiSliderFlags_Logarithmic))
        Set(base * static_cast<double>(mult));

    const float presets[] = { 0.5f, 1.0f, 2.0f, 4.0f, 8.0f };
    for (float p : presets) {
        char label[16];
        std::snprintf(label, sizeof(label), "%gx", p);
        if (ImGui::Button(label, ImVec2(52.0f, 0.0f))) { mult = p; Set(base * p); }
        ImGui::SameLine();
    }
    if (ImGui::Button("Reset", ImVec2(70.0f, 0.0f))) { mult = 1.0f; Set(base); }

    ImGui::Spacing();
    bool hold = Hold();
    if (ImGui::Checkbox("Keep it applied", &hold)) SetHold(hold);
    ImGui::SameLine();
    ImGui::TextDisabled("re-applies when the game resets it (room changes, menus)");

    if (mult > 4.0f) {
        ImGui::Spacing();
        ImGui::TextColored(ImVec4(0.95f, 0.80f, 0.35f, 1.0f),
                           "Above 4x the whole game runs fast - combat and input included.");
    }
}

} // namespace mod::gamespeed
