using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The open conversation and its options.
/// </summary>
/// <remarks>
/// Watched live (Verren, Brukk in Osbrook):
///   * a conversation is one o_dialogue: name/current_speaker is who talks,
///     text the line shown, topic the conversation;
///   * each option is an o_contract_button under it, with text (what it says),
///     func (the fragment key it leads to, e.g. "next", "trade", "leave") and
///     number (the key that picks it, 1 up);
///   * picking one, by click or number key, ends in scr_dialogue_advance as the
///     o_dialogue - but not always with func: "trade" leads on to
///     "osbrook_innkeeper", worked out by the button's click. So the harness
///     runs the button's click event (Mouse_4), which also reaches options
///     scrolled out of view, where number keys do not.
/// (TavernGames' DialogueOption threads its own key through scr_dialogue_advance.)
/// </remarks>
internal static class Dialogue
{
    public static InstanceRef? Open => Gm.Exact(Objects.o_dialogue.Name);

    public readonly record struct Option(int Number, string Key, string Text, bool Enabled, InstanceRef Button);

    public static List<Option> Options()
    {
        if (Open is not { } dlg) return [];
        long id = Gm.Id(dlg);
        return Gm.All(Objects.o_contract_button.Name)
            .Where(b => Gm.IdKey(b.Get("parent")) == id || Gm.IdKey(b.Get("parent")) < 0)
            .Select(b => new Option((int)Gm.Num(b, "number"), Gm.Str(b, "func"), Text(b), Gm.Num(b, "canPress", 1) > 0, b))
            .OrderBy(o => o.Number)
            .ToList();
    }

    private static string Text(InstanceRef button)
    {
        string t = Gm.Str(button, "text");
        return Gm.Plain(t.Length > 0 ? t : Gm.Str(button, "name"));
    }

    public static object? Describe()
    {
        if (Open is not { } dlg) return null;
        return new
        {
            id = Gm.Id(dlg),
            // name is the speaker as shown ("Brukk"); current_speaker is the key ("Innkeeper_Brukk").
            speaker = Gm.Str(dlg, "name") is { Length: > 0 } s ? s : Gm.Str(dlg, "current_speaker"),
            text = Gm.Plain(Gm.Str(dlg, "text")),
            topic = Gm.Str(dlg, "topic"),
            options = Options().Select(o => new
            {
                n = o.Number, key = o.Key, text = o.Text, enabled = o.Enabled,
                screen = Screen.FromGui(Gm.Num(o.Button, "x") + Gm.Num(o.Button, "guiWidth") / 2, Gm.Num(o.Button, "y") + Gm.Num(o.Button, "guiHeight") / 2)
                         is { } p ? new { x = p.X, y = p.Y } : null,
            }).ToList(),
        };
    }

    /// <summary>Picks an option by its number, its key or its text (any case); answers the key chosen.</summary>
    public static string Choose(string which)
    {
        var dlg = Open ?? throw new InvalidOperationException("no conversation is open");
        var options = Options();
        int index = int.TryParse(which, out int n)
            ? options.FindIndex(o => o.Number == n)
            : options.FindIndex(o => string.Equals(o.Key, which, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = options.FindIndex(o => o.Text.Contains(which, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || options[index].Key.Length == 0)
            throw new ArgumentException($"no option '{which}'; the options are: " +
                                        string.Join(", ", options.Select(x => $"{x.Number} {x.Key} \"{x.Text}\"")));
        var o = options[index];
        if (!o.Enabled) throw new InvalidOperationException($"option {o.Number} \"{o.Text}\" cannot be chosen now");
        // The button's click works out the fragment itself: "trade" leads on to
        // "osbrook_innkeeper", which opens the trade - calling
        // scr_dialogue_advance with the button's func would only end the talk.
        if (!Gm.RunEvent(o.Button, "Mouse_4")) Scripts.scr_dialogue_advance.CallAs(dlg, o.Key);
        return o.Key;
    }
}
