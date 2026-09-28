#pragma once

#include <string>
#include <vector>

namespace mod {

// Writes coreloader.log into paths::DataDir(): <game>\CoreLoader\Logs when
// installed, the build workspace in development, or SSMOD_DATA_DIR.
void LogInit();
void LogShutdown();
void Logf(const char* fmt, ...);

// Copy of the in-memory ring buffer, for display in the overlay.
std::vector<std::string> LogSnapshot();

} // namespace mod
