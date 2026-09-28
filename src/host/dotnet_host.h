#pragma once

#include <string>

namespace mod::host {

// Starts .NET inside the game and hands the CoreApi table to CoreLoader.dll,
// which discovers and loads the C# mods.
//
// Runs on the init thread: bringing the runtime up takes a few hundred ms and
// touches no GML. Mods are only *initialised* later, on the game thread, by the
// first Frame(). A missing runtime or loader is logged and leaves the native
// overlay working - it never takes the game down.
bool Start();

// Game thread, once per rendered frame. No-ops until Start() succeeded.
void Frame();

// Game thread, inside the overlay's Mods tab.
void DrawModsTab();

void Shutdown();

bool        Running();
std::string Status();   // a copy: the status is written from the init thread

} // namespace mod::host
