using CoreLoader;

[assembly: CoreModInfo(typeof(GlobalsEditor.GlobalsEditorMod), "Globals Editor", "1.0.0", "CoreLoader")]

namespace GlobalsEditor;

/// <summary>
/// Browse and edit any YYC game's global variables. Numbers and booleans can be
/// set directly, and a value can be frozen so the game cannot move it - which
/// covers most "infinite gold / never die" cheats in games that keep their
/// state in globals.
/// </summary>
public sealed class GlobalsEditorMod : CoreMod
{
    private IReadOnlyList<string> _names = Array.Empty<string>();
    private string _filter = "";
    private string _error = "";
    private readonly Dictionary<string, string> _edits = new();
    // The value as it was captured, kind included: a frozen bool stays a bool.
    private readonly Dictionary<string, RValue> _frozen = new();
    private int _nextRefresh;

    public override void OnUpdate()
    {
        // Re-assert frozen values every frame, after the game had its turn.
        foreach (var (name, value) in _frozen)
        {
            try
            {
                // variable_global_set would re-create a global the game has
                // removed; a freeze must only hold values that still exist.
                if (Globals.Exists(name)) Globals.Set(name, value);
            }
            catch (GmlException) { /* leave the entry for the user to see */ }
        }
    }

    public override void OnGUI()
    {
        if (UI.Button("Refresh list") || (_names.Count == 0 && _nextRefresh-- <= 0))
        {
            _nextRefresh = 120;
            try
            {
                _names = Globals.Names().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                _error = "";
            }
            catch (Exception ex) { _error = ex.Message; }
        }
        UI.SameLine();
        UI.Text($"{_names.Count} globals, {_frozen.Count} frozen");
        if (_error.Length > 0) UI.TextColored(1f, 0.45f, 0.45f, _error);

        UI.InputText("filter##globals", ref _filter, 100);
        UI.Separator();

        int shown = 0;
        foreach (var name in _names)
        {
            if (_filter.Length > 0 && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (++shown > 60) { UI.TextDisabled("... narrow the filter to see more"); break; }
            DrawRow(name);
        }
    }

    private void DrawRow(string name)
    {
        RValue value;
        try { value = Globals.Get(name); }
        catch (GmlException) { return; }

        UI.PushId(name);
        if (!value.IsNumber)
        {
            string shown = value.Kind == RValueKind.String ? $"\"{value}\"" : $"<{Gml.TypeOf(value)}>";
            UI.TextDisabled($"{name} = {Clip(shown)}");
            UI.PopId();
            return;
        }

        bool frozen = _frozen.ContainsKey(name);
        if (!_edits.TryGetValue(name, out var text)) text = value.AsReal.ToString("R");
        UI.Text($"{name} = {value.AsReal:G10}");
        UI.SameLine();
        UI.InputText("##v", ref text, 32);
        _edits[name] = text;
        UI.SameLine();
        if (UI.Button("Set") && double.TryParse(text, out var v))
        {
            var nv = value.Kind == RValueKind.Bool ? RValue.FromBool(v != 0) : RValue.FromReal(v);
            if (value.Kind == RValueKind.Bool) nv.Kind = RValueKind.Bool;
            Globals.Set(name, nv);
            if (frozen) _frozen[name] = nv;
            _edits.Remove(name);
            Log.Info($"global.{name} = {v}");
        }
        UI.SameLine();
        if (UI.Checkbox("freeze", ref frozen))
        {
            if (frozen) _frozen[name] = value;
            else _frozen.Remove(name);
        }
        UI.PopId();
    }

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
