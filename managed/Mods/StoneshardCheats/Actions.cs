using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// Every cheat goes through <see cref="Run"/>: the saves are backed up first,
/// the call is echoed to the log and the panel, and a failure is reported
/// instead of faulting the mod. The echo matters because many of these calls
/// were inferred from compiled code rather than documented: when one is wrong
/// the log shows exactly what was sent.
/// </summary>
internal static class Actions
{
    private const int Keep = 200;
    private static readonly List<(bool Ok, string Text)> Lines = new();

    public static Logger Log { get; set; } = new("StoneshardCheats");

    public static IReadOnlyList<(bool Ok, string Text)> History => Lines;

    /// <summary>Runs a cheat. Returns false (and reports why) when it threw.</summary>
    public static bool Run(string echo, Action action)
    {
        Backup.EnsureOnce(Log);
        Log.Info($"> {echo}");
        try
        {
            action();
            Add(true, $"> {echo}");
            return true;
        }
        // Anything short of running out of memory is reported rather than
        // rethrown: an escaped exception would fault the whole mod over one cheat.
        // (A stack overflow cannot be caught at all.)
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // GML failures carry their own context; anything else is a bug in the
            // cheat, and its type is the first thing needed to find it.
            string why = ex is GmlException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Log.Warning($"  failed: {why}");
            Add(false, $"> {echo}  - failed: {why}");
            return false;
        }
    }

    /// <summary>Adds a result line (a value read back, say) under the last action.</summary>
    public static void Report(string text, bool ok = true)
    {
        Log.Info($"  {text}");
        Add(ok, $"  {text}");
    }

    private static void Add(bool ok, string text)
    {
        Lines.Add((ok, text));
        if (Lines.Count > Keep) Lines.RemoveRange(0, Lines.Count - Keep);
    }

    /// <summary>The latest line, coloured by outcome. Drawn under every tab.</summary>
    public static void DrawLast()
    {
        if (Lines.Count == 0) { UI.TextDisabled("No cheats used yet this session."); return; }
        var (ok, text) = Lines[^1];
        if (ok) UI.TextColored(0.55f, 0.9f, 0.55f, text);
        else UI.TextColored(0.95f, 0.4f, 0.4f, text);
    }

    /// <summary>
    /// A button for one script call: greyed out when the game lacks the script,
    /// with the call it makes shown beside it.
    /// </summary>
    public static void CallRow(string label, string script, string preview, Action action)
    {
        bool available = Game.FindSymbol("gml_Script_" + script) != 0;
        UI.BeginDisabled(!available || !Player.Available);
        if (UI.Button(label)) Run(preview, action);
        UI.EndDisabled();
        UI.SameLine();
        UI.TextDisabled(available ? preview : $"{script}  (not in this game)");
    }
}
