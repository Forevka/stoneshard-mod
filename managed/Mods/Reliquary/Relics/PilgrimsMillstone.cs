using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Scaling passives: pure stat keys, positive and negative, and nothing else.
/// </summary>
/// <remarks>
/// The design's Carrying Capacity and Movement Speed are not stats Stoneshard
/// has (vanilla has neither a weight limit nor a move speed), so the boon and
/// the toll are expressed in stats the game does keep: every resistance, being
/// hard to shove, being hard to hit, and what abilities cost.
/// </remarks>
internal sealed class PilgrimsMillstone : Relic
{
    private const double Resist = 20, Footing = 40, Dodge = 20, EnergyCost = 30;

    // Every resistance the player shows, from the interop's harvested names.
    private static readonly string[] Resistances =
    {
        Objects.o_player.Vars.Physical_Resistance, Objects.o_player.Vars.Nature_Resistance,
        Objects.o_player.Vars.Magic_Resistance, Objects.o_player.Vars.Slashing_Resistance,
        Objects.o_player.Vars.Piercing_Resistance, Objects.o_player.Vars.Blunt_Resistance,
        Objects.o_player.Vars.Rending_Resistance, Objects.o_player.Vars.Fire_Resistance,
        Objects.o_player.Vars.Shock_Resistance, Objects.o_player.Vars.Poison_Resistance,
        Objects.o_player.Vars.Caustic_Resistance, Objects.o_player.Vars.Frost_Resistance,
        Objects.o_player.Vars.Arcane_Resistance, Objects.o_player.Vars.Unholy_Resistance,
        Objects.o_player.Vars.Sacred_Resistance, Objects.o_player.Vars.Psionic_Resistance,
    };

    public override string Id => "pilgrims_millstone";
    public override string Name => "Pilgrim's Millstone";
    public override string Family => "Scaling passives";
    public override string Flavor => "Carried far enough that carrying became the point.";
    public override string Boon =>
        $"~lg~+{Resist}%~/~ to every Resistance, ~lg~+{Footing}%~/~ Stun and Knockback Resistance.";
    public override string Toll =>
        $"~r~-{Dodge}%~/~ Dodge Chance, abilities cost ~r~{EnergyCost}%~/~ more Energy. Everything catches you, and nothing misses.";

    public override void OnStats(RelicItem item, Stats stats)
    {
        if (!item.Carried) return;
        foreach (var r in Resistances) stats.AddResistance(r, Resist);
        stats.Add(Objects.o_player.Vars.Stun_Resistance, Footing);
        stats.Add(Objects.o_player.Vars.Knockback_Resistance, Footing);
        stats.Add(Objects.o_player.Vars.EVS, -Dodge);
        stats.Add(Objects.o_player.Vars.Abilities_Energy_Cost, EnergyCost);
    }
}
