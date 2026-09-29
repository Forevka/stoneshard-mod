using System.Diagnostics;
using CoreLoader.Runtime;

namespace CoreLoader;

/// <summary>
/// The game's object table - every object's index, name and parent - read once
/// and cached for the rest of the session. Game thread only.
/// </summary>
/// <remarks>
/// GameMaker has no "list the objects" builtin: the table is found by asking
/// object_exists / object_get_name for every asset index until a long run of
/// gaps, then object_get_parent for each object. That is thousands of builtin
/// calls, so <see cref="Start"/> spreads them over frames within a few
/// milliseconds each, and <see cref="Ready"/> / <see cref="Progress"/> say how
/// far it got. Objects never change while the game runs, so nothing is re-read.
///
/// <see cref="GmlObject.All"/> and <see cref="GmlObject.Children"/> work at any
/// time: when the table is not there yet they finish the part they need on the
/// spot, in one frame.
/// </remarks>
public static class ObjectTable
{
    // Per-frame share of the game thread. A few ms keeps the frame rate intact
    // while still finishing a large game in a second or two.
    private const double BudgetMs = 4;

    // Asset indices are walked until this many in a row do not exist.
    private const int MissLimit = 64;
    private const int IndexLimit = 100_000;

    private static bool _wanted;

    // The scan: names by asset index (null for gaps) while it runs.
    private static readonly List<string?> ScanNames = new();
    private static int _next, _misses;

    // Set once the scan has finished with something in it.
    private static string?[]? _names;
    private static GmlObject[]? _objects;

    // Parents by asset index (-1 none), filled object by object after the scan.
    private static int[]? _parents;
    private static int _cursor;
    private static Dictionary<int, List<GmlObject>>? _children;

    /// <summary>
    /// Every object's name, index and parent is known. Until then
    /// <see cref="GmlObject.Parent"/> asks the runtime directly.
    /// </summary>
    public static bool Ready => _children != null;

    /// <summary>
    /// 0..1 while the parents are read. The name scan before it has no known
    /// total, so it reads 0; <see cref="Status"/> counts it instead.
    /// </summary>
    public static float Progress =>
        Ready ? 1f : _objects is { Length: > 0 } o ? (float)_cursor / o.Length : 0f;

    /// <summary>What the build is doing, for a progress bar or a log line.</summary>
    public static string Status =>
        Ready ? $"{_objects!.Length} objects"
        : !_wanted ? "not started"
        : _objects != null ? $"reading object parents ({_cursor} / {_objects.Length})..."
        : $"reading the object table ({_next} scanned)...";

    /// <summary>
    /// Starts building the table over the coming frames (from the moment mods
    /// start, if called earlier). Later calls do nothing.
    /// </summary>
    public static void Start() => _wanted = true;

    /// <summary>Finishes the table now, in this frame, instead of over several.</summary>
    public static void Complete()
    {
        Loader.EnsureGameThread();
        _wanted = true;
        while (!Ready)
        {
            if (!Step(double.PositiveInfinity)) return;
        }
    }

    // ------------------------------------------------------------ internals

    /// <summary>Every object in index order, scanning now if the table has not got that far.</summary>
    internal static GmlObject[] Objects()
    {
        if (_objects != null) return _objects;
        Loader.EnsureGameThread();
        while (_objects == null)
            if (!ScanStep()) break;
        return _objects ?? Array.Empty<GmlObject>();
    }

    /// <summary>An object by asset index, from the table when scanned, else from the runtime.</summary>
    internal static GmlObject? At(int index)
    {
        if (index < 0) return null;
        if (_names != null)
            return index < _names.Length && _names[index] is { } n ? new GmlObject(index, n) : null;
        if (!Game.CallBuiltin("object_exists", index).AsBool) return null;
        return new GmlObject(index, Game.CallBuiltin("object_get_name", index).ToString());
    }

