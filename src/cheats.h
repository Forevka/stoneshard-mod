#pragma once

namespace mod::cheats {

// ImGui tab. Every action is routed through console::Execute so the exact call
// is echoed into the console and the log, and so the save backup runs first.
void DrawCheatsTab();

} // namespace mod::cheats
