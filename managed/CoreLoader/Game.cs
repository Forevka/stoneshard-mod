using System.Collections.Concurrent;
using CoreLoader.Native;

namespace CoreLoader;

/// <summary>A compiled GML function the loader found in the game.</summary>
public readonly record struct GmlSymbol(string Name, nint Address)
{
    public bool IsScript => Name.StartsWith("gml_Script_", StringComparison.Ordinal);
    public bool IsObjectEvent => Name.StartsWith("gml_Object_", StringComparison.Ordinal);
}

/// <summary>The running game: its identity, its code and GameMaker's runtime.</summary>
public static unsafe class Game
{
    private static string? _name, _dir, _loaderDir;
    private static GmlSymbol[]? _symbols;
    private static Dictionary<string, nint>? _byName;

    /// <summary>The game exe's name without extension, e.g. "StoneShard".</summary>
    public static string Name => _name ??= Utf8.Read(Loader.Api->GameName()) ?? "";

    /// <summary>Folder holding the game exe.</summary>
    public static string Directory => _dir ??= Utf8.Read(Loader.Api->GameDir()) ?? "";

    /// <summary>Folder holding CoreLoader.dll.</summary>
    public static string LoaderDirectory => _loaderDir ??= Utf8.Read(Loader.Api->LoaderDir()) ?? "";

    /// <summary>The runtime helpers were found, so calls and strings work.</summary>
    public static bool IsGmlReady => Loader.Api->GmlReady() != 0;

    /// <summary>The calling-convention self-test passed this session.</summary>
    public static bool IsAbiProven => Loader.Api->AbiProven() != 0;

    /// <summary>Number of GameMaker builtins resolved (0 until the registry is found).</summary>
    public static int BuiltinCount => Loader.Api->BuiltinCount();

    /// <summary>Every gml_* function in the game, in name order.</summary>
    public static IReadOnlyList<GmlSymbol> Symbols
    {
        get
        {
            if (_symbols != null) return _symbols;
            int n = Loader.Api->SymbolCount();
            var list = new GmlSymbol[n];
            for (int i = 0; i < n; i++)
                list[i] = new GmlSymbol(Utf8.Read(Loader.Api->SymbolName(i)) ?? "", Loader.Api->SymbolAddress(i));
            return _symbols = list;
        }
    }

    private static string[]? _builtins;

    /// <summary>Every GameMaker builtin this runtime registers, sorted (empty until the registry resolves).</summary>
    public static IReadOnlyList<string> Builtins
    {
        get
        {
            if (_builtins is { Length: > 0 }) return _builtins;
            int n = BuiltinCount;
            var list = new string[n];
            for (int i = 0; i < n; i++) list[i] = Utf8.Read(Loader.Api->BuiltinName(i)) ?? "";
            return _builtins = list;
        }
    }

    /// <summary>Address of a gml_* function, or 0.</summary>
    public static nint FindSymbol(string name)
    {
        _byName ??= Symbols.ToDictionary(s => s.Name, s => s.Address, StringComparer.Ordinal);
        return _byName.TryGetValue(name, out var a) ? a : 0;
    }

    /// <summary>An instance the game is currently running code as, if any.</summary>
    public static Instance CurrentSelf => new(Loader.Api->CurrentSelf());

    /// <summary>
    /// Calls a script. <paramref name="name"/> may be the full symbol
    /// ("gml_Script_scr_foo") or the script name ("scr_foo").
    /// </summary>
    public static RValue CallScript(string name, params RValue[] args) =>
        CallScriptAs(default, default, name, args);

    /// <summary>Calls a script with an explicit self/other.</summary>
    public static RValue CallScriptAs(Instance self, Instance other, string name, params RValue[] args)
    {
        Loader.EnsureGameThread();
        var fn = Resolve(name, "gml_Script_");
        RValue result = RValue.Undefined;
        fixed (RValue* a = args)
        {
            if (Loader.Api->CallScript(fn, self.Pointer, other.Pointer, &result, a, args.Length) == 0)
                throw GmlException.CallFailed($"call to {name}");
        }
        return Values.Track(result);
    }

