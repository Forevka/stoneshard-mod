using CoreLoader;
using StoneShard;

namespace StoneshardTrials.Cards;

/// <summary>
/// Every card the deck can deal. A card's numbers grow with its tier (the won
/// trial's danger, 1-5), and the stronger ones carry a cost. The set and its
/// numbers come from .omc/research/trials-rewards-catalogue.md (its starter set).
/// </summary>
internal static class Catalog
{
    // The value for a tier from one per tier (1-5).
    private static T T<T>(int tier, params T[] values) => values[Math.Clamp(tier, 1, values.Length) - 1];

    private static string Signed(double v) => v >= 0 ? $"+{v}" : v.ToString();

    private static readonly string[] Potion12 = { "good_pt_healing", "good_pt_regeneration" };

    public static readonly IReadOnlyList<CardDef> All = new List<CardDef>
    {
        new()
        {
            Id = "purse",
            Title = "Heavy Purse",
            Icon = "s_inv_gold",
            Gains = l => new[] { $"+{Purse(l.Tier, l.Level)} crowns", "(500 for each level, more at higher tiers)" },
            Apply = (ctx, _) =>
            {
                int gold = Purse(ctx.Tier, ctx.Level);
                Effects.Gold(ctx.Player, gold);
                return gold.ToString();
            },
        },
        new()
        {
            Id = "hard-bargain",
            Title = "Hard Bargain",
            Gains = l => new[] { $"+{AbilityPoints(l.Tier)} ability point{(AbilityPoints(l.Tier) > 1 ? "s" : "")}" },
            Costs = l => new[] { $"Your {l.Detail ?? "worn item"} is lost" },
            CanOffer = ctx => Effects.WornItems().Any(IsArmour),
            Prepare = ctx =>
            {
                var armour = Effects.WornItems().Where(IsArmour).ToList();
                return armour.Count == 0 ? null : armour[ctx.Rng.Next(armour.Count)].Get("idName").ToString();
            },
            Apply = (ctx, item) =>
            {
                // The item chosen when dealt, or another piece if it is no longer worn.
                var worn = Effects.WornItems().Where(IsArmour).ToList();
                int i = worn.FindIndex(w => w.Get("idName").ToString() == item);
                if (i < 0 && worn.Count > 0) i = ctx.Rng.Next(worn.Count);
                string lost = "nothing";
                if (i >= 0)
                {
                    lost = worn[i].Get("idName").ToString();
                    if (worn[i].Resolve() is { } inst) Scripts.scr_item_destroy.CallAs(inst, inst);
                }
                Effects.AddAtr(ctx.Player, "SP", AbilityPoints(ctx.Tier));
                return lost;
            },
        },
        new()
        {
            Id = "vampirism",
            Title = "Vampirism",
            Icon = "s_b_vampiric_blood",
            Unique = true,
            Gains = l => new[] { $"+{T(l.Tier, 10, 10, 15, 20, 20)}% lifesteal, for good" },
            Costs = l => new[] { $"{T(l.Tier, -3, -3, -5, -5, -5)} evasion" },
            Apply = (ctx, _) =>
            {
                Effects.LongBuff(ctx.Player, "o_b_vampiric_blood", "vampirism", Vampire(ctx.Tier));
                return null;
            },
            Reapply = (ctx, boon) => Effects.Retune(ctx.Player, "o_b_vampiric_blood", "vampirism", Vampire(boon.Tier)),
        },
        new()
        {
            Id = "night-eyes",
            Title = "Night Eyes",
            Unique = true,
            Gains = _ => new[] { "You see in the dark, for good" },
            Apply = (ctx, _) =>
            {
                Effects.NightVision(ctx.Player);
                return null;
            },
            Reapply = (ctx, _) => Effects.NightVision(ctx.Player),
        },
        new()
        {
            Id = "alchemist",
            Title = "Alchemist's Gift",
            Icon = "s_inv_bottle",
            Gains = l => new[]
            {
                $"{(l.Tier >= 4 ? "Two potions" : "A potion")} of {string.Join(", ", PotionTags(l.Tier).Select(Effect))}",
            },
            Apply = (ctx, _) =>
            {
                for (int i = 0; i < (ctx.Tier >= 4 ? 2 : 1); i++) Effects.Potion(PotionTags(ctx.Tier), ctx.Log);
                return null;
            },
        },
        new()
        {
            Id = "forbidden-library",
            Title = "Forbidden Library",
            Weight = 8,
            Prepare = ctx =>
            {
                var locked = LockedTrees(ctx.Run).ToHashSet();
                var open = Trees.All.Where(t => !locked.Contains(t)).OrderBy(_ => ctx.Rng.Next()).ToList();
                if (open.Count < 3) throw new InvalidOperationException("too few open skill trees");
                return $"{open[0].Name}|{open[1].Name}|{open[2].Name}";
            },
            Gains = l => Library(l.Detail) is var (a, b, _) && a != null && b != null
                ? new[] { $"{a.Name} and {b.Name} treatises", $"(tier {Roman(LibraryTier(l.Tier))})" }
                : new[] { "Two treatises" },
            Costs = l => Library(l.Detail) is var (_, _, c) && c != null
                ? new[] { $"{c.Name}: no skill or treatise of it can be learned this run" }
                : new[] { "One skill tree is closed for the run" },
            Apply = (ctx, detail) =>
            {
                var (a, b, c) = Library(detail);
                if (a == null || b == null || c == null) throw new InvalidOperationException("the card chose no trees");
                Effects.Item(Trees.TreatiseObject(a, LibraryTier(ctx.Tier)));
                Effects.Item(Trees.TreatiseObject(b, LibraryTier(ctx.Tier)));
                return detail;
            },
        },
        new()
        {
            Id = "trained-body",
            Title = "Trained Body",
            Gains = l => new[] { $"+{T(l.Tier, 1, 1, 2, 2, 3)} attribute point{(T(l.Tier, 1, 1, 2, 2, 3) > 1 ? "s" : "")}" },
            Costs = l => new[] { $"Curse of Decay for {3 + l.Tier} trials" },
            CostTrials = tier => 3 + tier,
            CostBuff = "o_db_curse",
            Apply = (ctx, _) =>
            {
                Effects.AddAtr(ctx.Player, "AP", T(ctx.Tier, 1, 1, 2, 2, 3));
                return null;
            },
        },
        new()
        {
            Id = "light-feet",
            Title = "Light Feet",
            Gains = l => new[] { $"+{T(l.Tier, 3, 4, 5, 6, 8)} evasion, for good" },
            Apply = (ctx, _) =>
            {
                Effects.AddAtr(ctx.Player, "bEVS", T(ctx.Tier, 3, 4, 5, 6, 8));
                return null;
            },
        },
        new()
        {
            Id = "deep-reserves",
            Title = "Deep Reserves",
            Gains = l => new[] { $"+{T(l.Tier, 8, 12, 16, 20, 25)} maximum energy, for good" },
            Costs = _ => new[] { "Eternal hangover for 2 trials" },
            CostTrials = _ => 2,
            CostBuff = "o_db_hangover",
            Apply = (ctx, _) =>
            {
                Effects.AddAtr(ctx.Player, "bMp", T(ctx.Tier, 8, 12, 16, 20, 25));
                return null;
            },
        },
        new()
        {
            Id = "stone-skin",
            Title = "Stone Skin",
            Icon = "s_b_stoneskin",
            MinTier = 3,
            Unique = true,
            Gains = _ => new[] { "Stone Skin for good:", "+20 physical resistance, 10% less damage taken" },
            Costs = _ => new[] { "Mark of the Feast for 3 trials" },
            CostTrials = _ => 3,
            CostBuff = "o_db_feast",
            Apply = (ctx, _) =>
            {
                Effects.LongBuff(ctx.Player, "o_b_stoneskin", "stone-skin");
                return null;
            },
        },
        new()
        {
            Id = "unholy-pact",
            Title = "Unholy Pact",
            Unique = true,
            Gains = _ => new[] { "Unholy Blessing for good:", "lifesteal, energy drain, less damage taken" },
            Costs = _ => new[] { "Vampiric Corruption for good" },
            Apply = (ctx, _) =>
            {
                Effects.LongBuff(ctx.Player, "o_b_dark_blessing", "unholy-pact");
                Effects.LongBuff(ctx.Player, "o_db_relic_curse", "unholy-pact");
                return null;
            },
        },
    };

