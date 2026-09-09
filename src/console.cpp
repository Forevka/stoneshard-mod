#include "console.h"
#include "gml.h"
#include "log.h"
#include "savebackup.h"
#include "symbols.h"

#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <string>
#include <vector>

#include "imgui.h"

namespace mod::console {
namespace {

struct Entry {
    Line        kind;
    std::string text;
};

std::deque<Entry>        g_output;
std::vector<std::string> g_history;
int                      g_historyPos = -1;
bool                     g_scrollToBottom = false;
bool                     g_acknowledgedRisk = false;

constexpr std::size_t kMaxOutput = 1000;

ImVec4 ColorFor(Line k) {
    switch (k) {
    case Line::Echo:   return ImVec4(0.60f, 0.75f, 1.00f, 1.0f);
    case Line::Result: return ImVec4(0.45f, 0.90f, 0.45f, 1.0f);
    case Line::Error:  return ImVec4(0.95f, 0.40f, 0.40f, 1.0f);
    default:           return ImVec4(0.80f, 0.80f, 0.80f, 1.0f);
    }
}

// Splits on whitespace, honouring double quotes.
std::vector<std::string> Tokenize(const std::string& line) {
    std::vector<std::string> out;
    std::string cur;
    bool inQuotes = false, has = false;

    for (char c : line) {
        if (c == '"') { inQuotes = !inQuotes; has = true; continue; }
        // \r and \n matter: commands arrive from a file read with fgets, which
        // keeps the newline. Without this a symbol lookup sees "scr_savegame\n"
        // and fails silently.
        if (!inQuotes && (c == ' ' || c == '\t' || c == '\r' || c == '\n')) {
            if (has) { out.push_back(cur); cur.clear(); has = false; }
            continue;
        }
        cur += c;
        has = true;
    }
    if (has) out.push_back(cur);
    return out;
}

bool ParseNumber(const std::string& s, double& out) {
    if (s.empty()) return false;
    char* end = nullptr;
    out = std::strtod(s.c_str(), &end);
    return end && *end == '\0';
}

} // namespace

void Print(Line kind, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);

    g_output.push_back({kind, buf});
    if (g_output.size() > kMaxOutput) g_output.pop_front();
    g_scrollToBottom = true;
}

