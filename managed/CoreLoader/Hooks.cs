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
    /// <remarks>
    /// Scripts with managed handlers are called with private copies of their
    /// arguments, so the change reaches the original (and after-handlers) and
    /// nothing else - not the caller's variables, and not the constants YYC
    /// passes literals from. On a runtime without the value helpers there are
    /// no copies: the slot is the caller's, and what was in it is not released.
    /// </remarks>
    public void SetArg(int index, RValue value)
    {
        CheckArg(index);
        Values.StoreInto(_p->Args[index], value, releaseOld: Values.CanCopy && Values.CanFree);
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
            // Before the original runs, the result slot holds whatever the
            // caller left in its temporary - not necessarily an owned value -
            // so it is overwritten, not released.
            Values.StoreInto(_p->Result, value, releaseOld: IsAfter);
        }
    }

    /// <summary>
    /// Runs the original script once more with this call's self, other and
    /// (possibly modified) arguments, and returns its result. No hook handler -
    /// this one included - sees the extra call, so repeating an effect cannot
    /// recurse. Scripts only. The returned value is a plain copy: if it holds a
    /// string, array or struct, nothing releases that reference.
    /// </summary>
    public RValue CallOriginal()
    {
        RValue r = RValue.Undefined;
        if (Loader.Api->HookCallOriginal(_p, &r) == 0)
            throw new GmlException($"re-running {_symbol} failed (see the loader log)");
        return Values.Track(r);
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

/// <summary>
/// A one-shot request from <see cref="Hooks.NextBefore"/> / <see cref="Hooks.NextAfter"/>.
/// Dispose it to cancel; nothing is called after that.
/// </summary>
public sealed class NextCall : IDisposable
{
    internal NextCall(string symbol, long deadline, Action? onTimeout, LoadedMod? owner)
    {
        Symbol = symbol;
        Deadline = deadline;
        OnTimeout = onTimeout;
        Owner = owner;
    }

    /// <summary>The hooked function, as it was asked for.</summary>
    public string Symbol { get; }

    /// <summary>True until a matching call is handled, the request times out, or it is cancelled.</summary>
    public bool IsPending { get; internal set; } = true;

    internal long Deadline { get; }
    internal Action? OnTimeout { get; }
    internal LoadedMod? Owner { get; }
    internal HookHandle? Hook { get; set; }

    public void Dispose() => Hooks.Cancel(this);
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
    // Copy-on-write: a hooked Step event can dispatch thousands of times a
    // frame, so dispatch reads the array as it is - no snapshot allocated per
    // call - and add/remove replace it (a handler adding or removing hooks
    // mid-dispatch leaves the array being iterated untouched).
    private static readonly Dictionary<int, Subscription[]> ById = new();
    private static readonly Dictionary<int, string> Names = new();
    private static int _order;

    /// <summary>Runs <paramref name="handler"/> before <paramref name="symbol"/>; it may change arguments or skip the original.</summary>
    public static HookHandle Before(string symbol, HookHandler handler) => Add(symbol, handler, after: false);

    /// <summary>Runs <paramref name="handler"/> after <paramref name="symbol"/>; it may read or replace the result.</summary>
    public static HookHandle After(string symbol, HookHandler handler) => Add(symbol, handler, after: true);

    /// <summary>
    /// Runs <paramref name="action"/> once, before the next call of
    /// <paramref name="symbol"/> that <paramref name="match"/> accepts (null
    /// accepts any), then unhooks. If none comes within <paramref name="timeout"/>,
    /// the request is dropped and <paramref name="onTimeout"/> runs instead.
    /// </summary>
    /// <remarks>
    /// For work that only succeeds inside the game's own event: some scripts
    /// throw when called from outside the event frame they were written for, so
    /// the mod arms a request, makes the game run that event (spawn the object,
    /// set its alarm), and does the work from inside it.
    ///
    /// The request belongs to the calling mod and goes with it on unload, fault
    /// or hot reload. <paramref name="match"/> and <paramref name="action"/> run
    /// as hook handlers, so an exception from either faults the mod exactly as a
    /// throwing hook does; catch inside them to report a failure yourself. The
    /// request is disarmed before <paramref name="action"/> runs, so it fires at
    /// most once even if the action throws or makes the game call the symbol
    /// again. The timeout is checked every frame as well as on each call, so
    /// <paramref name="onTimeout"/> runs on time even if the symbol is never
    /// called; an exception from it faults the mod too.
    /// </remarks>
    public static NextCall NextBefore(string symbol, Func<HookCall, bool>? match, HookHandler action,
                                      TimeSpan timeout, Action? onTimeout = null) =>
        Next(symbol, match, action, timeout, onTimeout, after: false);

    /// <summary>
    /// Runs <paramref name="action"/> once, after the next call of
    /// <paramref name="symbol"/> that <paramref name="match"/> accepts. See
    /// <see cref="NextBefore"/> for the rules.
    /// </summary>
    public static NextCall NextAfter(string symbol, Func<HookCall, bool>? match, HookHandler action,
                                     TimeSpan timeout, Action? onTimeout = null) =>
        Next(symbol, match, action, timeout, onTimeout, after: true);

    /// <summary>Number of distinct functions the loader has detoured.</summary>
    public static int NativeHookCount => Loader.Api->HookCount();

    // The armed one-shot requests, for the per-frame timeout check.
    private static readonly List<NextCall> Requests = new();

    private static NextCall Next(string symbol, Func<HookCall, bool>? match, HookHandler action,
                                 TimeSpan timeout, Action? onTimeout, bool after)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "the timeout must be positive");
        Loader.EnsureGameThread();

        // Owned by the mod whose code this is, not by CoreLoader, where the
        // wrapper below is compiled.
        var owner = ModManager.OwnerOf(action);
        var req = new NextCall(symbol, Environment.TickCount64 + (long)timeout.TotalMilliseconds, onTimeout, owner);
        req.Hook = Add(symbol, call =>
        {
            if (!req.IsPending) return;
            if (Environment.TickCount64 > req.Deadline)
            {
                Expire(req);
                return;
            }
            if (match != null && !match(call)) return;
            Finish(req);
            action(call);
        }, after, owner);
        Requests.Add(req);
        return req;
    }

    private static void Finish(NextCall req)
    {
        req.IsPending = false;
        Requests.Remove(req);
        if (req.Hook != null) Remove(req.Hook);
    }

    private static void Expire(NextCall req)
    {
        Finish(req);
        if (req.OnTimeout is not { } onTimeout) return;
        if (req.Owner == null)
        {
            try { onTimeout(); }
            catch (Exception ex) { Log.Error($"timeout handler for {req.Symbol} threw", ex); }
            return;
        }
        if (req.Owner.State == ModState.Faulted || !ModManager.Mods.Contains(req.Owner)) return;
        ModManager.Invoke(req.Owner, $"timeout handler for {req.Symbol}", _ => onTimeout());
    }

    internal static void Cancel(NextCall req)
    {
        // Same reason as Remove: the request list belongs to the game thread.
        if (!Loader.OnGameThread)
        {
            Game.RunOnGameThread(() => Cancel(req));
            return;
        }
        if (req.IsPending) Finish(req);
    }

    /// <summary>Called every frame on the game thread: drops the requests whose time is up.</summary>
    internal static void TickRequests()
    {
        if (Requests.Count == 0) return;
        long now = Environment.TickCount64;
        foreach (var req in Requests.ToList())
            if (req.IsPending && now > req.Deadline) Expire(req);
    }

    private static HookHandle Add(string symbol, HookHandler handler, bool after) =>
        Add(symbol, handler, after, ModManager.OwnerOf(handler));

    private static HookHandle Add(string symbol, HookHandler handler, bool after, LoadedMod? owner)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Loader.EnsureGameThread();

        string full = Resolve(symbol, out nint target, out int kind);
        int id = Loader.Api->HookInstall(target, kind);
        if (id < 0) throw new GmlException($"could not hook {full} (see the loader log)");

        var sub = new Subscription { Handler = handler, After = after, Owner = owner, Order = _order++ };
        var list = ById.TryGetValue(id, out var existing) ? existing : Array.Empty<Subscription>();
        Names[id] = full;
        ById[id] = [.. list, sub];
        if (list.Length == 0) { Loader.Api->HookEnable(id, 1); Loader.Api->HookSetManaged(id, 1); }
        return new HookHandle(id, sub);
    }

    // Last subscriber gone: stop the managed transition and detach the detour,
    // so the function runs at full speed (the loader keeps its own users attached).
    private static void Release(int id)
    {
        Loader.Api->HookSetManaged(id, 0);
        Loader.Api->HookEnable(id, 0);
    }

    internal static void Remove(HookHandle h)
    {
        // A handle disposed from a Task, a finalizer or `using` in async code:
        // the subscription lists and the native detour belong to the game thread.
        if (!Loader.OnGameThread)
        {
            Game.RunOnGameThread(() => Remove(h));
            return;
        }
        if (!ById.TryGetValue(h.HookId, out var list)) return;
        var rest = Array.FindAll(list, s => s != h.Sub);
        if (rest.Length == list.Length) return;
        ById[h.HookId] = rest;
        if (rest.Length == 0) Release(h.HookId);
    }

    /// <summary>Drops every subscription a mod made (it faulted or is unloading).</summary>
    internal static void RemoveOwner(LoadedMod owner)
    {
        // Its one-shot requests end silently: no timeout handler runs for a
        // mod that is going away. Their subscriptions go with the rest below.
        foreach (var req in Requests.Where(r => r.Owner == owner).ToList())
        {
            req.IsPending = false;
            Requests.Remove(req);
        }

        foreach (var (id, list) in ById.ToList())
        {
            var rest = Array.FindAll(list, s => s.Owner != owner);
            if (rest.Length == list.Length) continue;
            ById[id] = rest;
            if (rest.Length == 0) Release(id);
        }
    }

    internal static int SubscriptionCount(LoadedMod owner) =>
        ById.Values.Sum(l => l.Count(s => s.Owner == owner));

    internal static void Dispatch(CoreHookCall* c)
    {
        // No MarkGameThread here: the game thread is named by Frame/Gui (and
        // natively at Present). A hooked function reached from some other
        // thread must not be able to move that mark and switch off the guard.
        if (!ById.TryGetValue(c->HookId, out var list) || list.Length == 0) return;

        bool after = c->Phase == 1;
        var call = new HookCall(c, Names[c->HookId]);
        foreach (var s in list)
        {
            if (s.After != after) continue;
            if (s.Owner is { State: ModState.Faulted }) continue;
            // The handler runs AS its mod: anything it registers (a hook added
            // lazily from inside a hook) belongs to it - not to whichever mod's
            // callback happened to make the game run this function.
            var previous = ModManager.Current;
            ModManager.Current = s.Owner;
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
            finally
            {
                ModManager.Current = previous;
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

        // Scripts take (self, other, result, argc, args); object events, room
        // creation code and 2.3+ global-script initialisers take (self, other).
        if (full.StartsWith("gml_Object_", StringComparison.Ordinal) ||
            full.StartsWith("gml_RoomCC_", StringComparison.Ordinal) ||
            full.StartsWith("gml_GlobalScript_", StringComparison.Ordinal)) kind = 1;
        else if (full.StartsWith("gml_Script_", StringComparison.Ordinal)) kind = 0;
        else throw new GmlException($"{full}: only gml_Script_*, gml_Object_*, gml_RoomCC_* and gml_GlobalScript_* functions can be hooked");
        return full;
    }

    /// <summary>
    /// Subscribes every [HookBefore]/[HookAfter] method in a mod's assembly.
    /// Static methods anywhere; instance methods on the mod class itself.
    /// </summary>
    internal static void AttachAttributes(LoadedMod m)
    {
        var asm = m.Instance.GetType().Assembly;
        // DeclaredOnly: a hook method on a base class is found once, on the
        // class that declares it - not again on every class deriving from it.
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                 BindingFlags.Instance | BindingFlags.DeclaredOnly;
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
