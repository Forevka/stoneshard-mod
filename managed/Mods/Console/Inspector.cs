using System.Globalization;
using System.Text;
using CoreLoader;

namespace CoreConsole;

/// <summary>
/// Click on anything in the game and see what it is made of: its object and
/// parents, every variable (editable, with GML expressions, and freezable),
/// and - read-only - the compiled events of its object: what each one calls
/// and which strings it uses, and who calls those in turn.
/// </summary>
internal sealed class Inspector
{
    private const int All = -3;   // GML's `all`

    // Built-in instance variables: not listed by variable_instance_get_names,
    // but often the first thing worth looking at.
    private static readonly string[] BuiltinVars =
    {
        "id", "object_index", "x", "y", "xstart", "ystart", "xprevious", "yprevious",
        "hspeed", "vspeed", "speed", "direction", "friction", "gravity", "gravity_direction",
        "sprite_index", "image_index", "image_speed", "image_number", "image_xscale", "image_yscale",
        "image_angle", "image_alpha", "image_blend", "mask_index", "depth", "layer",
        "visible", "persistent", "solid", "bbox_left", "bbox_top", "bbox_right", "bbox_bottom",
    };

    private readonly Evaluator _eval;
    private readonly Action<string, float, float, float> _print;
    private readonly Func<string> _dumpDir;

    private InstanceRef? _target;
    private string _targetObject = "";
    private string _targetLabel = "";
    private readonly List<(InstanceRef Ref, string Label)> _under = new();
    private List<(string Name, string Type, string Text, bool Container)> _rows = new();
    private long _rowsAt;
    private string _filter = "";
    private bool _showBuiltins = true;
    private string? _editing;
    private string _editText = "";
    private string _message = "";
    // Frozen variables and the (kept) value they are held at.
    private readonly Dictionary<string, RValue> _frozen = new(StringComparer.Ordinal);

    private string? _code;
    private readonly Stack<string> _codeBack = new();
    private string? _callersOf;
    private int _callerCursor = -1;
    private readonly List<string> _callers = new();
    private Dictionary<string, List<string>>? _eventsByObject;

    public Inspector(Evaluator eval, Action<string, float, float, float> print, Func<string> dumpDir)
    {
        _eval = eval;
        _print = print;
        _dumpDir = dumpDir;
    }

    // ------------------------------------------------------------- picking

    public void Pick()
    {
        Input.ArmPick();
        _message = "click an instance in the game (right click cancels)";
    }

    /// <summary>Selects the n-th instance of an object by name.</summary>
    public void Select(string obj, int n)
    {
        var o = GmlObject.Find(obj) ?? throw new ConsoleError($"no object named '{obj}'");
        if (o.InstanceCount <= n) throw new ConsoleError($"no live {obj}[{n}]");
        Select(o.Instance(n));
    }

    public void Select(InstanceRef r)
    {
        Release(keepPicking: true);
        _target = new InstanceRef(Values.Keep(r.Id));
        _targetObject = ObjectName(r);
        _targetLabel = $"{_targetObject} (id {ConsoleMod.Format(r.Get("id"))})";
        _editing = null;
        Refresh();
    }

    /// <summary>Once a frame: takes a pick click, re-applies frozen values, advances a caller search.</summary>
    public void Update()
    {
        if (Input.TryTakePick(out var click))
        {
            if (click.RightButton) _message = "pick cancelled";
            else TakePick(click.RoomX, click.RoomY);
        }

        if (_target is { } t && _frozen.Count > 0)
        {
            if (!t.Exists) { Unfreeze(null); _message = "the instance is gone; freezes dropped"; }
            else
            {
                foreach (var (name, value) in _frozen.ToList())
                {
                    try { t.Set(name, value); }
                    catch (GmlException ex) { Unfreeze(name); _message = $"unfroze {name}: {ex.Message}"; }
                }
            }
        }

        if (_callersOf != null && _callerCursor >= 0)
            _callers.AddRange(Code.FindCallers(_callersOf, ref _callerCursor, 1500));
    }

