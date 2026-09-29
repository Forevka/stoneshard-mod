using System.Runtime.ExceptionServices;
using System.Text.Json;
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

    private sealed class Line
    {
        public bool Ok;
        public string Text = "";
    }

    private static readonly List<Line> Lines = new();

    public static Logger Log { get; set; } = new("StoneshardCheats");

    /// <summary>Runs a cheat. Returns false (and reports why) when it threw.</summary>
    public static bool Run(string echo, Action action) => RunCapturing(echo, action, out _);

    private static bool RunCapturing(string echo, Action action, out Exception? error)
    {
        Backup.EnsureOnce(Log);
        Log.Info($"> {echo}");
        // The echo goes in first, so a result the action reports lands under
        // it; a failure rewrites it in place.
        var line = Add(true, $"> {echo}");
        try
        {
            action();
            error = null;
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
            line.Ok = false;
            line.Text = $"> {echo}  - failed: {why}";
            error = ex;
            return false;
        }
    }

    /// <summary>Adds a result line (a value read back, say) under the last action.</summary>
    public static void Report(string text, bool ok = true)
    {
        Log.Info($"  {text}");
        Add(ok, $"  {text}");
    }

    private static Line Add(bool ok, string text)
    {
        var line = new Line { Ok = ok, Text = text };
        Lines.Add(line);
        if (Lines.Count > Keep) Lines.RemoveRange(0, Lines.Count - Keep);
        return line;
    }

    /// <summary>The latest line, coloured by outcome. Drawn under every tab.</summary>
    public static void DrawLast()
    {
        if (Lines.Count == 0) { UI.TextDisabled("No cheats used yet this session."); return; }
        var last = Lines[^1];
        if (last.Ok) UI.TextColored(0.55f, 0.9f, 0.55f, last.Text);
        else UI.TextColored(0.95f, 0.4f, 0.4f, last.Text);
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

    // ------------------------------------------------------------ test host

    /// <summary>
    /// A test-host command that changes the game. It goes through <see cref="Run"/>
    /// like a button - saves backed up, echoed to the log and the panel - and a
    /// failure is passed on to the client as ok:false instead of only reported.
    /// </summary>
    public static void Command(string name, string help, Func<IReadOnlyList<JsonElement>, object?> body) =>
        TestHost.Register(name, args =>
        {
            object? result = null;
            string echo = $"[test] {name} {string.Join(' ', args.Select(a => a.GetRawText()))}";
            if (!RunCapturing(echo, () => result = body(args), out var error)) ExceptionDispatchInfo.Capture(error!).Throw();
            return result;
        }, help);

    /// <summary>A test-host command that only reads: no backup, no echo.</summary>
    public static void Query(string name, string help, Func<IReadOnlyList<JsonElement>, object?> body) =>
        TestHost.Register(name, body, help);

    public static JsonElement Arg(IReadOnlyList<JsonElement> args, int i, string what) =>
        i < args.Count ? args[i] : throw new ArgumentException($"missing argument {i + 1}: {what}");

    public static string Str(IReadOnlyList<JsonElement> args, int i, string what)
    {
        var a = Arg(args, i, what);
        return a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText();
    }

    public static double Num(IReadOnlyList<JsonElement> args, int i, string what)
    {
        var a = Arg(args, i, what);
        if (a.ValueKind == JsonValueKind.Number) return a.GetDouble();
        if (a.ValueKind == JsonValueKind.String && double.TryParse(a.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        throw new ArgumentException($"argument {i + 1} ({what}) must be a number, not {a.GetRawText()}");
    }

    public static double Num(IReadOnlyList<JsonElement> args, int i, string what, double fallback) =>
        i < args.Count ? Num(args, i, what) : fallback;
}
