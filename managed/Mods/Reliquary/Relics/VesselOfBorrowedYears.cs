using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Scaling passives: it saves your life every time, and every time it costs you some of it for good.
/// </summary>
/// <remarks>
/// The price is the character's, not the item's: the count of saves is kept
/// in the player's psyData (saved with the character), and the Max Health
/// loss is applied from there after every stat recalculation - so selling the
/// vessel ends the saving, never the debt. The game rebuilds stats from
/// scratch each time, so the loss is recomputed, not compounded.
///
/// A killing hit is caught inside scr_save_damage_received; bleeding and other
/// damage over time as scr_pure_damage returns (see ReliquaryMod.SaveIfDying).
/// </remarks>
internal sealed class VesselOfBorrowedYears : Relic
{
    private const double Restore = 0.25, LossPerSave = 0.03;

    // A psyData key no game key can collide with.
    private const string KeySaves = "ReliquaryVesselSaves";

    public override string Id => "vessel_of_borrowed_years";
    public override string Name => "Vessel of Borrowed Years";
    public override string Family => "Scaling passives";
    public override int DamageOrder => SavesLife;
    public override string Flavor => "The Ethnarch's mask, without the mercy of a daily limit.";
    public override string Boon =>
        $"Negates lethal damage ~lg~every time~/~ it would kill you, restoring ~lg~{Restore * 100:0}%~/~ Max Health. " +
        "No cooldown, no daily limit, no condition.";
    public override string Toll =>
        $"Each save permanently reduces your Max Health by ~r~{LossPerSave * 100:0}%~/~, stacking, with no floor - and the " +
        "loss stays with you if the vessel does not.";

    private static double Saves(InstanceRef player) => World.PsyNum(player, KeySaves) ?? 0;

    public override void OnPlayerDamaged(RelicItem item, InstanceRef player, double amount, RValue attacker) => Save(item, player);

    // Bleeding and other damage over time never pass the damage hook.
    public override void OnPlayerDying(RelicItem item, InstanceRef player) => Save(item, player);

    private static void Save(RelicItem item, InstanceRef player)
    {
        if (!item.Carried || World.Num(player, Objects.o_player.Vars.HP) > 0) return;
        // A player already at 0 at the end of the last frame is already dying: saving
        // them now would pull them back out of the game's death, not avoid it.
        if (ReliquaryMod.LastHp <= 0) return;

        // The debt is booked before the healing, so a save can never be free.
        double saves = Saves(player) + 1;
        World.PsySet(player, KeySaves, saves);
        World.Recalculate(player);

        // 25% of Max Health as it stands; the recalculation asked for above
        // lowers both by this save's loss (OnPlayerStats scales HP with the
        // maximum), which leaves 25% of the new maximum.
        player.Set(Objects.o_player.Vars.HP, Math.Max(1, Math.Round(World.MaxHp(player) * Restore)));
        World.Say($"~y~Vessel of Borrowed Years~/~ refuses your death. It has now taken " +
                  $"~r~{(1 - Math.Pow(1 - LossPerSave, saves)) * 100:0.#}%~/~ of your life.");
    }

    public override void OnPlayerStats(Stats stats)
    {
        if (World.Player is not { } player) return;
        double saves = Saves(player);
        if (saves <= 0) return;
        double max = stats.Get(Objects.o_player.Vars.max_hp);
        if (max <= 1) return;
        // Multiplicative, so it never reaches zero on its own, but with no floor it gets close.
        double kept = Math.Max(1, max * Math.Pow(1 - LossPerSave, saves));
        stats.Add(Objects.o_player.Vars.max_hp, kept - max);
        // The recalculation that just ran rebuilt Max Health from our lowered
        // value back to its base and scaled current HP up with it. Scale it
        // down by the same ratio, or every recalculation heals (live, +6% per
        // recalculation at two saves).
        double hp = stats.Get(Objects.o_player.Vars.HP);
        if (hp > 0) stats.Add(Objects.o_player.Vars.HP, hp * kept / max - hp);
    }

    public override string Status(RelicItem item) =>
        World.Player is { } p && Saves(p) > 0 ? $"Saved you {Saves(p):0} time(s)" : "Waiting";

    public override string PlayerStatus(InstanceRef player)
    {
        double saves = Saves(player);
        return saves > 0
            ? $"Borrowed years: {saves:0} save(s), Max Health x{Math.Pow(1 - LossPerSave, saves):0.00}"
            : "";
    }
}
