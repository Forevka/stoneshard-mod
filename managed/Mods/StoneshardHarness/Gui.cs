using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The game's own window buttons (CONFIRM, CANCEL, the reward window's, the
/// death screen's, the main menu's): o_button children, whose click runs the
/// user event their event variable names - o_confirm_button's is 0
/// (Other_10), the main menu's NEW GAME's is 5 (Other_15). Pressing one runs
/// that event, as its click does.
/// </summary>
internal static class Gui
{
    public static List<InstanceRef> Buttons() =>
        Gm.All("o_button")
          .Where(b => Gm.Num(b, "guiVisible", 1) > 0 && Gm.Flag(b, "visible") && Text(b).Length > 0)
          .ToList();

    public static string Text(InstanceRef b) => Gm.Plain(Gm.Str(b, "text"));

    public static object Describe(InstanceRef b)
    {
        double x = Gm.Num(b, "x"), y = Gm.Num(b, "y");
        var screen = Screen.FromGui(x, y);
        return new
        {
            id = Gm.Id(b),
            text = Text(b),
            obj = Gm.ObjectName(b),
            enabled = Gm.Num(b, "is_activate", 1) > 0 && !Gm.Flag(b, "is_deactivate"),
            screen = screen is { } p ? new { x = p.X, y = p.Y } : null,
        };
    }

    /// <summary>Presses a button by id or by its text (any case); answers the text pressed.</summary>
    public static string Press(string which)
    {
        var buttons = Buttons();
        int i = long.TryParse(which, out long id) ? buttons.FindIndex(b => Gm.Id(b) == id) : -1;
        if (i < 0) i = buttons.FindIndex(b => string.Equals(Text(b), which, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            throw new ArgumentException($"no button '{which}'; the buttons are: " +
                                        (buttons.Count == 0 ? "(none)" : string.Join(", ", buttons.Select(b => $"{Gm.Id(b)} \"{Text(b)}\""))));
        var button = buttons[i];
        if (!Gm.RunEvent(button, UserEvent(button)))
            throw new InvalidOperationException($"\"{Text(button)}\" has no press event ({UserEvent(button)})");
        return Text(button);
    }

    // A button's own event variable says which user event its click runs:
    // CONFIRM's is 0 (Other_10), the main menu's NEW GAME's 5 (Other_15).
    private static string UserEvent(InstanceRef button)
    {
        int n = (int)Gm.Num(button, "event", 0);
        return $"Other_{10 + Math.Clamp(n, 0, 15)}";
    }
}
