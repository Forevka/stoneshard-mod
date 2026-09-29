using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Panic buttons: one activation that turns a fight you were losing into one you can leave.
/// </summary>
/// <remarks>
/// Petrification is the game's own stone status (o_db_stone), put on each unit
/// as an instance; its duration counts down with the turns like any status.
/// Vanilla has no day counter the mod can read, so "once per day" is a
/// recharge counted in turns and kept on the item.
/// </remarks>
internal sealed class Gorgoneion : Relic
{
    private const int StoneTurns = 15, Recharge = 1000;
    private const double HealthPerTurn = 0.04, EnergyPerTurn = 0.06;

    public override string Id => "gorgoneion";
    public override string Name => "Gorgoneion";
    public override string Family => "Panic buttons";
    public override string Flavor => "The face on it is still surprised.";
    public override string Boon =>
        $"Activate: every hostile in sight turns to stone for ~y~{StoneTurns}~/~ turns - you included. While petrified you take " +
        $"no damage and regenerate ~lg~{HealthPerTurn * 100:0}%~/~ Health and ~lg~{EnergyPerTurn * 100:0}%~/~ Energy per turn.";
    public override string Toll =>
        "You are stone too, so it buys nothing offensive: they wake where they stood, still beside you, none the worse. " +
        $"Recharges over ~r~{Recharge}~/~ turns.";
    public override bool Activatable => true;

    private static readonly string Stone = Objects.o_db_stone.Name;

    public override string Activate(RelicItem item, InstanceRef player)
    {
        if (item.Get("stone") > 0) throw new InvalidOperationException("you are already stone");
        double wait = item.Get("recharge");
        if (wait > 0) throw new InvalidOperationException($"it is still recharging ({wait:0} turns)");

        // The player first, and the charge spent before anyone else is touched:
        // if the game refuses part-way, the relic is recharging rather than
        // free to be pressed again on the same foes.
        World.ApplyStatus(player, Stone, StoneTurns);
        item.Set("stone", StoneTurns);
        item.Set("recharge", Recharge);
        _skipFailures = 0;

        int stoned = 0;
        foreach (var e in World.Hostiles(player))
        {
            try
            {
                World.ApplyStatus(e, Stone, StoneTurns);
                stoned++;
            }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException) { /* that one stays flesh */ }
        }
        return $"~y~Gorgoneion~/~ opens its eyes. {stoned} foe(s) and you turn to stone.";
    }

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        double wait = item.Get("recharge");
        if (wait > 0)
        {
            item.Set("recharge", wait - 1);
            if (wait - 1 <= 0 && item.Carried) World.Say("~y~Gorgoneion~/~ is watching again.");
        }

        double stone = item.Get("stone");
        if (stone <= 0) return;
        item.Set("stone", stone - 1);
        if (!item.Carried) return;
        World.Heal(player, World.MaxHp(player) * HealthPerTurn);
        World.RestoreEnergy(player, World.MaxEnergy(player) * EnergyPerTurn);
        if (stone - 1 <= 0) World.Say("~y~Gorgoneion~/~ closes its eyes. The stone cracks.");
    }

    // Stone cannot act, and the game waits for the player's input all the same,
    // so the relic passes the turns itself: scr_skip_turn run as the player is
    // what the game's own wait does. It only fires when the game says the
    // player may act (turn_available), and at most every few frames, so a turn
    // still being resolved is never skipped twice.
    private const int SkipEveryFrames = 12, MaxSkipFailures = 5;
    private int _sinceSkip, _skipFailures;

    public override void OnFrame(RelicItem item, InstanceRef player)
    {
        if (item.Get("stone") <= 0 || ++_sinceSkip < SkipEveryFrames) return;
        if (!player.Get(Objects.o_player.Vars.turn_available).AsBool) return;
        _sinceSkip = 0;

        // Stone the game has already lifted (a cleanse, a potion) frees the
        // player: from then on the turns are theirs again.
        if (!World.HasStatus(player, Stone))
        {
            item.Set("stone", 0);
            return;
        }
        try
        {
            Scripts.scr_skip_turn.CallAs(player);
            _skipFailures = 0;
        }
        catch (GmlException) when (++_skipFailures < MaxSkipFailures) { }
        catch (GmlException)
        {
            // The game will not pass turns for us: hand them back to the player.
            item.Set("stone", 0);
            World.Say("~y~Gorgoneion~/~: the stone cannot wait. Act, if you can.");
        }
    }

    // Stone takes no damage: whatever got through is handed straight back - but
    // no more than was really lost. A hit arrives with HP already down and
    // clamped at zero, so the ceiling is what the player had at the end of the
    // last frame.
    public override void OnPlayerDamaged(RelicItem item, InstanceRef player, double amount, RValue attacker)
    {
        if (!item.Carried || item.Get("stone") <= 0) return;
        double now = World.Num(player, Objects.o_player.Vars.HP);
        double restored = Math.Min(now + amount, Math.Max(now, ReliquaryMod.LastHp));
        if (restored > now) World.Heal(player, restored - now);
    }

    public override string Status(RelicItem item)
    {
        double stone = item.Get("stone"), wait = item.Get("recharge");
        if (stone > 0) return $"Petrified: {stone:0} turns";
        return wait > 0 ? $"Recharging: {wait:0} turns" : "Ready";
    }
}
