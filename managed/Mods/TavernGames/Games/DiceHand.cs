namespace TavernGames;

/// <summary>
/// A poker-dice hand: five six-sided dice, ranked like the tavern game
/// everyone knows - five of a kind down to nothing - with ties broken by the
/// dice that make the hand, then by what is left over.
/// </summary>
internal readonly record struct DiceHand(int Rank, int[] Tiebreak, string Name) : IComparable<DiceHand>
{
    public const int FiveKind = 8, FourKind = 7, FullHouse = 6, HighStraight = 5, LowStraight = 4,
                     ThreeKind = 3, TwoPair = 2, Pair = 1, Nothing = 0;

    public static DiceHand Of(IReadOnlyList<int> dice)
    {
        if (dice.Count != 5) throw new ArgumentException("poker dice are five dice", nameof(dice));
        // Values grouped by how many show, biggest group first, then highest value.
        var groups = dice.GroupBy(d => d).Select(g => (Value: g.Key, Count: g.Count()))
                         .OrderByDescending(g => g.Count).ThenByDescending(g => g.Value).ToArray();
        int[] byGroup = groups.SelectMany(g => Enumerable.Repeat(g.Value, g.Count)).ToArray();
        var sorted = dice.OrderBy(d => d).ToArray();

        if (groups[0].Count == 5) return new(FiveKind, byGroup, $"Five {Plural(groups[0].Value)}");
        if (groups[0].Count == 4) return new(FourKind, byGroup, $"Four {Plural(groups[0].Value)}");
        if (groups[0].Count == 3 && groups[1].Count == 2) return new(FullHouse, byGroup, $"Full house, {Plural(groups[0].Value)} on {Plural(groups[1].Value)}");
        if (sorted.SequenceEqual([2, 3, 4, 5, 6])) return new(HighStraight, byGroup, "Six-high straight");
        if (sorted.SequenceEqual([1, 2, 3, 4, 5])) return new(LowStraight, byGroup, "Five-high straight");
        if (groups[0].Count == 3) return new(ThreeKind, byGroup, $"Three {Plural(groups[0].Value)}");
        if (groups[0].Count == 2 && groups[1].Count == 2) return new(TwoPair, byGroup, $"Two pair, {Plural(groups[0].Value)} and {Plural(groups[1].Value)}");
        if (groups[0].Count == 2) return new(Pair, byGroup, $"A pair of {Plural(groups[0].Value)}");
        return new(Nothing, byGroup, $"Nothing, {groups[0].Value} high");
    }

    public int CompareTo(DiceHand other)
    {
        if (Rank != other.Rank) return Rank.CompareTo(other.Rank);
        for (int i = 0; i < Math.Min(Tiebreak.Length, other.Tiebreak.Length); i++)
            if (Tiebreak[i] != other.Tiebreak[i]) return Tiebreak[i].CompareTo(other.Tiebreak[i]);
        return 0;
    }

    /// <summary>
    /// Which dice to throw again, the plain tavern way: keep what makes the
    /// hand and throw the rest; a made straight, full house or five stand pat;
    /// with nothing, keep four towards a straight. (It never breaks a pair to
    /// chase a straight, though that is sometimes the better odds.)
    /// </summary>
    public static bool[] RerollAdvice(IReadOnlyList<int> dice)
    {
        var hand = Of(dice);
        var reroll = new bool[5];
        switch (hand.Rank)
        {
            case FiveKind or FullHouse or HighStraight or LowStraight:
                return reroll;
            case FourKind or ThreeKind or Pair:
            {
                int keep = hand.Tiebreak[0];
                for (int i = 0; i < 5; i++) reroll[i] = dice[i] != keep;
                return reroll;
            }
            case TwoPair:
            {
                // Keep both pairs; the odd die goes again for a full house.
                var counts = dice.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
                for (int i = 0; i < 5; i++) reroll[i] = counts[dice[i]] == 1;
                return reroll;
            }
        }
        // Nothing: five different values with a gap in 2-5 (a missing 1 or 6
        // would be a straight), so both the 1 and the 6 are there. Without the
        // 1, the other four wait on one number for the six-high straight.
        for (int i = 0; i < 5; i++) reroll[i] = dice[i] == 1;
        return reroll;
    }

    private static string Plural(int v) => v switch
    {
        1 => "ones", 2 => "twos", 3 => "threes", 4 => "fours", 5 => "fives", _ => "sixes",
    };
}
