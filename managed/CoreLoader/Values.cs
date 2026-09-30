using CoreLoader.Native;

namespace CoreLoader;

/// <summary>
/// Lifetime of GML values that hold a reference (strings, arrays, structs).
///
/// Every such value the game hands a mod - call results, variable reads,
/// <see cref="RValue.FromString"/>, <see cref="HookCall.CallOriginal"/> - is
/// placed in a per-frame autorelease pool and released at the end of the frame.
/// Using a value within the frame (reading it, passing it to another call,
/// assigning it to a variable, which makes the game take its own reference) is
/// therefore always safe and never leaks. Only a value kept across frames needs
/// <see cref="Keep"/>, and then an explicit <see cref="Free"/> when done.
/// </summary>
public static unsafe class Values
{
    private static readonly List<RValue> Pool = new();

    /// <summary>Whether this runtime's free helper was found (else values are never released).</summary>
    public static bool CanFree { get; internal set; }

    /// <summary>Whether this runtime's copy helper was found.</summary>
    public static bool CanCopy { get; internal set; }

    /// <summary>Values waiting in this frame's pool.</summary>
    public static int Pending => Pool.Count;

    internal static bool HoldsReference(in RValue v) =>
        v.Kind is RValueKind.String or RValueKind.Array or RValueKind.Object;

    /// <summary>Registers a value the game just handed over; returns it unchanged.</summary>
    internal static RValue Track(RValue v)
    {
        if (CanFree && HoldsReference(v)) Pool.Add(v);
        return v;
    }

    /// <summary>
    /// Returns a reference to <paramref name="v"/> that survives the frame; the
    /// caller owns it and must <see cref="Free"/> it later. A pooled value is
    /// taken out of the pool; a value the mod does not own (a hook argument, a
    /// result slot) gets an independent copy, so freeing it never releases the
    /// game's reference.
    /// </summary>
    public static RValue Keep(RValue v)
    {
        if (!HoldsReference(v)) return v;
        // Nothing is ever released on a runtime without a free helper: every
        // value already outlives the frame, and there is no copy to make.
        if (!CanFree) return v;
        if (TakeFromPool(v))
        {
            if (v.Kind == RValueKind.Object) Root(v);
            return v;
        }
        return Copy(v);
    }

    // Structs are garbage-collected, not reference-counted: a pointer held in
    // C# is invisible to the collector, which frees the struct as soon as GML
    // stops using it. A kept struct is therefore also pushed into a GML array
    // held in a global, where the collector sees it; Free takes it out again.
    // The C# side keeps its own list too - who rooted what - so a mod that
    // unloads without freeing loses its roots, and roots wiped with the game's
    // globals (game_restart, a game resetting globals) are pushed again.
    private const string RootsName = "__coreloader_roots";
    private static bool? _canRoot;
    private static readonly List<(RValue Value, Runtime.LoadedMod? Owner)> Rooted = new();

    private static bool CanRoot => _canRoot ??=
        Game.BuiltinArity("array_push") != null && Game.BuiltinArity("array_delete") != null &&
        Game.BuiltinArity("array_create") != null;

    private static RValue Roots(bool create)
    {
        if (Globals.Exists(RootsName))
        {
            var r = Globals.Get(RootsName);
            if (r.Kind == RValueKind.Array) return r;
        }
        if (!create) return RValue.Undefined;
        Globals.Set(RootsName, Game.CallBuiltin("array_create", 0));
        var fresh = Globals.Get(RootsName);
        // The array was gone: everything still tracked goes back in.
        if (fresh.Kind == RValueKind.Array)
            foreach (var (value, _) in Rooted) Game.CallBuiltin("array_push", fresh, value);
        return fresh;
    }

    private static void Root(RValue v)
    {
        if (!CanRoot) return;
        try
        {
            var roots = Roots(create: true);
            if (roots.Kind == RValueKind.Array) Game.CallBuiltin("array_push", roots, v);
            Rooted.Add((v, Runtime.ModManager.Current));
        }
        catch (GmlException ex)
        {
            Loader.Log(LogLevel.Warning, "Lodestone", $"could not root a kept struct: {ex.Message}");
        }
    }