    public static CardDef? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>The trees closed by the Forbidden Library cards taken.</summary>
    public static IEnumerable<Trees.Tree> LockedTrees(Run run) =>
        run.Boons.Where(b => b.Id == "forbidden-library").Select(b => Library(b.Detail).Locked).OfType<Trees.Tree>();

    private static int Purse(int tier, int level) => (int)Math.Round(500 * level * (0.8 + 0.2 * tier));
    private static int AbilityPoints(int tier) => T(tier, 1, 1, 2, 2, 3);
    private static int LibraryTier(int tier) => T(tier, 1, 1, 2, 2, 3);
    private static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", _ => "IV" };

    private static Dictionary<string, double> Vampire(int tier) => new()
    {
        ["Lifesteal"] = T(tier, 10, 10, 15, 20, 20),
        ["EVS"] = T(tier, -3, -3, -5, -5, -5),
    };

    private static string[] PotionTags(int tier) => tier switch
    {
        <= 2 => Potion12,
        <= 4 => new[] { "good_pt_healing", "good_pt_regeneration", "good_pt_lifesteal" },
        _ => new[] { "good_pt_healing", "good_pt_regeneration", "good_pt_lifesteal", "good_pt_fortifying" },
    };

    private static string Effect(string tag) => tag switch
    {
        "good_pt_healing" => "healing",
        "good_pt_regeneration" => "regeneration",
        "good_pt_lifesteal" => "life drain",
        "good_pt_fortifying" => "fortitude",
        _ => tag,
    };

    // Armour and jewellery, never what is in the hands.
    private static bool IsArmour(InstanceRef item) => item.Get("slot").ToString() != "hand";

    // "Given|Given|Locked", as Prepare chose them.
    private static (Trees.Tree? A, Trees.Tree? B, Trees.Tree? Locked) Library(string? detail)
    {
        var parts = detail?.Split('|') ?? Array.Empty<string>();
        return parts.Length == 3 ? (Trees.Find(parts[0]), Trees.Find(parts[1]), Trees.Find(parts[2])) : (null, null, null);
    }
}