    private void TakePick(double x, double y)
    {
        _under.Clear();
        if (double.IsNaN(x)) { _message = "could not read the game's mouse position"; return; }

        foreach (var r in InstancesAt(x, y))
            _under.Add((r, $"{ObjectName(r)} {ConsoleMod.Format(r.Get("id"))}"));

        if (_under.Count == 0)
        {
            // Nothing with a collision mask there: offer the nearest instance.
            var near = Game.CallBuiltin("instance_nearest", x, y, All);
            var r = new InstanceRef(near);
            if (!r.Exists) { _message = $"nothing at ({x:0}, {y:0})"; return; }
            _under.Add((r, $"{ObjectName(r)} {ConsoleMod.Format(r.Get("id"))} (nearest)"));
        }

        Select(_under[0].Ref);
        _message = _under.Count > 1 ? $"{_under.Count} instances under the cursor; showing the smallest" : $"picked at ({x:0}, {y:0})";
    }

    /// <summary>
    /// Instances whose collision mask covers a room point, most specific first:
    /// smallest bounding box, then nearest the viewer (lowest depth). Games put
    /// room-sized objects (floors, wall layers, controllers) under everything,
    /// and "what I clicked" is almost always the small thing on top of them.
    /// </summary>
    private static List<InstanceRef> InstancesAt(double x, double y)
    {
        var found = new List<(InstanceRef Ref, double Area, double Depth)>();
        var list = Game.CallBuiltin("ds_list_create");
        try
        {
            int n = Game.BuiltinArity("instance_position_list") == 5
                ? (int)Game.CallBuiltin("instance_position_list", x, y, All, list, false).AsReal
                : (int)Game.CallBuiltin("instance_position_list", x, y, All, list).AsReal;
            for (int i = 0; i < n && i < 64; i++)
            {
                var r = new InstanceRef(Game.CallBuiltin("ds_list_find_value", list, i));
                double w = r.Get("bbox_right").AsReal - r.Get("bbox_left").AsReal;
                double h = r.Get("bbox_bottom").AsReal - r.Get("bbox_top").AsReal;
                found.Add((r, Math.Max(1, w) * Math.Max(1, h), r.Get("depth").AsReal));
            }
        }
        finally
        {
            Game.CallBuiltin("ds_list_destroy", list);
        }
        return found.OrderBy(f => f.Area).ThenBy(f => f.Depth).Select(f => f.Ref).ToList();
    }

    // --------------------------------------------------------------- model

    private static string ObjectName(InstanceRef r)
    {
        try { return Game.CallBuiltin("object_get_name", r.Get("object_index")).ToString(); }
        catch (GmlException) { return "?"; }
    }

    private static List<string> Parents(string obj)
    {
        var chain = new List<string>();
        var o = GmlObject.Find(obj);
        for (int guard = 0; o is { } cur && guard < 32; guard++)
        {
            var p = Game.CallBuiltin("object_get_parent", cur.Index);
            int idx = p.IsNumber ? (int)p.AsReal : p.Kind == RValueKind.Reference ? p.Int32 : -1;
            if (idx < 0) break;
            string name = Game.CallBuiltin("object_get_name", idx).ToString();
            chain.Add(name);
            o = new GmlObject(idx, name);
        }
        return chain;
    }

    private IReadOnlyList<string> Events(string obj)
    {
        _eventsByObject ??= BuildEventIndex();
        return _eventsByObject.TryGetValue(obj, out var l) ? l : Array.Empty<string>();
    }

    // "gml_Object_<obj>_<Event>_<n>": object names may contain underscores, so
    // the split is on the known event names instead.
    private static readonly string[] EventKinds =
        { "Create", "Destroy", "Alarm", "Step", "Collision", "Keyboard", "Mouse", "Other", "Draw", "KeyPress", "KeyRelease", "Trigger", "CleanUp", "Gesture", "PreCreate" };

