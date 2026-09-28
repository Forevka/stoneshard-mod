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
                throw new GmlException($"call to {name} failed (see the loader log)");
        }
        return Values.Track(result);
    }

    /// <summary>Runs an object event (e.g. "gml_Object_o_x_Step_0") as <paramref name="self"/>.</summary>
    public static void CallEvent(string name, Instance self, Instance other = default)
    {
        Loader.EnsureGameThread();
        var fn = Resolve(name, "gml_Object_");
        if (Loader.Api->CallEvent(fn, self.Pointer, other.Pointer) == 0)
            throw new GmlException($"event {name} failed (see the loader log)");
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
        RValue result = RValue.Undefined;
        fixed (byte* n = Utf8.Get(name))
        fixed (RValue* a = args)
        {
            if (Loader.Api->CallBuiltin(n, &result, a, args.Length, self.Pointer, 0) == 0)
                throw new GmlException($"builtin {name} failed (see the loader log)");
        }
        return Values.Track(result);
    }

    // ---------------------------------------------------------- game thread

    private static readonly ConcurrentQueue<Action> Pending = new();

    /// <summary>Runs <paramref name="action"/> on the game thread at the start of the next frame.</summary>
    public static void RunOnGameThread(Action action) => Pending.Enqueue(action);

    internal static void DrainPending(Logger log)
    {
        while (Pending.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { log.Error("queued game-thread action threw", ex); }
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