    /// <summary>
    /// Calls a script as the instance <paramref name="self"/> names (self and
    /// other are both that instance). Throws if the instance no longer exists,
    /// or if this runtime's id lookup is unavailable (see <see cref="CanResolveInstances"/>).
    /// </summary>
    public static RValue CallScriptAs(InstanceRef self, string name, params RValue[] args)
    {
        var instance = self.Resolve() ?? throw new GmlException(CanResolveInstances
            ? $"call to {name} failed: the instance does not exist (destroyed or deactivated)"
            : $"call to {name} failed: this runtime's instance lookup is unavailable (see the loader log)");
        return CallScriptAs(instance, instance, name, args);
    }

    /// <summary>
    /// Whether instance ids can be turned into instances here (<see cref="InstanceRef.Resolve"/>).
    /// The runtime's id table is found by pattern and proven on the live game;
    /// this stays false until that proof has passed (it needs a live instance).
    /// </summary>
    public static bool CanResolveInstances
    {
        get
        {
            Loader.EnsureGameThread();
            return Loader.Api->InstanceFromId(null) != 0;
        }
    }

    /// <summary>Runs an object event (e.g. "gml_Object_o_x_Step_0") as <paramref name="self"/>.</summary>
    public static void CallEvent(string name, Instance self, Instance other = default)
    {
        Loader.EnsureGameThread();
        var fn = Resolve(name, "gml_Object_");
        if (Loader.Api->CallEvent(fn, self.Pointer, other.Pointer) == 0)
            throw GmlException.CallFailed($"event {name}");
    }

    /// <summary>
    /// How many arguments this game's runtime registered <paramref name="name"/>
    /// with: -1 variadic, null if the builtin does not exist. Registries do not
    /// always match the manual, and a call with the wrong count is refused.
    /// </summary>
    public static int? BuiltinArity(string name)
    {
        fixed (byte* n = Utf8.Get(name))
        {
            int a = Loader.Api->BuiltinArity(n);
            return a == -2 ? null : a;
        }
    }

    /// <summary>Calls a GameMaker builtin, e.g. <c>CallBuiltin("instance_number", obj)</c>.</summary>
    public static RValue CallBuiltin(string name, params RValue[] args) =>
        CallBuiltinAs(default, name, args);

    /// <summary>Calls a builtin with an explicit self.</summary>
    public static RValue CallBuiltinAs(Instance self, string name, params RValue[] args)
    {
        Loader.EnsureGameThread();

        // Variable-name arguments: creating a variable makes the runtime keep a
        // pointer to the NAME's characters, and a pooled string would be freed
        // under it at the end of the frame. Such names are swapped for the
        // permanent string of that name.
        if (NameArgument(name) is { } ni && ni < args.Length && args[ni].Kind == RValueKind.String)
        {
            args = (RValue[])args.Clone();
            args[ni] = NameValue(args[ni].ToString());
        }
        return Invoke(self, name, args);
    }

    /// <summary>
    /// A builtin whose argument <paramref name="nameIndex"/> is a variable or
    /// member name, given as C# text: it goes straight to the name's permanent
    /// string, with no GML string made and converted back per call.
    /// </summary>
    internal static RValue CallWithName(Instance self, string builtin, int nameIndex, string variable, params RValue[] others)
    {
        Loader.EnsureGameThread();
        var args = new RValue[others.Length + 1];
        for (int i = 0, o = 0; i < args.Length; i++)
            args[i] = i == nameIndex ? NameValue(variable) : others[o++];
        return Invoke(self, builtin, args);
    }

    private static RValue Invoke(Instance self, string name, RValue[] args)
    {
        RValue result = RValue.Undefined;
        fixed (byte* n = Utf8.Get(name))
        fixed (RValue* a = args)
        {
            if (Loader.Api->CallBuiltin(n, &result, a, args.Length, self.Pointer, 0) == 0)
                throw GmlException.CallFailed($"builtin {name}");
        }
        return Values.Track(result);
    }

    // One GML string per variable name, made once and kept for the life of the
    // process: the runtime may keep a pointer to a new variable's name, and a
    // name used every frame should not cost a string per call. Only as many as
    // the names mods actually use. Never pooled, never freed.
    private static readonly Dictionary<string, RValue> Names = new(StringComparer.Ordinal);

    internal static RValue NameValue(string variable)
    {
        if (!Names.TryGetValue(variable, out var v))
            Names[variable] = v = RValue.FromStringPermanent(variable);
        return v;
    }

