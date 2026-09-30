using CoreLoader;
using StoneShard;

namespace TavernGames;

/// <summary>
/// The player's crowns, moved only through the game's own gold scripts:
/// scr_gold_count to read, scr_gold_add to pay out, scr_gold_write_off to
/// take (gold is coin items in the bag; these add and remove them). Every change is
/// read back, so a script that quietly refuses is noticed rather than turned
/// into free money.
/// </summary>
internal static class Purse
{
    /// <summary>The crowns the player carries, or null when the game cannot say (no player, a refused call).</summary>
    public static int? Count(InstanceRef player)
    {
        try
        {
            var v = Scripts.scr_gold_count.CallAs(player);
            return v.IsNumber ? (int)Math.Round(v.AsReal) : null;
        }
        catch (GmlException) { return null; }
    }

    /// <summary>
    /// Adds <paramref name="delta"/> crowns (negative takes them) and returns
    /// how much the count really moved, which the caller compares with what it
    /// asked for.
    /// </summary>
    /// <exception cref="InvalidOperationException">The purse is short, or cannot be counted.</exception>
    public static int Change(InstanceRef player, int delta)
    {
        if (delta == 0) return 0;
        int before = Count(player) ?? throw new InvalidOperationException("cannot count your crowns");
        if (before + delta < 0) throw new InvalidOperationException($"you carry only {before} crowns");
        // Two scripts, one per direction: scr_gold_add with a negative amount
        // does not take gold (live: -10 added 9), while scr_gold_write_off is
        // what the game's own dialogue uses to take a payment.
        if (delta > 0) Scripts.scr_gold_add.CallAs(player, delta);
        else Scripts.scr_gold_write_off.CallAs(player, -delta);
        int after = Count(player) ?? throw new InvalidOperationException("cannot count your crowns");
        return after - before;
    }
}
