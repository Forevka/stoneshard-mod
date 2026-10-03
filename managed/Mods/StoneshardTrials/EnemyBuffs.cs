using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// Lasting stat changes on an enemy, made the way the game makes its own enemy
/// modifiers: one o_temp_incr_atr buff per stat.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25; .omc/research/trials-next.md 2 and 5):
///   * an enemy's derived stats are rebuilt by scr_atr_calc every turn and its
///     base stats are reset by a load, but its buffs feed that rebuild and are
///     saved with it;
///   * an o_temp_incr_atr owned by the enemy, with `atr` / `param` (what its
///     Alarm 2 rebuilds its data from on a load), is_enemy_modificator 1 and a
///     long duration, in the enemy's buff list, applies at once after
///     scr_atr_calc and comes back by itself after Save &amp; Exit and a load;
///   * keys that apply: max_hp (HP scales with it), Weapon_Damage (percent),
///     Hit_Chance, PRR, EVS, CRT, Damage_Received (percent; negative is tougher),
///     Lifesteal, the damage-type keys;
///   * only one key per buff survives a load, so one buff per stat;
///   * the game gives enemies short ones of its own (duration 1-3): the mod's
///     are told apart by a duration of at least <see cref="Ours"/>.
/// </remarks>
internal static class EnemyBuffs
{
    private const double Turns = 99999;
    /// <summary>A buff lasting this long is the mod's (a load brings it back as 100000).</summary>
    public const double Ours = 10000;

    /// <summary>Adds lasting stat changes to the enemy (one buff each) and applies them now.</summary>
    public static void Add(InstanceRef enemy, IReadOnlyDictionary<string, double> stats)
    {
        var obj = Objects.o_temp_incr_atr.Object ?? throw new InvalidOperationException("no o_temp_incr_atr");
        var list = new DsList(enemy.Get("buffs"));
        if (!list.Exists) throw new InvalidOperationException("the enemy has no buff list");
        foreach (var (stat, amount) in stats)
        {
            var buff = new InstanceRef(Builtins.instance_create_depth(-15000, -15000, 0, obj.Index));
            if (!buff.Exists) continue;
            buff.Set("owner", enemy.Id);
            buff.Set("target", enemy.Id);
            buff.Set("atr", stat);
            buff.Set("param", amount);
            buff.Set("is_enemy_modificator", 1);
            buff.Set("duration", Turns);
            list.Add(buff.Id);
            var data = new DsMap(buff.Get("data"));
            if (data.Exists)
            {
                data.Clear();
                data.Set(stat, amount);
            }
        }
        enemy.Set("buffs_is_change", true);
        if (enemy.Resolve() is { } inst) Game.CallScriptAs(inst, inst, "scr_atr_calc");
    }

    /// <summary>Whether the mod already gave this enemy its buffs (they survive a load, so they are never added twice).</summary>
    public static bool HasOurs(InstanceRef enemy)
    {
        var list = new DsList(enemy.Get("buffs"));
        if (!list.Exists || Objects.o_temp_incr_atr.Object is not { } obj) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var b = new InstanceRef(list.At(i));
            if (World.IdKey(b.Id) >= 0 && b.Exists && b.Get("object_index").AsReal == obj.Index && World.Num(b, "duration") >= Ours) return true;
        }
        return false;
    }
}