void Execute(const std::string& line) {
    if (line.empty()) return;

    Print(Line::Echo, "> %s", line.c_str());
    g_history.push_back(line);
    g_historyPos = -1;

    if (!gml::Ready()) {
        Print(Line::Error, "GML bridge unavailable: %s", gml::Status());
        return;
    }

    const auto tokens = Tokenize(line);
    if (tokens.empty()) return;

    // Built-ins.
    if (tokens[0] == "clear") { g_output.clear(); return; }
    if (tokens[0] == "help") {
        Print(Line::Info, "<symbol> [args...]   call a game function by name");
        Print(Line::Info, "find <text>          search the %zu resolved symbols", sym::Count());
        Print(Line::Info, "clear                clear this output");
        Print(Line::Info, "Numbers pass as reals; everything else as strings. Quote to force a string.");
        return;
    }
    if (tokens[0] == "find") {
        if (tokens.size() < 2) { Print(Line::Error, "usage: find <text>"); return; }
        const auto hits = sym::Search(tokens[1], 40);
        Print(Line::Info, "%zu match(es), first %zu:", hits.size(), hits.size());
        for (const auto* e : hits) Print(Line::Info, "   %s", e->name);
        return;
    }

    // Resolve the symbol, tolerating the bare short name.
    std::string symbol = tokens[0];
    void* fn = sym::Find(symbol);
    if (!fn) {
        symbol = "gml_Script_" + tokens[0];
        fn = sym::Find(symbol);
    }
    if (!fn) {
        Print(Line::Error, "unknown symbol '%s' (try: find %s)",
              tokens[0].c_str(), tokens[0].c_str());
        return;
    }

    // Anything that reaches the game may alter the save.
    backup::EnsureBackupOnce();

    // Object events use the 2-argument convention; invoking one with the script
    // signature corrupts the stack, so send it down the right path.
    if (gml::IsEventSymbol(symbol)) {
        Logf("console: running event %s", symbol.c_str());
        if (gml::CallEvent(fn, nullptr, nullptr)) {
            Print(Line::Result, "%s ran (event)", symbol.c_str());
        } else {
            Print(Line::Error, "%s failed%s%s", symbol.c_str(),
                  gml::LastError()[0] ? ": " : "", gml::LastError());
        }
        return;
    }

    // Build the argument list. The string buffers must outlive the call, which
    // is why `storage` is kept alive for the whole scope.
    const std::size_t argc = tokens.size() - 1;
    std::vector<std::string> storage(argc);
    std::vector<gml::RValue> values(argc);
    std::vector<gml::RValue*> argv(argc);

    for (std::size_t i = 0; i < argc; ++i) {
        const std::string& tok = tokens[i + 1];
        double number = 0.0;
        if (tok == "undefined" || tok == "_") {
            gml::SetUndefined(values[i]);
        } else if (ParseNumber(tok, number)) {
            gml::SetReal(values[i], number);
        } else {
            storage[i] = tok;
            if (!gml::SetString(values[i], storage[i].c_str())) {
                Print(Line::Error, "could not build string argument %zu", i);
                return;
            }
        }
        argv[i] = &values[i];
    }

    Logf("console: calling %s with %zu arg(s)", symbol.c_str(), argc);

    gml::RValue result{};
    if (!gml::Call(fn, &result, argc ? argv.data() : nullptr, static_cast<int>(argc))) {
        if (gml::LastError()[0])
            Print(Line::Error, "%s rejected by the game: %s", symbol.c_str(), gml::LastError());
        else
            Print(Line::Error, "%s faulted (caught; game state may be inconsistent - "
                               "consider reloading)", symbol.c_str());
        return;
    }

    Print(Line::Result, "%s -> kind=%d  %s",
          symbol.c_str(), result.kind, gml::ToString(result).c_str());
}

void DrawConsoleTab() {
    if (!g_acknowledgedRisk) {
        ImGui::TextWrapped(
            "Raw invocation calls game functions directly. Wrong arguments can crash "
            "the game or corrupt the current character. Your saves are copied to the "
            "mod's save-backups folder before the first call.");
        ImGui::Spacing();
        if (ImGui::Button("I understand - enable the console"))
            g_acknowledgedRisk = true;
        return;
    }

    const float footer = ImGui::GetFrameHeightWithSpacing() + 4.0f;
    ImGui::BeginChild("##out", ImVec2(0.0f, -footer), true,
                      ImGuiWindowFlags_HorizontalScrollbar);
    for (const Entry& e : g_output) {
        ImGui::PushStyleColor(ImGuiCol_Text, ColorFor(e.kind));
        ImGui::TextUnformatted(e.text.c_str());
        ImGui::PopStyleColor();
    }
    if (g_scrollToBottom) { ImGui::SetScrollHereY(1.0f); g_scrollToBottom = false; }
    ImGui::EndChild();

    static char input[512] = "";

    ImGui::SetNextItemWidth(-90.0f);
    const bool submitted = ImGui::InputTextWithHint(
        "##cmd", "type 'help', or a symbol name and arguments", input, sizeof(input),
        ImGuiInputTextFlags_EnterReturnsTrue);

    ImGui::SameLine();
    const bool clicked = ImGui::Button("Run", ImVec2(70.0f, 0.0f));

    if (submitted || clicked) {
        Execute(input);
        input[0] = '\0';
        ImGui::SetKeyboardFocusHere(-1);
    }

    // Live autocomplete against the resolved symbol map.
    if (input[0] && !submitted) {
        const auto hits = sym::Search(input, 6);
        if (!hits.empty()) {
            ImGui::TextDisabled("matches:");
            for (const auto* e : hits) {
                ImGui::SameLine();
                ImGui::TextDisabled("%s", e->name);
            }
        }
    }
}

} // namespace mod::console
