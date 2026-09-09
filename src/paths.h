#pragma once

#include <string>

namespace mod::paths {

// Root for every file the mod writes: the log, imgui.ini, debug-cmd.txt,
// debug-reply.txt and the save backups.
//
// Defaults to the compile-time MOD_DATA_DIR, overridden per process by the
// SSMOD_DATA_DIR environment variable. Two game instances on one machine MUST
// have different roots: remote::Poll consumes the command file by deleting it,
// so a shared root means whichever instance polls first eats the other's
// command, and both would be writing one log.
//
// Resolved once, lazily, and the directory is created if it is missing.
// Nothing here logs - this runs before the log file is open.
const std::string& DataDir();

// DataDir() + "\\" + name.
std::string File(const char* name);

// True when SSMOD_DATA_DIR supplied the root. Shown in the overlay so two
// windows are tellable apart at a glance.
bool Overridden();

} // namespace mod::paths
