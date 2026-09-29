using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Riders: both directions of the same interception point - what you take goes
/// back to whoever gave it, and a share of what you give comes back to you.
/// </summary>
/// <remarks>
/// scr_save_damage_received names the attacker as its first argument; for a
/// melee hit that is the unit itself (the Ember's "only the player's hits"
/// test relies on exactly that). A spell or projectile may name something that
/// is not a unit: then there is nobody with HP to reflect to, and nothing happens.
/// </remarks>
internal sealed class FacelessMirror : Relic
{
    private const double Reflected = 0.5, Returned = 0.25;

    public override string Id => "faceless_mirror";
    public override string Name => "Faceless Mirror";
    public override string Family => "Riders";
    public override string Flavor => "It does not distinguish between the blow and the one who struck it.";
    public override string Boon =>
        $"~lg~{Reflected * 100:0}%~/~ of all damage you take is reflected to whoever dealt it.";
    public override string Toll =>
        $"~r~{Returned * 100:0}%~/~ of all damage you deal is reflected to you. It rewards a wall and punishes a glass cannon " +
        "- though the glass will not be what kills you: it leaves you one breath.";

    public override void OnPlayerDamaged(RelicItem item, InstanceRef player, double amount, RValue attacker)
    {
        if (!item.Carried || World.IdKey(attacker) < 0) return;
        var source = new InstanceRef(attacker);
        if (World.IsPlayer(source.Id) || !World.Alive(source)) return;
        double dealt = World.Hurt(source, Math.Round(amount * Reflected, 1));
        if (dealt > 0) World.Say($"~y~Faceless Mirror~/~ returns {dealt:0.#} damage.");
    }

    public override void OnEnemyDamaged(RelicItem item, InstanceRef player, InstanceRef victim, double amount)
    {
        if (!item.Carried) return;
        double back = World.HurtPlayer(player, Math.Round(amount * Returned, 1));
        if (back > 0) World.Say($"~r~Faceless Mirror~/~ shows you {back:0.#} of it.");
    }
}
