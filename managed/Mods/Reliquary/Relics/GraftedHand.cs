using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Grafts: trade against the body's own six-limb condition system.
/// </summary>
/// <remarks>
/// The arm's condition is the player's Body_Parts_map "rhand" entry (0-100).
/// "Never heals" is enforced each turn: whatever raised it since - bandage,
/// rest, surgery - is put back down to the graft's cap, and the cap itself
/// sinks a point every 50 turns.
///
/// Once grafted the hand belongs to the character, not to the bag: the graft
/// is kept in the player's own psyche map (psyData, which the game saves with
/// the character), so dropping, selling or stashing the item neither lifts the
/// curse nor ends the bonus, and a second hand cannot be grafted on top. The
/// item stays behind as a spent husk.
/// </remarks>
internal sealed class GraftedHand : Relic
{
    private const string Arm = "rhand";
    private const double Damage = 40;
    private const int DecayEvery = 50;

    // Keys in the player's psyData, named so no game key can collide with them.
    private const string KeyCap = "ReliquaryGraftCap";
    private const string KeyAge = "ReliquaryGraftAge";

    public override string Id => "grafted_hand";
    public override string Name => "Grafted Hand of the Hanged Man";
    public override string Family => "Grafts";
    public override string Flavor => "It held on longer than the rest of him.";
    public override string Boon =>
        $"Activate to graft it onto your right arm: ~lg~+{Damage}%~/~ Weapon Damage with one-handed weapons, for good.";
    public override string Toll =>
        $"The arm's condition ~r~never heals~/~ again - not by bandage, surgery, rest or miracle - and decays ~r~1%~/~ every " +
        $"{DecayEvery} turns for the rest of the character's life. It cannot be taken off.";
    public override bool Activatable => true;

    private static DsMap Psyche(InstanceRef player) => new(player.Get(Objects.o_player.Vars.psyData));

    private static double? Cap(InstanceRef player)
    {
        var psy = Psyche(player);
        var v = psy.Exists ? psy.Get(KeyCap) : RValue.Undefined;
        return v.IsNumber ? v.AsReal : null;
    }

    public override string Activate(RelicItem item, InstanceRef player)
    {
        if (item.Get("spent") > 0) throw new InvalidOperationException("this one is a husk; the hand is already part of you");
        if (Cap(player) != null) throw new InvalidOperationException("you already carry a grafted hand");
        var psy = Psyche(player);
        if (!psy.Exists) throw new InvalidOperationException("the character's psyche map is unreadable");
        var body = World.Body(player);
        var arm = body.Exists ? body.Get(Arm) : RValue.Undefined;
        if (!arm.IsNumber) throw new InvalidOperationException("the right arm's condition is unreadable");

        psy.Set(KeyCap, arm.AsReal);
        psy.Set(KeyAge, 0);
        item.Set("spent", 1);
        return $"~r~The Hanged Man's hand~/~ takes the place of yours. Condition {arm.AsReal:0}, and it will never be more.";
    }

    public override void OnPlayerTurn(InstanceRef player)
    {
        if (Cap(player) is not { } cap) return;
        var psy = Psyche(player);
        var ageValue = psy.Get(KeyAge);
        double age = (ageValue.IsNumber ? ageValue.AsReal : 0) + 1;
        if (age >= DecayEvery)
        {
            age = 0;
            cap = Math.Max(0, cap - 1);
            psy.Set(KeyCap, cap);
        }
        psy.Set(KeyAge, age);

        var body = World.Body(player);
        var arm = body.Exists ? body.Get(Arm) : RValue.Undefined;
        if (arm.IsNumber && arm.AsReal > cap) body.Set(Arm, cap);
    }

    public override void OnPlayerStats(Stats stats)
    {
        if (ReliquaryMod.WeaponHands != 1 || World.Player is not { } player || Cap(player) == null) return;
        stats.Add(Objects.o_player.Vars.Weapon_Damage, Damage);
    }

    public override string Status(RelicItem item) =>
        item.Get("spent") > 0 ? "A husk: the hand is part of you now" : "Not grafted";

    public override string PlayerStatus(InstanceRef player)
    {
        if (Cap(player) is not { } cap) return "";
        var age = Psyche(player).Get(KeyAge);
        return $"Grafted: arm capped at {cap:0}, next decay in {DecayEvery - (age.IsNumber ? age.AsReal : 0):0} turns" +
               (ReliquaryMod.WeaponHands == 1 ? "" : " (needs a one-handed weapon)");
    }
}
