using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// Endless runs: when no untouched dungeon fits, one is made to fit. Its saved
/// rooms are deleted (the way the game's own reset does) and its data rewritten
/// to the wanted tier with a master of that tier, so it is generated afresh.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25):
///   * a dungeon's visited floors are kept in global.locationsRoomsDataMap under
///     its cell ("29_9") as rooms "r_dungeon_1", "r_dungeon_2"...;
///     scr_locationRoomDelete(location, room) forgets one, and the next visit
///     generates the floor anew (all its enemies and its master back);
///   * scr_globaltile_dungeon_set(key, value, x, y) rewrites a dungeon's data
///     before it is entered: dungeon_tier sets the enemies' tiers, Boss_Type the
///     master (an object name), mob_lvl_min/max the level the game recommends;
///   * dungeon_amountFloors must not claim floors the tier has no layouts for:
///     floor-2 layouts exist only for tiers 3 and up, and a tier 2 dungeon that
///     still says 2 floors stops the game with an error (array_shuffle on a
///     missing room list, o_dungeon_controller). Rewritten dungeons say 1.
///     There are no tier 5 crypt layouts either (no
///     scr_dungeonRoomsArrayCryptTier5*), and some kinds fail to generate
///     at some tiers as one floor: see Unsafe.
/// </remarks>
internal static class Endless
{
    /// <summary>The masters each kind of dungeon has at each tier (the game's mob table).</summary>
    private static readonly Dictionary<string, string[][]> Masters = new()
    {
        ["Crypt"] = new[]
        {
            new[] { "o_occultist" },
            new[] { "o_archivist", "o_necromancer" },
            new[] { "o_undertaker", "o_ritualist", "o_armored_husk" },
            new[] { "o_mortician", "o_crypt_warden", "o_restless_hero" },
            new[] { "o_wraithbinder", "o_revenant", "o_spectral_herald" },
        },
        ["Catacombs"] = new[]
        {
            new[] { "o_proselyte_apostate", "o_proselyte_abomination" },
            new[] { "o_proselyte_matriarch", "o_proselyte_seer" },
            new[] { "o_proselyte_brander", "o_proselyte_admonisher", "o_proselyte_begotten" },
            new[] { "o_proselyte_anmarrak", "o_proselyte_juggernaut", "o_proselyte_wormbearer" },
            new[] { "o_proselyte_nakkatar", "o_proselyte_apostle", "o_proselyte_leechlord" },
        },
        ["Bastion"] = new[]
        {
            new[] { "o_bandit_kingpin", "o_bandit_madman" },
            new[] { "o_bandit_firestarter", "o_bandit_rabblerouser" },
            new[] { "o_bandit_warlock", "o_bandit_bonebreaker_axe", "o_bandit_bonebreaker_mace" },
            new[] { "o_bandit_huntmaster", "o_bandit_paymaster", "o_bandit_condottiere" },
            new[] { "o_bandit_arcanist", "o_bandit_raubritter_2hsword", "o_bandit_witchhunter" },
        },
    };

    // The character levels the game itself recommends per tier (mob_lvl_min/max on a fresh world).
    private static readonly (int Min, int Max)[] Levels = { (1, 5), (5, 10), (10, 15), (15, 20), (10, 20) };

    /// <summary>
    /// The dungeon an endless trial takes: an untouched one at the wanted tier
    /// if there is one (another kind than last time when it can), else any
    /// other dungeon - a won one first - to be rewritten to that tier.
    /// </summary>
    public static (World.Dungeon Dungeon, bool Rewrite, int Tier)? Pick(IReadOnlyList<World.Dungeon> all, int tier,
                                                               string? lastKind, (int X, int Y)? lastCell, Random rng)
    {
        if (all.Count == 0) return null;
        var exact = all.Where(d => d.BossAlive && d.Tier == tier).ToList();
        if (exact.Count > 0) return (Prefer(exact, lastKind, lastCell, rng), false, tier);
        // The wanted tier first, then the nearest ones (lower first): a world
        // whose only single-floor cells are crypts cannot give tier 5, and the
        // run must not stall at the door for it.
        foreach (int t in Enumerable.Range(1, 5).OrderBy(t => Math.Abs(t - tier)).ThenBy(t => t))
        {
            var pool = all.Where(d => (d.X, d.Y) != lastCell && Rewritable(d, t)).ToList();
            if (pool.Count == 0) pool = all.Where(d => Rewritable(d, t)).ToList();
            if (pool.Count == 0) continue;
            var won = pool.Where(d => !d.BossAlive).ToList();
            return (Prefer(won.Count > 0 ? won : pool, lastKind, lastCell, rng), true, t);
        }
        // Nothing can be remade: an untouched dungeon of any tier, as a finite run would.
        return Progression.Pick(all.Where(d => d.BossAlive).ToList(), tier, lastKind, rng) is { } any ? (any, false, any.Tier) : null;
    }

