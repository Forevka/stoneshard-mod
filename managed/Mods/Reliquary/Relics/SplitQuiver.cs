using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Riders: every arrow that lands insists on a second target, and is not particular which.
/// </summary>
/// <remarks>
/// "An arrow" is a hit the player lands on a foe more than one tile away while
/// wielding a bow, crossbow or sling (the wielded weapon's `type`). The phantom
/// is HP taken off a different hostile in sight, less its piercing resistance;
/// with no other hostile in sight it finds the only other thing there - you.
/// </remarks>
internal sealed class SplitQuiver : Relic
{
    private const double Share = 0.4;
    private static readonly string[] Launchers = { "bow", "crossbow", "sling" };

    public override string Id => "split_quiver";
    public override string Name => "The Split Quiver";
    public override string Family => "Riders";
    public override string Flavor => "It insists on a second target. It is not particular about which.";
    public override string Boon =>
        $"Every arrow that hits fires a phantom second arrow at a ~y~different~/~ enemy in sight for ~lg~{Share * 100:0}%~/~ damage.";
    public override string Toll =>
        "When there is no second enemy, the phantom still fires, and you are the only other thing in sight. " +
        "Duels and boss fights punish you for carrying it.";

    // The wielded weapon walks every gear item to find, so it is read once per turn.
    // The mod's own scan already knows the wielded weapon; walking every gear
    // item again here would double that cost each turn.
    private static string Weapon => ReliquaryMod.WeaponType;


    private bool Shooting => Launchers.Any(l => Weapon.Contains(l, StringComparison.OrdinalIgnoreCase));

    public override void OnEnemyDamaged(RelicItem item, InstanceRef player, InstanceRef victim, double amount)
    {
        if (!item.Carried || amount <= 0) return;
        if (!Shooting || World.Tiles(player, victim) <= 1) return;

        double share = amount * Share;
        var other = World.Hostiles(player)
            .Where(e => World.IdKey(e.Id) != World.IdKey(victim.Id) && World.Alive(e))
            .OrderBy(e => World.Tiles(player, e))
            .FirstOrDefault();
        if (other is { } target)
        {
            double dealt = World.Hurt(target, World.Resisted(target, share, Objects.o_player.Vars.Piercing_Resistance));
            if (dealt > 0) World.Say($"~y~The Split Quiver~/~: a phantom arrow finds another foe for {dealt:0.#}.");
            return;
        }
        double self = World.HurtPlayer(player, World.Resisted(player, share, Objects.o_player.Vars.Piercing_Resistance));
        if (self > 0) World.Say($"~r~The Split Quiver~/~: with no one else in sight, the phantom arrow finds you ({self:0.#}).");
    }

    public override string Status(RelicItem item) =>
        Weapon.Length == 0 ? "" : Shooting ? $"Nocked: {Weapon}" : "No bow, crossbow or sling in hand: dormant";
}
