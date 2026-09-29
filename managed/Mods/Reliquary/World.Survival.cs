using CoreLoader;
using StoneShard;

namespace Reliquary;

/// <summary>
/// Helpers for the survival and scaling relics: character-level state in the
/// psyche map, gold, XP, worn gear, and the game's own teleport.
/// </summary>
internal static partial class World
{
    // ------------------------------------------------------------ psyche map

    /// <summary>
    /// A number kept in the player's psyData. That map is saved with the
    /// character (the Grafted Hand's cap survived a save and load there), so it
    /// is where an effect that belongs to the character rather than to an item
    /// lives.
    /// </summary>
    public static double? PsyNum(InstanceRef player, string key)
    {
        var psy = new DsMap(player.Get(VarPsyche));
        var v = psy.Exists ? psy.Get(key) : RValue.Undefined;
        return v.IsNumber ? v.AsReal : null;
    }

    public static void PsySet(InstanceRef player, string key, double value)
    {
        var psy = new DsMap(player.Get(VarPsyche));
        if (psy.Exists) psy.Set(key, value);
    }

    // ------------------------------------------------------------ gold and XP

    /// <summary>
    /// The crowns the player carries, as the game counts them: scr_gold_count
    /// run as the player is what StoneshardCheats' gold read-back uses. Null
    /// when the game will not answer.
    /// </summary>
    public static double? Gold(InstanceRef player)
    {
        try
        {
            var v = Scripts.scr_gold_count.CallAs(player);
            return v.IsNumber ? v.AsReal : null;
        }
        catch (GmlException) { return null; }
    }

    /// <summary>XP through the game's real level-up path (scr_get_XP), not a raw write.</summary>
    public static void GrantXp(InstanceRef player, double amount) => Scripts.scr_get_XP.CallAs(player, amount);

    // ------------------------------------------------------------ gear

    /// <summary>
    /// Gear the player is wearing: o_inv_slot itself (every piece of gear is
    /// that object; its children are the other inventory items), equipped, in
    /// the player's own inventory.
    /// </summary>
    public static List<InstanceRef> WornGear()
    {
        var worn = new List<InstanceRef>();
        if (Objects.o_inv_slot.Object is not { } obj) return worn;
        foreach (var r in obj.Instances())
        {
            try
            {
                if (IdKey(r.Get(InstanceVars.object_index)) != obj.Index) continue;
                if (!r.Get("equipped").AsBool) continue;
                var owner = r.Get("owner");
                if (IdKey(owner) < 0 || !Builtins.instance_exists(owner).AsBool) continue;
                if (Builtins.object_get_name(new InstanceRef(owner).Get(InstanceVars.object_index)).ToString() != Objects.o_inventory.Name)
                    continue;
                worn.Add(r);
            }
            catch (GmlException) { /* an item going away mid-walk costs only itself */ }
        }
        return worn;
    }

    /// <summary>
    /// Breaks a piece of gear the game's own way: its `data` keeps durability
    /// as Duration (out of MaxDuration), and at zero the game treats it as broken.
    /// </summary>
    public static bool BreakGear(InstanceRef gear)
    {
        var data = new DsMap(gear.Get("data"));
        if (!data.Exists || !data.Get("Duration").IsNumber) return false;
        data.Set("Duration", 0);
        return true;
    }

    public static string GearName(InstanceRef gear)
    {
        var data = new DsMap(gear.Get("data"));
        var n = data.Exists ? data.Get("Name") : RValue.Undefined;
        if (n.Kind != RValueKind.String) n = data.Exists ? data.Get("idName") : RValue.Undefined;
        return n.Kind == RValueKind.String ? n.ToString() : "something";
    }

    // ------------------------------------------------------------ hostiles

    /// <summary>Every living hostile on the floor, however far.</summary>
    public static List<InstanceRef> AllHostiles(InstanceRef player) => Hostiles(player, double.MaxValue);

    /// <summary>Tops a unit's HP up to its max_hp.</summary>
    public static void HealFull(InstanceRef unit)
    {
        double max = Num(unit, VarMaxHp, 0);
        if (max > 0) unit.Set(VarHp, max);
    }

    // ------------------------------------------------------------ teleport

    /// <summary>
    /// EXPERIMENTAL: moves a unit with the game's own scr_teleport, run as the
    /// unit. Its two arguments are read from the compiled code but their
    /// meaning (x, y in world units is the guess) is not confirmed on the live
    /// game, and writing x/y by hand does not stick for units - the grid puts
    /// them back. So this reports whether the unit really moved, and callers
    /// only try it when their experimental switch is on.
    /// </summary>
    public static bool TryTeleport(InstanceRef unit, double x, double y)
    {
        var (bx, by) = Position(unit);
        try { Scripts.scr_teleport.CallAs(unit, x, y); }
        catch (GmlException) { return false; }
        var (ax, ay) = Position(unit);
        return Math.Abs(ax - bx) > 1 || Math.Abs(ay - by) > 1;
    }
}