    private static int? NameArgument(string builtin) => builtin switch
    {
        "variable_global_get" or "variable_global_set" or "variable_global_exists" => 0,
        "variable_instance_get" or "variable_instance_set" or "variable_instance_exists" => 1,
        "variable_struct_get" or "variable_struct_set" or "variable_struct_exists" or "variable_struct_remove" => 1,
        "struct_get" or "struct_set" or "struct_exists" or "struct_remove" => 1,
        _ => null,
    };

    /// <summary>
    /// Whether the game has loaded its assets (sprite or room 0 exists). Some
    /// games load them seconds after their first frame.
    /// </summary>
    /// <param name="whenUnsure">The answer when the check itself fails.</param>
    internal static bool AssetsLoaded(bool whenUnsure = true)
    {
        try
        {
            return CallBuiltin("sprite_exists", 0).AsBool || CallBuiltin("room_exists", 0).AsBool;
        }
        catch (GmlException)
        {
            return whenUnsure;
        }
    }

    // ---------------------------------------------------------- game thread

    private static readonly ConcurrentQueue<(Action Action, Runtime.LoadedMod? Owner)> Pending = new();

    /// <summary>
    /// Runs <paramref name="action"/> on the game thread at the start of the next
    /// frame, as the mod that queued it (so anything it registers belongs to that
    /// mod, and an exception faults that mod). Dropped if the mod is unloaded first.
    /// </summary>
    public static void RunOnGameThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        // From another thread "the current mod" means nothing: the owner is
        // read from the code being queued.
        Pending.Enqueue((action, Runtime.ModManager.OwnerOf(action)));
    }

    internal static void DrainPending(Logger log)
    {
        // Only what was queued before this frame: an action that queues itself
        // again ("check again next frame") runs next frame, not in an endless
        // loop that never lets Present return.
        for (int n = Pending.Count; n > 0 && Pending.TryDequeue(out var item); n--)
        {
            var (a, owner) = item;
            // Code from a mod that has been unloaded since it queued this.
            if (owner == null &&
                System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a.Method.Module.Assembly) is Runtime.ModLoadContext)
                continue;
            if (owner == null)
            {
                try { a(); }
                catch (Exception ex) { log.Error("queued game-thread action threw", ex); }
                continue;
            }
            if (!Runtime.ModManager.Mods.Contains(owner) || owner.State == Runtime.ModState.Faulted) continue;
            Runtime.ModManager.Invoke(owner, "queued action", _ => a());
        }
    }

    private static nint Resolve(string name, string prefix)
    {
        var fn = FindSymbol(name);
        if (fn == 0 && !name.StartsWith("gml_", StringComparison.Ordinal)) fn = FindSymbol(prefix + name);
        if (fn == 0) throw new GmlException($"{name} does not exist in {Name}");
        return fn;
    }
}

/// <summary>A live GameMaker instance (CInstance*). Default is "none".</summary>
public readonly unsafe struct Instance : IEquatable<Instance>
{
    public nint Pointer { get; }

    public Instance(nint pointer) => Pointer = pointer;

    public bool IsNull => Pointer == 0;

    /// <summary>Reads an instance variable by name.</summary>
    public RValue Get(string variable)
    {
        Loader.EnsureGameThread();
        if (IsNull) throw new InvalidOperationException("null instance");
        RValue v = RValue.Undefined;
        fixed (byte* n = Utf8.Get(variable))
        {
            if (Loader.Api->VarGet(Pointer, n, &v) == 0)
                throw new GmlException($"could not read '{variable}'");
        }
        return Values.Track(v);
    }

    /// <summary>Writes an instance variable by name.</summary>
    public void Set(string variable, RValue value)
    {
        Loader.EnsureGameThread();
        if (IsNull) throw new InvalidOperationException("null instance");
        fixed (byte* n = Utf8.Get(variable))
        {
            if (Loader.Api->VarSet(Pointer, n, &value) == 0)
                throw new GmlException($"could not write '{variable}'");
        }
    }

    public RValue this[string variable]
    {
        get => Get(variable);
        set => Set(variable, value);
    }

    public bool Equals(Instance other) => Pointer == other.Pointer;
    public override bool Equals(object? obj) => obj is Instance i && Equals(i);
    public override int GetHashCode() => Pointer.GetHashCode();
    public override string ToString() => IsNull ? "Instance(null)" : $"Instance(0x{Pointer:X})";
}
