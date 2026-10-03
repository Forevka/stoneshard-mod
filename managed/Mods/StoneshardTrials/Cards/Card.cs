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
/// A reward a card gives. Its lines describe it at a tier (1-5, the won
/// trial's danger); Apply does it once when the card is taken and answers what
/// it chose, if anything, which is kept on the boon. A reward of power 2 or 3
/// comes with a cost (<see cref="CostDef"/>) of about its weight, unless it
/// carries its own (<see cref="SelfCosted"/>, its Costs lines).
/// </summary>
internal sealed class CardDef
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>A sprite to show on the card (an item's or a status's icon); null for none.</summary>
    public string? Icon { get; init; }
    public required Func<CardLook, IReadOnlyList<string>> Gains { get; init; }
    /// <summary>The cost lines of a reward that carries its own cost.</summary>
    public Func<CardLook, IReadOnlyList<string>> Costs { get; init; } = _ => Array.Empty<string>();
    /// <summary>1 a small reward (free), 2 a strong one, 3 a great one (each draws a cost of about that weight).</summary>
    public int Power { get; init; } = 1;
    /// <summary>The cost is part of the reward itself (in its numbers, or its nature): no other is drawn.</summary>
    public bool SelfCosted { get; init; }
    /// <summary>The cost this reward always takes, when it can be paid (else one is drawn).</summary>
    public string? Cost { get; init; }
    /// <summary>How often it is drawn, against the other cards' weights.</summary>
    public int Weight { get; init; } = 10;
    public int MinTier { get; init; } = 1;
    /// <summary>Taken at most once per run (a permanent effect that does not stack).</summary>
    public bool Unique { get; init; }
    /// <summary>
    /// The status objects it keeps on the player. No two boons of a run share
    /// one: after a load the game makes them anew without the mod's tag, and
    /// two of a kind could not be told apart.
    /// </summary>
    public string[] Carriers { get; init; } = Array.Empty<string>();
    /// <summary>Whether it can be offered now (it would do something, its cost can be paid).</summary>
    public Func<CardContext, bool>? CanOffer { get; init; }
    /// <summary>Chooses, when dealt, what it will act on (shown on the card, kept with the offer).</summary>
    public Func<CardContext, string?>? Prepare { get; init; }
    /// <summary>Does it, given what Prepare chose; answers what to keep on the boon.</summary>
    public required Func<CardContext, string?, string?> Apply { get; init; }
    /// <summary>
    /// Puts back, every second, what the game does not keep (after a load, in
    /// each room's new player instance); null when the game keeps the effect.
    /// Must do nothing when the effect is already there.
    /// </summary>
    public Action<CardContext, Boon>? Reapply { get; init; }
}

/// <summary>
/// A cost a strong reward comes with: paid when the card is taken, and either
/// for good or for a number of trials won, after which it ends.
/// </summary>
internal sealed class CostDef
{
    public required string Id { get; init; }
    public required Func<CardLook, IReadOnlyList<string>> Text { get; init; }
    /// <summary>1 light, 2 heavy, 3 grave: matched to the reward's power.</summary>
    public int Severity { get; init; } = 1;
    /// <summary>Trials it lasts at a tier; 0 for good (or for a cost paid at once).</summary>
    public Func<int, int> Trials { get; init; } = _ => 0;
    /// <summary>The debuff it is, kept on the player (lasting) while the cost lasts, then ended.</summary>
    public string? Buff { get; init; }
    public int Weight { get; init; } = 10;
    public Func<CardContext, bool>? CanOffer { get; init; }
    /// <summary>Chooses what it takes, knowing what its reward chose (so it never takes that back).</summary>
    public Func<CardContext, string?, string?>? Prepare { get; init; }
    /// <summary>Pays it (beyond putting on <see cref="Buff"/>); answers what to keep. Null: the buff is all of it.</summary>
    public Func<CardContext, string?, string?>? Apply { get; init; }
    /// <summary>Undoes what Apply did, when a timed cost ends (beyond ending <see cref="Buff"/>).</summary>
    public Action<CardContext, Boon>? Expire { get; init; }
    /// <summary>Its debuff is the object Apply chose and kept as the boon's CostDetail (Old Wound's part and level).</summary>
    public bool BuffIsDetail { get; init; }
    /// <summary>The debuff a boon paying this cost keeps on while the cost lasts, if any.</summary>
    public string? KeptBuff(Boon b) => Buff ?? (BuffIsDetail ? b.CostDetail : null);
    /// <summary>The tag its buff carries: shared by every boon paying this cost (one debuff of a kind does).</summary>
    public string Tag => "cost:" + Id;
}

/// <summary>Draws the cards offered after a won trial.</summary>
internal static class Deck
{
    public const int Size = 3;

