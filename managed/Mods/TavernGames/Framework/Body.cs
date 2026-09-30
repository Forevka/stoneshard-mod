using CoreLoader;
using StoneShard;

namespace TavernGames;

/// <summary>
/// What the physical games do to the character for real, always through the
/// game's own effects and attributes, so the game's own rules take it from
/// there. Watched live:
///   * an ale is scr_effect_create(o_db_drunk, 20): 20 more turns of
///     Drunkenness, whose stage follows the turns left - about one stage per
///     100 (II from about 130, IV from about 300), and the stages bring the
///     game's Confusion, vomiting and, at IV, sleep;
///   * scr_effect_create puts its effect on the player whoever calls it, so
///     scr_effect_create(o_db_sleep, n) is the player passing out: "Can't
///     perform any actions" for n turns, the screen dark with z's; turns pass
///     as the player waits (one wait sleeps several turns away);
///   * scr_fatigue_change(n) adds to the Fatigue attribute;
///   * an ale also calls scr_atr_incr("Thirsty", -5).
/// A refused call costs its effect, never the round: every helper swallows
/// the game's error.
/// </summary>
internal static class Body
{
    /// <summary>The player's strength (STR) as the game counts it (scr_atr), else the instance's own, else 10.</summary>
    public static double Strength(InstanceRef player)
    {
        try
        {
            var v = Scripts.scr_atr.CallAs(player, "STR");
            if (v.IsNumber && v.AsReal > 0) return v.AsReal;
        }
        catch (GmlException) { }
        return Tavern.Num(player, "STR", 10);
    }

    /// <summary>Real drink: <paramref name="turns"/> more turns of Drunkenness, and a little less thirst.</summary>
    public static void Drink(InstanceRef player, double turns)
    {
        Effect(player, Objects.o_db_drunk.Name, turns);
        try { Scripts.scr_atr_incr.CallAs(player, "Thirsty", -3); }
        catch (GmlException) { }
    }

    /// <summary>The player's Drunkenness right now: its stage (0 sober, up to 4) and turns left.</summary>
    public static (int Stage, double Turns) Drunkenness(InstanceRef player)
    {
        try
        {
            var buffs = new DsList(player.Get(Objects.o_player.Vars.buffs));
            if (!buffs.Exists) return (0, 0);
            int count = buffs.Count;
            for (int i = 0; i < count; i++)
            {
                var b = new InstanceRef(buffs.At(i));
                if (!b.Exists || Tavern.ObjectName(b) != Objects.o_db_drunk.Name) continue;
                return ((int)Tavern.Num(b, "stage", 1), Tavern.Num(b, "duration"));
            }
        }
        catch (GmlException) { }
        return (0, 0);
    }

    /// <summary>The player passes out for <paramref name="turns"/> turns.</summary>
    public static void PassOut(InstanceRef player, int turns) => Effect(player, Objects.o_db_sleep.Name, turns);

    public static void Tire(InstanceRef player, double amount)
    {
        try { Scripts.scr_fatigue_change.CallAs(player, amount); }
        catch (GmlException) { }
    }

    private static void Effect(InstanceRef player, string effect, double turns)
    {
        try
        {
            var obj = Builtins.asset_get_index(effect);
            if (obj.IsNumber && obj.AsReal >= 0) Scripts.scr_effect_create.CallAs(player, obj, turns);
        }
        catch (GmlException) { }
    }
}
