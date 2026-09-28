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
        for (int i = Pool.Count - 1; i >= 0; i--)
        {
            if (Pool[i].Pointer == v.Pointer && Pool[i].Kind == v.Kind)
            {
                Pool.RemoveAt(i);
                return v;
            }
        }
        return Copy(v);
    }

    /// <summary>Releases a value obtained with <see cref="Keep"/> (or <see cref="Copy"/>).</summary>
    public static void Free(ref RValue v)
    {
        Loader.EnsureGameThread();
        if (!HoldsReference(v)) { v = RValue.Undefined; return; }
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

    private static void TakeFromPool(RValue v)
    {
        for (int i = Pool.Count - 1; i >= 0; i--)
        {
            if (Pool[i].Pointer == v.Pointer && Pool[i].Kind == v.Kind)
            {
                Pool.RemoveAt(i);
                return;
            }
        }
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