    /// <summary>The parent's asset index, or -1.</summary>
    internal static int ParentIndex(int index)
    {
        if (_parents != null && _children != null)
            return index >= 0 && index < _parents.Length ? _parents[index] : -1;
        return AssetIndex(Game.CallBuiltin("object_get_parent", index));
    }

    internal static IReadOnlyList<GmlObject> ChildrenOf(int index)
    {
        Complete();
        return _children != null && _children.TryGetValue(index, out var l) ? l : Array.Empty<GmlObject>();
    }

    /// <summary>
    /// An asset index from what the runtime returned: older runtimes answer
    /// with the plain index, 2024+ runtimes with a typed reference whose low 32
    /// bits are the index. "No object" is negative either way.
    /// </summary>
    internal static int AssetIndex(RValue v) =>
        v.IsNumber ? (int)v.AsReal : v.Kind == RValueKind.Reference ? (int)(v.Int64 & 0xFFFFFFFF) : -1;

    /// <summary>Called every frame on the game thread: one budgeted slice of the build.</summary>
    internal static void Tick()
    {
        // Not before the game has its assets (mods start at the same moment):
        // a table read too early comes back empty.
        if (!_wanted || Ready || !ModManager.Started) return;
        Step(BudgetMs);
    }

    // Works until the budget runs out or the table is done. False when the
    // scan found nothing: the game is not ready, and the next frame tries again.
    private static bool Step(double budgetMs)
    {
        var clock = Stopwatch.StartNew();
        while (!Ready && clock.Elapsed.TotalMilliseconds < budgetMs)
        {
            if (_objects == null)
            {
                if (!ScanStep()) return false;
            }
            else ParentStep();
        }
        return true;
    }

    // One asset index. False when a finished scan came back empty.
    private static bool ScanStep()
    {
        if (_misses >= MissLimit || _next >= IndexLimit)
        {
            EndScan();
            return _objects != null;
        }
        int i = _next++;
        ScanNames.Add(null);
        // A call that fails reads as a gap, so a bad index cannot stop the
        // scan (or, from the frame loop, fail again every frame).
        string? name = null;
        try
        {
            if (Game.CallBuiltin("object_exists", i).AsBool)
                name = Game.CallBuiltin("object_get_name", i).ToString();
        }
        catch (GmlException) { }
        if (name == null) { _misses++; return true; }
        _misses = 0;
        ScanNames[i] = name;
        return true;
    }

    private static void EndScan()
    {
        var objects = new List<GmlObject>();
        for (int i = 0; i < ScanNames.Count; i++)
            if (ScanNames[i] is { } n) objects.Add(new GmlObject(i, n));

        if (objects.Count == 0)
        {
            // Too early: nothing is cached, so a later call scans again.
            ScanNames.Clear();
            _next = _misses = 0;
            return;
        }
        _names = ScanNames.ToArray();
        _objects = objects.ToArray();
        _parents = new int[_names.Length];
        Array.Fill(_parents, -1);
        ScanNames.Clear();

        // A runtime without the builtin: every object stands alone, and the
        // table is done rather than failing once per object.
        if (Game.BuiltinArity("object_get_parent") == null) _cursor = _objects.Length;
        if (_cursor >= _objects.Length) EndParents();
    }

    private static void ParentStep()
    {
        var o = _objects![_cursor++];
        int p;
        try { p = AssetIndex(Game.CallBuiltin("object_get_parent", o.Index)); }
        catch (GmlException) { p = -1; }
        // Only a parent that is itself in the table counts: -100 (no parent)
        // and anything unexpected both read as none.
        _parents![o.Index] = p >= 0 && p < _names!.Length && _names[p] != null ? p : -1;
        if (_cursor >= _objects.Length) EndParents();
    }

    private static void EndParents()
    {
        var children = new Dictionary<int, List<GmlObject>>();
        foreach (var o in _objects!)
        {
            int p = _parents![o.Index];
            if (p < 0) continue;
            if (!children.TryGetValue(p, out var l)) children[p] = l = new List<GmlObject>();
            l.Add(o);
        }
        _children = children;
    }
}
