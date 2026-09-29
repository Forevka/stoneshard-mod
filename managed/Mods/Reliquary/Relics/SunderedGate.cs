using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Panic buttons: an escape that hands the floor's progress back for your life.
/// </summary>
/// <remarks>
/// The design moves you to the floor's entrance and every enemy back to where
/// it started. Units cannot be moved by writing x/y (the grid puts them back),
/// and the game's scr_teleport has two arguments whose meaning is not yet
/// confirmed live, so the working default keeps what can be done natively:
/// every hostile on the floor is restored to full health, and you vanish from
/// their tracking behind the game's untargetable status. With
/// <see cref="Experimental"/> on (test host: reliq.gate.teleport on) it also
/// tries scr_teleport - you to where you entered the floor (your xstart/ystart,
/// the position you were created at), each enemy to its own.
///
/// An escape with no limit at all would make the untargetable fallback
/// permanent invisibility, so the gate needs a while to close again.
/// </remarks>
internal sealed class SunderedGate : Relic
{
    private const int HiddenTurns = 8, Recharge = 300;

    /// <summary>Try the game's teleport as well. Off until its arguments are confirmed.</summary>
    internal static bool Experimental { get; set; }

    private static readonly string Untargetable = Objects.o_b_untargetable.Name;

    public override string Id => "sundered_gate";
    public override string Name => "The Sundered Gate";
    public override string Family => "Panic buttons";
    public override string Flavor => "A door with one side.";
    public override string Boon =>
        $"Activate: from anywhere, at any health, you slip out of the fight - for ~y~{HiddenTurns}~/~ turns nothing on the floor " +
        "can find you. The single most reliable escape in the game.";
    public override string Toll =>
        "Every enemy on the floor is restored to ~r~full health~/~. The floor is not progress you keep - it's progress you " +
        $"hand back for your life. The gate takes ~r~{Recharge}~/~ turns to open again.";
    public override bool Activatable => true;

    public override void Install(IRelicHost host)
    {
        if (!TestHost.Enabled) return;
        TestHost.Register("reliq.gate.teleport", args =>
        {
            Experimental = args.Count > 0 && args[0].GetString() is "on" or "true" or "1";
            return Experimental;
        }, "reliq.gate.teleport on|off: lets the Sundered Gate try scr_teleport for you and the floor (EXPERIMENTAL)");
    }

    public override string Activate(RelicItem item, InstanceRef player)
    {
        double wait = item.Get("recharge");
        if (wait > 0) throw new InvalidOperationException($"it is still closing ({wait:0} turns)");

        // The price first: the floor is handed back before you leave it.
        var foes = World.AllHostiles(player);
        foreach (var e in foes)
        {
            try { World.HealFull(e); }
            catch (GmlException) { /* one that will not heal still counts as reset */ }
        }

        int moved = 0;
        bool home = false;
        if (Experimental)
        {
            foreach (var e in foes)
                if (TryReturn(e)) moved++;
            home = TryReturn(player);
        }

        // Vanishing is what makes it an escape either way.
        // The recharge is spent first: if the status is refused, the healed
        // floor is not free to heal again on the next press.
        item.Set("recharge", Recharge);
        World.ApplyStatus(player, Untargetable, HiddenTurns);
        return home
            ? $"~y~The Sundered Gate~/~ opens onto the floor's entrance. {foes.Count} foe(s) are whole again ({moved} sent home)."
            : $"~y~The Sundered Gate~/~ swallows you. {foes.Count} foe(s) are whole again, and none of them can find you.";
    }

    private static bool TryReturn(InstanceRef unit)
    {
        double x = World.Num(unit, InstanceVars.xstart, double.NaN), y = World.Num(unit, InstanceVars.ystart, double.NaN);
        return !double.IsNaN(x) && !double.IsNaN(y) && World.TryTeleport(unit, x, y);
    }

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        double wait = item.Get("recharge");
        if (wait <= 0) return;
        item.Set("recharge", wait - 1);
        if (wait - 1 <= 0 && item.Carried) World.Say("~y~The Sundered Gate~/~ stands open again.");
    }

    public override string Status(RelicItem item)
    {
        double wait = item.Get("recharge");
        return wait > 0 ? $"Closing: {wait:0} turns" : "Open";
    }
}
