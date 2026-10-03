using System.Text.Json;
using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>Test-host commands for looking at the live game while the mod is built.</summary>
internal static class Probe
{
    public static void Register(Logger log)
    {
        TestHost.Register("tr.dungeons", _ => Dungeons(), "tr.dungeons: every crypt, catacombs and bastion on the world map");
        TestHost.Register("tr.globals", args =>
        {
            // global is instance -5; the names come back as a GML array.
            string filter = args.Count > 0 ? args[0].GetString()! : "";
            var names = Game.CallBuiltin("variable_instance_get_names", -5);
            var found = new List<object>();
            for (int i = 0, n = Gml.ArrayLength(names); i < n; i++)
            {
                string name = Gml.ArrayGet(names, i).ToString();
                if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var v = Globals.Get(name);
                found.Add(new { name, kind = v.Kind.ToString(), value = v.ToString() });
            }
            return found;
        }, "tr.globals [filter]: global variables whose name contains the filter, with their kind and value");
        TestHost.Register("tr.where", _ => new
        {
            cell = new[] { Globals.Get("playerGridX").AsReal, Globals.Get("playerGridY").AsReal },
            inDungeon = Scripts.scr_is_in_dungeon.Call().ToString(),
            floor = Globals.Get("locationFloor").ToString(),
            player = Objects.o_player.First is { } p ? new[] { p.Get("x").AsReal, p.Get("y").AsReal } : null,
        }, "tr.where: the player's cell, floor, whether in a dungeon, and position");
        TestHost.Register("tr.inst", args => Instances(args[0].GetString()!, args.Count > 1 ? args[1].GetString()! : ""),
            "tr.inst <object> [var,var...]: live instances (children too) with x, y, object name and the vars");
        TestHost.Register("tr.event", args =>
        {
            var obj = GmlObject.Find(args[0].GetString()!) ?? throw new ArgumentException("no such object");
            var inst = obj.Instance(args[1].GetInt32()).Resolve() ?? throw new ArgumentException("no such instance");
            Game.CallEvent(args[2].GetString()!, inst);
            return "ok";
        }, "tr.event <object> <n> <event symbol>: runs an event as that instance");
        TestHost.Register("tr.goto", args =>
        {
            int x = args[0].GetInt32(), y = args[1].GetInt32();
            if (Objects.o_player.First is not { } player) return "no player";
            int room = (int)Scripts.scr_globaltile_get_room.Call(x, y).AsReal;
            double c = 45 * 26 + 13;
            Scripts.scr_atr_set_simple.CallAs(player, "localX", c);
            Scripts.scr_atr_set_simple.CallAs(player, "localY", c);
            Globals.Set("playerGridX", x);
            Globals.Set("playerGridY", y);
            Scripts.scr_smoothRoomChange.CallAs(player, room, Builtins.array_create(1, 4), -1, false);
            return Builtins.room_get_name(room).ToString();
        }, "tr.goto <x> <y>: walks over the border into that world cell, as a border crossing does");
        TestHost.Register("tr.snap", _ =>
        {
            _snap = Snapshot();
            return _snap.Count;
        }, "tr.snap: remembers every global's value (numbers and strings)");
        TestHost.Register("tr.diff", _ =>
        {
            var now = Snapshot();
            return now.Where(kv => !_snap.TryGetValue(kv.Key, out var old) || old != kv.Value)
                      .Select(kv => $"{kv.Key}: {(_snap.TryGetValue(kv.Key, out var o) ? o : "(new)")} -> {kv.Value}")
                      .OrderBy(s => s).ToArray();
        }, "tr.diff: the globals that changed since tr.snap");
        TestHost.Register("tr.gwatch", args =>
        {
            string symbol = args[0].GetString()!, global = args[1].GetString()!;
            _watches.Add(Hooks.Before(symbol, c => Safe(log, () => log.Info($"gwatch {symbol} before: {global}={Globals.Get(global)}"))));
            _watches.Add(Hooks.After(symbol, c => Safe(log, () => log.Info($"gwatch {symbol} after: {global}={Globals.Get(global)}"))));
            return "watching";
        }, "tr.gwatch <function> <global>: logs the global before and after each call");
        TestHost.Register("tr.dumpresult", args =>
        {
            string symbol = args[0].GetString()!;
            _watches.Add(Hooks.After(symbol, c => Safe(log, () =>
            {
                var r = new InstanceRef(c.Result);
                if (!r.Exists) return;
                var vars = r.VariableNames().OrderBy(n => n).Select(n => $"{n}={r.Get(n)}");
                log.Info($"dumpresult {symbol} self={Builtins.object_get_name(c.Self.Get("object_index"))}: {string.Join("; ", vars)}");
            })));
            return "watching";
        }, "tr.dumpresult <function>: logs every variable of the instance each call returns");
        TestHost.Register("tr.snapdiff", args =>
        {
            string symbol = args[0].GetString()!;
            Dictionary<string, string>? before = null;
            Dictionary<string, string>? attrsBefore = null;
            _watches.Add(Hooks.Before(symbol, c => Safe(log, () =>
            {
                before = Snapshot();
                attrsBefore = PlayerAttributes();
            })));
            _watches.Add(Hooks.After(symbol, c => Safe(log, () =>
            {
                if (before is null) return;
                var now = Snapshot();
                var changed = now.Where(kv => !before.TryGetValue(kv.Key, out var o) || o != kv.Value)
                                 .Select(kv => $"{kv.Key}: {(before.TryGetValue(kv.Key, out var o) ? o : "(new)")} -> {kv.Value}");
                var attrs = PlayerAttributes();
                var attrChanged = attrs.Where(kv => attrsBefore is null || !attrsBefore.TryGetValue(kv.Key, out var o) || o != kv.Value)
                                       .Select(kv => $"atr.{kv.Key} -> {kv.Value}");
                log.Info($"snapdiff {symbol}: {string.Join("; ", changed.Concat(attrChanged))}");
            })));
            return "watching";
        }, "tr.snapdiff <function>: logs the globals and player attributes each call changes");
        TestHost.Register("tr.gunwatch", _ =>
        {
            foreach (var h in _watches) h.Dispose();
            _watches.Clear();
            return "ok";
        }, "tr.gunwatch: drops every tr.gwatch");
        TestHost.Register("tr.tp", args =>
        {
            var obj = GmlObject.Find(args[0].GetString()!) ?? throw new ArgumentException("no such object");
            var who = obj.Instance(args[1].GetInt32());
            Scripts.scr_invisible_teleport.CallAs(who, args[2].GetDouble(), args[3].GetDouble());
            return new[] { who.Get("x").AsReal, who.Get("y").AsReal };
        }, "tr.tp <object> <n> <x> <y>: moves that instance with the game's own scr_invisible_teleport");
        TestHost.Register("tr.dkeys", args =>
        {
            int x = args[0].GetInt32(), y = args[1].GetInt32();
            var keys = new[] { "dungeon_type", "dungeon_tier", "dungeon_tier_perm", "dungeon_faction", "Boss_Type", "boss_alive",
                "IsSpecial", "is_quest_dungeon", "dungeon_is_open", "dungeon_reset", "dungeon_amountFloors", "dungeon_modification",
                "contract_modification", "DungeonSeed", "MapZone", "mob_lvl_min", "mob_lvl_max", "generatorVersion" };
            var player = Objects.o_player.First;
            return keys.ToDictionary(k => k, k =>
            {
                try
                {
                    var v = player is { } p ? Scripts.scr_globaltile_dungeon_get.CallAs(p, k, x, y) : Scripts.scr_globaltile_dungeon_get.Call(k, x, y);
                    return $"{Gml.TypeOf(v)}: {v}";
                }
                catch (GmlException ex) { return "error: " + ex.Message; }
            });
        }, "tr.dkeys <x> <y>: the dungeon data the game keeps for that world cell");
        TestHost.Register("tr.gear", _ =>
        {
            var rows = new List<object>();
            if (Objects.o_inv_slot.Object is not { } slots) return rows;
            foreach (var r in slots.Instances())
            {
                if (!World.Truthy(r.Get("equipped"))) continue;
                var row = new Dictionary<string, string> { ["object"] = Builtins.object_get_name(r.Get("object_index")).ToString() };
                foreach (var v in new[] { "name", "Tier", "quality", "Rare", "Unique", "Treasure", "Curse", "price", "equipped_slot", "type", "slot", "Metatype", "rarity" })
                    row[v] = r.Has(v) ? r.Get(v).ToString() : "-";
                var data = new DsMap(r.Get("data"));
                if (data.Exists)
                    foreach (var k in new[] { "Name", "Tier", "rarity", "quality", "Metatype", "Slot", "Rarity" })
                        if (data.Has(k)) row["data." + k] = data.Get(k).ToString();
                rows.Add(row);
            }
            return rows;
        }, "tr.gear: every worn item with its tier, quality and rarity fields");
        TestHost.Register("tr.rooms", args =>
        {
            string filter = args.Count > 0 ? args[0].GetString()! : "";
            var map = new DsMap(Globals.Get("locationsRoomsDataMap"));
            return map.Exists ? map.Entries().Select(e => e.Key).Where(k => k.Contains(filter)).OrderBy(k => k).ToArray() : Array.Empty<string>();
        }, "tr.rooms [filter]: the keys of the saved rooms (global.locationsRoomsDataMap) containing the filter");
        TestHost.Register("tr.roomkeys", args =>
        {
            var map = new DsMap(Globals.Get("locationsRoomsDataMap"));
            if (!map.Exists || !map.Has(args[0].GetString()!)) return "none";
            var inner = map.Get(args[0].GetString()!);
            var sub = new DsMap(inner);
            return sub.Exists ? sub.Entries().Select(e => $"{e.Key} = {Gml.TypeOf(e.Value)}").ToArray() : new[] { Gml.TypeOf(inner) + ": " + inner };
        }, "tr.roomkeys <location>: what a saved location holds (its rooms)");
        TestHost.Register("tr.call", args =>
        {
            var rest = args.Skip(1).Select(Arg).ToArray();
            return Game.CallScript(args[0].GetString()!, rest).ToString();
        }, "tr.call <script> [args]: calls a script and answers the result as text");
    }