    /// <summary>
    /// The kinds and tiers a one-floor dungeon cannot be remade at, tried on
    /// the running game (0.9.4.25, tr.next x y tier, two cells each). The
    /// failing ones stop the game during generation with the same error (no
    /// room of some role: array_shuffle from scr_dungeonGetLesslesRoomStructByRole
    /// in scr_dungeonWeldingTwoRooms): crypt 3 (with an Armored Husk, both
    /// cells) and bastion 3, 4 and 5 (several masters). Crypt 4 and catacombs
    /// 3, 4 and 5 generated fine; there are no tier 5 crypt layouts at all.
    /// </summary>
    private static readonly HashSet<(string Kind, int Tier)> Unsafe = new()
    {
        ("Crypt", 3), ("Crypt", 5),
        ("Bastion", 3), ("Bastion", 4), ("Bastion", 5),
    };

    /// <summary>Whether a one-floor dungeon of that kind can be generated at that tier.</summary>
    public static bool CanBe(string kind, int tier) => !Unsafe.Contains((kind, tier));

    public static bool Rewritable(World.Dungeon d, int tier) => d.Floors == 1 && CanBe(d.Kind, tier);
    private static World.Dungeon Prefer(List<World.Dungeon> from, string? lastKind, (int X, int Y)? lastCell, Random rng)
    {
        var other = from.Where(d => d.Kind != lastKind && (d.X, d.Y) != lastCell).ToList();
        var list = other.Count > 0 ? other : from;
        return list[rng.Next(list.Count)];
    }

    /// <summary>Forgets the dungeon's floors and rewrites it to the tier, with a master of that tier.</summary>
    public static World.Dungeon Rewrite(InstanceRef player, World.Dungeon d, int tier, Random rng, Logger log)
    {
        string location = $"{d.X}_{d.Y}";
        var rooms = new DsMap(Globals.Get("locationsRoomsDataMap"));
        if (rooms.Exists && rooms.Has(location) && new DsMap(rooms.Get(location)) is { Exists: true } saved)
        {
            foreach (var (room, _) in saved.Entries().ToList())
                if (room.StartsWith("r_dungeon", StringComparison.Ordinal))
                    Scripts.scr_locationRoomDelete.CallAs(player, location, room);
        }
        string? master = MasterFor(d.Kind, tier, rng);
        if (master == null)
        {
            master = Scripts.scr_globaltile_dungeon_get.CallAs(player, "Boss_Type", d.X, d.Y).ToString();
            log.Warning($"no tier {tier} master known for a {d.Kind}; keeping {master}");
        }
        var (min, max) = Levels[tier - 1];
        Set(player, d, "dungeon_tier", tier);
        Set(player, d, "dungeon_amountFloors", 1);
        Set(player, d, "mob_lvl_min", min);
        Set(player, d, "mob_lvl_max", max);
        Set(player, d, "Boss_Type", master);
        Set(player, d, "boss_alive", true);
        ForgetBossName(player, d, log);
        log.Info($"rewrote {d.Name} ({d.Kind} at {d.X},{d.Y}) from tier {d.Tier} to {tier}, master {master}");
        return d with { Tier = tier, BossAlive = true };
    }

    // The master's name comes from the dungeon's Boss_Name_Compound (its seeds,
    // faction and title pool), built for the old Boss_Type: an Undertaker kept
    // a proselyte's "Theognostic Harold". Cleared, the game builds a fitting one
    // for the new master when it is made (and on loads).
    private static void ForgetBossName(InstanceRef player, World.Dungeon d, Logger log)
    {
        try
        {
            var tile = new DsMap(Scripts.scr_globaltile_get_tile.CallAs(player, d.X, d.Y));
            var dungeon = tile.Exists ? new DsMap(tile.Get("dungeon")) : default;
            var compound = dungeon.Exists ? new DsMap(dungeon.Get("Boss_Name_Compound")) : default;
            if (compound.Exists) compound.Clear();
        }
        catch (GmlException ex) { log.Warning($"{d.Name}: the old master's name stays ({ex.Message})"); }
    }

    private static string? MasterFor(string kind, int tier, Random rng)
    {
        if (!Masters.TryGetValue(kind, out var tiers)) return null;
        // Only masters this build of the game has.
        var have = tiers[Math.Clamp(tier, 1, 5) - 1].Where(n => GmlObject.Find(n) is not null).ToArray();
        return have.Length > 0 ? have[rng.Next(have.Length)] : null;
    }

    private static void Set(InstanceRef player, World.Dungeon d, string key, RValue value) =>
        Scripts.scr_globaltile_dungeon_set.CallAs(player, key, value, d.X, d.Y);
}
