namespace CoreLoader;

/// <summary>
/// A GameMaker ds_map, by id, through the game's own builtins. Many games keep
/// their real state in these (an item's stats, a character's body parts) and
/// hand out only the numeric id. Game thread only.
/// </summary>
/// <remarks>
/// The struct holds nothing but the id, so it is safe to keep across frames: a
/// destroyed map simply stops <see cref="Exists"/>ing. What it returns is not:
/// strings read out of a map are pooled and released at the end of the frame,
/// so convert them (or <see cref="Values.Keep"/> them) before then.
/// </remarks>
public readonly record struct DsMap(RValue Id)
{
    // ds_type_map in every runtime.
    private const int TypeMap = 1;

    /// <summary>Whether the id names a live map. Anything that is not an id answers false.</summary>
    public bool Exists => IsId(Id) && Game.CallBuiltin("ds_exists", Id, TypeMap).AsBool;

    public int Count => (int)Game.CallBuiltin("ds_map_size", Id).AsReal;

    /// <summary>The value under <paramref name="key"/>, or undefined.</summary>
    public RValue Get(RValue key) => Game.CallBuiltin("ds_map_find_value", Id, key);

    /// <summary>Sets a key, adding it when missing (ds_map_replace does both).</summary>
    /// <remarks>
    /// A nested list or map stored here is stored as its bare id: the map no
    /// longer knows it is nested, and json_encode writes it as a number. Edit a
    /// nested structure in place (<c>new DsList(map.Get("key"))</c>) instead of
    /// writing its id back.
    /// </remarks>
    public void Set(RValue key, RValue value) => Game.CallBuiltin("ds_map_replace", Id, key, value);

    public bool Has(RValue key) => Game.CallBuiltin("ds_map_exists", Id, key).AsBool;

    public void Remove(RValue key) => Game.CallBuiltin("ds_map_delete", Id, key);

    public void Clear() => Game.CallBuiltin("ds_map_clear", Id);

    public RValue this[RValue key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    /// <summary>Every key as text, in the map's own (hash) order.</summary>
    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var (k, _) in Entries()) keys.Add(k);
        return keys;
    }

    /// <summary>Every key, as text, with its value, in the map's own (hash) order.</summary>
    public IReadOnlyList<(string Key, RValue Value)> Entries()
    {
        var list = new List<(string, RValue)>();
        int size = Count;
        var key = Game.CallBuiltin("ds_map_find_first", Id);
        // The size bounds the walk: a map edited mid-walk must not loop forever.
        for (int i = 0; i < size && !key.IsUndefined; i++)
        {
            list.Add((key.ToString(), Get(key)));
            key = Game.CallBuiltin("ds_map_find_next", Id, key);
        }
        return list;
    }

    /// <summary>
    /// The map as JSON, through the game's own json_encode, which is the one
    /// thing that knows which values are nested lists and maps.
    /// </summary>
    public string ToJson() => Game.CallBuiltin("json_encode", Id).ToString();

    // Older runtimes hand out ds ids as plain numbers; newer ones may type them
    // as references. Anything else (undefined, a string) is never an id, and is
    // refused before the runtime is asked.
    internal static bool IsId(RValue v) => v.IsNumber || v.Kind == RValueKind.Reference;
}

/// <summary>
/// A GameMaker ds_list, by id, through the game's own builtins. Game thread
/// only; the same value rules as <see cref="DsMap"/> apply.
/// </summary>
public readonly record struct DsList(RValue Id)
{
    // ds_type_list in every runtime.
    private const int TypeList = 2;

    /// <summary>Whether the id names a live list. Anything that is not an id answers false.</summary>
    public bool Exists => DsMap.IsId(Id) && Game.CallBuiltin("ds_exists", Id, TypeList).AsBool;

    public int Count => (int)Game.CallBuiltin("ds_list_size", Id).AsReal;

    public RValue At(int index) => Game.CallBuiltin("ds_list_find_value", Id, index);

    /// <summary>Replaces entry <paramref name="index"/> (ds_list_set pads a short list with zeros).</summary>
    public void Set(int index, RValue value) => Game.CallBuiltin("ds_list_set", Id, index, value);

    public RValue this[int index]
    {
        get => At(index);
        set => Set(index, value);
    }

    public void Add(RValue value) => Game.CallBuiltin("ds_list_add", Id, value);

    public void Insert(int index, RValue value) => Game.CallBuiltin("ds_list_insert", Id, index, value);

    public void RemoveAt(int index) => Game.CallBuiltin("ds_list_delete", Id, index);

    public void Clear() => Game.CallBuiltin("ds_list_clear", Id);

    /// <summary>Every entry, in order.</summary>
    public IReadOnlyList<RValue> Items()
    {
        int n = Count;
        var items = new List<RValue>(n);
        for (int i = 0; i < n; i++) items.Add(At(i));
        return items;
    }
}
