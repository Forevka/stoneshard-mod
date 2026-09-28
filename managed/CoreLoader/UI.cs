using System.Text;
using CoreLoader.Native;

namespace CoreLoader;

/// <summary>
/// Dear ImGui widgets for a mod's tab. Only valid inside <see cref="CoreMod.OnGUI"/>.
/// Labels follow ImGui rules: "Text##id" shows "Text" with a unique id.
/// </summary>
/// <remarks>
/// Every scope a mod opens (ids, tab bars, tab items) is tracked, so a mod that
/// throws halfway through - or simply forgets an End/Pop - is unwound exactly
/// back to where it started instead of leaving ImGui's stacks pointing at the
/// wrong bar. A mod cannot close a scope it did not open.
/// </remarks>
public static unsafe class UI
{
    private enum Scope { Id, TabBar, TabItem, Child }

    private static readonly List<Scope> Open = new();
    private static int _floor;

    /// <summary>True only while the loader is drawing the Mods tab.</summary>
    internal static bool InGui { get; set; }

    internal static int Mark => Open.Count;

    /// <summary>Scopes above <paramref name="mark"/> belong to the caller; closes whatever it left open.</summary>
    internal static int UnwindTo(int mark)
    {
        int closed = 0;
        while (Open.Count > mark)
        {
            var s = Open[^1];
            Open.RemoveAt(Open.Count - 1);
            CloseNative(s);
            closed++;
        }
        return closed;
    }

    /// <summary>Scopes at or below the floor belong to the loader and cannot be closed by a mod.</summary>
    internal static int SetFloor(int floor)
    {
        int old = _floor;
        _floor = floor;
        return old;
    }

    private static void CloseNative(Scope s)
    {
        switch (s)
        {
            case Scope.Id: Loader.Api->UiPopId(); break;
            case Scope.TabBar: Loader.Api->UiEndTabBar(); break;
            case Scope.TabItem: Loader.Api->UiEndTabItem(); break;
            case Scope.Child: Loader.Api->UiEndChild(); break;
        }
    }

    private static void Close(Scope expected, string call)
    {
        Guard();
        if (Open.Count <= _floor || Open[^1] != expected)
            throw new InvalidOperationException(
                $"UI.{call} does not match the most recent Begin/Push (open scopes: {string.Join(" > ", Open.Skip(_floor))})");
        Open.RemoveAt(Open.Count - 1);
        CloseNative(expected);
    }

    private static void Guard()
    {
        if (!InGui || !Loader.OnGameThread)
            throw new InvalidOperationException("UI can only be used inside OnGUI.");
    }

    public static void Text(string text)
    {
        Guard();
        fixed (byte* t = Utf8.Encode(text ?? "")) Loader.Api->UiText(t);
    }

    public static void TextColored(float r, float g, float b, string text)
    {
        Guard();
        fixed (byte* t = Utf8.Encode(text ?? "")) Loader.Api->UiTextColored(r, g, b, 1f, t);
    }

    public static void TextDisabled(string text)
    {
        Guard();
        fixed (byte* t = Utf8.Encode(text ?? "")) Loader.Api->UiTextDisabled(t);
    }

    public static bool Button(string label)
    {
        Guard();
        fixed (byte* l = Utf8.Get(label)) return Loader.Api->UiButton(l) != 0;
    }

    /// <summary>Returns true on the frame the box was toggled.</summary>
    public static bool Checkbox(string label, ref bool value)
    {
        Guard();
        int v = value ? 1 : 0;
        bool changed;
        fixed (byte* l = Utf8.Get(label)) changed = Loader.Api->UiCheckbox(l, &v) != 0;
        value = v != 0;
        return changed;
    }

    public static bool SliderFloat(string label, ref float value, float min, float max)
    {
        Guard();
        float v = value;
        bool changed;
        fixed (byte* l = Utf8.Get(label)) changed = Loader.Api->UiSliderFloat(l, &v, min, max) != 0;
        value = v;
        return changed;
    }

    public static bool InputInt(string label, ref int value)
    {
        Guard();
        int v = value;
        bool changed;
        fixed (byte* l = Utf8.Get(label)) changed = Loader.Api->UiInputInt(l, &v) != 0;
        value = v;
        return changed;
    }

    /// <summary>
    /// A single-line text box holding at most <paramref name="maxBytes"/>-1 UTF-8
    /// bytes. A longer <paramref name="value"/> is shown truncated on a character
    /// boundary.
    /// </summary>
    public static bool InputText(string label, ref string value, int maxBytes = 256)
    {
        Guard();
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 2);
        value ??= "";

        var buf = new byte[maxBytes];
        Encoding.UTF8.GetEncoder().Convert(value.AsSpan(), buf.AsSpan(0, maxBytes - 1), flush: true,
                                           out _, out int written, out _);
        buf[written] = 0;

