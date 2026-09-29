#pragma once

#include <string>

namespace mod::paths {

// Root for every file the loader writes: the log and imgui.ini.
//
// Defaults to the compile-time MOD_DATA_DIR, overridden per process by the
// CORELOADER_DATA_DIR environment variable (the old name, SSMOD_DATA_DIR, is
// still read as a fallback for one release). Two game instances on one machine
// should have different roots, or both would be writing one log.
//
// Resolved once, lazily, and the directory is created if it is missing.
// Nothing here logs - this runs before the log file is open.
const std::string& DataDir();

// DataDir() + "\\" + name.
std::string File(const char* name);

} // namespace mod::paths
