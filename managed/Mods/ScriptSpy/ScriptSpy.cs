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
        // A variable of self read before and after each call ("HP"), or null.
        // Answers "which of these scripts actually changes X" without guessing.
        public string? Track;
        public long Calls;
        // A stack, not one slot: a watched script that calls itself (directly
        // or through others) must pair each result with its own arguments.
        public readonly Stack<string> PendingArgs = new();
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
        if (TestHost.Enabled) RegisterCommands();

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

    // The same watches, driven from a test script: probing a game over the pipe
    // is how hook points are found without clicking through the overlay.
    private void RegisterCommands()
    {
        TestHost.Register("spy.watch", args =>
        {
            if (args.Count == 0) throw new ArgumentException("spy.watch <function> [self variable to track]");
            string symbol = Normalise(args[0].GetString() ?? "");
            string? track = args.Count > 1 ? args[1].GetString() : null;
            if (!AddWatch(symbol, track)) throw new InvalidOperationException(_error);
            return symbol;
        }, "spy.watch <function> [var]: records calls (args, self object, result; var = self's variable before->after)");
        TestHost.Register("spy.read", args =>
        {
            string? only = args.Count > 0 ? Normalise(args[0].GetString() ?? "") : null;
            return _watches.Where(w => only == null || w.Symbol == only)
                .Select(w => new { symbol = w.Symbol, calls = w.Calls, rows = w.Rows.ToArray() }).ToArray();
        }, "spy.read [function]: every watch (or one) with its call count and last rows, oldest first");
        TestHost.Register("spy.clear", _ =>
        {
            foreach (var w in _watches) { w.Rows.Clear(); w.Calls = 0; }
            return _watches.Count;
        }, "spy.clear: empties every watch's rows and counts");
        TestHost.Register("spy.unwatch", args =>
        {
            string which = args.Count > 0 ? args[0].GetString() ?? "all" : "all";
            which = which == "all" ? which : Normalise(which);
            int n = 0;
            foreach (var w in _watches.ToArray())
            {
                if (which != "all" && w.Symbol != which) continue;
                w.Before.Dispose();
                w.After.Dispose();
                _watches.Remove(w);
                n++;
            }
            return n;
        }, "spy.unwatch <function|all>: removes watches, answers how many");
    }

    // "scr_damage" means the script; anything already prefixed is left alone.
    private static string Normalise(string symbol) =>
        symbol.StartsWith("gml_", StringComparison.Ordinal) ? symbol : "gml_Script_" + symbol;

    private bool AddWatch(string symbol, string? track = null)
    {
        _error = "";
        if (symbol.Length == 0) return false;
        if (_watches.FirstOrDefault(w => w.Symbol == symbol) is { } existing)
        {
            // Watching again with a variable switches what it tracks.
            if (track != null) existing.Track = track;
            return true;
        }
        try
        {
            Watch? w = null;
            var before = Hooks.Before(symbol, c => OnBefore(w!, c));
            var after = Hooks.After(symbol, c => OnAfter(w!, c));
            w = new Watch { Symbol = symbol, Before = before, After = after, Track = track };
            _watches.Add(w);
            Log.Info($"watching {symbol}" + (track != null ? $" (tracking self.{track})" : ""));
            return true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return false;
        }
    }

    // Describing a value asks the game (typeof, struct names); a failure there
    // is shown in the row, never allowed to disable the spy.
    private static void OnBefore(Watch w, HookCall c)
    {
        w.Calls++;
        string text;
        try
        {
            var parts = new List<string>();
            for (int i = 0; i < Math.Min(c.ArgCount, MaxArgs); i++) parts.Add(Describe(c.GetArg(i)));
            if (c.ArgCount > MaxArgs) parts.Add("...");
            text = w.Symbol.StartsWith("gml_Object_", StringComparison.Ordinal)
                ? $"self={c.Self}"
                : $"({string.Join(", ", parts)}) self={SelfName(c)}";
            if (w.Track != null) text += $" {w.Track}={Tracked(w, c)}";
        }
        catch (Exception ex)
        {
            text = $"(arguments unreadable: {ex.Message})";
        }
        // Pushed even while paused, so a call that began before Resume still pairs up.
        w.PendingArgs.Push(text);
    }

    private void OnAfter(Watch w, HookCall c)
    {
        string args = w.PendingArgs.Count > 0 ? w.PendingArgs.Pop() : "";
        if (w.Paused) return;
        string result;
        try { result = Describe(c.Result); }
        catch (Exception ex) { result = $"(unreadable: {ex.Message})"; }
        string row = w.Symbol.StartsWith("gml_Object_", StringComparison.Ordinal)
            ? args
            : $"{args} -> {result}";
        if (w.Track != null)
        {
            try { row += $"  [{w.Track} after={Tracked(w, c)}]"; }
            catch (Exception ex) { row += $"  [{w.Track} unreadable: {ex.Message}]"; }
        }
        w.Rows.Enqueue(row);
        while (w.Rows.Count > MaxRows) w.Rows.Dequeue();

        // The first calls, then a sample, also go to the loader log so a
        // session can be studied afterwards without the overlay open.
        if (w.Calls <= LoggedCalls || w.Calls % 1000 == 0)
            Log.Info($"{w.Symbol} #{w.Calls}: {row}");
    }

    private const int LoggedCalls = 15;

    // Which object the script ran as: the same script runs as the player, an
    // enemy or a controller, and that is often the whole question.
    private static string SelfName(HookCall c)
    {
        if (c.Self.IsNull) return "none";
        try
        {
            var index = c.Self.Get("object_index");
            // A number here, a typed reference on newer runtimes; object_get_name takes both.
            return index.IsUndefined ? "?" : Game.CallBuiltin("object_get_name", index).ToString();
        }
        // A struct self, say: it costs this field, not the arguments beside it.
        catch (GmlException) { return "?"; }
    }

    // Not every self has the variable (a script runs as many objects), and that
    // must cost only this field, not the arguments beside it.
    private static string Tracked(Watch w, HookCall c)
    {
        if (c.Self.IsNull) return "-";
        try { return Describe(c.Self.Get(w.Track!)); }
        catch (GmlException) { return "n/a"; }
    }

    private static string Describe(RValue v) => v.Kind switch
    {
        RValueKind.String => $"\"{Truncate(v.ToString(), 40)}\"",
        RValueKind.Real or RValueKind.Int32 or RValueKind.Int64 or RValueKind.Bool => v.ToString(),
        RValueKind.Undefined or RValueKind.Unset => "undefined",
        _ => $"<{v.Kind}> {Truncate(v.ToString(), 40)}",
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
