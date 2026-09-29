using System.Text.Json;

namespace CoreLoader.Runtime;

/// <summary>
/// Collects the instance-variable names of every object while the game runs.
///
/// A YYC exe does not record which variables an object has - they come into
/// being as its code assigns them - so the only reliable source is live
/// instances. The harvester walks the shared object table (<see cref="ObjectTable"/>)
/// a slice per frame (never a hitch), reads the variable names of each object's first live instance, and
/// accumulates them across sessions in Interop/&lt;Game&gt;.vars.json. When it
/// learns something new, the interop is marked stale so the next launch (or
/// the Regenerate button) emits Objects.&lt;obj&gt;.Vars.&lt;name&gt; for it.
/// </summary>
internal static class VarHarvest
{
    private static readonly Logger Log = new("CoreLoader");
    // Budgeted by time, not by objects: one object with hundreds of variables
    // costs as much as a hundred with a few.
    private const double BudgetMs = 1.5;
    private const int PassIntervalFrames = 3600;   // a full pass about once a minute

    private static Dictionary<string, SortedSet<string>>? _known;
    private static IReadOnlyList<GmlObject>? _objects;
    private static int _cursor;
    private static int _idleFrames;
    private static bool _dirty;
    private static int _learnedThisSession;

    public static string FilePath =>
        Path.Combine(Game.LoaderDirectory, "Interop", InteropGenerator.SafeGameName + ".vars.json");

    public static IReadOnlyDictionary<string, SortedSet<string>> Known => _known ??= Load();

    public static int LearnedThisSession => _learnedThisSession;

    public static void Tick()
    {
        // Not before the game has its assets (mods start at the same moment):
        // an object table read too early can come back empty.
        if (Game.BuiltinCount == 0 || !ModManager.Started) return;
        _known ??= Load();

        if (_objects == null)
        {
            // The shared object table, built a slice per frame: asking for it
            // before it is done would finish the scan in this frame, a hitch.
            // It is fixed for the life of the game, so it is taken once.
            ObjectTable.Start();
            if (!ObjectTable.Ready) return;
            var all = GmlObject.All();
            if (all.Count == 0) return;
            _objects = all;
        }

        if (_cursor >= _objects.Count)
        {
            if (_dirty) Flush();
            if (++_idleFrames < PassIntervalFrames) return;
            _idleFrames = 0;
            _cursor = 0;
        }

        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                        (long)(BudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000);
        for (; _cursor < _objects.Count && System.Diagnostics.Stopwatch.GetTimestamp() < deadline; _cursor++)
        {
            var o = _objects[_cursor];
            try
            {
                if (o.InstanceCount == 0) continue;
                var names = o.Instance(0).VariableNames();
                if (names.Count == 0) continue;
                if (!_known.TryGetValue(o.Name, out var set)) _known[o.Name] = set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var n in names)
                    if (set.Add(n)) { _dirty = true; _learnedThisSession++; }
            }
            catch (GmlException) { /* instance vanished mid-read */ }
        }
    }

    public static void Flush()
    {
        if (!_dirty || _known == null) return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                _known.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
            InteropGenerator.MarkStale();
            Log.Info($"interop: {_learnedThisSession} instance variable name(s) learned this session; " +
                     "the interop regenerates on next launch (or press Regenerate)");
        }
        catch (Exception ex)
        {
            Log.Warning($"could not save {FilePath}: {ex.Message}");
        }
    }

    private static Dictionary<string, SortedSet<string>> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(FilePath));
                if (raw != null)
                    return raw.ToDictionary(kv => kv.Key, kv => new SortedSet<string>(kv.Value, StringComparer.Ordinal),
                                            StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"ignoring unreadable {FilePath}: {ex.Message}");
        }
        return new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    }
}
