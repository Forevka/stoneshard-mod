namespace TavernGames;

/// <summary>
/// The mod's books on every opponent it has met: how much they have left to
/// lose and how the player stands with them. A purse refills slowly as turns
/// pass (a drunk is paid again, eventually), up to what that NPC starts with.
/// </summary>
/// <remarks>
/// Kept in memory for the session. Vanilla townsfolk carry no money of their
/// own, so there is nothing in the save to read or write; a restart or a hot
/// reload starts everyone full again. A load does not: nothing tells a load
/// from a room change (both replace the player), and clearing on a room change
/// would refill a drained purse for walking out and back in. So a reload after
/// a loss leaves the opponent the richer - never the player.
/// </remarks>
internal sealed class Ledger
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal sealed class Entry
    {
        public Entry(int purse) => Purse = Bankroll = purse;

        /// <summary>What this NPC starts with, and refills to.</summary>
        public int Purse { get; }

        public int Bankroll { get; set; }

        /// <summary>The player's winnings off this NPC (negative: losses).</summary>
        public int Net { get; set; }

        public int Played { get; set; }

        // Refill arrives in whole crowns; the fraction is kept until it adds up.
        public double Refill { get; set; }
    }

    public Entry For(string key, int purse)
    {
        if (!_entries.TryGetValue(key, out var e)) _entries[key] = e = new Entry(purse);
        return e;
    }

    public IEnumerable<(string Key, Entry Entry)> All => _entries.Select(kv => (kv.Key, kv.Value));

    /// <summary>One player turn passed: every purse short of full gets a little back (all of it in ~300 turns).</summary>
    public void Turn()
    {
        foreach (var e in _entries.Values)
        {
            if (e.Bankroll >= e.Purse) continue;
            e.Refill += e.Purse / 300.0;
            int whole = (int)e.Refill;
            if (whole == 0) continue;
            e.Refill -= whole;
            e.Bankroll = Math.Min(e.Purse, e.Bankroll + whole);
        }
    }
}
