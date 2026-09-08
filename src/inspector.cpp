// Instance inspector: dump a window of game memory with heuristic typing.
//
// Every field is shown three ways at once - raw qword, the same bytes as a
// double, and what the value points at - because that is what makes an unknown
// struct readable. A "changed" marker highlights fields that moved since the
// last snapshot, which is the practical way to find things like a position.

#include "inspector.h"
#include "gml.h"
#include "symbols.h"

#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "imgui.h"

namespace mod::inspector {
namespace {

constexpr int kWindowBytes = 0x200;

unsigned char g_now[kWindowBytes];
unsigned char g_prev[kWindowBytes];
bool          g_havePrev = false;
const void*   g_lastBase = nullptr;

// Where does this value point, if anywhere? Naming the section is usually
// enough to tell a vtable from a string from a heap object.
const char* PointerHint(std::uintptr_t v) {
    if (!v) return nullptr;
    if (sym::TextRange().contains(v))  return "-> .text";
    if (sym::RdataRange().contains(v)) return "-> .rdata";
    if (sym::DataRange().contains(v))  return "-> .data";
    return nullptr;
}

bool LooksLikeDouble(double d) {
    if (d != d) return false;                       // NaN
    const double a = d < 0 ? -d : d;
    return a == 0.0 || (a > 1e-6 && a < 1e9);
}

} // namespace

void DrawInspectorTab() {
    static int  source   = 0;          // 0 player, 1 raw pointer
    static char rawPtr[32] = "";
    static int  offset   = 0;

    ImGui::TextWrapped(
        "Reads live memory from a game instance. Adjacent doubles that move as you walk are "
        "the position fields - that is the outstanding question this can settle.");

    ImGui::RadioButton("Player instance", &source, 0);
    ImGui::SameLine();
    ImGui::RadioButton("Raw pointer", &source, 1);
    if (source == 1) {
        ImGui::SameLine();
        ImGui::SetNextItemWidth(180.0f);
        ImGui::InputTextWithHint("##ptr", "hex address", rawPtr, sizeof(rawPtr));
    }

    const void* base = nullptr;
    if (source == 0) {
        base = gml::PlayerInstance();
    } else if (rawPtr[0]) {
        base = reinterpret_cast<const void*>(std::strtoull(rawPtr, nullptr, 16));
    }

    if (!base) {
        ImGui::TextDisabled("No instance yet - load a save, or enter a pointer.");
        return;
    }

    ImGui::Text("base = %p", base);
    ImGui::SetNextItemWidth(140.0f);
    ImGui::InputInt("start offset", &offset, 8, 64);
    if (offset < 0) offset = 0;
    offset &= ~7;                                    // keep 8-byte alignment

    if (!gml::ReadMemory(base, g_now, kWindowBytes)) {
        ImGui::TextColored(ImVec4(0.95f, 0.4f, 0.4f, 1.0f), "memory not readable");
        return;
    }
    if (base != g_lastBase) { g_havePrev = false; g_lastBase = base; }

    if (ImGui::Button("Snapshot (mark current values)")) {
        std::memcpy(g_prev, g_now, kWindowBytes);
        g_havePrev = true;
    }
    ImGui::SameLine();
    ImGui::TextDisabled(g_havePrev ? "changed fields are highlighted"
                                   : "take a snapshot, move, then compare");

    ImGui::BeginChild("##dump", ImVec2(0.0f, 320.0f), true,
                      ImGuiWindowFlags_HorizontalScrollbar);
    for (int off = offset; off + 8 <= kWindowBytes; off += 8) {
        std::uint64_t raw;
        double        asDouble;
        std::memcpy(&raw, g_now + off, 8);
        std::memcpy(&asDouble, g_now + off, 8);

        bool changed = false;
        if (g_havePrev) changed = std::memcmp(g_now + off, g_prev + off, 8) != 0;

        if (changed) ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(0.45f, 0.90f, 0.45f, 1.0f));

        char line[220];
        const char* hint = PointerHint(static_cast<std::uintptr_t>(raw));
        if (hint) {
            std::snprintf(line, sizeof(line), "+0x%03X  %016llX  %s",
                          off, static_cast<unsigned long long>(raw), hint);
        } else if (LooksLikeDouble(asDouble)) {
            std::snprintf(line, sizeof(line), "+0x%03X  %016llX  %.3f",
                          off, static_cast<unsigned long long>(raw), asDouble);
        } else {
            std::snprintf(line, sizeof(line), "+0x%03X  %016llX",
                          off, static_cast<unsigned long long>(raw));
        }
        ImGui::TextUnformatted(line);

        if (changed) ImGui::PopStyleColor();
    }
    ImGui::EndChild();
}

} // namespace mod::inspector
