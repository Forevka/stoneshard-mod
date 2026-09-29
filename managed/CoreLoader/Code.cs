using System.Text;
using CoreLoader.Native;

namespace CoreLoader;

/// <summary>What a compiled GML function is made of, as far as its machine code tells.</summary>
/// <param name="Name">The symbol, e.g. "gml_Object_o_player_Step_0".</param>
/// <param name="Address">Its entry point.</param>
/// <param name="Size">Bytes up to the next known function (an upper bound).</param>
/// <param name="ArgumentCount">Arguments a script reads (0 for events, or unknown).</param>
/// <param name="Calls">Named functions it calls - scripts, events and builtins - in first-call order.</param>
/// <param name="Strings">String literals it references, in order of first use.</param>
public sealed record CodeInfo(string Name, nint Address, int Size, int ArgumentCount,
                              IReadOnlyList<string> Calls, IReadOnlyList<string> Strings);

/// <summary>
/// Read-only views of the game's compiled code: what a script or event calls
/// and which strings it uses, and who calls it. YYC compiles GML to native
/// code, so there is no source to show - but the call graph and the strings go
/// a long way towards knowing what to hook. Game thread only.
/// </summary>
public static unsafe class Code
{
    // Scanning stops at the next function, or at this many bytes.
    private const int MaxBytes = 256 * 1024;

    private static nint[]? _starts;
    private static byte[] _buffer = Array.Empty<byte>();
    private static Dictionary<nint, string>? _names;
    private static readonly Dictionary<string, CodeInfo> Cache = new(StringComparer.Ordinal);

    /// <summary>Describes a gml_* function (the gml_Script_ prefix is optional). Cached.</summary>
    public static CodeInfo? Describe(string symbol)
    {
        Loader.EnsureGameThread();
        string name = symbol;
        nint addr = Game.FindSymbol(name);
        if (addr == 0 && !name.StartsWith("gml_", StringComparison.Ordinal))
            addr = Game.FindSymbol(name = "gml_Script_" + symbol);
        if (addr == 0) return null;
        if (Cache.TryGetValue(name, out var hit)) return hit;

        var bytes = Read(addr, out int size);
        var info = new CodeInfo(name, addr, size,
                                name.StartsWith("gml_Object_", StringComparison.Ordinal) ? 0 : Runtime.CodeScan.Scan(bytes),
                                CallTargets(addr, bytes).Where(n => n != name).Distinct().ToList(),
                                StringRefs(addr, bytes).Where(s => s != name).Distinct().ToList());
        return Cache[name] = info;
    }

    /// <summary>
    /// Functions whose code calls <paramref name="symbol"/> - a script, event
    /// or builtin. Scans every function, so it takes a moment in a large game:
    /// each call works for at most <paramref name="millisecondBudget"/> ms,
    /// resumed from <paramref name="cursor"/> (start at 0; finished when it
    /// comes back as -1). Call it once a frame until then.
    /// </summary>
    public static IReadOnlyList<string> FindCallers(string symbol, ref int cursor, double millisecondBudget = 4)
    {
        Loader.EnsureGameThread();
        nint target = Game.FindSymbol(symbol);
        int registry = -1;
        if (target == 0)
        {
            target = BuiltinAddress(symbol);
            registry = RegistryIndexOf(symbol);
        }
        var found = new List<string>();
        if ((target == 0 && registry < 0) || cursor < 0) { cursor = -1; return found; }

        var all = Game.Symbols;
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                        (long)(millisecondBudget * System.Diagnostics.Stopwatch.Frequency / 1000);
        int i = cursor;
        for (; i < all.Count; i++)
        {
            // Checked every few functions: reading the clock costs more than a small function.
            if ((i & 15) == 0 && i > cursor && System.Diagnostics.Stopwatch.GetTimestamp() > deadline) break;
            var s = all[i];
            if (s.Address == 0) continue;
            var bytes = Read(s.Address, out _);
            if ((target != 0 && CallsTo(s.Address, bytes, target)) ||
                (registry >= 0 && CallsBuiltin(s.Address, bytes, registry)))
                found.Add(s.Name);
        }
        cursor = i >= all.Count ? -1 : i;
        return found;
    }

