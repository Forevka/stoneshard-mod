using CoreLoader;

[assembly: CoreModInfo(typeof(StoneshardBoost.StoneshardBoostMod), "Stoneshard Boost", "1.0.0", "CoreLoader")]
[assembly: CoreModGame("StoneShard")]

namespace StoneshardBoost;

/// <summary>
/// Stoneshard tweaks built on hooks.
///
///  * XP multiplier. Every source of experience - kills (o_enemy's Destroy
///    event), books, traps, quest rewards, discovering locations - goes through
///    scr_get_XP(amount), so scaling its first argument before it runs scales
///    all of them, including the "+N XP" the action log shows.
///  * Loot multiplier. scr_loot places one item per call and has no count
///    parameter, so "more loot" means running it again: after the game's own
///    call, the original is re-run through HookCall.CallOriginal. A fractional
///    multiplier (1.5x) adds the extra roll half the time.
///
/// Both call paths were confirmed from the game's code (callees of
/// gml_Object_o_enemy_Destroy_0) rather than guessed from names.
/// </summary>
public sealed class StoneshardBoostMod : CoreMod
{
    private float _xpMultiplier = 1f;
    private float _lootMultiplier = 1f;
    private long _xpCalls;
    private double _xpBonus;
    private long _lootCalls;
    private long _lootExtra;
    private readonly Random _rng = new();

    public override void OnInitialize()
    {
        _xpMultiplier = Config.Get("xpMultiplier", 1f);
        _lootMultiplier = Config.Get("lootMultiplier", 1f);
        Log.Info($"XP x{_xpMultiplier}, loot x{_lootMultiplier}");
    }

    [HookBefore("scr_get_XP")]
    private void ScaleXp(HookCall c)
    {
        _xpCalls++;
        if (_xpMultiplier == 1f || c.ArgCount < 1) return;
        var amount = c.GetArg(0);
        if (!amount.IsNumber || amount.AsReal <= 0) return;

        double scaled = Math.Round(amount.AsReal * _xpMultiplier);
        _xpBonus += scaled - amount.AsReal;
        c.SetArg(0, scaled);
    }

    [HookAfter("scr_loot")]
    private void RepeatLoot(HookCall c)
    {
        _lootCalls++;
        if (_lootMultiplier <= 1f || c.OriginalSkipped) return;

        double extra = _lootMultiplier - 1f;
        int runs = (int)Math.Floor(extra);
        if (_rng.NextDouble() < extra - runs) runs++;
        for (int i = 0; i < runs; i++)
        {
            c.CallOriginal();
            _lootExtra++;
        }
    }

    public override void OnGUI()
    {
        UI.Text("Experience");
        if (UI.SliderFloat("XP x##xp", ref _xpMultiplier, 1f, 10f)) Config.Set("xpMultiplier", _xpMultiplier);
        UI.TextDisabled($"scr_get_XP calls: {_xpCalls:N0}, bonus XP granted: {_xpBonus:N0}");
        UI.Separator();

        UI.Text("Loot");
        if (UI.SliderFloat("loot x##loot", ref _lootMultiplier, 1f, 5f)) Config.Set("lootMultiplier", _lootMultiplier);
        UI.TextDisabled($"scr_loot calls: {_lootCalls:N0}, extra rolls: {_lootExtra:N0}");
    }
}
