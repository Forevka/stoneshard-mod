#pragma once

#include <string>

namespace mod::console {

enum class Line { Info, Echo, Result, Error };

void Print(Line kind, const char* fmt, ...);

// Parses and runs one invocation line:
//     <symbol> [arg]...
// Bare numbers become reals; anything else becomes a string (quote it to force
// a string that looks numeric). The symbol may be given with or without the
// "gml_Script_" prefix.
//
// MUST be called on the game's render thread. Everything in the overlay already
// runs inside the Present hook, so UI callbacks satisfy that.
void Execute(const std::string& line);

// ImGui tabs.
void DrawConsoleTab();

} // namespace mod::console
