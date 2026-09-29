using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Riders: a second consequence on an action you were already taking.
/// </summary>
/// <remarks>
/// scr_save_damage_received runs as the unit that was hit, after its HP has
/// dropped, with (attacker, amount): watched live, a 7-point hit on a 50 HP rat
/// arrived as (ref, 7) with the rat already at 43. That is the rider's trigger.
/// The rider itself is HP taken off the victim, less the victim's own
/// resistance to the rolled element.
/// </remarks>
internal sealed class StaveboundEmber : Relic
{
    private const double Share = 0.5, MagicTaken = 20, UnholySanity = 2;

    private static readonly (string Name, string Resistance, string Colour)[] Elements =
    {
        ("Fire", Objects.o_player.Vars.Fire_Resistance, "~r~"),
        ("Shock", Objects.o_player.Vars.Shock_Resistance, "~y~"),
        ("Frost", Objects.o_player.Vars.Frost_Resistance, "~lg~"),
        ("Caustic", Objects.o_player.Vars.Caustic_Resistance, "~lg~"),
        ("Unholy", Objects.o_player.Vars.Unholy_Resistance, "~r~"),
    };

    private static readonly string[] MagicResistances =
    {
        Objects.o_player.Vars.Magic_Resistance, Objects.o_player.Vars.Fire_Resistance,
        Objects.o_player.Vars.Shock_Resistance, Objects.o_player.Vars.Frost_Resistance,
        Objects.o_player.Vars.Caustic_Resistance, Objects.o_player.Vars.Arcane_Resistance,
        Objects.o_player.Vars.Unholy_Resistance, Objects.o_player.Vars.Sacred_Resistance,
        Objects.o_player.Vars.Psionic_Resistance, Objects.o_player.Vars.Poison_Resistance,
    };

    public override string Id => "stavebound_ember";
    public override string Name => "Stavebound Ember";
    public override string Family => "Riders";
    public override string Flavor => "A conduit doesn't choose what runs through it.";
    public override string Boon =>
        $"Every hit you land with a staff deals another ~lg~{Share * 100:0}%~/~ of its damage as magic, of an element rolled " +
        "fresh each hit: Fire, Shock, Frost, Caustic or Unholy.";
    public override string Toll =>
        $"You never pick the element, and resistant foes shrug it off. Unholy rolls cost ~r~{UnholySanity}~/~ Sanity. " +
        $"The conduit runs both ways: ~r~-{MagicTaken}%~/~ to every magic Resistance.";

    public override void OnEnemyDamaged(RelicItem item, InstanceRef player, InstanceRef victim, double amount)
    {
        // A victim the hit already killed has nothing left to burn, and must not cost Sanity.
        if (!item.Carried || amount <= 0 || !ReliquaryMod.WieldingStaff || World.Num(victim, Objects.o_player.Vars.HP) <= 0) return;
        var (name, resistance, colour) = Elements[Random.Shared.Next(Elements.Length)];
        double resist = Math.Clamp(World.Num(victim, resistance), -100, 100);
        double extra = Math.Round(amount * Share * (1 - resist / 100), 1);
        if (extra <= 0)
        {
            World.Say($"~y~Stavebound Ember~/~: the {name.ToLowerInvariant()} rider fizzles.");
            return;
        }
        double dealt = World.Hurt(victim, extra);
        if (dealt <= 0) return;
        if (name == "Unholy") World.LoseSanity(player, UnholySanity);
        World.Say($"~y~Stavebound Ember~/~ adds {colour}{dealt:0.#} {name.ToLowerInvariant()}~/~ damage.");
    }

    public override void OnStats(RelicItem item, Stats stats)
    {
        if (!item.Carried) return;
        foreach (var r in MagicResistances) stats.AddResistance(r, -MagicTaken);
    }

    public override string Status(RelicItem item) =>
        ReliquaryMod.WieldingStaff ? "A staff is in hand: the conduit is open" : "No staff in hand: dormant";
}