    /// <summary>
    /// Up to <paramref name="size"/> different rewards that can be offered at
    /// this tier, weighted, each with a cost when it is strong, from a
    /// generator seeded by the run and the trial so a reload deals the same hand.
    /// </summary>
    public static Offer Deal(IReadOnlyList<CardDef> all, IReadOnlyList<CostDef> costs, CardContext ctx, int trial, int size = Size,
                             IReadOnlyList<CardDef>? force = null)
    {
        var taken = ctx.Run.Boons.Select(b => b.Id).ToHashSet();
        // Every status the run keeps: the rewards' and the costs' (a reward and a cost
        // on one object would be two of a kind, which a load merges into one).
        var carried = ctx.Run.Boons.SelectMany(b => Catalog.Find(b.Id)?.Carriers ?? Array.Empty<string>()).ToHashSet();
        foreach (var b in ctx.Run.Boons)
            if (Catalog.FindCost(b.Cost) is { Buff: { } debuff }) carried.Add(debuff);
        // Forced (the test host): those cards, in that order, whatever the rules say.
        var pool = force?.ToList() ??
                   all.Where(c => ctx.Tier >= c.MinTier && !(c.Unique && taken.Contains(c.Id)) && !c.Carriers.Any(carried.Contains) && Offerable(c.Id, c.CanOffer, ctx)).ToList();
        var offer = new Offer { Trial = trial, Tier = ctx.Tier };
        while (offer.Cards.Count < size && pool.Count > 0)
        {
            var card = force != null ? pool[0] : Draw(pool, c => c.Weight, ctx.Rng);
            pool.Remove(card);
            // Two cards of one hand must not share a status either.
            pool.RemoveAll(c => c.Carriers.Any(card.Carriers.Contains));
            if (!Prepared(card.Id, card.Prepare, ctx, out var detail)) continue;
            string? costId = null, costDetail = null;
            if (!card.SelfCosted && (card.Power >= 2 || card.Cost != null))
            {
                var kept = carried.Concat(card.Carriers).ToHashSet();
                if (!PrepareCost(card, costs, ctx, detail, kept, out var cost, out costDetail)) continue;
                costId = cost!.Id;
                if (cost.Buff != null) carried.Add(cost.Buff);
            }
            carried.UnionWith(card.Carriers);
            pool.RemoveAll(c => c.Carriers.Any(carried.Contains));
            offer.Cards.Add(card.Id);
            offer.Details.Add(detail);
            offer.Costs.Add(costId);
            offer.CostDetails.Add(costDetail);
        }
        return offer;
    }

    // Its own cost when it names one and it can be paid and prepared; else one
    // of about its weight, never on a status the run or this hand already keeps.
    private static bool PrepareCost(CardDef card, IReadOnlyList<CostDef> costs, CardContext ctx, string? cardDetail, HashSet<string> taken,
                                    out CostDef? cost, out string? detail)
    {
        bool Usable(CostDef c) => (c.Buff == null || !taken.Contains(c.Buff)) && Offerable(c.Id, c.CanOffer, ctx);
        bool Ready(CostDef c, out string? d) => Prepared(c.Id, c.Prepare == null ? null : x => c.Prepare(x, cardDetail), ctx, out d);
        if (card.Cost != null && costs.FirstOrDefault(c => c.Id == card.Cost) is { } own && Usable(own) && Ready(own, out detail))
        {
            cost = own;
            return true;
        }
        int power = Math.Clamp(card.Power, 1, 3);
        var fit = costs.Where(c => c.Id != card.Cost && c.Severity <= power && c.Severity >= power - 1 && Usable(c)).ToList();
        while (fit.Count > 0)
        {
            var pick = Draw(fit, c => c.Weight, ctx.Rng);
            fit.Remove(pick);
            if (!Ready(pick, out detail)) continue;
            cost = pick;
            return true;
        }
        cost = null;
        detail = null;
        return false;
    }

    private static T Draw<T>(List<T> from, Func<T, int> weight, Random rng)
    {
        int roll = rng.Next(from.Sum(weight)), i = 0;
        while (roll >= weight(from[i])) roll -= weight(from[i++]);
        return from[i];
    }

    private static bool Offerable(string id, Func<CardContext, bool>? can, CardContext ctx)
    {
        try { return can?.Invoke(ctx) ?? true; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ctx.Log.Warning($"card {id}: {ex.Message}; not offered");
            return false;
        }
    }

    private static bool Prepared(string id, Func<CardContext, string?>? prepare, CardContext ctx, out string? detail)
    {
        detail = null;
        try
        {
            detail = prepare?.Invoke(ctx);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ctx.Log.Warning($"card {id}: {ex.Message}; not offered");
            return false;
        }
    }
}