    /// <summary>The native function behind a builtin, or 0.</summary>
    public static nint BuiltinAddress(string name)
    {
        fixed (byte* n = Utf8.Get(name)) return Loader.Api->BuiltinAddress(n);
    }

    // ------------------------------------------------------------------ bytes

    private static ReadOnlySpan<byte> Read(nint addr, out int size)
    {
        _starts ??= Game.Symbols.Select(s => s.Address).Where(a => a != 0).Distinct().OrderBy(a => a).ToArray();
        int at = Array.BinarySearch(_starts, addr);
        nint next = at >= 0 && at + 1 < _starts.Length ? _starts[at + 1] : 0;
        int limit = next > addr ? (int)Math.Min(next - addr, MaxBytes) : 4096;

        // One buffer, reused: a callers search reads tens of thousands of
        // functions, and every caller consumes the span before the next read.
        if (_buffer.Length < limit) _buffer = new byte[Math.Max(limit, 64 * 1024)];
        var buf = _buffer;
        int len = limit;
        fixed (byte* p = buf)
        {
            // The last function in .text may not have a full window behind it.
            while (len >= 16 && Loader.Api->MemoryRead(addr, p, len) == 0) len /= 2;
        }
        size = len < 16 ? 0 : len;
        return new ReadOnlySpan<byte>(buf, 0, size);
    }

    private static Dictionary<nint, string> Names()
    {
        if (_names != null) return _names;
        var map = new Dictionary<nint, string>();
        foreach (var s in Game.Symbols)
            if (s.Address != 0) map.TryAdd(s.Address, s.Name);
        foreach (var b in Game.Builtins)
        {
            var a = BuiltinAddress(b);
            if (a != 0) map.TryAdd(a, b);
        }
        return _names = map;
    }

    // YYC calls most builtins through one runner helper that takes the
    // builtin's position in the runner's registry as its 5th argument, loaded
    // from a global the runner fills at startup:
    //     mov  r32, [rip+global]     ; the registry index
    //     mov  [rsp+20h], r32        ; 5th argument
    //     ...  call helper
    // Returns the registry index when such a load starts at b[i], else -1.
    private static int LegacyBuiltinIndex(nint addr, ReadOnlySpan<byte> b, int i)
    {
        int p = i;
        bool rexR = false;
        if (b[p] is 0x44 or 0x40) { rexR = b[p] == 0x44; p++; }
        if (p + 6 > b.Length || b[p] != 0x8B || (b[p + 1] & 0xC7) != 0x05) return -1;
        int reg = (b[p + 1] >> 3) & 7;
        nint global = addr + p + 6 + BitConverter.ToInt32(b.Slice(p + 2, 4));
        // The store to [rsp+20h] from the same register follows closely; the
        // compiler may put a stack adjustment or other argument stores between.
        bool stored = false;
        for (int q = p + 6; q + 5 <= b.Length && q < p + 6 + 24 && !stored; q++)
        {
            int s = q;
            if (rexR) { if (b[s] != 0x44) continue; s++; }
            stored = b[s] == 0x89 && b[s + 1] == (0x44 | (reg << 3)) && b[s + 2] == 0x24 && b[s + 3] == 0x20;
        }
        if (!stored) return -1;

        int index;
        if (Loader.Api->MemoryRead(global, (byte*)&index, 4) == 0) return -1;
        return BuiltinNameAt(index) != null ? index : -1;
    }

    private static readonly Dictionary<int, string?> NameAtCache = new();

    /// <summary>The builtin at a runner-registry index, or null.</summary>
    internal static string? BuiltinNameAt(int index)
    {
        if (index < 0 || index > 100_000) return null;
        if (NameAtCache.TryGetValue(index, out var n)) return n;
        return NameAtCache[index] = Utf8.Read(Loader.Api->BuiltinNameAt(index));
    }