        bool changed;
        fixed (byte* l = Utf8.Get(label))
        fixed (byte* b = buf)
            changed = Loader.Api->UiInputText(l, b, maxBytes) != 0;
        if (changed)
        {
            int end = Array.IndexOf(buf, (byte)0);
            value = Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end);
        }
        return changed;
    }

    public static bool CollapsingHeader(string label)
    {
        Guard();
        fixed (byte* l = Utf8.Get(label)) return Loader.Api->UiCollapsingHeader(l) != 0;
    }

    public static void SameLine()
    {
        Guard();
        Loader.Api->UiSameLine();
    }

    public static void Separator()
    {
        Guard();
        Loader.Api->UiSeparator();
    }

    public static void PushId(string id)
    {
        Guard();
        fixed (byte* i = Utf8.Get(id)) Loader.Api->UiPushId(i);
        Open.Add(Scope.Id);
    }

    public static void PopId() => Close(Scope.Id, nameof(PopId));

    /// <summary>Call <see cref="EndTabBar"/> only when this returned true.</summary>
    public static bool BeginTabBar(string id)
    {
        Guard();
        bool open;
        fixed (byte* i = Utf8.Get(id)) open = Loader.Api->UiBeginTabBar(i) != 0;
        if (open) Open.Add(Scope.TabBar);
        return open;
    }

    public static void EndTabBar() => Close(Scope.TabBar, nameof(EndTabBar));

    /// <summary>Call <see cref="EndTabItem"/> only when this returned true.</summary>
    public static bool BeginTabItem(string label)
    {
        Guard();
        bool open;
        fixed (byte* l = Utf8.Get(label)) open = Loader.Api->UiBeginTabItem(l) != 0;
        if (open) Open.Add(Scope.TabItem);
        return open;
    }

    public static void EndTabItem() => Close(Scope.TabItem, nameof(EndTabItem));

    /// <summary>
    /// A scrolling region. <paramref name="height"/>: pixels, 0 to fill the
    /// rest, negative to leave that much room below. Always pair with
    /// <see cref="EndChild"/>, whatever this returns.
    /// </summary>
    public static bool BeginChild(string id, float height = 0f, bool border = true)
    {
        Guard();
        bool visible;
        fixed (byte* i = Utf8.Get(id)) visible = Loader.Api->UiBeginChild(i, height, border ? 1 : 0) != 0;
        Open.Add(Scope.Child);   // ImGui wants EndChild even when not visible
        return visible;
    }

    public static void EndChild() => Close(Scope.Child, nameof(EndChild));

    /// <summary>Gives keyboard focus to the next widget.</summary>
    public static void FocusNext()
    {
        Guard();
        Loader.Api->UiSetKeyboardFocusHere();
    }

    /// <summary>Scrolls the current region so the last item is visible (ratio 1 = bottom).</summary>
    public static void ScrollHere(float ratio = 1f)
    {
        Guard();
        Loader.Api->UiSetScrollHereY(ratio);
    }

    /// <summary>Whether the current region is scrolled to (or within a line of) its bottom.</summary>
    public static bool AtBottom
    {
        get
        {
            Guard();
            return Loader.Api->UiGetScrollY() >= Loader.Api->UiGetScrollMaxY() - 20f;
        }
    }

    public enum Key { Tab = 512, Left = 513, Right = 514, Up = 515, Down = 516, Enter = 525, Escape = 526 }

    public static bool KeyPressed(Key key)
    {
        Guard();
        return Loader.Api->UiIsKeyPressed((int)key) != 0;
    }

    /// <summary>
    /// A command line: returns true on the frame Enter is pressed, with the line
    /// in <paramref name="value"/>. Up/Down walk <paramref name="history"/>
    /// (oldest first); keep <paramref name="cursor"/> between frames (-1 = new line).
    /// </summary>
    public static bool InputLine(string label, ref string value, IReadOnlyList<string> history, ref int cursor,
                                 int maxBytes = 512)
    {
        Guard();
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 2);
        if (cursor >= history.Count || cursor < -1) cursor = -1;
        var buf = new byte[maxBytes];
        System.Text.Encoding.UTF8.GetEncoder().Convert((value ?? "").AsSpan(), buf.AsSpan(0, maxBytes - 1), true,
                                                       out _, out int written, out _);
        buf[written] = 0;

        // History entries are passed as NUL-terminated UTF-8, pinned for the call.
        var handles = new System.Runtime.InteropServices.GCHandle[history.Count];
        var ptrs = new nint[history.Count];
        try
        {
            for (int i = 0; i < history.Count; i++)
            {
                handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(Utf8.Encode(history[i]),
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                ptrs[i] = handles[i].AddrOfPinnedObject();
            }
            int c = cursor;
            bool enter;
            fixed (byte* l = Utf8.Get(label))
            fixed (byte* b = buf)
            fixed (nint* h = ptrs)
                enter = Loader.Api->UiInputHistory(l, b, maxBytes, (byte**)h, history.Count, &c) != 0;
            cursor = c;
            int end = Array.IndexOf(buf, (byte)0);
            value = System.Text.Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end);
            return enter;
        }
        finally
        {
            foreach (var h in handles) if (h.IsAllocated) h.Free();
        }
    }
}
