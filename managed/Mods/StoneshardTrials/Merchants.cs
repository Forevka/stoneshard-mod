using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// Traders in the hub tavern from the first won trial on: real NPCs with the
/// game's own trade window, sold whatever the trials call for, with no lock
/// but crowns. Their stock is made anew every two won trials (after trials
/// 1, 3, 5...).
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25; see .omc/research/trials-merchants.md):
///   * an o_npc_* with a stock event (User Event 9) made with
///     instance_create_depth in the tavern talks and trades, and is saved with
///     the room. Its stock is the entry named by its `id_name` in the npc_data
///     ds_map of its home settlement's world tile (Osbrook: 32,10), reached as
///     the NPC through scr_npc_get_global_data; a new NPC has no entry until
///     one is added there;
///   * the entry's trade_list (a ds_list of items) is generated when the trade
///     window opens with is_restock 1, from the NPC's Equipment_Tier_Min/Max,
///     rarity chances, Stock_Size and its selling_loot_category, which its own
///     User Event 9 fills; generation appends, so the list is cleared first.
///     It also makes the trader's purse (without one, buying was free);
///   * a town NPC keeps its day schedule and walks off: it is put back on its
///     spot, idle, every time the player is in the tavern;
///   * scr_npc_restock restocks traders on the game's own timers; ours are
///     skipped there, so only the trials refresh them.
/// </remarks>
internal sealed class Merchants
{
    public const string Prefix = "lodestone_";
    // In a trader's stock entry: which stock (the trials won when it was made) it holds.
    private const string StockKey = "lodestone_stock";
    private static readonly (int X, int Y) Osbrook = World.HubCell, Mannshire = (26, 23);

    // Home: the settlement tile whose npc_data holds this kind's stock.
    // Sells: category/count pairs that replace its own selling_loot_category
    // (null keeps the kind's own list); counts grow by one per two tiers.
    private sealed record Trader(string Key, string Object, (int X, int Y) Home, double X, double Y, int TierBonus, double Uncommon, double Rare,
                                 double StockSize, (string Category, int Count)[]? Sells = null);

    // The categories the game's traders use (read off their lists): Osbrook's
    // and Mannshire's merchants sell medicine, potion, scroll, treatise,
    // jewelry, tool, valuable, alcohol, weapon and armor.
    private static readonly (string, int)[] Goods =
    {
        ("potion", 4), ("medicine", 4), ("scroll", 3), ("treatise", 3), ("jewelry", 3), ("tool", 2), ("valuable", 1),
    };

    // Beside the innkeeper's counter, where the research's clones stood.
    // Osbrook's own merchant (Bert) opens with his caravan-quest introduction,
    // so the goods come from Mannshire's.
    private static readonly Trader[] Traders =
    {
        new(Prefix + "arms", Objects.o_npc_smith_osbrook.Name, Osbrook, 585, 377, 1, 35, 15, 1.5),
        new(Prefix + "goods", Objects.o_npc_merchant_mannshire.Name, Mannshire, 533, 377, 0, 25, 10, 1.5, Goods),
    };

    private readonly Logger _log;

    // The stock the traders are set for (their tier vars reset with a load,
    // so they are written again on every tavern tick) and this stock's salt.
    private int _stockTier = 1, _salt;
    private readonly HashSet<string> _unfound = new();

    public Merchants(Logger log) => _log = log;

    /// <summary>
    /// A trader rolls its stock when its trade window opens with a restock
    /// due, and runs its own User Event 9 then, which writes its kind's
    /// selling_loot_category with fresh counts. So a trader that sells its
    /// own range gets that range back right after the event, before the roll.
    /// </summary>
    public void InstallHooks()
    {
        foreach (var t in Traders)
        {
            if (GmlObject.Find(t.Object) is not { } obj || StockEventSymbol(obj.Index) is not { } symbol) continue;
            Hooks.After(symbol, c =>
            {
                try
                {
                    if (c.Self.IsNull || c.Self.Get("id_name").ToString() != t.Key) return;
                    Configure(new InstanceRef(c.Self.Get("id")), t);
                }
                catch (Exception ex) when (ex is GmlException or InvalidOperationException)
                {
                    _log.Warning($"trader {t.Key}: {ex.Message}");
                }
            });
        }
    }

