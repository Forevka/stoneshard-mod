#pragma once

#include <string>
#include <vector>

namespace mod {

// Writes to MOD_DATA_DIR/stoneshard-mod.log — deliberately inside the project
// workspace, never the game directory.
void LogInit();
void LogShutdown();
void Logf(const char* fmt, ...);

// Copy of the in-memory ring buffer, for display in the overlay.
std::vector<std::string> LogSnapshot();

} // namespace mod
