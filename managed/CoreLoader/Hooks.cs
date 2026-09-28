using System.Reflection;
using CoreLoader.Native;
using CoreLoader.Runtime;

namespace CoreLoader;

/// <summary>
/// One call of a hooked script or object event, as seen by a hook handler.
/// Only valid for the duration of the handler: do not keep it.
/// </summary>
public readonly unsafe struct HookCall
{
    private readonly CoreHookCall* _p;
    private readonly string _symbol;

    internal HookCall(CoreHookCall* p, string symbol)
    {
        _p = p;
        _symbol = symbol;
    }

    /// <summary>The hooked function, e.g. "gml_Script_scr_loot".</summary>
    public string Symbol => _symbol;

    /// <summary>The instance the call runs as.</summary>
    public Instance Self => new(_p->Self);

    public Instance Other => new(_p->Other);

    /// <summary>True in an After handler, when the original has already run (or was skipped).</summary>
    public bool IsAfter => _p->Phase == 1;

    /// <summary>True when a Before handler suppressed the original.</summary>
    public bool OriginalSkipped => _p->Skip != 0;

    /// <summary>Number of arguments the caller passed (0 for object events).</summary>
    public int ArgCount => _p->Argc;

    /// <summary>Reads argument <paramref name="index"/>.</summary>
    public RValue GetArg(int index)
    {
        CheckArg(index);
        return *_p->Args[index];
    }

    /// <summary>
    /// Replaces argument <paramref name="index"/> in place. In a Before handler
    /// this changes what the original receives.
    /// </summary>
    public void SetArg(int index, RValue value)
    {
        CheckArg(index);
        *_p->Args[index] = value;
    }

    /// <summary>
    /// The script's return value. In an After handler it holds what the original
    /// returned and can be replaced; in a Before handler that skips the original,
    /// set it to what the caller should receive. Scripts only.
    /// </summary>
    public RValue Result
    {
        get => _p->Result != null ? *_p->Result : RValue.Undefined;
        set
        {
            if (_p->Result == null) throw new InvalidOperationException("object events have no result");
            *_p->Result = value;
        }
    }

    /// <summary>
    /// Runs the original script once more with this call's self, other and
    /// (possibly modified) arguments, and returns its result. No hook handler -
    /// this one included - sees the extra call, so repeating an effect cannot
    /// recurse. Scripts only.
    /// </summary>
    public RValue CallOriginal()
    {
        RValue r = RValue.Undefined;
        if (Loader.Api->HookCallOriginal(_p, &r) == 0)
            throw new GmlException($"re-running {_symbol} failed (see the loader log)");
        return r;
    }

    /// <summary>Stops the original from running. Before handlers only.</summary>
    public void SkipOriginal()
    {
        if (IsAfter) throw new InvalidOperationException("SkipOriginal is only meaningful before the original runs");
        _p->Skip = 1;
    }

    private void CheckArg(int index)
    {
        if (_p->Args == null || (uint)index >= (uint)_p->Argc)
            throw new ArgumentOutOfRangeException(nameof(index), $"{_symbol} was called with {_p->Argc} argument(s)");
    }
}

/// <summary>Handles one hooked call.</summary>
public delegate void HookHandler(HookCall call);

/// <summary>A subscription; dispose it to unhook.</summary>
public sealed class HookHandle : IDisposable
{
    internal HookHandle(int hookId, Subscription sub)
    {
        HookId = hookId;
        Sub = sub;
    }

    internal int HookId { get; }
    internal Subscription Sub { get; }

    public void Dispose() => Hooks.Remove(this);
}

internal sealed class Subscription
{
    public required HookHandler Handler { get; init; }
    public required bool After { get; init; }
    public required LoadedMod? Owner { get; init; }
    public required int Order { get; init; }
}

/// <summary>
/// Run code before or after any script or object event in the game - the
/// equivalent of Harmony's Prefix/Postfix. Hooks are shared: however many mods
/// hook one function, it is detoured once.
/// </summary>
public static unsafe class Hooks
{
    private static readonly Logger Log = new("CoreLoader");
    private static readonly Dictionary<int, List<Subscription>> ById = new();
    private static readonly Dictionary<int, string> Names = new();
    private static int _order;

    /// <summary>Runs <paramref name="handler"/> before <paramref name="symbol"/>; it may change arguments or skip the original.</summary>
    public static HookHandle Before(string symbol, HookHandler handler) => Add(symbol, handler, after: false);

    /// <summary>Runs <paramref name="handler"/> after <paramref name="symbol"/>; it may read or replace the result.</summary>
    public static HookHandle After(string symbol, HookHandler handler) => Add(symbol, handler, after: true);

    /// <summary>Number of distinct functions the loader has detoured.</summary>
    public static int NativeHookCount => Loader.Api->HookCount();

    private static HookHandle Add(string symbol, HookHandler handler, bool after)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Loader.EnsureGameThread();

