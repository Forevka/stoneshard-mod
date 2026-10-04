namespace CoreLoader;

/// <summary>
/// The game's global variables (<c>global.x</c> in GML), through the runtime's
/// own reflection builtins. Game thread only.
/// </summary>
public static class Globals
{
    // GML's `global` keyword compiles to this instance id.
    private const double GlobalId = -5;

    public static RValue Get(string name) => Game.CallWithName(default, "variable_global_get", 0, name);

    public static void Set(string name, RValue value) => Game.CallWithName(default, "variable_global_set", 0, name, value);

    public static bool Exists(string name) => Game.CallWithName(default, "variable_global_exists", 0, name).AsBool;

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
        Game.CallWithName(default, "variable_struct_get", 1, name, structValue);

    public static void StructSet(RValue structValue, string name, RValue value) =>
        Game.CallWithName(default, "variable_struct_set", 1, name, structValue, value);
}

/// <summary>
/// An object type in the game (an asset index), with its live instances.
/// Works purely through GameMaker builtins, so it behaves the same in any game.
/// </summary>
public readonly record struct GmlObject(int Index, string Name)
{
    /// <summary>
    /// Every object in the game, in index order. Read once from the cached
    /// <see cref="ObjectTable"/>; the first call finishes the table's name scan
    /// on the spot if <see cref="ObjectTable.Start"/> has not already done it.
    /// </summary>
    public static IReadOnlyList<GmlObject> All() => ObjectTable.Objects();

    /// <summary>The object at asset index <paramref name="index"/>, or null.</summary>
    public static GmlObject? FromIndex(int index) => ObjectTable.At(index);

    /// <summary>
    /// The object this one inherits from, or null. From the table once it is
    /// <see cref="ObjectTable.Ready"/>, otherwise asked of the runtime.
    /// </summary>
    public GmlObject? Parent => ObjectTable.At(ObjectTable.ParentIndex(Index));

    /// <summary>Parent, grandparent and so on, nearest first.</summary>
    public IEnumerable<GmlObject> Ancestors()
    {
        // A cycle cannot be built in the IDE; the guard is for a table read wrong.
        var cur = Parent;
        for (int depth = 0; cur is { } p && depth < 64; depth++)
        {
            yield return p;
            cur = p.Parent;
        }
    }

    /// <summary>
    /// True when this object is <paramref name="name"/> or inherits from it -
    /// C#'s <c>is</c>, not GML's object_is_ancestor, which excludes the object itself.
    /// </summary>
    public bool IsA(string name)
    {
        if (Name == name) return true;
        foreach (var a in Ancestors())
            if (a.Name == name) return true;
        return false;
    }

    /// <summary>
    /// The objects that name this one as their parent (direct children only).
    /// Needs every object's parent: if the table is not <see cref="ObjectTable.Ready"/>,
    /// it is finished on the spot, which can take a noticeable frame.
    /// </summary>
    public IReadOnlyList<GmlObject> Children() => ObjectTable.ChildrenOf(Index);

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

    /// <summary>A name that now exists (a defined object): drops a remembered miss or stale entry.</summary>
    internal static void Forget(string name)
    {
        Cache.Remove(name);
        Misses.Remove(name);
    }

    private static GmlObject? Resolve(string name)
    {
        var idx = Game.CallBuiltin("asset_get_index", name);

        // Older runtimes return the plain index; 2024+ runtimes a typed asset
        // reference whose low 32 bits are the index. Either way the candidate is
        // only trusted once object_get_name gives the same name back.
        int candidate = ObjectTable.AssetIndex(idx);
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

    /// <summary>
    /// The live instance this id names, for calls that need one (scripts run
    /// as an instance). Null when it no longer exists or is deactivated, or when
    /// this runtime's id lookup is unavailable (<see cref="Game.CanResolveInstances"/>).
    /// Resolve again each frame rather than keeping the result: an Instance is a
    /// pointer, and it dangles once the instance is destroyed.
    /// </summary>
    public unsafe Instance? Resolve()
    {
        Loader.EnsureGameThread();
        var id = Id;
        nint p = Loader.Api->InstanceFromId(&id);
        return p == 0 ? null : new Instance(p);
    }

    /// <summary>Calls a script as this instance (self and other). See <see cref="Game.CallScriptAs(InstanceRef, string, RValue[])"/>.</summary>
    public RValue CallScript(string name, params RValue[] args) => Game.CallScriptAs(this, name, args);

    public RValue Get(string variable) => Game.CallWithName(default, "variable_instance_get", 1, variable, Id);

    public void Set(string variable, RValue value) => Game.CallWithName(default, "variable_instance_set", 1, variable, Id, value);

    public bool Has(string variable) => Game.CallWithName(default, "variable_instance_exists", 1, variable, Id).AsBool;

    public IReadOnlyList<string> VariableNames() =>
        Gml.ToStringList(Game.CallBuiltin("variable_instance_get_names", Id));

    public RValue this[string variable]
    {
        get => Get(variable);
        set => Set(variable, value);
    }
}