    private static Dictionary<string, int>? _registryIndex;

    private static int RegistryIndexOf(string builtin)
    {
        if (_registryIndex == null)
        {
            _registryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            // Skipped registry rows read as null too, so stop only after a long run of them.
            for (int i = 0, misses = 0; misses < 256 && i < 100_000; i++)
            {
                var n = BuiltinNameAt(i);
                if (n == null) { misses++; continue; }
                misses = 0;
                _registryIndex.TryAdd(n, i);
            }
        }
        return _registryIndex.TryGetValue(builtin, out var idx) ? idx : -1;
    }

    // call rel32, call [rip+disp32] through a pointer (imports and some
    // runtime functions), and builtins called by registry index.
    private static IEnumerable<string> CallTargets(nint addr, ReadOnlySpan<byte> b)
    {
        var names = Names();
        var list = new List<string>();
        for (int i = 0; i + 5 <= b.Length; i++)
        {
            int legacy = LegacyBuiltinIndex(addr, b, i);
            if (legacy >= 0 && BuiltinNameAt(legacy) is { } bn) { list.Add(bn); continue; }

            nint target = 0;
            if (b[i] == 0xE8)
            {
                target = addr + i + 5 + BitConverter.ToInt32(b.Slice(i + 1, 4));
            }
            else if (b[i] == 0xFF && i + 6 <= b.Length && b[i + 1] == 0x15)
            {
                nint slot = addr + i + 6 + BitConverter.ToInt32(b.Slice(i + 2, 4));
                nint p = 0;
                if (Loader.Api->MemoryRead(slot, (byte*)&p, sizeof(nint)) != 0) target = p;
            }
            if (target != 0 && names.TryGetValue(target, out var n)) list.Add(n);
        }
        return list;
    }

    private static bool CallsTo(nint addr, ReadOnlySpan<byte> b, nint target)
    {
        for (int i = 0; i + 5 <= b.Length; i++)
        {
            if (b[i] == 0xE8 && addr + i + 5 + BitConverter.ToInt32(b.Slice(i + 1, 4)) == target) return true;
        }
        return false;
    }

    private static bool CallsBuiltin(nint addr, ReadOnlySpan<byte> b, int registryIndex)
    {
        for (int i = 0; i + 12 <= b.Length; i++)
        {
            // Cheap pre-check before the full pattern: a mov r32,[rip+x].
            if (b[i] != 0x8B && !(b[i] is 0x44 or 0x40 && b[i + 1] == 0x8B)) continue;
            if (LegacyBuiltinIndex(addr, b, i) == registryIndex) return true;
        }
        return false;
    }

    // lea r64, [rip+disp32] pointing at printable, NUL-terminated text.
    private static IEnumerable<string> StringRefs(nint addr, ReadOnlySpan<byte> b)
    {
        var list = new List<string>();
        var text = new byte[256];
        for (int i = 0; i + 7 <= b.Length; i++)
        {
            if (b[i] is not (0x48 or 0x4C) || b[i + 1] != 0x8D || (b[i + 2] & 0xC7) != 0x05) continue;
            nint p = addr + i + 7 + BitConverter.ToInt32(b.Slice(i + 3, 4));
            int got;
            fixed (byte* t = text)
            {
                got = text.Length;
                while (got >= 8 && Loader.Api->MemoryRead(p, t, got) == 0) got /= 2;
            }
            if (got < 8) continue;
            int end = Array.IndexOf(text, (byte)0, 0, got);
            if (end < 3) continue;
            bool printable = true;
            for (int k = 0; k < end && printable; k++) printable = text[k] is >= 0x20 and < 0x7F or 0x09 or 0x0A;
            if (printable) list.Add(Encoding.ASCII.GetString(text, 0, end));
        }
        return list;
    }
}
