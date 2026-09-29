using CoreLoader;
using StoneShard;

namespace Reliquary;

/// <summary>
/// Helpers for the aura, body and needs relics: every unit near the player
/// (not only hostiles), statuses kept topped up rather than stacked, and the
/// enemy AI's own noise fields.
/// </summary>
internal static partial class World
{
    /// <summary>
    /// Every living unit within <paramref name="radius"/> tiles of the player,
    /// hostile or not - townsfolk and neutrals included - without the player.
    /// </summary>
    public static List<InstanceRef> UnitsNear(InstanceRef player, double radius)
    {
        var found = new List<InstanceRef>();
        if (Objects.o_enemy.Object is not { } units) return found;
        foreach (var u in units.Instances())
        {
            if (!u.Exists || Num(u, VarHp) <= 0) continue;
            if (Tiles(player, u) <= radius) found.Add(u);
        }
        return found;
    }

    /// <summary>
    /// Keeps a status on a unit for at least <paramref name="turns"/> more
    /// turns: an existing one has its duration raised, otherwise a new one is
    /// applied. An aura that re-applies every turn would otherwise pile up a
    /// new instance per turn.
    /// </summary>
    public static void RefreshStatus(InstanceRef unit, string statusObject, int turns)
    {
        if (FindStatus(unit, statusObject) is { } existing)
        {
            if (Num(existing, Objects.c_buff.Vars.duration) < turns) existing.Set(Objects.c_buff.Vars.duration, turns);
            return;
        }
        ApplyStatus(unit, statusObject, turns);
    }

    /// <summary>The unit's live status of this object, or null.</summary>
    public static InstanceRef? FindStatus(InstanceRef unit, string statusObject)
    {
        var buffs = new DsList(unit.Get(VarBuffs));
        if (!buffs.Exists) return null;
        for (int i = 0; i < buffs.Count; i++)
        {
            var s = new InstanceRef(buffs.At(i));
            if (s.Exists && Builtins.object_get_name(s.Get(Objects.o_player.Vars.object_index)).ToString() == statusObject)
                return s;
        }
        return null;
    }

    /// <summary>Destroys the unit's statuses of this object; returns how many went.</summary>
    public static int RemoveStatus(InstanceRef unit, string statusObject)
    {
        var buffs = new DsList(unit.Get(VarBuffs));
        if (!buffs.Exists) return 0;
        int removed = 0;
        // Back to front: removing an entry shifts the ones after it.
        for (int i = buffs.Count - 1; i >= 0; i--)
        {
            var s = new InstanceRef(buffs.At(i));
            if (!s.Exists || Builtins.object_get_name(s.Get(Objects.o_player.Vars.object_index)).ToString() != statusObject) continue;
            Destroy(s.Id);
            // The status's Destroy event may already have taken its own entry
            // out; only remove index i if it still holds this status, never a
            // neighbour that slid into its place.
            if (i < buffs.Count && IdKey(buffs.At(i)) == IdKey(s.Id)) buffs.RemoveAt(i);
            removed++;
        }
        if (removed > 0) Recalculate(unit);
        return removed;
    }

    /// <summary>
    /// Points an enemy's noise sense at the player: the same fields the game's
    /// noise system fills when a unit hears something (noise_x, noise_y,
    /// noise_source). Whether and how the AI answers is the game's business;
    /// the write itself is only a sound "heard".
    /// </summary>
    public static void MakeHeard(InstanceRef enemy, InstanceRef player)
    {
        var (x, y) = Position(player);
        enemy.Set(Objects.o_enemy.Vars.noise_x, x);
        enemy.Set(Objects.o_enemy.Vars.noise_y, y);
        enemy.Set(Objects.o_enemy.Vars.noise_source, player.Id);
    }
}
