using System.Text;
using CoreLoader.Native;

namespace CoreLoader;

/// <summary>
/// Dear ImGui widgets for a mod's tab. Only valid inside <see cref="CoreMod.OnGUI"/>.
/// Labels follow ImGui rules: "Text##id" shows "Text" with a unique id.
/// </summary>
public static unsafe class UI
{
    public static void Text(string text)
    {
        fixed (byte* t = Utf8.Encode(text)) Loader.Api->UiText(t);
    }

    public static void TextColored(float r, float g, float b, string text)
    {
        fixed (byte* t = Utf8.Encode(text)) Loader.Api->UiTextColored(r, g, b, 1f, t);
    }

    public static void TextDisabled(string text)
    {
        fixed (byte* t = Utf8.Encode(text)) Loader.Api->UiTextDisabled(t);
    }

    public static bool Button(string label)
    {
        fixed (byte* l = Utf8.Get(label)) return Loader.Api->UiButton(l) != 0;
    }

    /// <summary>Returns true on the frame the box was toggled.</summary>
    public static bool Checkbox(string label, ref bool value)
    {
        int v = value ? 1 : 0;
        bool changed;
        fixed (byte* l = Utf8.Get(label)) changed = Loader.Api->UiCheckbox(l, &v) != 0;
        value = v != 0;
        return changed;
    }

    public static bool SliderFloat(string label, ref float value, float min, float max)
    {
        float v = value;
        bool changed;
        fixed (byte* l = Utf8.Get(label)) changed = Loader.Api->UiSliderFloat(l, &v, min, max) != 0;
        value = v;
        return changed;
    }

    public static bool InputInt(string label, ref int value)
    {
        int v = value;
        bool changed;
        fixed (byte* l = Utf8.Get(label)) changed = Loader.Api->UiInputInt(l, &v) != 0;
        value = v;
        return changed;
    }

    /// <summary>A single-line text box holding at most <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    public static bool InputText(string label, ref string value, int maxBytes = 256)
    {
        var buf = new byte[maxBytes];
        int n = Math.Min(Encoding.UTF8.GetByteCount(value), maxBytes - 1);
        Encoding.UTF8.GetBytes(value.AsSpan(), buf.AsSpan(0, maxBytes - 1));
        buf[n] = 0;
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
        fixed (byte* l = Utf8.Get(label)) return Loader.Api->UiCollapsingHeader(l) != 0;
    }

    public static void SameLine() => Loader.Api->UiSameLine();

    public static void Separator() => Loader.Api->UiSeparator();

    public static void PushId(string id)
    {
        fixed (byte* i = Utf8.Get(id)) Loader.Api->UiPushId(i);
    }

    public static void PopId() => Loader.Api->UiPopId();

    public static bool BeginTabBar(string id)
    {
        fixed (byte* i = Utf8.Get(id)) return Loader.Api->UiBeginTabBar(i) != 0;
    }

    public static void EndTabBar() => Loader.Api->UiEndTabBar();

    public static bool BeginTabItem(string label)
    {
        fixed (byte* l = Utf8.Get(label)) return Loader.Api->UiBeginTabItem(l) != 0;
    }

    public static void EndTabItem() => Loader.Api->UiEndTabItem();
}
