using CoreLoader;

[assembly: CoreModInfo(typeof(XpProbe.Probe), "XP probe", "1.0.0", "Lodestone tests")]
[assembly: CoreModGame("StoneShard")]

namespace XpProbe;

/// <summary>
/// Regression fixture for StoneshardBoost's XP hook. Once a character is in the
/// world, grants 100 XP through the game's own scr_get_XP - the same function
/// kills, books and quests use - and logs how far the character's XP moved.
/// With StoneshardBoost at x3 the gain should be 300.
/// </summary>
public sealed class Probe : CoreMod
{
    private int _frames;
    private bool _done;

    public override void OnUpdate()
    {
        if (_done || ++_frames % 120 != 0) return;
        if (GmlObject.Find("o_player") is not { InstanceCount: > 0 }) return;
        if (_frames < 600) return;   // let the loaded character settle first
        _done = true;

        try
        {
            double before = Game.CallScript("scr_atr", "XP").AsReal;
            Game.CallScript("scr_get_XP", 100);
            double after = Game.CallScript("scr_atr", "XP").AsReal;
            Log.Info($"XP {before} -> {after}: granted 100, character gained {after - before}");

            // Phase 2: HookCall.CallOriginal. An After hook re-runs the original
            // once, with the arguments as the Before hooks left them, so the
            // same grant should now land twice.
            bool repeat = true;
            using var h = Hooks.After("scr_get_XP", c =>
            {
                if (!repeat) return;
                repeat = false;
                c.CallOriginal();
            });
            before = Game.CallScript("scr_atr", "XP").AsReal;
            Game.CallScript("scr_get_XP", 100);
            after = Game.CallScript("scr_atr", "XP").AsReal;
            Log.Info($"with CallOriginal: XP {before} -> {after}: granted 100, character gained {after - before}");
        }
        catch (Exception ex)
        {
            Log.Error("XP probe failed", ex);
        }
    }
}
