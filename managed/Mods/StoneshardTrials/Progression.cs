using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// How hard the next trial should be, from the character's level, what it
/// wears and how many trials lie behind it. See .omc/research/trials-progression.md
/// for where the numbers come from.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25):
///   * a dungeon's tier (1-5) sets its enemies' tiers (1-1, 1-2, 2-3, 3-4, 4-5)
///     and the character level the game itself recommends for it (1-5, 5-10,
///     10-15, 15-20); enemies have no level of their own;
///   * a worn item (o_inv_slot, equipped, in the player's bag) has a Tier of
///     1-5 and a quality: 1 common, 2 enchanted, 3 rare, 4 legendary,
///     5 cursed, 6 unique, 7 treasure;
///   * the character's level is o_player's LVL.
/// </remarks>
internal static class Progression
{
    public const int CoreSlots = 5;
    /// <summary>How far the wanted danger may run past a tier before it rounds up to the next.</summary>
    public const double Slack = 0.4;
    /// <summary>How far past the character's level tier a trial may go, difficulty and Blood Money included.</summary>
    public const double MaxAboveLevel = 1.5;

    /// <summary>The assessment; <c>Room</c> is how much more danger the cap above the level still lets a shift add.</summary>
    public readonly record struct Assessment(int Level, double LevelTier, double Gear, double Power, double Target, int Tier, double Room)
    {
        public override string ToString() =>
            $"level {Level} (tier {LevelTier:0.##}), gear {Gear:0.##}, power {Power:0.##} -> {Target:0.##} -> tier {Tier}";
    }

    /// <summary>The tier the trial numbered <paramref name="trial"/> should be, for this character.</summary>
    public static Assessment Assess(InstanceRef player, int trial, double shift = 0)
    {
        int level = Math.Max(1, (int)World.Num(player, "LVL", 1));
        // Continuous, on the game's own bands: level 1 is tier 1, 6 is 2, 11 is 3, 16 is 4.
        double levelTier = Math.Clamp(1 + (level - 1) / 5.0, 1, 5);
        double gear = GearScore();
        double power = 0.5 * levelTier + 0.5 * gear;
        // Each trial behind adds a quarter tier of pressure, up to one; never
        // more than a tier above the character, never below a floor that
        // creeps up with the trials so a run cannot stall at tier 1.
        double target = power + Math.Min(1.0, 0.25 * (trial - 1));
        double floor = 1 + (trial - 1) / 6.0;
        target = Math.Clamp(target, Math.Min(floor, power + 1), power + 1);
        // The difficulty setting and Blood Money move it (Easy -0.5 ... Brutal +1),
        // within the five tiers, but what they add stops at the cap above the
        // character's level: Brutal with Blood Money put a level 3 character into
        // a tier 4 crypt with an elite (play-test 2). The trial's own pressure is
        // not capped (a well-geared character on Normal keeps it).
        double limit = Math.Max(target, levelTier + MaxAboveLevel);
        target = Math.Clamp(Math.Min(target + shift, limit), 0.6, 5.4);
        double room = Math.Max(0, Math.Min(5.4, limit) - target);
        // Rounded up (the player's choice), but only once the fraction passes
        // 0.4: a fresh character whose starting kit holds a tier 2 unique
        // (power 1.35) still starts at tier 1, while level 3 with half its
        // gear at tier 2, three trials in (1.8), goes to tier 2.
        // Rounded to 6 places first: 2.4 summed from doubles may be 2.4000000000000004.
        int tier = Math.Clamp((int)Math.Ceiling(Math.Round(target - Slack, 6)), 1, 5);
        return new Assessment(level, levelTier, gear, power, target, tier, room);
    }

    /// <summary>
    /// The mean score of the character's five best worn items, an empty slot
    /// counting 0: an item scores its tier plus a little for its rarity.
    /// </summary>
    public static double GearScore()
    {
        var scores = new List<double>();
        if (Objects.o_inv_slot.Object is { } slots)
        {
            foreach (var item in slots.Instances())
            {
                try
                {
                    if (!World.Truthy(item.Get("equipped")) || !InBag(item)) continue;
                    double tier = World.Num(item, "Tier");
                    if (tier <= 0) continue;
                    scores.Add(tier + RarityBonus(Quality(item)));
                }
                catch (Exception ex) when (ex is GmlException or InvalidCastException) { }
            }
        }
        return scores.OrderByDescending(s => s).Take(CoreSlots).Sum() / CoreSlots;
    }

    // An item made in this session carries its quality on the instance; one
    // loaded from a save has it only in its data map, so that is read first.
    private static int Quality(InstanceRef item)
    {
        var data = new DsMap(item.Get("data"));
        if (data.Exists && data.Get("quality") is { IsNumber: true } q) return (int)q.AsReal;
        return (int)World.Num(item, "quality", 1);
    }

    private static double RarityBonus(int quality) => quality switch
    {
        2 => 0.25,          // enchanted
        3 => 0.5,           // rare
        4 => 0.75,          // legendary
        6 or 7 => 0.5,      // unique, treasure: often a character's starting kit
        5 => 0.25,          // cursed: strong, but it bites back
        _ => 0,
    };

    // The player's own bag (worn gear included) owns the item; a chest or a
    // trader's stock would be some other container (Reliquary's test).
    private static bool InBag(InstanceRef item)
    {
        var owner = item.Get("owner");
        return World.IdKey(owner) >= 0 && Builtins.instance_exists(owner).AsBool &&
               Builtins.object_get_name(new InstanceRef(owner).Get("object_index")).ToString() == Objects.o_inventory.Name;
    }

    /// <summary>
    /// The untouched dungeon nearest the wanted tier (the lower one on a tie,
    /// so a gap in the world never pushes a trial up), avoiding the kind of
    /// the last trial when another kind is as close.
    /// </summary>
    public static World.Dungeon? Pick(IReadOnlyList<World.Dungeon> open, int tier, string? lastKind, Random rng)
    {
        if (open.Count == 0) return null;
        int best = open.Min(d => Math.Abs(d.Tier - tier));
        var nearest = open.Where(d => Math.Abs(d.Tier - tier) == best).ToList();
        int lowest = nearest.Min(d => d.Tier);
        nearest = nearest.Where(d => d.Tier == lowest).ToList();
        var fresh = nearest.Where(d => d.Kind != lastKind).ToList();
        if (fresh.Count > 0) nearest = fresh;
        return nearest[rng.Next(nearest.Count)];
    }
}
