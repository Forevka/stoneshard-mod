using CoreLoader;
using StoneShard;

namespace StoneshardTrials.Cards;

/// <summary>
/// Every reward and cost the deck deals, from the research catalogue
/// (.omc/research/trials-rewards-catalogue.md) as the player approved it. A
/// card's numbers grow with its tier (the won trial's danger, 1-5); a reward
/// of power 2 or 3 is dealt with a cost of about its weight.
/// </summary>
internal static class Catalog
{
    // The value for a tier, from one per tier (1-5).
    private static T T<T>(int tier, params T[] values) => values[Math.Clamp(tier, 1, values.Length) - 1];
    private static string S(int n, string one, string many) => n == 1 ? one : many;

    private static readonly string[] Attributes = { "STR", "AGL", "PRC", "Vitality", "WIL" };
    private static string AttributeName(string key) => key switch
    {
        "STR" => "Strength", "AGL" => "Agility", "PRC" => "Perception", "Vitality" => "Vitality", "WIL" => "Willpower", _ => key,
    };

    private static readonly Dictionary<string, string> Perks = new()
    {
        ["o_perk_lifelong_journey"] = "Lifelong Journey",
        ["o_perk_berserk"] = "Berserk",
        ["o_perk_trained_eye"] = "Trained Eye",
        ["o_perk_wild_hunt"] = "Wild Hunt",
        ["o_perk_hunting_secrets"] = "Hunting Secrets",
        ["o_perk_magical_erudition"] = "Magical Erudition",
        ["o_perk_might_and_magic"] = "Might and Magic",
        ["o_perk_artifact_knowledge"] = "Artifact Knowledge",
        ["o_perk_vow_feat"] = "Feat of the Vow",
    };

    private static readonly string[] Kit =
    {
        "o_inv_salve", "o_inv_splint", "o_inv_bandage", "o_inv_antitoxin", "o_inv_antivenom", "o_inv_leech", "o_inv_herbal",
        "o_inv_inhaler", "o_inv_scroll_identification", "o_inv_scroll_disenchant", "o_inv_rope", "o_inv_lockpicks", "o_inv_torch",
    };

    private static readonly string[] WoundParts = { "tors", "legs", "head", "rhand" };

    // ------------------------------------------------------------ rewards

