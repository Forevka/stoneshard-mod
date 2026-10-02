using CoreLoader;

namespace StoneshardTrials.Cards;

/// <summary>What a card's actions get: the player, the run, the card's tier and a generator.</summary>
internal readonly record struct CardContext(InstanceRef Player, Run Run, int Tier, Random Rng, Logger Log)
{
    public int Level => Math.Max(1, (int)World.Num(Player, "LVL", 1));
}

/// <summary>
/// What a card's text is written for: its tier, the character's level, and
/// what it chose when dealt (the item it takes, the trees it touches), if anything.
/// </summary>
internal readonly record struct CardLook(int Tier, int Level, string? Detail = null);

/// <summary>
/// One kind of card. Its lines describe it at a tier (1-5, the won trial's
/// danger); Apply does it once when the card is taken and answers what it
/// chose, if anything (an item, a skill tree), which is kept on the boon.
/// </summary>
internal sealed class CardDef
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>A sprite to show on the card (an item's or a status's icon); null for none.</summary>
    public string? Icon { get; init; }
    public required Func<CardLook, IReadOnlyList<string>> Gains { get; init; }
    public Func<CardLook, IReadOnlyList<string>> Costs { get; init; } = _ => Array.Empty<string>();
    /// <summary>How often it is drawn, against the other cards' weights.</summary>
    public int Weight { get; init; } = 10;
    public int MinTier { get; init; } = 1;
    /// <summary>Taken at most once per run (a permanent effect that does not stack).</summary>
    public bool Unique { get; init; }
    /// <summary>Whether it can be offered now (it would do something, its cost can be paid).</summary>
    public Func<CardContext, bool>? CanOffer { get; init; }
    /// <summary>
    /// Chooses, when the card is dealt, what it will act on (shown on the card
    /// and kept with the offer); null for a card that chooses nothing.
    /// </summary>
    public Func<CardContext, string?>? Prepare { get; init; }
    /// <summary>Does it, given what Prepare chose; answers what to keep on the boon.</summary>
    public required Func<CardContext, string?, string?> Apply { get; init; }
    /// <summary>A cost that lasts this many trials (then <see cref="Expire"/> ends it); 0 for none.</summary>
    public Func<int, int> CostTrials { get; init; } = _ => 0;
    public Action<CardContext, Boon>? Expire { get; init; }
    /// <summary>
    /// The debuff a timed cost is: kept on the player, lasting, until the cost
    /// ends (some, like Curse of Decay, shorten their own turns), then ended.
    /// </summary>
    public string? CostBuff { get; init; }
    /// <summary>
    /// Puts back, after a load, what the game does not save itself; null when
    /// the game keeps the effect. Must do nothing when the effect is already there.
    /// </summary>
    public Action<CardContext, Boon>? Reapply { get; init; }
}

/// <summary>Draws the cards offered after a won trial.</summary>
internal static class Deck
{
    public const int Size = 3;

    /// <summary>
    /// Up to <see cref="Size"/> different cards that can be offered at this
    /// tier, weighted, with a generator seeded by the run and the trial so a
    /// reload deals the same hand.
    /// </summary>
    public static Offer Deal(IReadOnlyList<CardDef> all, CardContext ctx, int trial)
    {
        var taken = ctx.Run.Boons.Select(b => b.Id).ToHashSet();
        var pool = all.Where(c => ctx.Tier >= c.MinTier && !(c.Unique && taken.Contains(c.Id)) && Offerable(c, ctx)).ToList();
        var hand = new List<string>();
        var details = new List<string?>();
        while (hand.Count < Size && pool.Count > 0)
        {
            int roll = ctx.Rng.Next(pool.Sum(c => c.Weight)), i = 0;
            while (roll >= pool[i].Weight) roll -= pool[i++].Weight;
            var card = pool[i];
            pool.RemoveAt(i);
            string? detail = null;
            try { detail = card.Prepare?.Invoke(ctx); }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException)
            {
                ctx.Log.Warning($"card {card.Id}: {ex.Message}; not offered");
                continue;
            }
            hand.Add(card.Id);
            details.Add(detail);
        }
        return new Offer { Trial = trial, Tier = ctx.Tier, Cards = hand, Details = details };
    }

    private static bool Offerable(CardDef c, CardContext ctx)
    {
        try { return c.CanOffer?.Invoke(ctx) ?? true; }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException)
        {
            ctx.Log.Warning($"card {c.Id}: {ex.Message}; not offered");
            return false;
        }
    }
}
