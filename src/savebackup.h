#pragma once

namespace mod::backup {

// Copies %LOCALAPPDATA%\StoneShard\characters_v1 into the project workspace the
// first time a cheat runs in this session. Silent, at most once, and nothing is
// ever written into the game folder.
void EnsureBackupOnce();

bool        Done();
const char* LastResult();

} // namespace mod::backup
