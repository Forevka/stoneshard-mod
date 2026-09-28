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

    // null = not probed yet; whether string_copy can make runtime-owned strings here.
    private static bool? _canCopyStrings;

    /// <summary>
    /// A GML string holding <paramref name="text"/>, owned by the runtime and
    /// released with the frame's autorelease pool like any other value - so
    /// formatting a new string every frame is fine. Game thread only.
    /// </summary>
    /// <remarks>
    /// The runtime's string constructor keeps a pointer to the caller's
    /// characters, so the text is first wrapped around a temporary buffer and
    /// then copied by the game's own string_copy, which allocates its own. Only
    /// if a game lacks string_copy does the text fall back to being interned for
    /// the life of the process.
    /// </remarks>
    public static unsafe RValue FromString(string text)
    {
        Loader.EnsureGameThread();
        text ??= "";

        if (_canCopyStrings != false && Values.CanFree)
        {
            var bytes = Utf8.Encode(text);
            byte* buf = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)bytes.Length);
            try
            {
                bytes.AsSpan().CopyTo(new Span<byte>(buf, bytes.Length));
                RValue tmp = default;
                if (Loader.Api->SetString(&tmp, buf) != 0)
                {
                    RValue owned = RValue.Undefined;
                    // string_copy(s, 1, huge) is a full copy in every runtime seen:
                    // the count is clamped to the string's length.
                    RValue* args = stackalloc RValue[3];
                    args[0] = tmp;
                    args[1] = FromReal(1);
                    args[2] = FromReal(int.MaxValue);
                    int ok;
                    fixed (byte* name = Utf8.Get("string_copy"))
                        ok = Loader.Api->CallBuiltin(name, &owned, args, 3, 0, 0);
                    // A genuine copy is a different string; a runtime that answered
                    // a full-length copy with the same string (one more reference)
                    // would leave the result pointing at our temporary buffer.
                    bool copied = ok != 0 && owned.Kind == RValueKind.String && owned.Pointer != tmp.Pointer;
                    if (!copied && owned.Kind == RValueKind.String) Loader.Api->ValueFree(&owned);
                    Loader.Api->ValueFree(&tmp);   // the wrapper around our buffer

                    if (copied)
                    {
                        _canCopyStrings = true;
                        return Values.Track(owned);
                    }
                    // Only a failure with the runtime fully up is conclusive; an
                    // early call (builtins not resolved yet) just falls back once.
                    if (Game.BuiltinCount > 0)
                    {
                        if (_canCopyStrings == null)
                            Loader.Log(LogLevel.Warning, "CoreLoader", "string_copy unusable; strings from mods are interned instead");
                        _canCopyStrings = false;
                    }
                }
            }
            finally
            {
                System.Runtime.InteropServices.NativeMemory.Free(buf);
            }
        }

        RValue v = default;
        if (Loader.Api->SetString(&v, Utf8.Permanent(text)) == 0)
            throw new GmlException("the runtime's string constructor is unavailable in this game");
        return Values.Track(v);
    }

    /// <summary>
    /// A string whose characters stay valid for the life of the process and is
    /// not pooled. For the few places the runtime keeps a POINTER to the text,
    /// such as the name of a newly created variable.
    /// </summary>
    internal static unsafe RValue FromStringPermanent(string text)
    {
        RValue v = default;
        if (Loader.Api->SetString(&v, Utf8.Permanent(text ?? "")) == 0)
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