    private static Dictionary<string, string> _snap = new();

    // A debug hook that throws would fault the whole mod.
    private static void Safe(Logger log, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.Warning($"probe: {ex.Message}"); }
    }
    private static readonly List<HookHandle> _watches = new();

    // Scalars only: ids of ds structures and arrays would differ for no reason worth reading.
    private static Dictionary<string, string> Snapshot()
    {
        var d = new Dictionary<string, string>();
        foreach (var name in Globals.Names())
        {
            try
            {
                var v = Globals.Get(name);
                if (v.IsNumber || v.Kind == RValueKind.String || v.Kind == RValueKind.Reference) d[name] = v.ToString();
            }
            catch (GmlException) { }
        }
        return d;
    }

    // The player's own scalar variables (where scr_atr_set_simple's localX lands too).
    private static Dictionary<string, string> PlayerAttributes()
    {
        var d = new Dictionary<string, string>();
        if (Objects.o_player.First is not { } p) return d;
        foreach (var name in p.VariableNames())
        {
            try
            {
                var v = p.Get(name);
                if (v.IsNumber || v.Kind == RValueKind.String) d[name] = v.ToString();
            }
            catch (GmlException) { }
        }
        return d;
    }

    private static RValue Arg(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => e.GetString()!,
    };

    private static object Dungeons()
    {
        var list = new List<object>();
        foreach (var subtype in new[] { "Crypt", "Catacombs", "Bastion" })
        {
            var arr = Scripts.scr_glmap_getLocationBySubType.Call(subtype);
            int n = Gml.ArrayLength(arr);
            for (int i = 0; i < n; i++)
            {
                var s = Gml.ArrayGet(arr, i);
                int x = (int)Gml.StructGet(s, "x").AsReal, y = (int)Gml.StructGet(s, "y").AsReal;
                int room = (int)Scripts.scr_globaltile_get_room.Call(x, y).AsReal;
                list.Add(new
                {
                    subtype,
                    x,
                    y,
                    name = Gml.StructGet(s, "name").ToString(),
                    room = room >= 0 ? Builtins.room_get_name(room).ToString() : "-",
                });
            }
        }
        return list;
    }

    private static object Instances(string objectName, string vars)
    {
        var obj = GmlObject.Find(objectName) ?? throw new ArgumentException("no such object");
        var names = vars.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var rows = new List<object>();
        int n = obj.InstanceCount;
        for (int i = 0; i < n; i++)
        {
            var r = obj.Instance(i);
            var row = new Dictionary<string, object?>
            {
                ["n"] = i,
                ["object"] = Builtins.object_get_name(r.Get("object_index")).ToString(),
                ["x"] = r.Get("x").AsReal,
                ["y"] = r.Get("y").AsReal,
            };
            foreach (var v in names) row[v] = r.Has(v) ? r.Get(v).ToString() : null;
            rows.Add(row);
        }
        return rows;
    }
}
