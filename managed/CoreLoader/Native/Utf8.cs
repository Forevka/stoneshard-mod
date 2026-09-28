using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreLoader.Native;

internal static unsafe class Utf8
{
    // UI labels and symbol names are the same few hundred strings every frame,
    // so their encodings are cached instead of re-allocated 60 times a second.
    // Bounded: a mod that formats a fresh string per frame just churns the cache.
    private static readonly Dictionary<string, byte[]> Cache = new();
    private const int CacheLimit = 4096;

    /// <summary>NUL-terminated UTF-8, cached. Pin with <c>fixed</c> for the call.</summary>
    public static byte[] Get(string s)
    {
        if (Cache.TryGetValue(s, out var b)) return b;
        b = Encode(s);
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[s] = b;
        return b;
    }

    public static byte[] Encode(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    public static string? Read(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);

    // Strings handed to the GML runtime are stored BY POINTER and may be kept
    // by the game (e.g. assigned to a variable), so their memory can never be
    // freed. Interning bounds that to one copy per distinct string.
    private static readonly ConcurrentDictionary<string, nint> Pinned = new();

    public static byte* Permanent(string s) =>
        (byte*)Pinned.GetOrAdd(s, static str =>
        {
            var bytes = Encode(str);
            var mem = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.AsSpan().CopyTo(new Span<byte>(mem, bytes.Length));
            return (nint)mem;
        });
}
