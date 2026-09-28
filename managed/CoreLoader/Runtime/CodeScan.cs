using CoreLoader.Native;

namespace CoreLoader.Runtime;

/// <summary>
/// Reads facts about compiled scripts out of their machine code.
///
/// Argument count: YYC compiles every argument read as a guard on argc
/// followed by a load from the argument array - `argc > j ? args[j] : undefined`
/// becomes a cmp/test of the argc register, a conditional jump, then
/// `mov r64, [argsReg + 8*j]`. The largest such j, plus one, is how many
/// arguments the script reads. Scripts that index arguments dynamically
/// (argument[i]) show no guards and report 0, meaning "unknown".
/// Verified on both runtimes: scr_approach 3, scr_atr_set 2, dealDamage 6,
/// selectDrops 4, key_to_string 1.
/// </summary>
internal static unsafe class CodeScan
{
    private const int Window = 6000;

    /// <param name="function">The script's entry.</param>
    /// <param name="next">The next function's entry (0 if unknown): the scan stops
    /// there, so a small script never inherits the arguments of its neighbour.</param>
    public static int ArgumentCount(nint function, nint next = 0)
    {
        int limit = next > function ? (int)Math.Min(next - function, Window) : Window;
        var buf = new byte[limit];
        int len = limit;
        fixed (byte* p = buf)
        {
            // Near the end of .text the full window may not be readable; shrink.
            while (len >= 32 && Loader.Api->MemoryRead(function, p, len) == 0) len /= 2;
        }
        if (len < 32) return 0;
        return Scan(new ReadOnlySpan<byte>(buf, 0, len));
    }

    internal static int Scan(ReadOnlySpan<byte> b)
    {
        int best = -1;
        for (int i = 0; i + 24 < b.Length; i++)
        {
            if (!Guard(b, i, out int k, out int glen)) continue;
            int j = i + glen;
            if (b[j] >= 0x7C && b[j] <= 0x7F) j += 2;                                // jl/jge/jle/jg rel8
            else if (b[j] == 0x0F && b[j + 1] >= 0x8C && b[j + 1] <= 0x8F) j += 6;    // rel32
            else continue;

            for (int q = j; q < j + 12 && q + 5 < b.Length; q++)
            {
                int slot = Load(b, q);
                if (slot < 0) continue;
                if ((slot == k || slot == k - 1) && slot < 16) best = Math.Max(best, slot);
                break;
            }
        }
        return best + 1;
    }

    // cmp r32, imm8  (optionally REX)  or  test r32, r32 (same register).
    private static bool Guard(ReadOnlySpan<byte> b, int i, out int k, out int len)
    {
        k = 0; len = 0;
        int p = i;
        if (b[p] is 0x41 or 0x44 or 0x45) p++;
        if (b[p] == 0x83 && (b[p + 1] & 0xF8) == 0xF8)
        {
            k = b[p + 2];
            len = p + 3 - i;
            return true;
        }
        if (b[p] == 0x85 && (b[p + 1] & 0xC0) == 0xC0 && ((b[p + 1] >> 3) & 7) == (b[p + 1] & 7))
        {
            len = p + 2 - i;
            return true;
        }
        return false;
    }

    // mov r64, [base] / [base + disp8], including the SIB form r12/rsp bases need.
    private static int Load(ReadOnlySpan<byte> b, int i)
    {
        if (b[i] is not (0x48 or 0x49 or 0x4C or 0x4D) || b[i + 1] != 0x8B) return -1;
        int m = b[i + 2], mod = m >> 6, rm = m & 7;
        if (mod > 1) return -1;
        int at = i + 3;
        if (rm == 4)
        {
            if ((b[at] & 0x38) != 0x20) return -1;   // SIB with an index register: not an argument slot
            at++;
        }
        else if (rm == 5 && mod == 0) return -1;     // rip-relative
        if (mod == 0) return 0;
        int disp = b[at];
        return disp < 0x80 && disp % 8 == 0 ? disp / 8 : -1;
    }
}