    public static bool IsOurs(Instance npc) =>
        !npc.IsNull && npc.Get("id_name") is { Kind: RValueKind.String } key && key.ToString().StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// In the tavern: each trader there, on its spot, with a stock entry and
    /// set for <paramref name="stockTier"/>, and its stock made anew when
    /// <paramref name="refresh"/>. Answers whether every trader was there and
    /// (when asked) restocked.
    /// </summary>
    public bool Tend(int stockTier, bool refresh, int seedSalt)
    {
        _stockTier = Math.Clamp(stockTier, 1, 5);
        _salt = seedSalt;
        bool all = true;
        foreach (var t in Traders)
        {
            try
            {
                var found = Find(t.Key);
                // One of another kind (an older build's choice) makes way for this one.
                if (found is { } old && Builtins.object_get_name(old.Get("object_index")).ToString() != t.Object)
                {
                    Builtins.instance_destroy(old.Id);
                    found = null;
                }
                if ((found ?? Spawn(t)) is not { } npc)
                {
                    all = false;
                    continue;
                }
                Pin(npc, t);
                if (Entry(npc, t) is not { } entry)
                {
                    all = false;
                    continue;
                }
                Configure(npc, t);
                if (refresh) Restock(entry, t);
            }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException)
            {
                all = false;
                _log.Warning($"trader {t.Key}: {ex.Message}");
            }
        }
        return all;
    }

    /// <summary>For the test host: each trader found, where it stands, its tiers and how many rows its stock has.</summary>
    public static object Describe() => Traders.Select(t =>
    {
        if (Find(t.Key) is not { } npc) return (object)new { key = t.Key, present = false };
        var entry = new DsMap(Scripts.scr_npc_get_global_data.CallAs(npc));
        var list = entry.Exists ? new DsList(entry.Get("trade_list")) : default;
        return new
        {
            key = t.Key,
            present = true,
            id = World.IdKey(npc.Id),
            x = World.Num(npc, "x"),
            y = World.Num(npc, "y"),
            state = npc.Get("state").ToString(),
            tiers = $"{World.Num(npc, "Equipment_Tier_Min")}-{World.Num(npc, "Equipment_Tier_Max")}",
            entry = entry.Exists,
            stockRows = list.Exists ? list.Count : -1,
            restockPending = entry.Exists && World.Truthy(entry.Get("is_restock")),
        };
    }).ToList();

    // The trader with that key; any second one (an older build, a race with a
    // load) would wander unpinned and share its stock, so it is removed.
    private static InstanceRef? Find(string key)
    {
        if (Objects.o_NPC.Object is not { } npcs) return null;
        InstanceRef? first = null;
        foreach (var n in npcs.Instances().ToList())
        {
            if (n.Get("id_name") is not { Kind: RValueKind.String } k || k.ToString() != key) continue;
            if (first == null) first = n;
            else Builtins.instance_destroy(n.Id);
        }
        return first;
    }

    private InstanceRef? Spawn(Trader t)
    {
        var obj = GmlObject.Find(t.Object) ?? throw new InvalidOperationException($"no {t.Object}");
        var npc = new InstanceRef(Builtins.instance_create_depth(t.X, t.Y, -t.Y, obj.Index));
        if (!npc.Exists) return null;
        // Our own stock key, before anything reads the town trader's.
        npc.Set("id_name", t.Key);
        // Its User Event 9 says what it sells (selling_loot_category); a new NPC has it empty.
        RunStockEvent(npc, obj.Index);
        _log.Info($"trader {t.Key} ({t.Object}) arrives");
        return npc;
    }

    // Back on its spot, and kept from walking its town schedule.
    private static void Pin(InstanceRef npc, Trader t)
    {
        if (Objects.o_dialogue.Object is { InstanceCount: > 0 } || Objects.o_trade_inventory.Object is { InstanceCount: > 0 }) return;
        if (Math.Abs(World.Num(npc, "x") - t.X) > 13 || Math.Abs(World.Num(npc, "y") - t.Y) > 13)
            Scripts.scr_invisible_teleport.CallAs(npc, t.X, t.Y);
        if (npc.Get("state").ToString() != "idle") npc.Set("state", "idle");
    }

    // The trader's entry in npc_data, made on its home tile when it has none.
    // A new entry may only be found on a later tick; null until then.
    private DsMap? Entry(InstanceRef npc, Trader t)
    {
        string key = t.Key;
        if (Scripts.scr_npc_get_global_data.CallAs(npc) is { IsNumber: true } found && found.AsReal >= 0 && new DsMap(found) is { Exists: true } have)
            return have;
        var npcData = new DsMap(Scripts.scr_globaltile_get.CallAs(npc, "npc_data", t.Home.X, t.Home.Y));
        if (!npcData.Exists) throw new InvalidOperationException($"no npc_data on tile {t.Home.X},{t.Home.Y}");
        if (npcData.Has(key))
        {
            // Made on an earlier tick and still not found: said once, then waited for.
            if (_unfound.Add(key)) _log.Warning($"trader {key}: its stock entry is on tile {t.Home.X},{t.Home.Y} but the game does not find it yet");
            return null;
        }
        var e = Builtins.ds_map_create();
        try
        {
            Builtins.ds_map_add_list(e, "trade_list", Builtins.ds_list_create());
            Builtins.ds_map_add(e, "is_restock", 1);
            Builtins.ds_map_add(e, "current_floor", "T1");
            Builtins.ds_map_add(e, "next_floor", "T1");
        }
        catch
        {
            Builtins.ds_map_destroy(e);
            throw;
        }
        // Owned by npc_data from here on (and saved with the world).
        Builtins.ds_map_add_map(npcData.Id, key, e);
        _log.Info($"trader {key}: stock entry made on tile {t.Home.X},{t.Home.Y}");
        var entry = new DsMap(Scripts.scr_npc_get_global_data.CallAs(npc));
        return entry.Exists ? entry : null;
    }

    // The trader's numbers for the current stock: tiers from the trial's
    // (the arms trader one above), rarer goods as the trials go on, a size
    // varied per refresh (the game seeds its rolls by world and day, so the
    // same settings would roll the same stock), and its own range of goods.
    // Written only where they differ, as this runs every tavern tick.
    private void Configure(InstanceRef npc, Trader t)
    {
        int tier = _stockTier, min = tier, max = Math.Clamp(tier + t.TierBonus, 1, 5);
        SetIfNot(npc, "Equipment_Tier_Min", min);
        SetIfNot(npc, "Equipment_Tier_Max", max);
        SetIfNot(npc, "Equipment_Tier_Max_Base", max);
        SetIfNot(npc, "Equipment_Uncommon_Chance", Math.Min(80, t.Uncommon + 5 * tier));
        SetIfNot(npc, "Equipment_Rare_Chance", Math.Min(50, t.Rare + 3 * tier));
        SetIfNot(npc, "Stock_Size", t.StockSize + 0.1 * (_salt % 4));
        if (t.Sells == null) return;
        var want = t.Sells.SelectMany(s => new RValue[] { s.Category, s.Count + (tier - 1) / 2 }).ToList();
        var sells = new DsList(npc.Get("selling_loot_category"));
        if (!sells.Exists) return;
        var have = sells.Items();
        if (have.Count == want.Count && have.Zip(want).All(p => p.First.ToString() == p.Second.ToString())) return;
        sells.Clear();
        foreach (var v in want) sells.Add(v);
    }

    private static void SetIfNot(InstanceRef npc, string name, double value)
    {
        if (World.Num(npc, name, double.NaN) != value) npc.Set(name, value);
    }

    // A new stock on the next opening of the trade window. Generation appends,
    // so the old stock (and the old purse) goes first.
    private void Restock(DsMap entry, Trader t)
    {
        // Already made for this stock (the refresh failed only for another trader, and is retried).
        if (entry.Get(StockKey) is { IsNumber: true } made && made.AsReal == _salt) return;
        entry.Set(StockKey, _salt);
        var list = new DsList(entry.Get("trade_list"));
        if (list.Exists) list.Clear();
        else Builtins.ds_map_add_list(entry.Id, "trade_list", Builtins.ds_list_create());
        entry.Set("is_restock", 1);
        _log.Info($"trader {t.Key}: new stock at tier {_stockTier}-{Math.Clamp(_stockTier + t.TierBonus, 1, 5)}, made when its trade window next opens");
    }

    // The NPC's own User Event 9, or the nearest parent's when it inherits it.
    private static string? StockEventSymbol(int objectIndex)
    {
        for (int o = objectIndex, guard = 0; o >= 0 && guard < 16; guard++)
        {
            string symbol = $"gml_Object_{Builtins.object_get_name(o)}_Other_19";
            if (Game.FindSymbol(symbol) != 0) return symbol;
            o = (int)Builtins.object_get_parent(o).AsReal;
        }
        return null;
    }

    private static void RunStockEvent(InstanceRef npc, int objectIndex)
    {
        if (StockEventSymbol(objectIndex) is { } symbol && npc.Resolve() is { } inst) Game.CallEvent(symbol, inst);
    }
}