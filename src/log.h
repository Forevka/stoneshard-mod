#pragma once

#include <string>
#include <vector>

namespace mod {

// Writes lodestone.log into paths::DataDir(): <game>\Lodestone\Logs when
// installed, the build workspace in development, or CORELOADER_DATA_DIR.
void LogInit();
void LogShutdown();
void Logf(const char* fmt, ...);

// Copy of the in-memory ring buffer, for display in the overlay.
std::vector<std::string> LogSnapshot();

} // namespace mod
