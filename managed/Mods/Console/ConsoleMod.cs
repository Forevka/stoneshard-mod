using CoreLoader;

[assembly: CoreModInfo(typeof(CoreConsole.ConsoleMod), "Console", "1.0.0", "Lodestone")]
[assembly: CoreModAnyGame]

namespace CoreConsole;

/// <summary>
/// An in-game console for any YYC GameMaker game. Type GML-style expressions
/// against the running game - call builtins and scripts, read and assign
/// globals and instance variables - plus a few inspection commands. Nothing in
/// it knows which game it is in.
/// </summary>
public sealed class ConsoleMod : CoreMod
{
    private const int MaxLines = 2000;

    private readonly List<(string Text, float R, float G, float B)> _lines = new();
    private readonly List<string> _history = new();
    private readonly Dictionary<string, (HookHandle Before, HookHandle After)> _hooks = new(StringComparer.Ordinal);
    private readonly Evaluator _eval = new();
    private Inspector? _inspector;
    private Freezer? _freezer;
    private ObjectsTab? _objects;
    private VariableTable? _globals;
    private IDisposable? _gameDrawing;
    private string _input = "";
    private int _historyCursor = -1;
    private bool _focus = true;
    private bool _scrollToEnd;

    public override void OnInitialize()
    {
        _history.AddRange(Config.Get("history", "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Print($"Lodestone console - {Game.Name}. Type 'help'.", 0.6f, 0.8f, 1f);
        // The Objects tab needs the object table: built a slice per frame from
        // now, it is ready long before anyone opens the tab.
        ObjectTable.Start();
        _freezer = new Freezer(m => Print(m, 0.6f, 0.8f, 1f));
        _inspector = new Inspector(_eval, _freezer, Print, () => Path.Combine(Directory, "Console", "dumps"));
        _objects = new ObjectsTab(_inspector, _freezer);
        _globals = new VariableTable(_eval, _freezer, Print, globals: true);
        _gameDrawing = GameDraw.OnGui(_inspector.DrawGame);
        if (TestHost.Enabled)
            TestHost.Register("console", args => RunForTest(string.Join(' ', args.Select(a =>
                    a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString() : a.GetRawText()))),
                "console <line>: runs a console line (expression or command), answers its output");
    }

    // Output of the line being run for the test host; null otherwise.
    private List<string>? _capture;
    private bool _failed;

    // The same Execute the input line uses, with its output captured. A line
    // the console reports as an error answers ok:false with that output.
    private string RunForTest(string line)
    {
        var captured = _capture = new List<string>();
        _failed = false;
        try { Execute(line, remember: false); }
        finally { _capture = null; }
        // The "> line" echo comes first, one captured line per line of input.
        string output = string.Join("\n", captured.Skip(line.Split('\n').Length));
        if (_failed) throw new InvalidOperationException(output);
        return output;
    }

    public override void OnUpdate()
    {
        _inspector?.Update();
        // After the game's own Step: what the game changed this frame is set back.
        _freezer?.Update();
        try { AdvanceCallers(); }
        catch (Exception ex) { Print($"callers search stopped: {ex.Message}", 1f, 0.5f, 0.45f); _callersOf = null; }
    }

    private long _lastDrawn;

    public override void OnGUI()
    {
        if (!UI.BeginTabBar("##console_tabs")) return;
        if (UI.BeginTabItem("Console"))
        {
            DrawConsole();
            UI.EndTabItem();
        }
        if (UI.BeginTabItem("Inspector"))
        {
            _inspector?.Draw();
            UI.EndTabItem();
        }
        if (UI.BeginTabItem("Objects"))
        {
            _objects?.Draw();
            UI.EndTabItem();
        }
        if (UI.BeginTabItem("Globals"))
        {
            DrawGlobals();
            UI.EndTabItem();
        }
        UI.EndTabBar();
    }

    private void DrawGlobals()
    {
        if (_globals == null || _freezer == null) return;
        // Reads the game live, like the Inspector: errors show in the tab.
        UI.Guarded(() => { _freezer.Draw(); _globals.Draw(); },
            ex => UI.TextColored(1f, 0.5f, 0.45f, $"{ex.GetType().Name}: {ex.Message}"));
    }

    private void DrawConsole()
    {
        // Switching to the console tab (it was not drawn a moment ago) puts the
        // cursor in the input line, so typing never falls through to the game.
        long now = Environment.TickCount64;
        if (now - _lastDrawn > 250) _focus = true;
        _lastDrawn = now;

        // Output: everything but the input row.
        UI.BeginChild("##console_out", -28f, border: true);
        foreach (var (text, r, g, b) in _lines) UI.TextColored(r, g, b, text);
        if (_scrollToEnd) { UI.ScrollHere(1f); _scrollToEnd = false; }
        UI.EndChild();

        if (_focus) { UI.FocusNext(); _focus = false; }
        if (UI.InputLine(">##console_in", ref _input, _history, ref _historyCursor))
        {
            var line = _input.Trim();
            _input = "";
            _historyCursor = -1;   // focus stays in the line (the widget keeps it)
            if (line.Length > 0) Execute(line);
        }
    }

    public override void OnShutdown()
    {
        foreach (var (b, a) in _hooks.Values) { b.Dispose(); a.Dispose(); }
        _gameDrawing?.Dispose();
        _inspector?.Release();
        _freezer?.Clear();
        _eval.Release();
        Config.Set("history", string.Join('\n', _history.TakeLast(100)));
    }

    // ------------------------------------------------------------ execution

    private void Execute(string line, bool remember = true)
    {
        if (remember && (_history.Count == 0 || _history[^1] != line)) _history.Add(line);
        if (_history.Count > 200) _history.RemoveRange(0, _history.Count - 200);
        Print("> " + line, 0.55f, 0.55f, 0.6f);
        try
        {
            if (!TryCommand(line))
            {
                var v = _eval.Run(line);
                Print(Format(v), 0.85f, 0.95f, 0.85f);
            }
        }
        catch (ConsoleError ex) { _failed = true; Print(ex.Message, 1f, 0.5f, 0.45f); }
        catch (GmlException ex) { _failed = true; Print("game: " + ex.Message, 1f, 0.5f, 0.45f); }
        catch (Exception ex) { _failed = true; Print($"{ex.GetType().Name}: {ex.Message}", 1f, 0.5f, 0.45f); }
    }

    private bool TryCommand(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0];
        string arg = parts.Length > 1 ? parts[1].Trim() : "";

        // A command word followed by '(' '.' '=' '[' is an expression instead.
        if (parts.Length == 1 && line.IndexOfAny(new[] { '(', '.', '=', '[' }) >= 0) return false;
        if (parts.Length > 1 && "(.=[".Contains(arg.Length > 0 ? arg[0] : ' ')) return false;

        switch (cmd)
        {
            case "help": Help(); return true;
            case "clear": _lines.Clear(); return true;
            case "find": Find(arg); return true;
            case "objects": Objects(arg); return true;
            case "vars": Vars(arg); return true;
            case "globals": GlobalsCmd(arg); return true;
            case "where": Where(arg); return true;
            case "frozen": Frozen(); return true;
            case "unfreeze": Unfreeze(arg); return true;
            case "hook": Hook(arg); return true;
            case "unhook": Unhook(arg); return true;
            case "inspect": Inspect(arg); return true;
            case "code": CodeCmd(arg); return true;
            case "callers": Callers(arg); return true;
            case "dump":
                if (_inspector == null) return true;
                if (arg.Length > 0) Inspect(arg);
                _inspector.DumpToFile();
                return true;
            case "hooks":
                Print(_hooks.Count == 0 ? "no hooks" : string.Join(", ", _hooks.Keys));
                return true;
            default: return false;
        }
    }

    private void Help()
    {
        foreach (var l in new[]
        {
            "Expressions (GML-style), evaluated in the running game:",
            "  instance_number(o_enemy)       builtins, with this runtime's argument counts",
            "  scr_some_script(1, \"a\")        scripts by name (gml_Script_ optional)",
            "  global.gold      global.gold = 500      global.gold += 100",
            "  oSys.gold        oSys.gold *= 2         o_enemy[3].hp = 1",
            "  sprite_get_name(0) + \"!\"        numbers, strings, + - * / %, == < && || !",
            "  bare names resolve as assets (o_player, spr_x, rm_start); ans = last result",
            "Commands:",
            "  find <text>        scripts, events and builtins containing text",
            "  objects [filter]   objects with live instances (all matching with a filter)",
            "  vars <obj>[n]      every variable of an instance, e.g. vars oSys  /  vars o_enemy 2",
            "  globals [filter]   global variables and their values (the Globals tab edits and freezes them)",
            "  where <text>       live instances with a variable named like text (also in the Objects tab)",
            "  frozen             variables held by a freeze;  unfreeze all",
            "  hook <script>      print each call's arguments and result;  unhook <script|all>;  hooks",
            "  inspect            click an instance in the game, then see it in the Inspector tab",
            "  inspect <obj> [n]  inspect an object's n-th live instance (variables, events, dump)",
            "  dump [<obj> [n]]   write the inspected instance (variables, events) to Mods/Console/dumps",
            "  code <function>    what a script or event calls and the strings it uses (read-only)",
            "  callers <function> every script and event that calls it (scans all code)",
            "  clear              Up/Down in the input line walks history",
        })
            Print(l, 0.7f, 0.8f, 0.9f);
    }

    private void Find(string text)
    {
        if (text.Length < 2) throw new ConsoleError("find needs at least 2 characters");
        int n = 0;
        foreach (var s in Game.Symbols)
        {
            if (!s.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || s.Name.Contains('@')) continue;
            if (++n > 40) break;
            Print("  " + s.Name);
        }
        int b = 0;
        foreach (var name in AllBuiltins().Where(x => x.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            if (++b > 20) break;
            Print($"  builtin {name} ({Game.BuiltinArity(name)} args)");
        }
        Print($"{Math.Min(n, 40)} function(s), {Math.Min(b, 20)} builtin(s) shown", 0.6f, 0.6f, 0.65f);
    }

    private static IEnumerable<string> AllBuiltins() => Game.Builtins;

    private void Objects(string filter)
    {
        int shown = 0;
        foreach (var o in GmlObject.All())
        {
            if (filter.Length > 0 && !o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            int n = o.InstanceCount;
            if (n == 0 && filter.Length == 0) continue;
            if (++shown > 80) { Print("  ... add a filter"); break; }
            Print($"  {o.Name} x{n}");
        }
    }

    private void Vars(string arg)
    {
        var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) throw new ConsoleError("vars <object> [index]");
        var o = GmlObject.Find(p[0]) ?? throw new ConsoleError($"no object named '{p[0]}'");
        int idx = p.Length > 1 && int.TryParse(p[1], out var k) ? k : 0;
        if (o.InstanceCount <= idx) throw new ConsoleError($"no live {p[0]}[{idx}]");
        var inst = o.Instance(idx);
        foreach (var name in inst.VariableNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            Print($"  {name} = {Format(inst.Get(name))}");
    }

    private void GlobalsCmd(string filter)
    {
        int shown = 0;
        foreach (var name in Globals.Names().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var v = Globals.Get(name);
            if (v.Kind == RValueKind.Object && filter.Length == 0) continue;   // skip methods unless asked
            if (++shown > 100) { Print("  ... add a filter"); break; }
            Print($"  global.{name} = {Format(v)}");
        }
    }

    private void Where(string text)
    {
        if (text.Length < 2) throw new ConsoleError("where needs at least 2 characters");
        // Searching before the table is built would scan every object in this
        // frame, a visible hitch; the table finishes within seconds of startup.
        if (!ObjectTable.Ready)
        {
            ObjectTable.Start();
            throw new ConsoleError($"object table not ready: {ObjectTable.Status}");
        }
        var hits = ObjectsTab.FindVariable(text, 60);
        foreach (var (o, name, value) in hits) Print($"  {o.Name}[0].{name} = {Format(value)}");
        Print(hits.Count == 0 ? "no live instance has a variable like that" : $"{hits.Count} match(es)", 0.6f, 0.6f, 0.65f);
    }

    private void Frozen()
    {
        if (_freezer == null || _freezer.Count == 0) { Print("nothing frozen"); return; }
        foreach (var l in _freezer.Describe()) Print("  " + l);
    }

    private void Unfreeze(string arg)
    {
        if (arg != "all") throw new ConsoleError("unfreeze all  (a single freeze is lifted in the Inspector, Objects or Globals tab)");
        int n = _freezer?.Count ?? 0;
        _freezer?.Clear();
        Print($"unfroze {n} variable(s)");
    }

    private void Hook(string symbol)
    {
        if (symbol.Length == 0) throw new ConsoleError("hook <script or event>");
        if (_hooks.ContainsKey(symbol)) { Print($"already hooked {symbol}"); return; }
        long calls = 0;
        // A stack, not one slot: the hooked script may call itself (or be
        // reached again from inside), and each After must print its own call.
        var pending = new Stack<(long N, string Args)>();
        bool isEvent = symbol.StartsWith("gml_Object_", StringComparison.Ordinal);
        // Formatting asks the game about the values (typeof, struct names); a
        // failure there must cost one line of output, not fault the console.
        var before = Hooks.Before(symbol, c =>
        {
            calls++;
            string text;
            try { text = $"({string.Join(", ", Enumerable.Range(0, Math.Min(c.ArgCount, 8)).Select(i => Format(c.GetArg(i))))})"; }
            catch (Exception ex) { text = $"(arguments unreadable: {ex.Message})"; }
            pending.Push((calls, text));
        });
        var after = Hooks.After(symbol, c =>
        {
            var (n, args) = pending.Count > 0 ? pending.Pop() : (calls, "");
            // Throttled: a Step event can fire thousands of times a second.
            if (n > 20 && n % 500 != 0) return;
            string result;
            try { result = isEvent ? "" : " -> " + Format(c.Result); }
            catch (Exception ex) { result = $" -> (unreadable: {ex.Message})"; }
            Print($"[{symbol} #{n}] {args}{result}", 0.85f, 0.8f, 0.5f);
        });
        _hooks[symbol] = (before, after);
        Print($"hooked {symbol}");
    }

    private void Inspect(string arg)
    {
        if (_inspector == null) return;
        var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0)
        {
            _inspector.Pick();
            Print("click an instance in the game (right click cancels); it opens in the Inspector tab");
            return;
        }
        int n = p.Length > 1 && int.TryParse(p[1], out var k) ? k : 0;
        _inspector.Select(p[0], n);
        Print($"inspecting {p[0]}[{n}] - see the Inspector tab");
    }

    private void CodeCmd(string symbol)
    {
        if (symbol.Length == 0) throw new ConsoleError("code <script or event>");
        var info = CoreLoader.Code.Describe(symbol);
        if (info == null)
        {
            nint b = CoreLoader.Code.BuiltinAddress(symbol);
            if (b == 0) throw new ConsoleError($"{symbol} is not a compiled function or a builtin");
            Print($"builtin {symbol}: native code at 0x{b:X}, {Game.BuiltinArity(symbol)} argument(s) " +
                  "(use 'callers' to see which scripts and events call it)", 0.6f, 0.85f, 1f);
            return;
        }
        Print($"{info.Name}  0x{info.Address:X}, up to {info.Size:N0} bytes" +
              (info.ArgumentCount > 0 ? $", reads {info.ArgumentCount} argument(s)" : ""), 0.6f, 0.85f, 1f);
        Print($"  calls ({info.Calls.Count}):");
        foreach (var c in info.Calls.Take(80)) Print("    " + (c.StartsWith("gml_", StringComparison.Ordinal) ? c : "builtin " + c));
        if (info.Calls.Count > 80) Print($"    ... {info.Calls.Count - 80} more (see the Inspector's code view)");
        Print($"  strings ({info.Strings.Count}):");
        foreach (var s in info.Strings.Take(40)) Print("    \"" + s + "\"", 0.7f, 0.7f, 0.75f);
        _inspector?.ShowCode(info.Name);
    }

    // A callers search reads every function in the game: it runs a few
    // milliseconds per frame (OnUpdate) rather than freezing one frame.
    private string? _callersOf;
    private int _callersCursor = -1;
    private readonly List<string> _callersFound = new();

    private void Callers(string symbol)
    {
        if (symbol.Length == 0) throw new ConsoleError("callers <script, event or builtin>");
        var target = CoreLoader.Code.Describe(symbol)?.Name ?? symbol;
        if (CoreLoader.Code.Describe(target) == null && CoreLoader.Code.BuiltinAddress(target) == 0)
            throw new ConsoleError($"{symbol} is not a compiled function or a builtin");
        _callersOf = target;
        _callersCursor = 0;
        _callersFound.Clear();
        Print($"searching for callers of {target}...", 0.6f, 0.6f, 0.65f);
    }

    private void AdvanceCallers()
    {
        if (_callersOf == null) return;
        _callersFound.AddRange(CoreLoader.Code.FindCallers(_callersOf, ref _callersCursor));
        if (_callersCursor >= 0) return;
        Print($"{_callersFound.Count} caller(s) of {_callersOf}:", 0.6f, 0.85f, 1f);
        foreach (var c in _callersFound.Take(100)) Print("  " + c);
        if (_callersFound.Count > 100) Print($"  ... and {_callersFound.Count - 100} more");
        _callersOf = null;
    }

    private void Unhook(string symbol)
    {
        var targets = symbol == "all" ? _hooks.Keys.ToList() : new List<string> { symbol };
        foreach (var s in targets)
        {
            if (!_hooks.Remove(s, out var h)) { Print($"not hooked: {s}"); continue; }
            h.Before.Dispose();
            h.After.Dispose();
            Print($"unhooked {s}");
        }
    }

    // --------------------------------------------------------------- output

    private void Print(string text, float r = 0.9f, float g = 0.9f, float b = 0.9f)
    {
        foreach (var l in text.Split('\n'))
        {
            _capture?.Add(l);
            _lines.Add((l, r, g, b));
            if (_lines.Count > MaxLines) _lines.RemoveAt(0);
        }
        _scrollToEnd = true;
    }

    internal static string Format(RValue v) => Format(v, 0);

    // Depth-limited: GML arrays are references and can contain themselves, and
    // unbounded recursion would end in a StackOverflow nothing can catch.
    private static string Format(RValue v, int depth)
    {
        if (depth > 3 && (v.Kind == RValueKind.Array || v.Kind == RValueKind.Object)) return "…";
        switch (v.Kind)
        {
            case RValueKind.Undefined:
            case RValueKind.Unset:
                return "undefined";
            case RValueKind.String:
                var s = v.ToString();
                return "\"" + (s.Length > 200 ? s[..200] + "…" : s) + "\"";
            case RValueKind.Array:
                int n = Gml.ArrayLength(v);
                var items = Enumerable.Range(0, Math.Min(n, 8)).Select(i => Format(Gml.ArrayGet(v, i), depth + 1));
                return $"[{string.Join(", ", items)}{(n > 8 ? $", … ({n})" : "")}]";
            // Newer runtimes hand out typed references (instances, assets) whose
            // string() is just "<ref>"; the index in the low half is what matters.
            case RValueKind.Reference:
                return $"ref {v.Int32}";
            default:
                if (v.IsNumber) return v.AsReal.ToString("G15", System.Globalization.CultureInfo.InvariantCulture);
                var t = Gml.TypeOf(v);
                return t == "struct" ? "{" + string.Join(", ", Gml.StructNames(v).Take(10)) + "}" : $"<{t}> {v}";
        }
    }
}
