using CoreLoader;
using StoneShard;

namespace Reliquary;

/// <summary>
/// Helpers for the relics that deal, reflect, bank or echo damage. Everything
/// here writes HP directly, which is what the game itself does to a unit after
/// its damage maths (an enemy written to 0 HP dies on the next frame).
/// </summary>
/// <remarks>
/// The player is never written to 0 by these helpers. Whether the game runs
/// its death sequence for a player whose HP is written (rather than dealt) to
/// 0 has not been established on the running game, and a half-dead character
/// is worse than a relic that stops one point short. Every "can kill you"
/// toll therefore leaves the player at 1 HP until that is verified.
/// </remarks>
internal static partial class World
{
    private const string VarHpDamage = Objects.o_player.Vars.HP;

    /// <summary>The grid cell a unit stands on (units sit on 26-unit tiles).</summary>
    public static (int X, int Y) TileOf(InstanceRef unit)
    {
        var (x, y) = Position(unit);
        return ((int)Math.Floor(x / Tile), (int)Math.Floor(y / Tile));
    }

    public static bool Alive(InstanceRef unit) => unit.Exists && Num(unit, VarHpDamage) > 0;

    /// <summary>Takes HP off the player, but never the last point (see the class remarks).</summary>
    public static double HurtPlayer(InstanceRef player, double amount)
    {
        double hp = Num(player, VarHpDamage);
        double after = Math.Max(Math.Min(hp, 1), hp - amount);
        player.Set(VarHpDamage, after);
        return hp - after;
    }

    /// <summary>Hurts any unit: the player through <see cref="HurtPlayer"/>, anything else outright.</summary>
    public static double HurtAny(InstanceRef unit, double amount, InstanceRef player) =>
        IdKey(unit.Id) == IdKey(player.Id) ? HurtPlayer(player, amount) : Hurt(unit, amount);

    /// <summary>
    /// <paramref name="amount"/> after the unit's resistance to it (a percent,
    /// read from the named variable; a unit without it resists nothing).
    /// </summary>
    public static double Resisted(InstanceRef unit, double amount, string resistance)
    {
        double r = Math.Clamp(Num(unit, resistance), -100, 100);
        return Math.Max(0, Math.Round(amount * (1 - r / 100), 1));
    }

    /// <summary>
    /// The living units a relic's blast may touch at a tile distance of
    /// <paramref name="radius"/> or less from (tx, ty): hostiles, and the player.
    /// Townsfolk are left out on purpose - an echo or a blast that hurt them
    /// would make the player a criminal for a relic's doing.
    /// </summary>
    public static List<InstanceRef> UnitsNear(InstanceRef player, int tx, int ty, int radius)
    {
        var found = new List<InstanceRef>();
        var (px, py) = TileOf(player);
        if (Math.Max(Math.Abs(px - tx), Math.Abs(py - ty)) <= radius) found.Add(player);
        if (Objects.o_enemy.Object is not { } enemies) return found;
        foreach (var e in enemies.Instances())
        {
            if (!e.Exists || Num(e, VarHostile) <= 0 || Num(e, VarHpDamage) <= 0) continue;
            var (ex, ey) = TileOf(e);
            if (Math.Max(Math.Abs(ex - tx), Math.Abs(ey - ty)) <= radius) found.Add(e);
        }
        return found;
    }

    /// <summary>
    /// The wielded weapon's `type` ("bow", "crossbow", "2hStaff"...), or "" with
    /// none: worn gear (o_inv_slot itself) whose data says Metatype "Weapon",
    /// owned by the player's bag. The same test the carrier scan uses; it walks
    /// every gear item, so callers cache it per turn.
    /// </summary>
    public static string WieldedWeaponType()
    {
        if (GmlObject.Find(Objects.o_inv_slot.Name) is not { } gear) return "";
        foreach (var r in gear.Instances())
        {
            try
            {
                if (IdKey(r.Get(Objects.o_player.Vars.object_index)) != gear.Index) continue;
                if (!r.Get("equipped").AsBool) continue;
                var data = new DsMap(r.Get("data"));
                if (!data.Exists || data.Get("Metatype").ToString() != "Weapon") continue;
                var owner = r.Get("owner");
                if (IdKey(owner) < 0 || !Builtins.instance_exists(owner).AsBool) continue;
                if (Builtins.object_get_name(new InstanceRef(owner).Get(Objects.o_player.Vars.object_index)).ToString() != Objects.o_inventory.Name)
                    continue;
                return r.Get("type").ToString();
            }
            catch (GmlException) { }
        }
        return "";
    }
}
