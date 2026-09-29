using CoreLoader;

namespace CoreConsole;

/// <summary>
/// Variables held at a value: each frame, after the game had its turn, every
/// frozen variable is set back. One list for globals and instance variables of
/// any number of instances, so a freeze made in the Inspector, the Objects tab
/// or the Globals tab stays until it is lifted or its instance is gone - not
/// just while that instance is on screen.
/// </summary>
internal sealed class Freezer
{
    private sealed class Entry
    {
        // Null for a global. The id is kept (Values.Keep), like the value.
        public InstanceRef? Target;
        public string Name = "";
        public string Label = "";
        public string Key = "";
        public RValue Value;
    }

    private readonly List<Entry> _entries = new();
    private readonly Action<string> _report;

    // report is told when a freeze is dropped on its own (instance gone, set refused).
    public Freezer(Action<string> report) => _report = report;

    public int Count => _entries.Count;

    // The id's raw bits identify the instance whatever the runtime hands out:
    // a plain number on older runtimes, a typed reference on 2024+.
    private static string KeyOf(InstanceRef? target, string name) =>
        target is { } t ? $"{t.Id.Int64}.{name}" : "global." + name;

    public bool IsFrozen(InstanceRef? target, string name)
    {
        string key = KeyOf(target, name);
        return _entries.Exists(e => e.Key == key);
    }

    /// <summary>
    /// Holds <paramref name="name"/> at <paramref name="value"/> from now on.
    /// The value may be this frame's: the freezer keeps its own reference.
    /// </summary>
    public void Freeze(InstanceRef? target, string name, RValue value, string label)
    {
        Unfreeze(target, name);
        _entries.Add(new Entry
        {
            Target = target is { } t ? new InstanceRef(Values.Keep(t.Id)) : null,
            Name = name,
            Label = label,
            Key = KeyOf(target, name),
            Value = Values.Keep(value),
        });
    }

    public void Unfreeze(InstanceRef? target, string name)
    {
        string key = KeyOf(target, name);
        int i = _entries.FindIndex(e => e.Key == key);
        if (i >= 0) Remove(i);
    }

    /// <summary>Lifts every freeze (and releases what they kept).</summary>
    public void Clear()
    {
        for (int i = _entries.Count - 1; i >= 0; i--) Remove(i);
    }

    private void Remove(int i)
    {
        var e = _entries[i];
        _entries.RemoveAt(i);
        Values.Free(ref e.Value);
        if (e.Target is { } t) { var id = t.Id; Values.Free(ref id); }
    }

    /// <summary>Once a frame: sets every frozen variable back to its value.</summary>
    public void Update()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            try
            {
                if (e.Target is { } t)
                {
                    if (t.Exists) t.Set(e.Name, e.Value);
                    else { Remove(i); _report($"unfroze {e.Label}: the instance is gone"); }
                }
                // variable_global_set would re-create a global the game has
                // removed; a freeze only holds values that still exist, and
                // stays listed in case the global comes back.
                else if (Globals.Exists(e.Name)) Globals.Set(e.Name, e.Value);
            }
            catch (GmlException ex) { Remove(i); _report($"unfroze {e.Label}: {ex.Message}"); }
        }
    }

    /// <summary>The frozen variables, each with a button that lifts it.</summary>
    public void Draw()
    {
        if (_entries.Count == 0) return;
        if (!UI.TreeNode($"Frozen ({_entries.Count})###frozen")) return;
        if (UI.SmallButton("unfreeze all")) Clear();
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (UI.SmallButton($"x##unfreeze{i}")) { Remove(i); break; }
            UI.SameLine();
            UI.Text(Line(e));
        }
        UI.TreePop();
    }

    /// <summary>One line per freeze, for the console's 'frozen' command.</summary>
    public IEnumerable<string> Describe() => _entries.Select(Line).ToList();

    // A frozen global the game has removed stays listed (Update explains why),
    // but is marked, so the list does not claim a value is being held.
    private static string Line(Entry e)
    {
        string line = $"{e.Label} = {ConsoleMod.Format(e.Value)}";
        if (e.Target != null) return line;
        bool exists;
        try { exists = Globals.Exists(e.Name); } catch (GmlException) { exists = false; }
        return exists ? line : line + "  (inactive: global gone)";
    }
}
