using CoreLoader;

[assembly: CoreModInfo(typeof(InstanceInspector.InstanceInspectorMod), "Instance Inspector", "1.0.0", "CoreLoader")]

namespace InstanceInspector;

/// <summary>
/// Every object in any YYC game, the instances alive right now, and their
/// variables - readable and, for numbers, editable and freezable. Also searches
/// all live instances for a variable name, which is the quickest way to find
/// where a game keeps its gold, health or score.
/// </summary>
public sealed class InstanceInspectorMod : CoreMod
{
    private IReadOnlyList<GmlObject> _objects = Array.Empty<GmlObject>();
    private string _objFilter = "";
    private string _varFilter = "";
    private string _search = "";
    private GmlObject? _selected;
    private int _instanceIndex;
    private string _error = "";
    private readonly List<string> _searchHits = new();
    private readonly Dictionary<string, string> _edits = new();
    private readonly List<(InstanceRef Inst, string Var, double Value, string Label)> _frozen = new();

    public override void OnUpdate()
    {
        for (int i = _frozen.Count - 1; i >= 0; i--)
        {
            var f = _frozen[i];
            try
            {
                if (f.Inst.Exists) f.Inst.Set(f.Var, f.Value);
                else _frozen.RemoveAt(i);
            }
            catch (GmlException) { _frozen.RemoveAt(i); }
        }
    }

    public override void OnGUI()
    {
        if (_objects.Count == 0 || UI.Button("Rescan objects"))
        {
            try { _objects = GmlObject.All(); _error = ""; }
            catch (Exception ex) { _error = ex.Message; }
        }
        UI.SameLine();
        UI.Text($"{_objects.Count} objects, {_frozen.Count} frozen value(s)");
        if (_error.Length > 0) UI.TextColored(1f, 0.45f, 0.45f, _error);

        DrawSearch();
        UI.Separator();

        if (_selected is { } sel) DrawInstance(sel);
        else DrawObjectList();
    }

    private void DrawSearch()
    {
        UI.InputText("find variable in live instances##search", ref _search, 64);
        UI.SameLine();
        if (UI.Button("Search") && _search.Length >= 2) Search();
        foreach (var hit in _searchHits) UI.TextDisabled(hit);
    }

    // Walks every object with live instances and checks the first instance's
    // variable names. Objects share variables across instances in practice, so
    // one per object keeps this fast enough to run on a click.
    private void Search()
    {
        _searchHits.Clear();
        foreach (var o in _objects)
        {
            if (o.InstanceCount == 0) continue;
            var inst = o.Instance(0);
            foreach (var name in inst.VariableNames())
            {
                if (!name.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
                var v = inst.Get(name);
                _searchHits.Add($"{o.Name}[0].{name} = {Show(v)}");
                if (_searchHits.Count >= 40) return;
            }
        }
        if (_searchHits.Count == 0) _searchHits.Add("no live instance has a variable like that");
    }

    private void DrawObjectList()
    {
        UI.InputText("filter objects##objs", ref _objFilter, 64);
        int shown = 0;
        foreach (var o in _objects)
        {
            if (_objFilter.Length > 0 && !o.Name.Contains(_objFilter, StringComparison.OrdinalIgnoreCase)) continue;
            int n = o.InstanceCount;
            if (n == 0 && _objFilter.Length == 0) continue;   // unfiltered: only live objects
            if (UI.Button($"{o.Name} ({n})##{o.Index}") && n > 0)
            {
                _selected = o;
                _instanceIndex = 0;
            }
            if (++shown >= 60) { UI.TextDisabled("... filter to see more"); break; }
        }
    }

    private void DrawInstance(GmlObject o)
    {
        if (UI.Button("< back")) { _selected = null; return; }
        UI.SameLine();
        int count = o.InstanceCount;
        UI.Text($"{o.Name}: instance {_instanceIndex + 1} of {count}");
        if (count == 0) { UI.TextDisabled("no instances left"); return; }
        _instanceIndex = Math.Clamp(_instanceIndex, 0, count - 1);
        UI.SameLine();
        if (UI.Button("prev")) _instanceIndex = Math.Max(0, _instanceIndex - 1);
        UI.SameLine();
        if (UI.Button("next")) _instanceIndex = Math.Min(count - 1, _instanceIndex + 1);

        UI.InputText("filter variables##vars", ref _varFilter, 64);
        var inst = o.Instance(_instanceIndex);
        int shown = 0;
        foreach (var name in inst.VariableNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (_varFilter.Length > 0 && !name.Contains(_varFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (++shown > 80) { UI.TextDisabled("... filter to see more"); break; }
            DrawVariable(o, inst, name);
        }
    }

    private void DrawVariable(GmlObject o, InstanceRef inst, string name)
    {
        var v = inst.Get(name);
        UI.PushId(name);
        if (!v.IsNumber)
        {
            UI.TextDisabled($"{name} = {Show(v)}");
            UI.PopId();
            return;
        }

        string key = $"{o.Index}:{_instanceIndex}:{name}";
        if (!_edits.TryGetValue(key, out var text)) text = v.AsReal.ToString("R");
        UI.Text($"{name} = {v.AsReal:G10}");
        UI.SameLine();
        UI.InputText("##edit", ref text, 32);
        _edits[key] = text;
        UI.SameLine();
        if (UI.Button("Set") && double.TryParse(text, out var d))
        {
            inst.Set(name, d);
            _edits.Remove(key);
            Log.Info($"{o.Name}[{_instanceIndex}].{name} = {d}");
        }
        UI.SameLine();
        int fi = _frozen.FindIndex(f => f.Label == key);
        bool frozen = fi >= 0;
        if (UI.Checkbox("freeze", ref frozen))
        {
            if (frozen) _frozen.Add((inst, name, v.AsReal, key));
            else if (fi >= 0) _frozen.RemoveAt(fi);
        }
        UI.PopId();
    }

    private static string Show(RValue v)
    {
        string s = v.Kind switch
        {
            RValueKind.String => $"\"{v}\"",
            RValueKind.Array => $"<array[{Gml.ArrayLength(v)}]>",
            _ when v.IsNumber => v.AsReal.ToString("G10"),
            _ => $"<{Gml.TypeOf(v)}>",
        };
        return s.Length <= 70 ? s : s[..70] + "…";
    }
}
