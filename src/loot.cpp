#include "loot.h"

#include "log.h"
#include "rewrite.h"

#include <cstdio>

#include "imgui.h"

namespace mod::loot {
namespace {

// The one script both enemies and containers route through.
constexpr const char* kSymbol = "scr_loot";

double g_multiplier = 1.0;

// Matching on the resolved name, because that is what the hook stores.
const rewrite::Status* Find() {
    for (const rewrite::Status& s : rewrite::Hooks())
        if (s.rule.symbol == "gml_Script_scr_loot" || s.rule.symbol == kSymbol) return &s;
    return nullptr;
}

} // namespace

double      Multiplier() { return g_multiplier; }
const char* LastError()  { return rewrite::LastError(); }

bool Active() {
    const rewrite::Status* s = Find();
    return s && s->rule.enabled && s->rule.repeat != 1.0;
}

unsigned Calls()      { const rewrite::Status* s = Find(); return s ? s->hits : 0; }
unsigned Repeated()   { const rewrite::Status* s = Find(); return s ? s->repeated : 0; }
unsigned Suppressed() { const rewrite::Status* s = Find(); return s ? s->suppressed : 0; }

bool SetMultiplier(double multiplier) {
    if (multiplier < 0.0)  multiplier = 0.0;
    if (multiplier > 20.0) multiplier = 20.0;      // the hook caps repeats here too

    rewrite::Rule r;
    r.symbol  = kSymbol;
    r.repeat  = multiplier;
    r.enabled = (multiplier != 1.0);               // exactly vanilla means disarmed

    if (!rewrite::Install(r)) return false;

    g_multiplier = multiplier;
    Logf("loot: multiplier x%g (%s)", multiplier, r.enabled ? "armed" : "vanilla");
    return true;
}

void Disable() {
    rewrite::Remove("gml_Script_scr_loot");
    g_multiplier = 1.0;
}

// ---------------------------------------------------------------------- tab

void DrawLootTab() {
    ImGui::TextWrapped(
        "How much loot enemies drop and containers hold. Enemies resolve their drops when "
        "they die and containers when they are opened, so a change here applies to the "
        "next thing you kill or open - not to items already on the floor.");
    ImGui::Spacing();

    const struct { const char* label; double value; const char* blurb; } kPresets[] = {
        { "Brutal",   0.25, "a quarter of the usual drops" },
        { "Lean",     0.50, "half - a harder run" },
        { "Vanilla",  1.00, "the game as shipped" },
        { "Rich",     3.00, "three times the drops" },
        { "Generous", 5.00, "five times - exploration pays" },
        { "Absurd",   7.00, "seven times, and it shows" },
    };

    for (const auto& p : kPresets) {
        const bool current = (Multiplier() == p.value);
        if (current) ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.25f, 0.55f, 0.30f, 1.0f));
        if (ImGui::Button(p.label, ImVec2(96.0f, 0.0f))) SetMultiplier(p.value);
        if (current) ImGui::PopStyleColor();
        ImGui::SameLine();
    }
    ImGui::NewLine();

    ImGui::Spacing();
    ImGui::SeparatorText("Exact multiplier");

    static float custom = 1.0f;
    if (custom != static_cast<float>(Multiplier())) custom = static_cast<float>(Multiplier());

    ImGui::SetNextItemWidth(300.0f);
    if (ImGui::SliderFloat("##loot", &custom, 0.0f, 10.0f, "x%.2f",
                           ImGuiSliderFlags_AlwaysClamp))
        SetMultiplier(static_cast<double>(custom));
    ImGui::SameLine();
    ImGui::TextDisabled("%s", Multiplier() == 1.0 ? "vanilla" : "modified");

    ImGui::TextDisabled(
        "Fractions work: 2.5 drops twice as much plus a coin flip for a third. "
        "Below 1 the drop is sometimes skipped entirely.");

    ImGui::Spacing();
    ImGui::SeparatorText("Is it working?");

    if (!Active()) {
        ImGui::TextDisabled("Not armed - loot is exactly as the game intends.");
    } else {
        ImGui::TextColored(ImVec4(0.55f, 0.90f, 0.55f, 1.0f),
                           "ARMED at x%.2f", Multiplier());
    }

    // Counters rather than a claim: these move only when the game really calls
    // the hooked function, so they answer "did it fire" without guesswork.
    ImGui::Text("loot rolls seen   %u", Calls());
    ImGui::Text("multiplied        %u", Repeated());
    ImGui::Text("skipped           %u", Suppressed());

    if (Active() && Calls() == 0)
        ImGui::TextDisabled("Nothing killed or opened yet - go find something.");

    if (LastError()[0])
        ImGui::TextColored(ImVec4(0.95f, 0.45f, 0.45f, 1.0f), "%s", LastError());

    ImGui::Spacing();
    if (ImGui::Button("Remove the hook", ImVec2(160.0f, 0.0f))) Disable();
    ImGui::SameLine();
    ImGui::TextDisabled("unhooks entirely, rather than just setting x1");
}

} // namespace mod::loot
