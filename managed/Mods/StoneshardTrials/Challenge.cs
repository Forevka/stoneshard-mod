using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// What a trial adds to its dungeon on arrival when the character calls for
/// more than the dungeon's tier gives: extra enemies, and an elite master.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25): scr_enemy_create(x, y, object)
/// run as the player in a dungeon makes a real enemy, which notices the player,
/// turns hostile and fights like the dungeon's own. Walkable cells are those
/// astar_get_cell(o_controller.cleangrid, x, y) - the room's A* grid, a struct -
/// answers 0 for (StoneshardHarness).
/// </remarks>
internal static class Challenge
{
    private const int Cell = 26;
    private const double EliteHealth = 1.5;

    public readonly record struct Plan(int Extras, bool Elite)
    {
        public static readonly Plan None = new(0, false);
        public bool Any => Extras > 0 || Elite;
    }

    /// <summary>
    /// How much the trial adds: nothing while the wanted danger is within the
    /// slack the tier's rounding allows (see <see cref="Progression.Slack"/>), so
    /// a fresh character's first trial adds nothing; past it, one extra enemy
    /// and one more for every 0.3 further (up to 4), and an elite master once it
    /// runs a whole tier past. The difficulty adds its own extras (Brutal also
    /// the elite).
    /// </summary>
    public static Plan For(double target, int dungeonTier, int difficultyExtras)
    {
        double over = Math.Round(target - dungeonTier, 6);
        double past = Math.Round(over - Progression.Slack, 6);
        int extras = past <= 0 ? 0 : Math.Min(4, 1 + (int)Math.Floor(past / 0.3));
        return new Plan(Math.Clamp(extras + difficultyExtras, 0, 6), over >= 1.0 || difficultyExtras >= 2);
    }

    /// <summary>Spawns the plan's extra enemies in the dungeon the player has just arrived in; answers how many.</summary>
    public static int SpawnExtras(InstanceRef player, Plan plan, Random rng, Logger log)
    {
        if (plan.Extras <= 0) return 0;
        int made = Spawn(player, Enemies().Where(e => !e.Master).ToList(), plan.Extras, rng);
        log.Info($"spawned {made} extra enemy(ies) of {plan.Extras} planned");
        return made;
    }

    /// <summary>
    /// Elite prefixes, named from the game's own (unused) boss prefix table
    /// (table_bosses_types), each a set of lasting stat changes on top of the
    /// elite's half again of health.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Dictionary<string, double>> Affixes = new Dictionary<string, Dictionary<string, double>>
    {
        ["Persistent"] = new() { ["max_hp"] = 0.5 },        // a share of the plain maximum (see Stats)
        ["Powerful"] = new() { ["Weapon_Damage"] = 25 },
        ["Resistant"] = new() { ["Damage_Received"] = -15, ["PRR"] = 10 },
        ["Nimble"] = new() { ["EVS"] = 15 },
        ["Leeching"] = new() { ["Lifesteal"] = 20 },
        ["Watchful"] = new() { ["Hit_Chance"] = 20, ["CRT"] = 10 },
    };

    /// <summary>
    /// Finds the trial's master once it is there to be found (on the floor the
    /// player arrives on, or on a natural two-floor dungeon's floor below) and
    /// makes it the elite: its prefix before its name, and lasting buffs for
    /// half again of its health and the prefix's stats. The buffs are saved
    /// with it, so after a load only the name (which the game rebuilds from the
    /// dungeon) is written again. Answers it, its own name, the prefix and
    /// whether it was made just now; null while no master is loaded.
    /// </summary>
    public static (InstanceRef Master, string Name, string Affix, bool Made)? TryElite(string affix, Logger log)
    {
        // The master itself first; a miniboss only where no master is to be found.
        var candidates = Enemies().Where(e => e.Master).OrderByDescending(e => World.Truthy(e.Ref.Get("isBoss"))).ToList();
        foreach (var e in candidates)
        {
            var master = e.Ref;
            // A master that has not finished its creation has no name or health yet.
            if (master.Get("name") is not { Kind: RValueKind.String } nameValue || World.Num(master, "max_hp") <= 0) continue;
            string name = nameValue.ToString();
            string prefix = affix + " ";
            // A save from before the prefixes named it "Elite X".
            if (name.StartsWith("Elite ", StringComparison.Ordinal)) { name = name[6..]; master.Set("name", name); }
            if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
            else master.Set("name", prefix + name);
            bool made = false;
            if (!EnemyBuffs.HasOurs(master))
            {
                double max = World.Num(master, "max_hp");
                bool full = World.Num(master, "HP") >= max;
                EnemyBuffs.Add(master, Stats(affix, max));
                // Made at full health: it starts at its new maximum.
                if (full) master.Set("HP", World.Num(master, "max_hp"));
                made = true;
                log.Info($"elite master: {affix} {name}, {World.Num(master, "HP")}/{World.Num(master, "max_hp")} HP");
            }
            return (master, name, affix, made);
        }
        return null;
    }