    public static readonly IReadOnlyList<CardDef> All = new List<CardDef>
    {
        // Crowns, points and the character's own numbers (the game saves them).
        new()
        {
            Id = "purse", Title = "Heavy Purse", Icon = "s_inv_gold",
            Gains = l => new[] { $"+{Purse(l.Tier, l.Level)} crowns", "(500 for each level, more at higher tiers)" },
            Apply = (ctx, _) => Done(() => Effects.Gold(ctx.Player, Purse(ctx.Tier, ctx.Level)), Purse(ctx.Tier, ctx.Level).ToString()),
        },
        new()
        {
            Id = "ability-points", Title = "Hard-Won Skill", Power = 2,
            Gains = l => new[] { $"+{T(l.Tier, 1, 1, 2, 2, 3)} {S(T(l.Tier, 1, 1, 2, 2, 3), "ability point", "ability points")}" },
            // "SP" is what the Abilities window shows as AP.
            Apply = (ctx, _) => Done(() => Effects.AddAtr(ctx.Player, "SP", T(ctx.Tier, 1, 1, 2, 2, 3))),
        },
        new()
        {
            Id = "attribute-points", Title = "Tempered Body", Power = 2,
            Gains = l => new[] { $"+{T(l.Tier, 1, 1, 2, 2, 3)} {S(T(l.Tier, 1, 1, 2, 2, 3), "attribute point", "attribute points")}" },
            // "AP" is what the character sheet shows as SP.
            Apply = (ctx, _) => Done(() => Effects.AddAtr(ctx.Player, "AP", T(ctx.Tier, 1, 1, 2, 2, 3))),
        },
        new()
        {
            Id = "trained-body", Title = "Trained Body", Power = 2,
            Prepare = ctx => Attributes[ctx.Rng.Next(Attributes.Length)],
            Gains = l => new[] { $"+{T(l.Tier, 1, 1, 2, 2, 3)} {AttributeName(l.Detail ?? "STR")}, for good" },
            Apply = (ctx, key) => Done(() => Effects.AddAtr(ctx.Player, key ?? "STR", T(ctx.Tier, 1, 1, 2, 2, 3)), key),
        },
        Bonus("light-feet", "Light Feet", "bEVS", t => T(t, 3, 4, 5, 6, 8), "evasion"),
        Bonus("deep-reserves", "Deep Reserves", "bMp", t => T(t, 8, 12, 16, 20, 25), "maximum energy"),
        Bonus("hawk-eyes", "Hawk Eyes", "bVSN", t => T(t, 1, 1, 1, 2, 2), "vision"),
        new()
        {
            Id = "streetwise", Title = "Streetwise",
            Gains = l => new[] { $"+{T(l.Tier, 10, 10, 15, 15, 20)} savvy and +{T(l.Tier, 10, 10, 15, 15, 20)} trap avoidance, for good" },
            Apply = (ctx, _) => Done(() =>
            {
                Effects.AddAtr(ctx.Player, "bSavvy", T(ctx.Tier, 10, 10, 15, 15, 20));
                Effects.AddAtr(ctx.Player, "bAvoiding_Trap", T(ctx.Tier, 10, 10, 15, 15, 20));
            }),
        },
        new()
        {
            Id = "quick-study", Title = "Quick Study", Power = 2,
            Gains = l => new[] { $"+{T(l.Tier, 10, 15, 15, 20, 20)}% experience, for good" },
            // Received_XP is a percentage in the character data (100 by default).
            Apply = (ctx, _) => Done(() => Effects.AddAtr(ctx.Player, "Received_XP", T(ctx.Tier, 10, 15, 15, 20, 20))),
        },

        // Lasting statuses. Natives keep the game's own numbers; customs are the
        // mod's numbers on a carrier status, written again every second.
        new()
        {
            Id = "vampirism", Title = "Vampirism", Icon = "s_b_vampiric_blood", Power = 2, Unique = true, SelfCosted = true,
            Carriers = new[] { "o_b_vampiric_blood" },
            Gains = l => new[] { $"+{T(l.Tier, 10, 10, 15, 20, 20)}% lifesteal, for good" },
            Costs = l => new[] { $"{T(l.Tier, -3, -3, -5, -5, -5)} evasion" },
            Apply = (ctx, _) => Done(() => Effects.LongBuff(ctx.Player, "o_b_vampiric_blood", "vampirism", Vampire(ctx.Tier))),
            Reapply = (ctx, b) =>
            {
                Effects.KeepBuff(ctx.Player, "o_b_vampiric_blood", "vampirism", Vampire(b.Tier));
                Effects.Retune(ctx.Player, "o_b_vampiric_blood", "vampirism", Vampire(b.Tier));
            },
        },
        new()
        {
            Id = "night-eyes", Title = "Night Eyes", Power = 2, Unique = true, Scales = false,
            Gains = _ => new[] { "You see in the dark, for good" },
            Apply = (ctx, _) => Done(() => Effects.NightVision(ctx.Player)),
            Reapply = (ctx, _) => Effects.NightVision(ctx.Player),
        },
        Native("clear-vision", "Clear Vision", "o_b_clearsight", 2, "+5 crit chance, +5 accuracy, +5 range, +10 vision"),
        Native("sturdiness", "Sturdiness", "o_b_burly", 2, "+25 fortitude, +16 maximum health, +1.5 health regeneration"),
        Native("blessing", "Blessing", "o_b_bless", 1, "+9 fortitude, +9 unholy resistance, +9 crit avoidance, +5% experience"),
        Native("strength-training", "Strength Training", "o_b_strength_training", 1, "+5 armour piercing, +5 weapon damage, +10 bodypart damage, +5% experience"),
        Native("agility-training", "Agility Training", "o_b_agility_training", 1, "+5 counter chance, -5 fumble, -5 miscast, +5% experience"),
        Native("endurance-training", "Endurance Training", "o_b_endurance_training", 1, "+10 energy regeneration, -10% cooldowns, +5% experience",
            "+10 fatigue gain"),
        Native("precision-training", "Precision Training", "o_b_coordination_training", 1, "+5 miracle chance, +5 crit chance, +5 accuracy, +5% experience"),
        Custom("elusiveness", "Elusiveness", "o_b_elusive", 2,
            t => new() { ["EVS"] = T(t, 6, 8, 10, 12, 15), ["max_hp"] = -10 },
            t => $"+{T(t, 6, 8, 10, 12, 15)} evasion", _ => "-10 maximum health"),
        Custom("precision", "Exceptional Precision", "o_b_accurate", 2,
            t => new() { ["Hit_Chance"] = T(t, 5, 7, 10, 12, 15), ["CRT"] = T(t, 2, 3, 4, 5, 6), ["CRTD"] = -15 },
            t => $"+{T(t, 5, 7, 10, 12, 15)} accuracy, +{T(t, 2, 3, 4, 5, 6)} crit chance", _ => "-15 crit efficiency"),
        Custom("battle-rage", "Battle Rage", "o_b_rage", 2,
            t => new() { ["CRTD"] = 20, ["CRT"] = 5, ["Weapon_Damage"] = 10, ["Damage_Received"] = T(t, 10, 10, 10, 7, 7) },
            _ => "+20 crit efficiency, +5 crit chance, +10% weapon damage", t => $"+{T(t, 10, 10, 10, 7, 7)}% damage taken"),
        Custom("energy-drain", "Energy Drain", "o_b_manasteal", 2,
            t => new() { ["Manasteal"] = T(t, 5, 7, 10, 12, 15), ["MP_Restoration"] = -10 },
            t => $"+{T(t, 5, 7, 10, 12, 15)}% energy drain", _ => "-10 energy regeneration"),
        Custom("adrenaline", "Adrenaline", "o_b_adrenaline", 2,
            _ => new() { ["Abilities_Energy_Cost"] = -5, ["CRT"] = 3, ["MP_Restoration"] = 10, ["Pain_Resistance"] = 10, ["Damage_Received"] = -3, ["Fatigue_Gain"] = 10 },
            _ => "-5% energy costs, +3 crit chance, +10 energy regeneration, +10 pain resistance, 3% less damage taken",
            _ => "+10 fatigue gain", scales: false),
        new()
        {
            Id = "unholy-pact", Title = "Unholy Pact", Power = 2, Unique = true, SelfCosted = true, Scales = false,
            Carriers = new[] { "o_b_dark_blessing", "o_db_relic_curse" },
            Gains = _ => new[] { "Unholy Blessing for good:", "lifesteal, energy drain, less damage taken" },
            Costs = _ => new[] { "Vampiric Corruption for good" },
            Apply = (ctx, _) => Done(() =>
            {
                Effects.LongBuff(ctx.Player, "o_b_dark_blessing", "unholy-pact");
                Effects.LongBuff(ctx.Player, "o_db_relic_curse", "unholy-pact");
            }),
            Reapply = (ctx, _) =>
            {
                Effects.KeepBuff(ctx.Player, "o_b_dark_blessing", "unholy-pact");
                Effects.KeepBuff(ctx.Player, "o_db_relic_curse", "unholy-pact");
            },
        },

        // Swaps: one stat for another, on their own carrier (sharing a carrier
        // with the card of that status, so a run has one or the other).
        Custom("glass-cannon", "Glass Cannon", "o_b_rage", 2,
            t => new() { ["Weapon_Damage"] = T(t, 8, 10, 12, 14, 16), ["max_hp"] = -T(t, 10, 12, 15, 18, 20) },
            t => $"+{T(t, 8, 10, 12, 14, 16)}% weapon damage", t => $"-{T(t, 10, 12, 15, 18, 20)} maximum health"),
        Custom("bulwark", "Bulwark", "o_b_bless", 2,
            t => new() { ["max_hp"] = T(t, 10, 12, 15, 18, 20), ["Weapon_Damage"] = -T(t, 8, 10, 12, 14, 16) },
            t => $"+{T(t, 10, 12, 15, 18, 20)} maximum health", t => $"-{T(t, 8, 10, 12, 14, 16)}% weapon damage"),
        Custom("troll-blood", "Troll Blood", "o_b_burly", 2,
            t => new() { ["Health_Restoration"] = T(t, 10, 15, 20, 25, 30), ["Healing_Received"] = -15 },
            t => $"+{T(t, 10, 15, 20, 25, 30)} health regeneration", _ => "-15% healing received"),
        Custom("quick-hands", "Quick Hands", "o_b_adrenaline", 2,
            t => new() { ["Cooldown_Reduction"] = -T(t, 8, 10, 12, 14, 15), ["Abilities_Energy_Cost"] = 10 },
            t => $"-{T(t, 8, 10, 12, 14, 15)}% cooldowns", _ => "+10% energy costs"),

        new()
        {
            Id = "foreign-perk", Title = "Another Hero's Way", Power = 3, MinTier = 3,
            CanOffer = ctx => Perks.Keys.Any(p => !Effects.HasPerk(ctx.Player, p)),
            Prepare = ctx =>
            {
                var open = Perks.Keys.Where(p => !Effects.HasPerk(ctx.Player, p)).ToList();
                return open[ctx.Rng.Next(open.Count)];
            },
            Gains = l => new[] { $"The perk {(l.Detail != null && Perks.TryGetValue(l.Detail, out var n) ? n : "of another hero")}", "(another origin's own)" },
            Apply = (ctx, perk) => Done(() => Effects.GivePerk(ctx.Player, perk ?? throw new InvalidOperationException("no perk chosen")), perk),
        },

        // Things in the bag.
        new()
        {
            Id = "alchemist", Title = "Alchemist's Gift", Icon = "s_inv_bottle",
            Gains = l => new[] { $"{(l.Tier >= 4 ? "Two potions" : "A potion")} of {string.Join(", ", PotionTags(l.Tier).Select(Effect))}" },
            Apply = (ctx, _) => Done(() =>
            {
                for (int i = 0; i < (ctx.Tier >= 4 ? 2 : 1); i++) Effects.Potion(PotionTags(ctx.Tier), ctx.Log);
            }),
        },
        new()
        {
            Id = "forbidden-library", Title = "Forbidden Library", Power = 2, Cost = "forbidden-art", Weight = 8,
            Prepare = ctx =>
            {
                var locked = LockedTrees(ctx.Run).ToHashSet();
                var open = Trees.All.Where(t => !locked.Contains(t)).OrderBy(_ => ctx.Rng.Next()).ToList();
                if (open.Count < 2) throw new InvalidOperationException("too few open skill trees");
                return $"{open[0].Name}|{open[1].Name}";
            },
            Gains = l => Pair(l.Detail) is var (a, b) && a != null && b != null
                ? new[] { $"{a.Name} and {b.Name} treatises", $"(tier {Roman(T(l.Tier, 1, 1, 2, 2, 3))})" }
                : new[] { "Two treatises" },
            Apply = (ctx, detail) =>
            {
                var (a, b) = Pair(detail);
                if (a == null || b == null) throw new InvalidOperationException("the card chose no trees");
                Effects.Item(Trees.TreatiseObject(a, T(ctx.Tier, 1, 1, 2, 2, 3)));
                Effects.Item(Trees.TreatiseObject(b, T(ctx.Tier, 1, 1, 2, 2, 3)));
                return detail;
            },
        },
        new()
        {
            Id = "tome", Title = "Tome of Experience", Power = 2, Scales = false,
            Gains = _ => new[] { "Half a level of experience" },
            Apply = (ctx, _) => Done(() => Effects.Xp(ctx.Player, Math.Max(10, World.Num(ctx.Player, "max_xp", 250) * 0.5))),
        },
        new()
        {
            Id = "armoury-drop", Title = "Armoury Drop", Power = 2,
            CanOffer = ctx => Gear.OfTier(DropTier(ctx.Tier)).Count > 0,
            Prepare = ctx =>
            {
                var pieces = Gear.OfTier(DropTier(ctx.Tier));
                return pieces[ctx.Rng.Next(pieces.Count)].Name;
            },
            Gains = l => new[] { $"{DropRarityName(l.Tier)} {l.Detail ?? "gear"}", $"(tier {DropTier(l.Tier)})" },
            Apply = (ctx, name) => Done(() => Gear.Give(name ?? throw new InvalidOperationException("no piece chosen"), DropRarity(ctx.Tier)), name),
        },
        new()
        {
            Id = "cursed-heirloom", Title = "Cursed Heirloom", Power = 2, SelfCosted = true,
            CanOffer = ctx => Gear.OfTier(Math.Min(5, ctx.Tier + 1)).Count > 0,
            Prepare = ctx =>
            {
                var pieces = Gear.OfTier(Math.Min(5, ctx.Tier + 1));
                return pieces[ctx.Rng.Next(pieces.Count)].Name;
            },
            Gains = l => new[] { $"{l.Detail ?? "A piece of gear"}", $"(tier {Math.Min(5, l.Tier + 1)}, a tier above the trial's)" },
            Costs = _ => new[] { "It carries a curse" },
            // Rarity 5 is cursed: its data gets a Curse_of_* drawback.
            Apply = (_, name) => Done(() => Gear.Give(name ?? throw new InvalidOperationException("no piece chosen"), 5), name),
        },
        new()
        {
            Id = "field-kit", Title = "Field Kit", Icon = "s_inv_salve",
            Prepare = ctx => string.Join("|", Kit.OrderBy(_ => ctx.Rng.Next()).Take(KitSize(ctx.Tier))),
            Gains = l => new[] { $"{KitSize(l.Tier)} supplies:", string.Join(", ", (l.Detail ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Select(KitName)) },
            Apply = (ctx, detail) => Done(() =>
            {
                foreach (var o in (detail ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)) Effects.Item(o);
            }, detail),
        },

        // The run itself.
        new()
        {
            Id = "merchants-favour", Title = "Merchant's Favour", ForTrialsAhead = true, Scales = false,
            Gains = _ => new[] { "The tavern traders' next two stocks", "come a tier higher, with rarer goods" },
            Apply = (ctx, _) => Done(() => ctx.Run.FavourRefreshes = 2),
        },
        new()
        {
            Id = "second-look", Title = "Second Look", ForTrialsAhead = true, Scales = false,
            Gains = _ => new[] { "One more card to choose from", "after the next trial" },
            Apply = (ctx, _) => Done(() => ctx.Run.ExtraCards++),
        },
        new()
        {
            Id = "blood-money", Title = "Blood Money", Power = 2, SelfCosted = true, ForTrialsAhead = true,
            CanOffer = ctx => ctx.Run.BloodPay <= 1,
            Gains = l => new[] { $"The next trial pays x{BloodPay(l.Tier)}" },
            Costs = l => new[] { $"and is {(l.Tier >= 4 ? "a whole tier" : "half a tier")} more dangerous" },
            Apply = (ctx, _) => Done(() =>
            {
                ctx.Run.BloodShift = ctx.Tier >= 4 ? 1.0 : 0.5;
                ctx.Run.BloodPay = BloodPay(ctx.Tier);
            }),
        },
    };

    // ------------------------------------------------------------ costs

    public static readonly IReadOnlyList<CostDef> Costs = new List<CostDef>
    {
        new()
        {
            Id = "tithe", Severity = 1,
            Text = l => new[] { $"Pay {T(l.Tier, 25, 25, 35, 35, 50)}% of your crowns" },
            CanOffer = ctx => Effects.GoldCount(ctx.Player) >= 100,
            Apply = (ctx, _) =>
            {
                int take = (int)Math.Round(Effects.GoldCount(ctx.Player) * T(ctx.Tier, 25, 25, 35, 35, 50) / 100.0);
                Effects.TakeGold(ctx.Player, take);
                return take.ToString();
            },
        },
        Debuff("curse-of-decay", "Curse of Decay", "o_db_curse", 2, t => 3 + t),
        Debuff("vampiric-corruption", "Vampiric Corruption", "o_db_relic_curse", 3, _ => 0),
        Debuff("enervation", "Enervation", "o_db_fragile", 2, _ => 4),
        Debuff("weakness", "Weakness", "o_db_weak", 2, _ => 3),
        Debuff("gluttony", "Gluttony", "o_db_gluttony", 3, _ => 1),
        Debuff("heart-of-darkness", "Heart of Darkness", "o_db_heart_of_darkness", 2, _ => 3),
        Debuff("hangover", "Eternal hangover", "o_db_hangover", 1, _ => 2),
        Debuff("mark-of-feast", "Mark of the Feast", "o_db_feast", 2, _ => 3),
        Debuff("blood-hex", "Blood Hex", "o_db_fever", 1, _ => 3),
        Debuff("pestilence", "Pestilence", "o_db_pest", 2, _ => 1),
        Debuff("coughing", "Coughing", "o_db_cough", 1, _ => 1),
        new()
        {
            Id = "weary", Severity = 1, Trials = _ => 3,
            Text = _ => new[] { "+20 fatigue gain for 3 trials" },
            Apply = (ctx, _) => Done(() => Effects.AddAtr(ctx.Player, "Fatigue_Gain", 20)),
            Expire = (ctx, _) => Effects.AddAtr(ctx.Player, "Fatigue_Gain", -20),
        },
        new()
        {
            Id = "old-wound", Severity = 2, Trials = _ => 5, BuffIsDetail = true,
            Prepare = (ctx, _) => WoundParts[ctx.Rng.Next(WoundParts.Length)],
            Text = l => new[] { $"An old {WoundName(l.Detail)} wound for 5 trials" },
            // The wound object is chosen when dealt (its part) and by tier (its level); kept on the boon.
            Apply = (ctx, part) =>
            {
                string wound = $"o_db_inj_{part ?? "tors"}{(ctx.Tier >= 3 ? 2 : 1)}";
                Effects.LongBuff(ctx.Player, wound, "cost:old-wound");
                return wound;
            },
            Expire = (ctx, b) => Effects.EndBuff(ctx.Player, b.CostDetail ?? "o_db_inj_tors1", "cost:old-wound"),
        },
        new()
        {
            Id = "shattered-gear", Severity = 2,
            CanOffer = ctx => Effects.WornItems().Any(IsArmour),
            Prepare = (ctx, _) =>
            {
                var armour = Effects.WornItems().Where(IsArmour).ToList();
                return armour[ctx.Rng.Next(armour.Count)].Get("idName").ToString();
            },
            Text = l => new[] { $"Your {l.Detail ?? "worn item"} is lost" },
            Apply = (ctx, item) =>
            {
                // The piece named on the card, or another if it is no longer worn.
                var worn = Effects.WornItems().Where(IsArmour).ToList();
                int i = worn.FindIndex(w => w.Get("idName").ToString() == item);
                if (i < 0 && worn.Count > 0) i = ctx.Rng.Next(worn.Count);
                if (i < 0) return "nothing";
                string lost = worn[i].Get("idName").ToString();
                if (worn[i].Resolve() is { } inst) Scripts.scr_item_destroy.CallAs(inst, inst);
                return lost;
            },
        },
        new()
        {
            Id = "forbidden-art", Severity = 3,
            // Never a tree the card itself teaches (Forbidden Library's "Tree|Tree").
            Prepare = (ctx, given) =>
            {
                var locked = LockedTrees(ctx.Run).ToHashSet();
                var taught = (given ?? "").Split('|');
                var open = Trees.All.Where(t => !locked.Contains(t) && !taught.Contains(t.Name)).ToList();
                if (open.Count == 0) throw new InvalidOperationException("every tree is closed");
                return open[ctx.Rng.Next(open.Count)].Name;
            },
            Text = l => new[] { $"{l.Detail ?? "One skill tree"}: no skill or treatise of it can be learned this run" },
            Apply = (_, tree) => tree,
        },
        new()
        {
            Id = "attribute-loss", Severity = 2,
            // Never the attribute the card raises (Trained Body's key).
            Prepare = (ctx, raised) =>
            {
                var others = Attributes.Where(a => a != raised).ToArray();
                return others[ctx.Rng.Next(others.Length)];
            },
            Text = l => new[] { $"-1 {AttributeName(l.Detail ?? "AGL")}, for good" },
            Apply = (ctx, key) => Done(() => Effects.AddAtr(ctx.Player, key ?? "AGL", -1), key),
        },
    };

    public static CardDef? Find(string id) => All.FirstOrDefault(c => c.Id == id);
    public static CostDef? FindCost(string? id) => id == null ? null : Costs.FirstOrDefault(c => c.Id == id);

    /// <summary>The trees closed by the Forbidden Art costs paid.</summary>
    public static IEnumerable<Trees.Tree> LockedTrees(Run run) =>
        run.Boons.Where(b => b.Cost == "forbidden-art").Select(b => Trees.Find(b.CostDetail ?? "")).OfType<Trees.Tree>();

    // ------------------------------------------------------------ makers

    private static string? Done(Action act, string? keep = null)
    {
        act();
        return keep;
    }

    private static CardDef Bonus(string id, string title, string key, Func<int, int> amount, string what) => new()
    {
        Id = id, Title = title,
        Gains = l => new[] { $"+{amount(l.Tier)} {what}, for good" },
        Apply = (ctx, _) => Done(() => Effects.AddAtr(ctx.Player, key, amount(ctx.Tier))),
    };

    // A status with the game's own numbers, for good.
    private static CardDef Native(string id, string title, string buff, int power, string gains, string? cost = null) => new()
    {
        Id = id, Title = title, Power = power, Unique = true, SelfCosted = cost != null, Scales = false,
        Carriers = new[] { buff },
        Gains = _ => new[] { $"{title} for good:", gains },
        Costs = _ => cost != null ? new[] { cost } : Array.Empty<string>(),
        Apply = (ctx, _) => Done(() => Effects.LongBuff(ctx.Player, buff, id)),
        // For good: put back if lost, and its turns kept lasting (a load may reset them).
        Reapply = (ctx, _) => Effects.KeepBuff(ctx.Player, buff, id),
    };

    // A status carrying only the mod's numbers, its cost among them.
    private static CardDef Custom(string id, string title, string carrier, int power, Func<int, Dictionary<string, double>> numbers,
                                  Func<int, string> gains, Func<int, string> cost, bool scales = true) => new()
    {
        Id = id, Title = title, Power = power, Unique = true, SelfCosted = true, Scales = scales,
        Carriers = new[] { carrier },
        Gains = l => new[] { $"{gains(l.Tier)}, for good" },
        Costs = l => new[] { cost(l.Tier) },
        Apply = (ctx, _) => Done(() => Effects.LongBuff(ctx.Player, carrier, id, numbers(ctx.Tier), exact: true)),
        Reapply = (ctx, b) =>
        {
            Effects.KeepBuff(ctx.Player, carrier, id, numbers(b.Tier), exact: true);
            Effects.Retune(ctx.Player, carrier, id, numbers(b.Tier), exact: true);
        },
    };

    private static CostDef Debuff(string id, string name, string buff, int severity, Func<int, int> trials) => new()
    {
        Id = id, Severity = severity, Buff = buff, Trials = trials,
        Text = l => new[] { trials(l.Tier) is var n && n > 0 ? $"{name} for {n} {S(n, "trial", "trials")}" : $"{name} for good" },
    };

    // ------------------------------------------------------------ numbers and names

    private static int Purse(int tier, int level) => (int)Math.Round(500 * level * (0.8 + 0.2 * tier));
    private static double BloodPay(int tier) => tier >= 4 ? 2.5 : 2;
    // The trial's tier (one above from tier 4), Uncommon / Uncommon / Rare / Rare / Epic.
    private static int DropTier(int tier) => Math.Min(5, tier >= 4 ? tier + 1 : tier);
    private static int DropRarity(int tier) => T(tier, 2, 2, 3, 3, 4);
    private static string DropRarityName(int tier) => T(tier, "Enchanted", "Enchanted", "Rare", "Rare", "Legendary");
    private static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", _ => "IV" };

    private static Dictionary<string, double> Vampire(int tier) => new()
    {
        ["Lifesteal"] = T(tier, 10, 10, 15, 20, 20),
        ["EVS"] = T(tier, -3, -3, -5, -5, -5),
    };

    private static string[] PotionTags(int tier) => tier switch
    {
        <= 2 => new[] { "good_pt_healing", "good_pt_regeneration" },
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



    private static int KitSize(int tier) => T(tier, 3, 4, 5, 6, 6);

    private static string KitName(string obj) => obj switch
    {
        "o_inv_salve" => "a healing salve", "o_inv_splint" => "a splint", "o_inv_bandage" => "a bandage",
        "o_inv_antitoxin" => "an antitoxin", "o_inv_antivenom" => "an antidote syringe", "o_inv_leech" => "leeches",
        "o_inv_herbal" => "a herbal extract", "o_inv_inhaler" => "an ether inhaler", "o_inv_scroll_identification" => "an identification scroll",
        "o_inv_scroll_disenchant" => "a disenchantment scroll", "o_inv_rope" => "a rope", "o_inv_lockpicks" => "lockpicks", "o_inv_torch" => "a torch",
        _ => obj,
    };

    private static string WoundName(string? part) => part switch { "legs" => "leg", "head" => "head", "rhand" => "hand", _ => "chest" };

    // Armour and jewellery, never what is in the hands.
    private static bool IsArmour(InstanceRef item) => item.Get("slot").ToString() != "hand";

    // "Tree|Tree", as Prepare chose them.
    private static (Trees.Tree? A, Trees.Tree? B) Pair(string? detail)
    {
        var parts = detail?.Split('|') ?? Array.Empty<string>();
        return parts.Length == 2 ? (Trees.Find(parts[0]), Trees.Find(parts[1])) : (null, null);
    }
}
