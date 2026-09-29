using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Auras: a region of the floor that means something, indiscriminate on purpose.
/// </summary>
/// <remarks>
/// The carrier is a real Copper Ring from the game's armor table, so "while it
/// is on your finger" is the game's own equipped flag. Stoneshard has no prayer
/// action for the player, so the prayer is the ring's activation.
/// </remarks>
internal sealed class CopperRing : Relic
{
    private const double Radius = 4;
    private const int Blessing = 100, PerCleanse = 100, MaxBlessing = 1000;
    private const double Bonus = 10;

    public override string Id => "copper_ring";
    public override string Name => "Copper Ring of Faith";
    public override string Family => "Auras";
    public override CarrierKind Carrier => CarrierKind.Ring;
    public override string Flavor => "Gold was never the point. It was only ever the misunderstanding.";
    public override string Boon =>
        $"Pray with it worn: a ~y~Sanctified Circle~/~ of radius {Radius:0} rises around you. Every hostile inside is stripped of " +
        $"every effect it carries, and each cleansing adds ~lg~{PerCleanse}~/~ turns to your Blessing " +
        $"(~lg~+{Bonus}%~/~ Weapon Damage and Magic Power), up to {MaxBlessing} turns.";
    public override string Toll =>
        "The circle does not discriminate: your Poison and Bleeding come off them too. It collapses, Blessing and all, " +
        "the instant the ring leaves your finger.";
    public override bool Activatable => true;

    public override string Activate(RelicItem item, InstanceRef player)
    {
        if (!item.Equipped) throw new InvalidOperationException("it has to be worn to pray with");
        if (item.Get("blessing") > 0) throw new InvalidOperationException("the circle is already standing");
        item.Set("blessing", Blessing);
        return "~y~Copper Ring of Faith~/~: a Sanctified Circle rises.";
    }

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        double blessing = item.Get("blessing");
        if (blessing <= 0) return;
        if (!item.Equipped)
        {
            item.Set("blessing", 0);
            World.Recalculate(player);
            World.Say("~r~The Sanctified Circle collapses~/~: the ring left your finger.");
            return;
        }

        int cleansed = 0;
        foreach (var e in World.Hostiles(player, Radius))
            if (World.StripStatuses(e) > 0) cleansed++;
        blessing = Math.Min(MaxBlessing, blessing - 1 + cleansed * PerCleanse);
        item.Set("blessing", blessing);
        if (cleansed > 0) World.Say($"~y~The Sanctified Circle~/~ cleanses {cleansed} foe(s).");
        if (blessing <= 0)
        {
            World.Recalculate(player);
            World.Say("~y~The Sanctified Circle~/~ fades.");
        }
    }

    public override void OnStats(RelicItem item, Stats stats)
    {
        if (!item.Equipped || item.Get("blessing") <= 0) return;
        stats.Add(Objects.o_player.Vars.Weapon_Damage, Bonus);
        stats.Add(Objects.o_player.Vars.Magic_Power, Bonus);
    }

    public override string Status(RelicItem item)
    {
        double blessing = item.Get("blessing");
        return blessing > 0 ? $"Blessing: {blessing:0} turns" : item.Equipped ? "Ready to pray" : "Not worn";
    }
}