    /// <summary>
    /// Endless runs past tier 5: the dungeons go no higher, so every enemy of
    /// the trial gets lasting buffs that grow with the tier-5 trials already
    /// won (<paramref name="steps"/>, capped at 10): +10% health, +5% weapon
    /// damage and +2 accuracy per step. Enemies already buffed by the mod (the
    /// elite) are left alone, and so are masters while <paramref name="spareMasters"/>
    /// (one still to be made the elite) and enemies not yet fully made.
    /// Answers how many were scaled.
    /// </summary>
    public static int ScaleForEndless(int steps, bool spareMasters, Logger log)
    {
        steps = Math.Clamp(steps, 0, 10);
        if (steps == 0) return 0;
        int scaled = 0;
        foreach (var e in Enemies())
        {
            try
            {
                if ((spareMasters && e.Master) || EnemyBuffs.HasOurs(e.Ref)) continue;
                double max = World.Num(e.Ref, "max_hp");
                if (max <= 0) continue;
                bool full = World.Num(e.Ref, "HP") >= max;
                EnemyBuffs.Add(e.Ref, new Dictionary<string, double>
                {
                    ["max_hp"] = Math.Round(max * 0.1 * steps),
                    ["Weapon_Damage"] = 5 * steps,
                    ["Hit_Chance"] = 2 * steps,
                });
                if (full) e.Ref.Set("HP", World.Num(e.Ref, "max_hp"));
                scaled++;
            }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException) { log.Warning($"scaling an enemy: {ex.Message}"); }
        }
        log.Info($"endless past tier 5: {scaled} enemy(ies) scaled by {steps} step(s)");
        return scaled;
    }

    // Half again of health for every elite, and the prefix's own (Persistent
    // adds another share of the plain maximum).
    private static Dictionary<string, double> Stats(string affix, double plainMax)
    {
        var stats = new Dictionary<string, double> { ["max_hp"] = Math.Round(plainMax * (EliteHealth - 1)) };
        if (Affixes.TryGetValue(affix, out var extra))
            foreach (var (k, v) in extra)
                stats[k] = k == "max_hp" ? stats.GetValueOrDefault("max_hp") + Math.Round(plainMax * v) : v;
        return stats;
    }
    private readonly record struct Enemy(InstanceRef Ref, int Object, int Tier, int Gx, int Gy, bool Master);

    private static List<Enemy> Enemies()
    {
        var list = new List<Enemy>();
        if (Objects.o_enemy.Object is not { } obj) return list;
        foreach (var e in obj.Instances())
        {
            try
            {
                if (World.Num(e, "HP") <= 0) continue;
                bool master = World.Truthy(e.Get("isBoss")) || World.Truthy(e.Get("isMiniboss"));
                list.Add(new Enemy(e, (int)e.Get("object_index").AsReal, (int)World.Num(e, "Tier", 1),
                    (int)Math.Floor(World.Num(e, "x") / Cell), (int)Math.Floor(World.Num(e, "y") / Cell), master));
            }
            catch (GmlException) { }
        }
        return list;
    }

    // Copies of the dungeon's own toughest kind of enemy, each beside one of
    // them (so they stand where the dungeon keeps its guards, not at the door).
    private static int Spawn(InstanceRef player, List<Enemy> natives, int count, Random rng)
    {
        if (natives.Count == 0 || Objects.o_controller.First is not { } controller) return 0;
        var grid = controller.Get("cleangrid");
        var positions = controller.Get("posgrid");
        if (grid.Kind != RValueKind.Object || !positions.IsNumber) return 0;
        int top = natives.Max(n => n.Tier);
        var kinds = natives.Where(n => n.Tier == top).ToList();
        int px = (int)Math.Floor(World.Num(player, "x") / Cell), py = (int)Math.Floor(World.Num(player, "y") / Cell);
        int made = 0;
        // Cells taken this call, in case the room's grid has not caught up with a spawn yet.
        var used = new HashSet<(int, int)>();
        for (int i = 0; i < count * 4 && made < count; i++)
        {
            var near = kinds[rng.Next(kinds.Count)];
            int x = near.Gx + rng.Next(-2, 3), y = near.Gy + rng.Next(-2, 3);
            // Not on top of the player as they arrive.
            if (Math.Max(Math.Abs(x - px), Math.Abs(y - py)) < 6 || used.Contains((x, y))) continue;
            try
            {
                if (Scripts.astar_get_cell.CallAs(controller, grid, x, y) is not { IsNumber: true } cell || cell.AsReal < 0) continue;
                if (World.IdKey(Builtins.ds_grid_get(positions, x, y)) >= 0) continue;
                Scripts.scr_enemy_create.CallAs(player, x * Cell + Cell / 2, y * Cell + Cell / 2, near.Object);
                used.Add((x, y));
                made++;
            }
            catch (GmlException) { }
        }
        return made;
    }
}
