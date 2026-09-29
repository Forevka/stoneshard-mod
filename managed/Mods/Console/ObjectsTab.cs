using CoreLoader;

namespace CoreConsole;

/// <summary>
/// Every object in the game with its live instance count, paging through an
/// object's instances, and a search of all live instances for a variable name -
/// the quickest way to find where a game keeps its gold, health or score. The
/// selected instance's variables are the Inspector's table, so editing and
/// freezing work the same here, and a freeze outlives paging on to the next.
/// </summary>
internal sealed class ObjectsTab
{
    private const int MaxHits = 40;

    private readonly Inspector _inspector;
    private readonly Freezer _freezer;

    private string _objFilter = "";
    private string _search = "";
    private readonly List<(GmlObject Obj, string Line)> _hits = new();
    private string _searchNote = "";

    // Live instance counts, re-read twice a second rather than every frame:
    // there is one instance_number call per object in the game.
    private readonly List<(GmlObject Obj, int Count)> _rows = new();
    private long _rowsAt;
    private string _rowsFilter = "";

    private GmlObject? _selected;
    private int _instanceIndex;
    // The Inspector's selection version as this tab last set it: a different
    // one means a pick or 'inspect' chose another instance, and the object
    // shown here no longer matches the variables under it.
    private int _selectionVersion;

    public ObjectsTab(Inspector inspector, Freezer freezer)
    {
        _inspector = inspector;
        _freezer = freezer;
    }

    public void Draw() =>
        UI.Guarded(DrawUnguarded, ex => UI.TextColored(1f, 0.5f, 0.45f, $"{ex.GetType().Name}: {ex.Message}"));

    private void DrawUnguarded()
    {
        // The table is built a slice per frame from the start (ConsoleMod asks
        // for it); reading it before it is done would stall this frame.
        if (!ObjectTable.Ready)
        {
            ObjectTable.Start();
            UI.Text(ObjectTable.Status);
            UI.ProgressBar(ObjectTable.Progress);
            return;
        }

        if (_selected != null && _inspector.SelectionVersion != _selectionVersion) _selected = null;

        _freezer.Draw();
        DrawSearch();
        UI.Separator();
        if (_selected is { } sel) DrawInstance(sel);
        else DrawObjectList();
    }

    private void DrawSearch()
    {
        bool enter = UI.InputTextEnter("find variable in live instances##search", ref _search, 64);
        UI.SameLine();
        if ((UI.Button("Search") || enter) && _search.Length >= 2)
        {
            _hits.Clear();
            foreach (var (o, name, value) in FindVariable(_search, MaxHits))
                _hits.Add((o, $"{o.Name}[0].{name} = {Clip(ConsoleMod.Format(value))}##{o.Index}.{name}"));
            _searchNote = _hits.Count == 0 ? "no live instance has a variable like that" : "";
        }
        if (_searchNote.Length > 0) UI.TextDisabled(_searchNote);
        foreach (var (o, line) in _hits)
        {
            // Opens the instance with its variables narrowed to the match.
            if (!UI.SmallButton(line)) continue;
            if (o.InstanceCount == 0) { _searchNote = $"no live {o.Name} left"; break; }
            Open(o, 0, _search);
            break;
        }
    }

    private void Open(GmlObject o, int index, string? filter = null)
    {
        _selected = o;
        _instanceIndex = index;
        _inspector.Select(o.Instance(index), filter);
        _selectionVersion = _inspector.SelectionVersion;
    }

    /// <summary>
    /// Variables whose name contains <paramref name="text"/>, read from the first
    /// live instance of each object. Instances of one object share their
    /// variables in practice, so one per object keeps this fast enough to run
    /// on a click. A parent's first instance is often a child's too: an
    /// instance is looked at once. An instance or variable the game refuses to
    /// read (destroyed mid-search, a getter that throws) is skipped, not fatal.
    /// </summary>
    public static List<(GmlObject Obj, string Name, RValue Value)> FindVariable(string text, int limit)
    {
        var hits = new List<(GmlObject, string, RValue)>();
        var seen = new HashSet<long>();
        foreach (var o in GmlObject.All())
        {
            IEnumerable<string> names;
            InstanceRef inst;
            try
            {
                if (o.InstanceCount == 0) continue;
                inst = o.Instance(0);
                if (!seen.Add(inst.Id.Int64)) continue;
                names = inst.VariableNames();
            }
            catch (GmlException) { continue; }
            foreach (var name in names)
            {
                if (!name.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                RValue value;
                try { value = inst.Get(name); } catch (GmlException) { continue; }
                hits.Add((o, name, value));
                if (hits.Count >= limit) return hits;
            }
        }
        return hits;
    }

    private void DrawObjectList()
    {
        UI.InputText("filter objects##objs", ref _objFilter, 64);
        if (_objFilter != _rowsFilter || Environment.TickCount64 - _rowsAt > 500) RefreshRows();
        UI.TextDisabled(_objFilter.Length == 0 ? "objects with live instances; filter to see every object" : $"{_rows.Count} match(es)");

        int shown = 0;
        foreach (var (o, n) in _rows)
        {
            if (UI.Button($"{o.Name} ({n})##{o.Index}") && n > 0) Open(o, 0);
            if (++shown >= 60) { UI.TextDisabled("... filter to see more"); break; }
        }
    }

    private void RefreshRows()
    {
        _rowsAt = Environment.TickCount64;
        _rowsFilter = _objFilter;
        _rows.Clear();
        foreach (var o in GmlObject.All())
        {
            if (_objFilter.Length > 0 && !o.Name.Contains(_objFilter, StringComparison.OrdinalIgnoreCase)) continue;
            int n = o.InstanceCount;
            if (n == 0 && _objFilter.Length == 0) continue;   // unfiltered: only live objects
            _rows.Add((o, n));
        }
    }

    private void DrawInstance(GmlObject o)
    {
        if (UI.Button("< back")) { _selected = null; return; }
        UI.SameLine();
        int count = o.InstanceCount;
        if (count == 0) { UI.TextDisabled($"{o.Name}: no instances left"); return; }
        int index = Math.Clamp(_instanceIndex, 0, count - 1);
        UI.Text($"{o.Name}: instance {index + 1} of {count}");
        UI.SameLine();
        if (UI.Button("prev")) index = Math.Max(0, index - 1);
        UI.SameLine();
        if (UI.Button("next")) index = Math.Min(count - 1, index + 1);
        // The Inspector holds the instance by id: rows shift as instances are
        // created and destroyed, and the one on screen should not change under
        // the user's cursor. Only paging picks another.
        if (index != _instanceIndex) Open(o, index);

        var parents = o.Ancestors().Select(a => a.Name).ToList();
        if (parents.Count > 0) UI.TextDisabled("  <  " + string.Join("  <  ", parents));
        UI.TextColored(0.6f, 0.85f, 1f, _inspector.TargetLabel);
        _inspector.DrawVariables();
    }

    private static string Clip(string s) => s.Length <= 70 ? s : s[..70] + "…";
}
