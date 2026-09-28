using System.Runtime.InteropServices;
using CoreLoader.Native;

namespace CoreLoader;

/// <summary>GameMaker value kinds, as the YYC runtime stores them at RValue+0xC.</summary>
public enum RValueKind
{
    Real = 0,
    String = 1,
    Array = 2,
    Pointer = 3,
    Undefined = 5,
    Object = 6,
    Int32 = 7,
    Int64 = 10,
    Bool = 13,
    Reference = 15,
    Unset = 0x00FFFFFF,
}

/// <summary>
/// A GameMaker value, laid out exactly like the runtime's own 16-byte RValue so
/// it can be passed to and from the game without conversion.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct RValue
{
    [FieldOffset(0)] public double Real;
    [FieldOffset(0)] public long Int64;
    [FieldOffset(0)] public int Int32;
    [FieldOffset(0)] public nint Pointer;
    [FieldOffset(8)] public int Flags;
    [FieldOffset(12)] public RValueKind Kind;

    public static RValue Undefined => new() { Kind = RValueKind.Undefined };

    public static RValue FromReal(double v) => new() { Real = v, Kind = RValueKind.Real };

    public static RValue FromBool(bool v) => new() { Real = v ? 1 : 0, Kind = RValueKind.Real };

    /// <summary>
    /// Builds a GML string through the runtime's own constructor. The text is
    /// interned for the life of the process, because the game keeps a pointer to it.
    /// Must be called on the game thread.
    /// </summary>
    public static unsafe RValue FromString(string text)
    {
        Loader.EnsureGameThread();
        RValue v = default;
        if (Loader.Api->SetString(&v, Utf8.Permanent(text)) == 0)
            throw new GmlException("the runtime's string constructor is unavailable in this game");
        return v;
    }

    public static implicit operator RValue(double v) => FromReal(v);
    public static implicit operator RValue(int v) => FromReal(v);
    public static implicit operator RValue(bool v) => FromBool(v);
    public static implicit operator RValue(string v) => FromString(v);

    public readonly bool IsUndefined => Kind is RValueKind.Undefined or RValueKind.Unset;

    public readonly bool IsNumber => Kind is RValueKind.Real or RValueKind.Int32 or RValueKind.Int64 or RValueKind.Bool;

    /// <summary>The value as a double; strings and references read as NaN.</summary>
    public readonly double AsReal => Kind switch
    {
        RValueKind.Real => Real,
        RValueKind.Int32 => Int32,
        // The runtime has been seen to hold bools both as a 0/1 double and as an
        // int in the low word; a double that is exactly 0 or 1 settles which.
        RValueKind.Bool => Real is 0.0 or 1.0 ? Real : Int32,
        RValueKind.Int64 => Int64,
        _ => double.NaN,
    };

    public readonly bool AsBool => IsNumber && AsReal > 0.5;

    /// <summary>Text through the runtime's own formatting. Game thread only.</summary>
    public override readonly unsafe string ToString()
    {
        if (!Loader.OnGameThread) return $"<kind {(int)Kind}>";
        var self = this;
        Span<byte> small = stackalloc byte[256];
        int len;
        fixed (byte* p = small) len = Loader.Api->ValueToString(&self, p, small.Length);
        if (len < small.Length) return System.Text.Encoding.UTF8.GetString(small[..len]);

        var big = new byte[len + 1];
        fixed (byte* p = big) Loader.Api->ValueToString(&self, p, big.Length);
        return System.Text.Encoding.UTF8.GetString(big, 0, len);
    }
}
