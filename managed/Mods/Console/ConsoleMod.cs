using CoreLoader;

[assembly: CoreModInfo(typeof(CoreConsole.ConsoleMod), "Console", "1.0.0", "CoreLoader")]

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
    private string _input = "";
    private int _historyCursor = -1;
    private bool _focus = true;
    private bool _scrollToEnd;

    public override void OnInitialize()
    {
        _history.AddRange(Config.Get("history", "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Print($"CoreLoader console - {Game.Name}. Type 'help'.", 0.6f, 0.8f, 1f);
    }

    private long _lastDrawn;

    public override void OnGUI()
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
        Config.Set("history", string.Join('\n', _history.TakeLast(100)));
    }

    // ------------------------------------------------------------ execution

    private void Execute(string line)
    {
        if (_history.Count == 0 || _history[^1] != line) _history.Add(line);
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
        catch (ConsoleError ex) { Print(ex.Message, 1f, 0.5f, 0.45f); }
        catch (GmlException ex) { Print("game: " + ex.Message, 1f, 0.5f, 0.45f); }
        catch (Exception ex) { Print($"{ex.GetType().Name}: {ex.Message}", 1f, 0.5f, 0.45f); }
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
            case "hook": Hook(arg); return true;
            case "unhook": Unhook(arg); return true;
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
            "  globals [filter]   global variables and their values",
            "  hook <script>      print each call's arguments and result;  unhook <script|all>;  hooks",
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

    private void Hook(string symbol)
    {
        if (symbol.Length == 0) throw new ConsoleError("hook <script or event>");
        if (_hooks.ContainsKey(symbol)) { Print($"already hooked {symbol}"); return; }
        long calls = 0;
        // A stack, not one slot: the hooked script may call itself (or be
        // reached again from inside), and each After must print its own call.
        var pending = new Stack<(long N, string Args)>();
        bool isEvent = symbol.StartsWith("gml_Object_", StringComparison.Ordinal);
        var before = Hooks.Before(symbol, c =>
        {
            calls++;
            var args = Enumerable.Range(0, Math.Min(c.ArgCount, 8)).Select(i => Format(c.GetArg(i)));
            pending.Push((calls, $"({string.Join(", ", args)})"));
        });
        var after = Hooks.After(symbol, c =>
        {
            var (n, args) = pending.Count > 0 ? pending.Pop() : (calls, "");
            // Throttled: a Step event can fire thousands of times a second.
            if (n <= 20 || n % 500 == 0)
                Print($"[{symbol} #{n}] {args}{(isEvent ? "" : " -> " + Format(c.Result))}", 0.85f, 0.8f, 0.5f);
        });
        _hooks[symbol] = (before, after);
        Print($"hooked {symbol}");
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
            _lines.Add((l, r, g, b));
            if (_lines.Count > MaxLines) _lines.RemoveAt(0);
        }
        _scrollToEnd = true;
    }

    private static string Format(RValue v) => Format(v, 0);

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
            default:
                if (v.IsNumber) return v.AsReal.ToString("G15", System.Globalization.CultureInfo.InvariantCulture);
                var t = Gml.TypeOf(v);
                return t == "struct" ? "{" + string.Join(", ", Gml.StructNames(v).Take(10)) + "}" : $"<{t}> {v}";
        }
    }
}