    private static Dictionary<string, List<string>> BuildEventIndex()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in Game.Symbols)
        {
            if (!s.Name.StartsWith("gml_Object_", StringComparison.Ordinal)) continue;
            string rest = s.Name["gml_Object_".Length..];
            int cut = -1;
            foreach (var k in EventKinds)
            {
                int at = rest.LastIndexOf("_" + k + "_", StringComparison.Ordinal);
                if (at > cut) cut = at;
            }
            if (cut <= 0) continue;
            string obj = rest[..cut];
            if (!map.TryGetValue(obj, out var l)) map[obj] = l = new List<string>();
            l.Add(s.Name);
        }
        return map;
    }

    private void Refresh()
    {
        _rowsAt = Environment.TickCount64;
        var rows = new List<(string, string, string, bool)>();
        if (_target is not { } t || !t.Exists) { _rows = rows; return; }

        var names = t.VariableNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (_showBuiltins) names = BuiltinVars.Concat(names).ToList();
        foreach (var n in names)
        {
            if (_filter.Length > 0 && !n.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
            RValue v;
            try { v = t.Get(n); } catch (GmlException) { continue; }
            string type = SafeType(v);
            bool container = v.Kind is RValueKind.Array || type == "struct";
            rows.Add((n, type, ConsoleMod.Format(v) + AssetName(n, v), container));
        }
        _rows = rows;
    }

    // The asset behind the index-valued built-ins, by name.
    private static string AssetName(string variable, RValue v)
    {
        string? fn = variable switch
        {
            "object_index" => "object_get_name",
            "sprite_index" or "mask_index" => "sprite_get_name",
            "layer" => "layer_get_name",
            _ => null,
        };
        if (fn == null || !(v.IsNumber || v.Kind == RValueKind.Reference)) return "";
        if (v.IsNumber && v.AsReal < 0) return "";
        try { return $"  ({Game.CallBuiltin(fn, v)})"; }
        catch (GmlException) { return ""; }
    }

    private static string SafeType(RValue v)
    {
        try { return Gml.TypeOf(v); } catch (GmlException) { return v.Kind.ToString(); }
    }

    // ------------------------------------------------------------------ UI

    public void Draw()
    {
        if (UI.Button(Input.IsPicking ? "Waiting for a click...##pick" : "Pick in game")) Pick();
        UI.SameLine();
        if (UI.Button("Refresh")) Refresh();
        if (_target != null)
        {
            UI.SameLine();
            if (UI.Button("Dump to file")) DumpToFile();
            UI.SameLine();
            if (UI.Button("Copy dump")) { UI.SetClipboard(Dump()); _message = "dump copied to the clipboard"; }
        }
        if (_message.Length > 0) UI.TextDisabled(_message);

        if (_under.Count > 1)
        {
            UI.Text("Under the cursor:");
            for (int i = 0; i < _under.Count && i < 12; i++)
            {
                if (i > 0) UI.SameLine();
                if (UI.Button($"{_under[i].Label}##under{i}")) Select(_under[i].Ref);
            }
        }

        if (_target is not { } t)
        {
            UI.TextDisabled("Nothing selected. Pick in game, or type 'inspect <object> [n]' in the console.");
            return;
        }
        if (!t.Exists)
        {
            UI.TextColored(1f, 0.6f, 0.4f, $"{_targetLabel} no longer exists.");
            return;
        }

        // Values change while the game runs: the table re-reads twice a second.
        if (Environment.TickCount64 - _rowsAt > 500) Refresh();

        var parents = Parents(_targetObject);
        UI.TextColored(0.6f, 0.85f, 1f, _targetLabel + (parents.Count > 0 ? "  <  " + string.Join("  <  ", parents) : ""));

        if (UI.BeginTabBar("##insp_tabs"))
        {
            if (UI.BeginTabItem("Variables"))
            {
                DrawVariables(t);
                UI.EndTabItem();
            }
            if (UI.BeginTabItem("Code (read-only)"))
            {
                DrawCode(parents);
                UI.EndTabItem();
            }
            UI.EndTabBar();
        }
    }

    private void DrawVariables(InstanceRef t)
    {
        if (UI.InputText("filter##insp_filter", ref _filter, 64)) Refresh();
        UI.SameLine();
        if (UI.Checkbox("built-ins", ref _showBuiltins)) Refresh();
        UI.TextDisabled($"{_rows.Count} variable(s); edit takes any GML expression (Enter applies); freeze re-applies it every frame");

        UI.BeginChild("##insp_vars", 0f, border: true);
        foreach (var (name, type, text, container) in _rows)
        {
            UI.PushId(name);
            bool frozen = _frozen.ContainsKey(name);
            if (UI.Button(_editing == name ? "x" : "edit")) { _editing = _editing == name ? null : name; _editText = type == "string" ? text : StripEllipsis(text); }
            UI.SameLine();
            if (container)
            {
                if (UI.TreeNode($"{name}  [{type}]  {text}"))
                {
                    try { DrawChildren(t.Get(name), 1); } catch (GmlException ex) { UI.TextDisabled(ex.Message); }
                    UI.TreePop();
                }
            }
            else if (frozen) UI.TextColored(0.5f, 0.8f, 1f, $"{name} = {text}  [{type}, frozen]");
            else UI.Text($"{name} = {text}  [{type}]");

            if (_editing == name)
            {
                bool enter = UI.InputTextEnter("##edit", ref _editText, 512);
                bool apply = UI.Button("apply");
                UI.SameLine();
                bool freeze = UI.Button(frozen ? "unfreeze" : "freeze");
                if (enter || apply || freeze) Apply(t, name, freezeToggle: freeze);
            }
            UI.PopId();
        }
        UI.EndChild();
    }

    private void Apply(InstanceRef t, string name, bool freezeToggle)
    {
        try
        {
            if (freezeToggle && _frozen.ContainsKey(name)) { Unfreeze(name); _message = $"unfroze {name}"; return; }
            var v = _eval.Run(_editText);
            t.Set(name, v);
            if (freezeToggle) _frozen[name] = Values.Keep(v);
            _message = $"{name} = {ConsoleMod.Format(v)}{(freezeToggle ? " (frozen)" : "")}";
            _print($"inspect: {_targetObject}.{name} = {_editText}", 0.7f, 0.9f, 0.7f);
            _editing = null;
            Refresh();
        }
        catch (Exception ex) when (ex is ConsoleError or GmlException)
        {
            _message = ex.Message;
        }
    }

    private static string StripEllipsis(string s) => s.Replace("…", "");

    private static void DrawChildren(RValue v, int depth)
    {
        if (depth > 5) { UI.TextDisabled("…"); return; }
        if (v.Kind == RValueKind.Array)
        {
            int n = Gml.ArrayLength(v);
            for (int i = 0; i < n && i < 200; i++) DrawChild($"[{i}]", Gml.ArrayGet(v, i), depth);
            if (n > 200) UI.TextDisabled($"… {n - 200} more");
        }
        else if (Gml.TypeOf(v) == "struct")
        {
            foreach (var m in Gml.StructNames(v).Take(200)) DrawChild(m, Gml.StructGet(v, m), depth);
        }
    }

    private static void DrawChild(string label, RValue v, int depth)
    {
        string type = SafeType(v);
        if (v.Kind == RValueKind.Array || type == "struct")
        {
            if (UI.TreeNode($"{label}  [{type}]  {ConsoleMod.Format(v)}##{depth}"))
            {
                DrawChildren(v, depth + 1);
                UI.TreePop();
            }
        }
        else UI.Text($"{label} = {ConsoleMod.Format(v)}  [{type}]");
    }

    // ---------------------------------------------------------------- code

    /// <summary>Opens the read-only code view on a function.</summary>
    public void ShowCode(string symbol)
    {
        if (_code != null && _code != symbol) _codeBack.Push(_code);
        _code = symbol;
        _callersOf = null;
        _callers.Clear();
    }

    private void DrawCode(List<string> parents)
    {
        if (_code == null)
        {
            UI.TextDisabled("Events of the object and its parents. Pick one to see what it calls.");
            UI.BeginChild("##insp_events", 0f, border: true);
            foreach (var obj in new[] { _targetObject }.Concat(parents))
            {
                var events = Events(obj);
                if (events.Count == 0) continue;
                UI.Text(obj + (obj == _targetObject ? "" : "  (parent)"));
                string? open = null;
                foreach (var e in events)
                {
                    if (UI.Button(e["gml_Object_".Length..] + "##" + e)) open = e;
                }
                if (open != null) { ShowCode(open); break; }
            }
            UI.EndChild();
            return;
        }

        if (UI.Button("< back")) { _code = _codeBack.Count > 0 ? _codeBack.Pop() : null; _callersOf = null; _callers.Clear(); return; }
        UI.SameLine();
        if (UI.Button("events")) { _code = null; _codeBack.Clear(); return; }

        var info = Code.Describe(_code);
        if (info == null) { UI.TextDisabled($"{_code}: not a compiled function"); return; }
        UI.TextColored(0.6f, 0.85f, 1f, info.Name);
        UI.TextDisabled($"at 0x{info.Address:X}, up to {info.Size:N0} bytes" +
                        (info.ArgumentCount > 0 ? $", reads {info.ArgumentCount} argument(s)" : ""));

        UI.BeginChild("##insp_code", 0f, border: true);
        UI.Text($"Calls ({info.Calls.Count}):");
        foreach (var c in info.Calls)
        {
            if (c.StartsWith("gml_", StringComparison.Ordinal)) { if (UI.Button(c)) { ShowCode(c); break; } }
            else UI.TextDisabled("  builtin " + c);
        }
        UI.Separator();
        UI.Text($"Strings ({info.Strings.Count}):");
        foreach (var s in info.Strings.Take(300)) UI.TextDisabled("  \"" + s + "\"");
        UI.Separator();
        if (_callersOf != info.Name)
        {
            if (UI.Button("Find callers")) { _callersOf = info.Name; _callerCursor = 0; _callers.Clear(); }
        }
        else
        {
            UI.Text(_callerCursor >= 0 ? $"Callers (searching... {_callerCursor:N0}/{Game.Symbols.Count:N0}):" : $"Callers ({_callers.Count}):");
            foreach (var c in _callers.ToList())
                if (UI.Button(c + "##caller")) { ShowCode(c); break; }
        }
        UI.EndChild();
    }

    // -------------------------------------------------------------- dumping

    public string Dump()
    {
        var sb = new StringBuilder();
        if (_target is not { } t || !t.Exists) return "(nothing selected)";
        sb.AppendLine($"# {_targetLabel} in {Game.Name}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var parents = Parents(_targetObject);
        if (parents.Count > 0) sb.AppendLine("parents: " + string.Join(" < ", parents));
        sb.AppendLine();
        sb.AppendLine("## variables");
        foreach (var n in BuiltinVars.Concat(t.VariableNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase)))
        {
            try { DumpValue(sb, n, t.Get(n), 0); } catch (GmlException ex) { sb.AppendLine($"{n} = <{ex.Message}>"); }
        }
        sb.AppendLine();
        sb.AppendLine("## events");
        foreach (var obj in new[] { _targetObject }.Concat(parents))
        {
            foreach (var e in Events(obj))
            {
                var info = Code.Describe(e);
                if (info == null) continue;
                sb.AppendLine($"{e}  (0x{info.Address:X}, {info.Size} bytes)");
                if (info.Calls.Count > 0) sb.AppendLine("  calls: " + string.Join(", ", info.Calls));
                if (info.Strings.Count > 0) sb.AppendLine("  strings: " + string.Join(", ", info.Strings.Take(40).Select(s => "\"" + s + "\"")));
            }
        }
        return sb.ToString();
    }

    private static void DumpValue(StringBuilder sb, string name, RValue v, int depth)
    {
        string pad = new(' ', depth * 2);
        string type = SafeType(v);
        if (depth < 3 && v.Kind == RValueKind.Array)
        {
            int n = Gml.ArrayLength(v);
            sb.AppendLine($"{pad}{name} = [array of {n}]");
            for (int i = 0; i < n && i < 100; i++) DumpValue(sb, $"[{i}]", Gml.ArrayGet(v, i), depth + 1);
        }
        else if (depth < 3 && type == "struct")
        {
            sb.AppendLine($"{pad}{name} = {{struct}}");
            foreach (var m in Gml.StructNames(v).Take(100)) DumpValue(sb, m, Gml.StructGet(v, m), depth + 1);
        }
        else sb.AppendLine($"{pad}{name} = {ConsoleMod.Format(v)}  [{type}]");
    }

    public void DumpToFile()
    {
        try
        {
            string dir = _dumpDir();
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{Sanitize(_targetObject)}_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, Dump());
            _message = "dumped to " + file;
            _print("inspect: " + _message, 0.7f, 0.9f, 0.7f);
        }
        catch (IOException ex) { _message = ex.Message; }
        catch (UnauthorizedAccessException ex) { _message = ex.Message; }
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ------------------------------------------------------------ in-game

    /// <summary>GameDraw handler: outlines the selection, and while picking, what is under the mouse.</summary>
    public void DrawGame()
    {
        if (!Mapping(out var map)) return;
        if (_target is { } t && t.Exists) Outline(t, map, 0x00FFFF, _targetObject);   // yellow (BGR)
        if (Input.IsPicking)
        {
            double mx = Game.CallBuiltin("device_mouse_x", 0).AsReal, my = Game.CallBuiltin("device_mouse_y", 0).AsReal;
            // What a click here would pick.
            if (InstancesAt(mx, my) is { Count: > 0 } under)
                Outline(under[0], map, 0xFFFF00, ObjectName(under[0]));                        // cyan
        }
    }

    private readonly record struct ViewMap(double X, double Y, double Sx, double Sy);

    // Room coordinates to GUI coordinates, through view 0's camera.
    private static bool Mapping(out ViewMap map)
    {
        map = default;
        try
        {
            var cam = Game.CallBuiltin("view_get_camera", 0);
            double cx = Game.CallBuiltin("camera_get_view_x", cam).AsReal, cy = Game.CallBuiltin("camera_get_view_y", cam).AsReal;
            double cw = Game.CallBuiltin("camera_get_view_width", cam).AsReal, ch = Game.CallBuiltin("camera_get_view_height", cam).AsReal;
            if (!(cw > 0 && ch > 0)) return false;
            map = new ViewMap(cx, cy, GameDraw.GuiWidth / cw, GameDraw.GuiHeight / ch);
            return true;
        }
        catch (GmlException) { return false; }
    }

    private static void Outline(InstanceRef r, ViewMap m, int colour, string label)
    {
        double l = r.Get("bbox_left").AsReal, top = r.Get("bbox_top").AsReal;
        double rt = r.Get("bbox_right").AsReal, b = r.Get("bbox_bottom").AsReal;
        if (double.IsNaN(l) || rt < l) { l = rt = r.Get("x").AsReal; top = b = r.Get("y").AsReal; }
        double x1 = (l - m.X) * m.Sx, y1 = (top - m.Y) * m.Sy, x2 = (rt - m.X) * m.Sx, y2 = (b - m.Y) * m.Sy;
        Game.CallBuiltin("draw_set_alpha", 1);
        Game.CallBuiltin("draw_set_colour", colour);
        Game.CallBuiltin("draw_rectangle", x1 - 1, y1 - 1, x2 + 1, y2 + 1, true);
        Game.CallBuiltin("draw_text", x1, Math.Max(0, y1 - 16), label);
    }

    private void Unfreeze(string? name)
    {
        foreach (var n in name == null ? _frozen.Keys.ToList() : new List<string> { name })
        {
            if (!_frozen.Remove(n, out var v)) continue;
            Values.Free(ref v);
        }
    }

    /// <summary>Drops the selection (and its freezes); cancels pick mode unless told not to.</summary>
    public void Release(bool keepPicking = false)
    {
        if (!keepPicking && Input.IsPicking) Input.CancelPick();
        Unfreeze(null);
        if (_target is { } t) { var id = t.Id; Values.Free(ref id); }
        _target = null;
    }

    internal static string Num(double d) => d.ToString("G15", CultureInfo.InvariantCulture);
}
