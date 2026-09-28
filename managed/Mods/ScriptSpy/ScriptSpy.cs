using CoreLoader;

[assembly: CoreModInfo(typeof(ScriptSpy.ScriptSpyMod), "Script Spy", "1.0.0", "CoreLoader")]

namespace ScriptSpy;

/// <summary>
/// A modding tool for any YYC game: hook a script or object event by name and
/// watch its calls live - arguments on the way in, result on the way out. This
/// is how the argument meanings for every other mod were found.
/// </summary>
public sealed class ScriptSpyMod : CoreMod
{
    private const int MaxRows = 30;
    private const int MaxArgs = 8;

    private sealed class Watch
    {
        public required string Symbol;
        public required HookHandle Before;
        public required HookHandle After;
        public long Calls;
        public string PendingArgs = "";
        public readonly Queue<string> Rows = new();
        public bool Paused;
    }

    private readonly List<Watch> _watches = new();
    private string _symbol = "";
    private string _filter = "";
    private string _error = "";

    public override void OnGUI()
    {
        UI.Text("Hook a gml_Script_* or gml_Object_* function by name (the gml_Script_ prefix is optional).");
        UI.InputText("function##spy", ref _symbol, 200);
        UI.SameLine();
        if (UI.Button("Watch")) AddWatch(_symbol.Trim());
        if (_error.Length > 0) UI.TextColored(1f, 0.45f, 0.45f, _error);

        UI.InputText("find##spyfind", ref _filter, 100);
        if (_filter.Length >= 3) ShowMatches();

        UI.Separator();
        foreach (var w in _watches.ToArray())
        {
            UI.PushId(w.Symbol);
            if (UI.CollapsingHeader($"{w.Symbol}  ({w.Calls:N0} calls)"))
            {
                if (UI.Button(w.Paused ? "Resume" : "Pause")) w.Paused = !w.Paused;
                UI.SameLine();
                if (UI.Button("Clear")) w.Rows.Clear();
                UI.SameLine();
                if (UI.Button("Unhook"))
                {
                    w.Before.Dispose();
                    w.After.Dispose();
                    _watches.Remove(w);
                }
                foreach (var row in w.Rows.Reverse()) UI.TextDisabled(row);
            }
            UI.PopId();
        }
    }

    private void ShowMatches()
    {
        int shown = 0;
        foreach (var s in Game.Symbols)
        {
            if (!s.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!s.IsScript && !s.IsObjectEvent) continue;
            if (UI.Button(s.Name)) AddWatch(s.Name);
            if (++shown == 15) { UI.TextDisabled("... narrow the search"); break; }
        }
    }

    public override void OnInitialize()
    {
        // Watches listed in ScriptSpy.txt next to the dll (one symbol per line)
        // start with the game - handy for functions that only run at startup.
        var file = Path.Combine(Directory, "ScriptSpy.txt");
        if (!File.Exists(file)) return;
        foreach (var line in File.ReadAllLines(file))
        {
            var s = line.Trim();
            if (s.Length > 0 && !s.StartsWith('#')) AddWatch(s);
        }
    }

    private void AddWatch(string symbol)
    {
        _error = "";
        if (symbol.Length == 0) return;
        if (_watches.Any(w => w.Symbol == symbol)) return;
        try
        {
            Watch? w = null;
            var before = Hooks.Before(symbol, c => OnBefore(w!, c));
            var after = Hooks.After(symbol, c => OnAfter(w!, c));
            w = new Watch { Symbol = symbol, Before = before, After = after };
            _watches.Add(w);
            Log.Info($"watching {symbol}");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private static void OnBefore(Watch w, HookCall c)
    {
        w.Calls++;
        if (w.Paused) return;
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(c.ArgCount, MaxArgs); i++) parts.Add(Describe(c.GetArg(i)));
        if (c.ArgCount > MaxArgs) parts.Add("...");
        w.PendingArgs = w.Symbol.StartsWith("gml_Object_", StringComparison.Ordinal)
            ? $"self={c.Self}"
            : $"({string.Join(", ", parts)})";
    }

    private void OnAfter(Watch w, HookCall c)
    {
        if (w.Paused) return;
        string row = w.Symbol.StartsWith("gml_Object_", StringComparison.Ordinal)
            ? w.PendingArgs
            : $"{w.PendingArgs} -> {Describe(c.Result)}";
        w.Rows.Enqueue(row);
        while (w.Rows.Count > MaxRows) w.Rows.Dequeue();

        // The first calls, then a sample, also go to the loader log so a
        // session can be studied afterwards without the overlay open.
        if (w.Calls <= LoggedCalls || w.Calls % 1000 == 0)
            Log.Info($"{w.Symbol} #{w.Calls}: {row}");
    }

    private const int LoggedCalls = 15;

    private static string Describe(RValue v) => v.Kind switch
    {
        RValueKind.String => $"\"{Truncate(v.ToString(), 40)}\"",
        RValueKind.Real or RValueKind.Int32 or RValueKind.Int64 or RValueKind.Bool => v.ToString(),
        RValueKind.Undefined or RValueKind.Unset => "undefined",
        _ => $"<{v.Kind}> {Truncate(v.ToString(), 40)}",
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