        string full = Resolve(symbol, out nint target, out int kind);
        int id = Loader.Api->HookInstall(target, kind);
        if (id < 0) throw new GmlException($"could not hook {full} (see the loader log)");

        var sub = new Subscription { Handler = handler, After = after, Owner = ModManager.Current, Order = _order++ };
        if (!ById.TryGetValue(id, out var list))
        {
            ById[id] = list = new List<Subscription>();
            Names[id] = full;
        }
        list.Add(sub);
        if (list.Count == 1) Loader.Api->HookSetManaged(id, 1);
        return new HookHandle(id, sub);
    }

    internal static void Remove(HookHandle h)
    {
        if (!ById.TryGetValue(h.HookId, out var list)) return;
        list.Remove(h.Sub);
        if (list.Count == 0) Loader.Api->HookSetManaged(h.HookId, 0);
    }

    /// <summary>Drops every subscription a faulted mod made.</summary>
    internal static void RemoveOwner(LoadedMod owner)
    {
        foreach (var (id, list) in ById)
        {
            if (list.RemoveAll(s => s.Owner == owner) > 0 && list.Count == 0)
                Loader.Api->HookSetManaged(id, 0);
        }
    }

    internal static int SubscriptionCount(LoadedMod owner) =>
        ById.Values.Sum(l => l.Count(s => s.Owner == owner));

    internal static void Dispatch(CoreHookCall* c)
    {
        Loader.MarkGameThread();
        if (!ById.TryGetValue(c->HookId, out var list) || list.Count == 0) return;

        bool after = c->Phase == 1;
        var call = new HookCall(c, Names[c->HookId]);
        // Snapshot: a handler may add or remove hooks while we iterate.
        foreach (var s in list.ToArray())
        {
            if (s.After != after) continue;
            if (s.Owner is { State: ModState.Faulted }) continue;
            try
            {
                s.Handler(call);
            }
            catch (Exception ex)
            {
                if (s.Owner != null)
                    ModManager.Fault(s.Owner, $"hook on {call.Symbol} threw {ex.GetType().Name}: {ex.Message}", ex);
                else
                    Log.Error($"hook on {call.Symbol} threw", ex);
            }
        }
    }

    private static string Resolve(string symbol, out nint target, out int kind)
    {
        string full = symbol;
        target = Game.FindSymbol(symbol);
        if (target == 0 && !symbol.StartsWith("gml_", StringComparison.Ordinal))
        {
            full = "gml_Script_" + symbol;
            target = Game.FindSymbol(full);
        }
        if (target == 0) throw new GmlException($"{symbol} does not exist in {Game.Name}");

        if (full.StartsWith("gml_Object_", StringComparison.Ordinal)) kind = 1;
        else if (full.StartsWith("gml_Script_", StringComparison.Ordinal) ||
                 full.StartsWith("gml_GlobalScript_", StringComparison.Ordinal)) kind = 0;
        else throw new GmlException($"{full}: only gml_Script_* and gml_Object_* functions can be hooked");
        return full;
    }

    /// <summary>
    /// Subscribes every [HookBefore]/[HookAfter] method in a mod's assembly.
    /// Static methods anywhere; instance methods on the mod class itself.
    /// </summary>
    internal static void AttachAttributes(LoadedMod m)
    {
        var asm = m.Instance.GetType().Assembly;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        foreach (var type in asm.GetTypes())
        {
            foreach (var method in type.GetMethods(all))
            {
                foreach (var attr in method.GetCustomAttributes<HookAttribute>())
                {
                    object? target = null;
                    if (!method.IsStatic)
                    {
                        if (!type.IsInstanceOfType(m.Instance))
                            throw new InvalidOperationException(
                                $"{type.Name}.{method.Name}: instance hook methods must be on the mod class");
                        target = m.Instance;
                    }
                    var ps = method.GetParameters();
                    if (method.ReturnType != typeof(void) || ps.Length != 1 || ps[0].ParameterType != typeof(HookCall))
                        throw new InvalidOperationException(
                            $"{type.Name}.{method.Name}: a hook method must be void M(HookCall call)");

                    var handler = (HookHandler)method.CreateDelegate(typeof(HookHandler), target);
                    if (attr.After) After(attr.Symbol, handler);
                    else Before(attr.Symbol, handler);
                }
            }
        }
    }
}

/// <summary>Base for the hook attributes.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public abstract class HookAttribute : Attribute
{
    public string Symbol { get; }
    internal abstract bool After { get; }

    protected HookAttribute(string symbol) => Symbol = symbol;
}

/// <summary>Runs the method before the named script or event: <c>void M(HookCall call)</c>.</summary>
public sealed class HookBeforeAttribute : HookAttribute
{
    public HookBeforeAttribute(string symbol) : base(symbol) { }
    internal override bool After => false;
}

/// <summary>Runs the method after the named script or event: <c>void M(HookCall call)</c>.</summary>
public sealed class HookAfterAttribute : HookAttribute
{
    public HookAfterAttribute(string symbol) : base(symbol) { }
    internal override bool After => true;
}