    private static void Unroot(RValue v)
    {
        if (!CanRoot) return;
        int tracked = Rooted.FindIndex(r => r.Value.Pointer == v.Pointer);
        if (tracked < 0) return;
        Rooted.RemoveAt(tracked);
        try
        {
            var roots = Roots(create: false);
            if (roots.Kind != RValueKind.Array) return;
            for (int i = Gml.ArrayLength(roots) - 1; i >= 0; i--)
            {
                if (Gml.ArrayGet(roots, i).Pointer != v.Pointer) continue;
                Game.CallBuiltin("array_delete", roots, i, 1);
                return;
            }
        }
        catch (GmlException) { }
    }

    /// <summary>A mod going away releases the structs it kept and never freed.</summary>
    internal static void RemoveOwner(Runtime.LoadedMod owner)
    {
        foreach (var (value, _) in Rooted.Where(r => r.Owner == owner).ToList())
        {
            try { Unroot(value); }
            catch (Exception ex) { Loader.Log(LogLevel.Warning, "Lodestone", $"releasing a kept struct: {ex.Message}"); }
        }
    }

    /// <summary>Number of kept structs rooted for the collector (diagnostics).</summary>
    public static int RootedStructs => Rooted.Count;

    /// <summary>
    /// Releases a value obtained with <see cref="Keep"/> (or <see cref="Copy"/>).
    /// A value still in this frame's pool may be released early with it too.
    /// Never free what the game lends you - a hook's arguments or result.
    /// </summary>
    public static void Free(ref RValue v)
    {
        Loader.EnsureGameThread();
        if (!HoldsReference(v)) { v = RValue.Undefined; return; }
        // Still pooled: the pool would release it again at the end of the
        // frame. Taking it out makes this the one release.
        TakeFromPool(v);
        if (v.Kind == RValueKind.Object) Unroot(v);
        fixed (RValue* p = &v) Loader.Api->ValueFree(p);
        v = RValue.Undefined;
    }

    /// <summary>
    /// A second, independently owned reference to the same value (not pooled):
    /// free it with <see cref="Free"/>. Numbers are returned as they are.
    /// </summary>
    public static RValue Copy(RValue src)
    {
        Loader.EnsureGameThread();
        if (!HoldsReference(src)) return src;
        if (!CanCopy) throw new GmlException("this runtime's copy helper was not found");
        RValue dst = RValue.Undefined;
        if (Loader.Api->ValueCopy(&dst, &src) == 0) throw new GmlException("copying the value failed");
        if (dst.Kind == RValueKind.Object) Root(dst);
        return dst;
    }

    /// <summary>
    /// Stores <paramref name="value"/> into a slot the game owns (an argument or
    /// a result), releasing what was there. The slot gets its own reference, so
    /// the pool releasing the mod's copy later cannot free the slot's value.
    /// </summary>
    internal static void StoreInto(RValue* slot, RValue value, bool releaseOld = true)
    {
        // Take the new reference FIRST: `value` may be the very value in the
        // slot (c.Result = c.Result), and releasing the slot before copying
        // would free it out from under the copy.
        RValue stored = value;
        bool copied = false;
        if (HoldsReference(value) && CanCopy)
        {
            stored = RValue.Undefined;
            copied = Loader.Api->ValueCopy(&stored, &value) != 0;
            if (!copied) stored = value;
        }

        RValue old = *slot;
        *slot = stored;
        if (releaseOld && CanFree && HoldsReference(old)) Loader.Api->ValueFree(&old);

        // Without a copy helper the reference is handed over instead: the value
        // leaves the pool so it is not released under the game.
        if (!copied && HoldsReference(value)) TakeFromPool(value);
    }

    private static bool TakeFromPool(RValue v)
    {
        for (int i = Pool.Count - 1; i >= 0; i--)
        {
            if (Pool[i].Pointer == v.Pointer && Pool[i].Kind == v.Kind)
            {
                Pool.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Re-reads whether the runtime's (verified) free/copy helpers are available.</summary>
    internal static void Probe()
    {
        RValue a = RValue.Undefined, b = RValue.Undefined;
        CanFree = Loader.Api->ValueFree(&a) != 0;
        CanCopy = Loader.Api->ValueCopy(&b, &a) != 0;
    }

    /// <summary>End of frame: releases everything still pooled.</summary>
    internal static void Drain()
    {
        if (Pool.Count == 0) return;
        for (int i = 0; i < Pool.Count; i++)
        {
            var v = Pool[i];
            Loader.Api->ValueFree(&v);
        }
        Pool.Clear();
    }
}
