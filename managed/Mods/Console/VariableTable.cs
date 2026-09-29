using CoreLoader;

namespace CoreConsole;

/// <summary>
/// The variables of one instance, or the game's globals, as a live table:
/// every row editable with a GML expression and freezable, arrays and structs
/// expandable. The Inspector, Objects and Globals tabs all draw one.
/// </summary>
internal sealed class VariableTable
{
    // Built-in instance variables: not listed by variable_instance_get_names,
    // but often the first thing worth looking at.
    internal static readonly string[] BuiltinVars =
    {
        "id", "object_index", "x", "y", "xstart", "ystart", "xprevious", "yprevious",
        "hspeed", "vspeed", "speed", "direction", "friction", "gravity", "gravity_direction",
        "sprite_index", "image_index", "image_speed", "image_number", "image_xscale", "image_yscale",
        "image_angle", "image_alpha", "image_blend", "mask_index", "depth", "layer",
        "visible", "persistent", "solid", "bbox_left", "bbox_top", "bbox_right", "bbox_bottom",
    };

    // Rows are formatted on every refresh, twice a second: a game with
    // thousands of globals would spend its frames on rows nobody can see.
    private const int MaxRows = 300;

    private readonly Evaluator _eval;
    private readonly Freezer _freezer;
    private readonly Action<string, float, float, float> _print;
    private readonly bool _globals;

    private InstanceRef? _target;
    private string _label = "";
    private List<(string Name, string Type, string Text, bool Container)> _rows = new();
    private int _matched;
    private long _rowsAt;
    private string _filter = "";
    private bool _showBuiltins = true;
    private string? _editing;
    private string _editText = "";
    private string _message = "";

    // globals: the table shows the game's globals instead of an instance.
    public VariableTable(Evaluator eval, Freezer freezer, Action<string, float, float, float> print, bool globals)
    {
        _eval = eval;
        _freezer = freezer;
        _print = print;
        _globals = globals;
        _label = globals ? "global" : "";
    }

    /// <summary>
    /// Shows an instance's variables. The caller keeps <paramref name="target"/>'s
    /// id alive for as long as it is shown; <paramref name="label"/> names it in
    /// freezes and the console ("o_enemy 100012"). The filter is replaced by
    /// <paramref name="filter"/>: one typed for the last instance would hide the
    /// new one's variables without saying why.
    /// </summary>
    public void Show(InstanceRef? target, string label, string filter = "")
    {
        _target = target;
        _label = label;
        _filter = filter;
        _editing = null;
        _message = "";
        Refresh();
    }

    private RValue Get(string name) => _target is { } t ? t.Get(name) : Globals.Get(name);

    private void Set(string name, RValue v)
    {
        if (_target is { } t) t.Set(name, v);
        else Globals.Set(name, v);
    }

    public void Refresh()
    {
        _rowsAt = Environment.TickCount64;
        var rows = new List<(string, string, string, bool)>();
        _matched = 0;
        IEnumerable<string> names;
        if (_globals) names = Globals.Names().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        else if (_target is { } t && t.Exists)
        {
            names = t.VariableNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            if (_showBuiltins) names = BuiltinVars.Concat(names);
        }
        else { _rows = rows; return; }

        foreach (var n in names)
        {
            if (_filter.Length > 0 && !n.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (++_matched > MaxRows) continue;
            RValue v;
            try { v = Get(n); } catch (GmlException) { continue; }
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

    internal static string SafeType(RValue v)
    {
        try { return Gml.TypeOf(v); } catch (GmlException) { return v.Kind.ToString(); }
    }

    // ------------------------------------------------------------------ UI

    public void Draw()
    {
        // Values change while the game runs: the table re-reads twice a second.
        if (Environment.TickCount64 - _rowsAt > 500) Refresh();

        if (UI.InputText("filter##vt_filter", ref _filter, 64)) Refresh();
        if (!_globals)
        {
            UI.SameLine();
            if (UI.Checkbox("built-ins", ref _showBuiltins)) Refresh();
        }
        string count = _matched > MaxRows ? $"{MaxRows} of {_matched} variables (narrow the filter)" : $"{_rows.Count} variable(s)";
        UI.TextDisabled($"{count}; edit takes any GML expression (Enter applies); freeze re-applies it every frame");
        if (_message.Length > 0) UI.TextDisabled(_message);

        UI.BeginChild("##vt_rows", 0f, border: true);
        foreach (var (name, type, text, container) in _rows)
        {
            UI.PushId(name);
            bool frozen = _freezer.IsFrozen(_target, name);
            if (UI.Button(_editing == name ? "x" : "edit")) { _editing = _editing == name ? null : name; _editText = type == "string" ? text : StripEllipsis(text); }
            UI.SameLine();
            if (container)
            {
                // "###": the value is display only; the node's identity is the
                // name, so a value that changes does not collapse it.
                if (UI.TreeNode($"{name}  [{type}]  {text}###{name}"))
                {
                    try { DrawChildren(Get(name), 1); } catch (GmlException ex) { UI.TextDisabled(ex.Message); }
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
                if (enter || apply || freeze) Apply(name, freezeToggle: freeze);
            }
            UI.PopId();
        }
        UI.EndChild();
    }

    private void Apply(string name, bool freezeToggle)
    {
        string label = $"{_label}.{name}";
        try
        {
            if (freezeToggle && _freezer.IsFrozen(_target, name))
            {
                _freezer.Unfreeze(_target, name);
                _message = $"unfroze {name}";
                return;
            }
            var v = _eval.Run(_editText);
            // A bool stays a bool, as the game made it: typeof() and saved JSON
            // tell true from 1, and an edit should not change what the game reads.
            if (v.IsNumber && v.Kind != RValueKind.Bool && Get(name).Kind == RValueKind.Bool)
                v = new RValue { Real = v.AsReal != 0 ? 1 : 0, Kind = RValueKind.Bool };
            Set(name, v);
            if (freezeToggle) _freezer.Freeze(_target, name, v, label);
            _message = $"{name} = {ConsoleMod.Format(v)}{(freezeToggle ? " (frozen)" : "")}";
            _print($"set {label} = {_editText}", 0.7f, 0.9f, 0.7f);
            _editing = null;
            Refresh();
        }
        catch (Exception ex)
        {
            // Whatever the expression did wrong is the user's to read, never a
            // reason to disable the console.
            _message = ex is ConsoleError or GmlException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
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
            if (UI.TreeNode($"{label}  [{type}]  {ConsoleMod.Format(v)}###{label}"))
            {
                DrawChildren(v, depth + 1);
                UI.TreePop();
            }
        }
        else UI.Text($"{label} = {ConsoleMod.Format(v)}  [{type}]");
    }
}
