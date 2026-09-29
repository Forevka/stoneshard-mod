using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// GameMaker ds_map / ds_list access through the game's own builtins. Stoneshard
/// keeps most character and item state in these (psyData, Body_Parts_map, an
/// item's data map, the buffs list), addressed by a numeric id.
/// </summary>
internal static class Ds
{
    public static bool MapExists(RValue map) =>
        map.IsNumber && Game.CallBuiltin("ds_exists", map, 1 /* ds_type_map */).AsBool;

    public static bool ListExists(RValue list) =>
        list.IsNumber && Game.CallBuiltin("ds_exists", list, 2 /* ds_type_list */).AsBool;

    /// <summary>Every key of a map with its value, in the map's own order.</summary>
    public static List<(string Key, RValue Value)> Entries(RValue map)
    {
        var list = new List<(string, RValue)>();
        int size = (int)Game.CallBuiltin("ds_map_size", map).AsReal;
        var key = Game.CallBuiltin("ds_map_find_first", map);
        // The size bounds the walk: a map edited mid-walk must not loop forever.
        for (int i = 0; i < size && !key.IsUndefined; i++)
        {
            list.Add((key.ToString(), Game.CallBuiltin("ds_map_find_value", map, key)));
            key = Game.CallBuiltin("ds_map_find_next", map, key);
        }
        return list;
    }

    public static RValue Get(RValue map, string key) => Game.CallBuiltin("ds_map_find_value", map, key);

    public static bool Has(RValue map, string key) => Game.CallBuiltin("ds_map_exists", map, key).AsBool;

    /// <summary>Sets a key, adding it when missing (ds_map_replace does both).</summary>
    public static void Set(RValue map, string key, RValue value) =>
        Game.CallBuiltin("ds_map_replace", map, key, value);

    public static int Count(RValue list) => (int)Game.CallBuiltin("ds_list_size", list).AsReal;

    public static RValue At(RValue list, int index) => Game.CallBuiltin("ds_list_find_value", list, index);

    public static List<RValue> Items(RValue list)
    {
        int n = Count(list);
        var items = new List<RValue>(n);
        for (int i = 0; i < n; i++) items.Add(At(list, i));
        return items;
    }

    public static void Add(RValue list, RValue value) => Game.CallBuiltin("ds_list_add", list, value);

    public static void Clear(RValue list) => Game.CallBuiltin("ds_list_clear", list);

    /// <summary>A map as JSON, through the game's json_encode.</summary>
    public static string Json(RValue map) => Game.CallBuiltin("json_encode", map).ToString();
}
