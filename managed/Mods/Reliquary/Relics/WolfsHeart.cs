using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Vessels: a live fill the item shows, filled by the room and spent on demand.
/// </summary>
internal sealed class WolfsHeart : Relic
{
    private const int FullAt = 7, RageTurns = 60, HazeVision = 3;
    private const double MaxDamage = 30, MaxCrit = 10, SanityCost = 10;

    public override string Id => "wolfs_heart";
    public override string Name => "Wolf's Heart";
    public override string Family => "Vessels";
    public override string Flavor => "It is calm in an empty room. That is the problem with it.";
    public override string Boon =>
        $"Beats faster for every hostile in sight; ~y~{FullAt}~/~ fill it. Activate to turn the beat into Rage for ~y~{RageTurns}~/~ turns: " +
        $"up to ~lg~+{MaxDamage}%~/~ Weapon Damage and ~lg~+{MaxCrit}%~/~ Crit Chance, scaled to the fill.";
    public override string Toll =>
        $"Drains the moment you break away. Drinking it full costs ~r~{SanityCost}~/~ Sanity and brings ~r~Blood Haze~/~: " +
        $"you see no further than {HazeVision} tiles while the Rage lasts.";
    public override bool Activatable => true;

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        if (!item.Carried) return;
        int before = (int)item.Get("fill");
        int fill = Math.Min(FullAt, World.Hostiles(player).Count);
        item.Set("fill", fill);
        double rage = item.Get("rage");
        if (fill == FullAt && before < FullAt && rage <= 0) World.Say("~r~Wolf's Heart~/~ is pounding.");

        if (rage > 0)
        {
            item.Set("rage", rage - 1);
            if (rage - 1 <= 0)
            {
                item.Set("haze", 0);
                World.Recalculate(player);
                World.Say("~y~Wolf's Heart~/~ quiets. The Rage is spent.");
            }
        }
    }

    public override string Activate(RelicItem item, InstanceRef player)
    {
        if (item.Get("rage") > 0) throw new InvalidOperationException("the Rage is still running");
        int fill = (int)item.Get("fill");
        if (fill <= 0) throw new InvalidOperationException("it is calm: no hostiles in sight");

        item.Set("power", (double)fill / FullAt);
        item.Set("rage", RageTurns);
        item.Set("fill", 0);
        bool full = fill >= FullAt;
        item.Set("haze", full ? 1 : 0);
        if (full) World.LoseSanity(player, SanityCost);
        return full
            ? $"~r~Wolf's Heart~/~ bursts: full Rage, and the world narrows to a {HazeVision}-tile haze."
            : $"~y~Wolf's Heart~/~ feeds a Rage of {fill}/{FullAt}.";
    }

    public override void OnStats(RelicItem item, Stats stats)
    {
        if (!item.Carried || item.Get("rage") <= 0) return;
        double power = item.Get("power");
        stats.Add(Objects.o_player.Vars.Weapon_Damage, MaxDamage * power);
        stats.Add(Objects.o_player.Vars.CRT, MaxCrit * power);
        if (item.Get("haze") > 0) stats.Cap(Objects.o_player.Vars.VSN, HazeVision);
    }

    public override string Status(RelicItem item)
    {
        double rage = item.Get("rage");
        return rage > 0
            ? $"Rage: {rage:0} turns ({item.Get("power") * 100:0}%)" + (item.Get("haze") > 0 ? ", Blood Haze" : "")
            : $"Heartbeat: {item.Get("fill"):0}/{FullAt}";
    }
}
