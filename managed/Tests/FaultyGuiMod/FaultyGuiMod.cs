using CoreLoader;

[assembly: CoreModInfo(typeof(FaultyGuiMod.ThrowsInsideTabBar), "Faulty: throws in tab bar", "1.0.0", "CoreLoader tests")]

namespace FaultyGuiMod;

/// <summary>
/// Regression fixture for UI scope recovery. Opens its own tab bar and throws
/// before any tab item: without the loader's unwind, the loader's EndTabItem
/// would land on this bar (LastTabItemIdx == -1) and read Tabs[-1].
/// Expected: this mod shows as disabled, the game keeps running, and every tab
/// after it - in the Mods bar and the overlay's own bar - still draws.
/// </summary>
public sealed class ThrowsInsideTabBar : CoreMod
{
    public override void OnGUI()
    {
        UI.PushId("leaked-on-purpose");
        if (UI.BeginTabBar("##faulty_inner"))
            throw new InvalidOperationException("thrown on purpose inside an open tab bar");
    }
}
