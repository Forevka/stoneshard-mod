#pragma once

namespace mod::inspector {

// Live view of an instance's raw memory, typed heuristically. This is the
// "attach and inspect" surface: pick the player, a hooked instance, or a raw
// pointer, and read what is actually there.
//
// It is also how the CInstance x/y offsets get settled honestly - walk around
// and watch which adjacent doubles track your movement, instead of guessing.
void DrawInspectorTab();

} // namespace mod::inspector
