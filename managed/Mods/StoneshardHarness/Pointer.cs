using System.Runtime.InteropServices;
using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The fallbacks: a real mouse click at desktop pixels, and a key press the way
/// GameMaker's own keyboard_key_press simulates one.
/// </summary>
/// <remarks>
/// A click moves the real cursor and presses the real button (SendInput), so it
/// is refused unless the game owns the foreground window: on a shared desktop a
/// click meant for the game must never land in something else. The button goes
/// down only if the cursor is still on the target and the game's window is
/// what lies under it, and whatever is held is let go on unload (ReleaseAll). It is spread
/// over frames - move, a few frames for the game to see the hover, press, about
/// 150 ms, release - since the game and the overlay both miss a press and
/// release that arrive in the same frame.
///
/// A key goes through keyboard_key_press/keyboard_key_release, which feed the
/// game's input state directly and need no focus.
/// </remarks>
internal static partial class Pointer
{
    private const int HoverFrames = 3;
    private const int HoldFrames = 9;
    private const int KeyHoldFrames = 2;

    public static object Click(Scheduler at, int x, int y, bool right)
    {
        if (!Screen.IsForeground)
            throw new InvalidOperationException("the game window is not in the foreground: a click would land elsewhere, so none was sent");
        if (!Screen.OnScreen((x, y))) throw new ArgumentException($"({x}, {y}) is outside the game window");
        SetCursorPos(x, y);
        at.After(HoverFrames, () => Press(right, x, y));
        at.After(HoverFrames + HoldFrames, ReleaseAll);
        var room = Screen.ToRoom(x, y);
        return new { clicked = new { x, y }, right, room = new { x = Gm.Round(room.X), y = Gm.Round(room.Y) }, frames = HoverFrames + HoldFrames };
    }

    // What is held down right now, so that an unload, a hot reload or a fault
    // between press and release can still let go (ReleaseAll).
    private static bool? _buttonHeld;
    private static readonly HashSet<int> KeysHeld = [];

    // The button goes down only if, at that moment, the game still has the
    // foreground, the cursor is still where it was put (the user may have
    // moved the mouse since) and no other window covers that point.
    private static void Press(bool right, int x, int y)
    {
        if (!Screen.IsForeground || !GetCursorPos(out var at) || at.X != x || at.Y != y) return;
        if (GetAncestor(WindowFromPoint(at), GaRoot) != Screen.Window) return;
        if (Send(right ? MouseRightDown : MouseLeftDown)) _buttonHeld = right;
    }

    /// <summary>Lets go of a held mouse button and every key the harness pressed.</summary>
    public static void ReleaseAll()
    {
        if (_buttonHeld is { } right)
        {
            Send(right ? MouseRightUp : MouseLeftUp);
            _buttonHeld = null;
        }
        foreach (int vk in KeysHeld)
        {
            try { Builtins.keyboard_key_release(vk); }
            catch (GmlException) { }
        }
        KeysHeld.Clear();
    }

    private static bool Send(uint flag)
    {
        var input = new MouseInput { Type = InputMouse, Flags = flag };
        // 0 means Windows refused it (another integrity level owns the input).
        return SendInput(1, ref input, Marshal.SizeOf<MouseInput>()) == 1;
    }

    // ---------------------------------------------------------------- keys

    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = 27, ["escape"] = 27, ["enter"] = 13, ["return"] = 13, ["space"] = 32, ["tab"] = 9,
        ["backspace"] = 8, ["shift"] = 16, ["ctrl"] = 17, ["control"] = 17, ["alt"] = 18,
        ["left"] = 37, ["up"] = 38, ["right"] = 39, ["down"] = 40,
        ["pageup"] = 33, ["pagedown"] = 34, ["home"] = 36, ["end"] = 35, ["delete"] = 46, ["insert"] = 45,
    };

    /// <summary>A key name (a letter, a digit, f1-f12, or esc/enter/space/tab/arrows...) as a virtual key code.</summary>
    public static int KeyCode(string key)
    {
        if (Named.TryGetValue(key, out int vk)) return vk;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return char.ToUpperInvariant(key[0]);
        if (key.Length is 2 or 3 && (key[0] is 'f' or 'F') && int.TryParse(key.AsSpan(1), out int f) && f is >= 1 and <= 12) return 111 + f;
        if (int.TryParse(key, out int code) && code is > 0 and < 256) return code;
        throw new ArgumentException($"unknown key '{key}' (a letter, a digit, f1-f12, esc, enter, space, tab, arrows, or a virtual key code)");
    }

    public static object Key(Scheduler at, string key)
    {
        int vk = KeyCode(key);
        Builtins.keyboard_key_press(vk);
        KeysHeld.Add(vk);
        at.After(KeyHoldFrames, () =>
        {
            KeysHeld.Remove(vk);
            Builtins.keyboard_key_release(vk);
        });
        return new { key, code = vk };
    }

    // ---------------------------------------------------------------- win32

    private const uint InputMouse = 0;
    private const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008, MouseRightUp = 0x0010;

    // INPUT with its MOUSEINPUT member, as laid out on x64: the union starts on
    // an 8-byte boundary, after the type.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int Dx;
        [FieldOffset(12)] public int Dy;
        [FieldOffset(16)] public uint MouseData;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public nint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, ref MouseInput input, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    private const uint GaRoot = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);
}

/// <summary>Work to run on a later frame, from the mod's own update.</summary>
internal sealed class Scheduler
{
    private readonly List<(long Due, Action Work)> _queue = [];

    public long Frame { get; private set; }

    public void After(int frames, Action work) => _queue.Add((Frame + Math.Max(1, frames), work));

    /// <summary>Advances a frame and runs what is due; one failure does not stop the rest.</summary>
    public void Tick(Logger log)
    {
        Frame++;
        for (int i = 0; i < _queue.Count;)
        {
            if (_queue[i].Due > Frame) { i++; continue; }
            var work = _queue[i].Work;
            _queue.RemoveAt(i);
            // Every job runs even if one fails: a release queued behind a
            // failing job must still go out.
            try { work(); }
            catch (Exception ex) { log.Warning($"scheduled work failed: {ex.Message}"); }
        }
    }

    public void Clear() => _queue.Clear();
}
