namespace CoreLoader;

/// <summary>
/// The game's global variables (<c>global.x</c> in GML), through the runtime's
/// own reflection builtins. Game thread only.
/// </summary>
public static class Globals
{
    // GML's `global` keyword compiles to this instance id.
    private const double GlobalId = -5;

    public static RValue Get(string name) => Game.CallBuiltin("variable_global_get", name);

    public static void Set(string name, RValue value) => Game.CallBuiltin("variable_global_set", name, value);

    public static bool Exists(string name) => Game.CallBuiltin("variable_global_exists", name).AsBool;

    /// <summary>Every global variable name, as the runtime lists them.</summary>
    public static IReadOnlyList<string> Names() =>
        Gml.ToStringList(Game.CallBuiltin("variable_instance_get_names", GlobalId));
}

/// <summary>Helpers for GML values that need the runtime to inspect.</summary>
public static class Gml
{
    /// <summary>GML's typeof(): "number", "string", "array", "struct", "ref", ...</summary>
    public static string TypeOf(RValue value) => Game.CallBuiltin("typeof", value).ToString();

    public static int ArrayLength(RValue array) => (int)Game.CallBuiltin("array_length", array).AsReal;

    public static RValue ArrayGet(RValue array, int index) => Game.CallBuiltin("array_get", array, index);

    /// <summary>Converts a GML array to strings; anything that is not an array gives an empty list.</summary>
    public static IReadOnlyList<string> ToStringList(RValue array)
    {
        if (array.Kind != RValueKind.Array) return Array.Empty<string>();
        int n = ArrayLength(array);
        var list = new List<string>(n);
        for (int i = 0; i < n; i++) list.Add(ArrayGet(array, i).ToString());
        return list;
    }

    /// <summary>Names of a struct's members (e.g. a GML constructor instance).</summary>
    public static IReadOnlyList<string> StructNames(RValue structValue) =>
        Gml.ToStringList(Game.CallBuiltin("variable_struct_get_names", structValue));

    public static RValue StructGet(RValue structValue, string name) =>
        Game.CallBuiltin("variable_struct_get", structValue, name);

    public static void StructSet(RValue structValue, string name, RValue value) =>
        Game.CallBuiltin("variable_struct_set", structValue, name, value);
}

/// <summary>
/// An object type in the game (an asset index), with its live instances.
/// Works purely through GameMaker builtins, so it behaves the same in any game.
/// </summary>
public readonly record struct GmlObject(int Index, string Name)
{
    /// <summary>Every object in the game. Walks asset indices until they stop existing.</summary>
    public static IReadOnlyList<GmlObject> All()
    {
        var list = new List<GmlObject>();
        int misses = 0;
        for (int i = 0; misses < 64 && i < 100_000; i++)
        {
            if (!Game.CallBuiltin("object_exists", i).AsBool) { misses++; continue; }
            misses = 0;
            list.Add(new GmlObject(i, Game.CallBuiltin("object_get_name", i).ToString()));
        }
        return list;
    }

    private static readonly Dictionary<string, GmlObject> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> Misses = new(StringComparer.Ordinal);

    /// <summary>Looks an object up by name, e.g. "o_player". Cached per name.</summary>
    public static GmlObject? Find(string name)
    {
        if (Cache.TryGetValue(name, out var hit)) return hit;

        // A miss costs a full object scan, so it is remembered briefly: a mod
        // polling a name every frame stays cheap, and an object that only
        // appears once the game has finished loading is still found soon after.
        long now = Environment.TickCount64;
        if (Misses.TryGetValue(name, out var at) && now - at < 2000) return null;

        var found = Resolve(name);
        if (found is { } f) { Cache[name] = f; Misses.Remove(name); }
        else Misses[name] = now;
        return found;
    }

    private static GmlObject? Resolve(string name)
    {
        var idx = Game.CallBuiltin("asset_get_index", name);

        // Older runtimes return the plain index; 2024+ runtimes a typed asset
        // reference whose low 32 bits are the index. Either way the candidate is
        // only trusted once object_get_name gives the same name back.
        int candidate = idx.IsNumber ? (int)idx.AsReal
                      : idx.Kind == RValueKind.Reference ? (int)(idx.Int64 & 0xFFFFFFFF)
                      : -1;
        if (candidate >= 0 && Game.CallBuiltin("object_exists", candidate).AsBool &&
            Game.CallBuiltin("object_get_name", candidate).ToString() == name)
            return new GmlObject(candidate, name);

        foreach (var o in All())
            if (o.Name == name) return o;
        return null;
    }

    /// <summary>Live instances of this object (including children).</summary>
    public int InstanceCount => (int)Game.CallBuiltin("instance_number", Index).AsReal;

    /// <summary>The n-th live instance, as a GML instance reference.</summary>
    public InstanceRef Instance(int n) => new(Game.CallBuiltin("instance_find", Index, n));

    public IEnumerable<InstanceRef> Instances()
    {
        int n = InstanceCount;
        for (int i = 0; i < n; i++) yield return Instance(i);
    }
}

/// <summary>
/// A GML instance by id/reference (what instance_find returns) rather than by
/// CInstance pointer. Safe to hold across frames: a destroyed instance simply
/// stops existing instead of dangling.
/// </summary>
public readonly record struct InstanceRef(RValue Id)
{
    public bool Exists => !Id.IsUndefined && Game.CallBuiltin("instance_exists", Id).AsBool;

    public RValue Get(string variable) => Game.CallBuiltin("variable_instance_get", Id, variable);

    public void Set(string variable, RValue value) => Game.CallBuiltin("variable_instance_set", Id, variable, value);

    public bool Has(string variable) => Game.CallBuiltin("variable_instance_exists", Id, variable).AsBool;

    public IReadOnlyList<string> VariableNames() =>
        Gml.ToStringList(Game.CallBuiltin("variable_instance_get_names", Id));

    public RValue this[string variable]
    {
        get => Get(variable);
        set => Set(variable, value);
    }
}
