using CoreLoader;

[assembly: CoreModInfo(typeof(ReflectionProbe.Probe), "Reflection probe", "1.0.0", "CoreLoader tests")]

namespace ReflectionProbe;

/// <summary>
/// Exercises the object/instance reflection API end to end and logs what it
/// finds, without any UI. After a delay it enumerates every object, and for
/// each live one logs the variables whose names contain any keyword from
/// ReflectionProbe.txt (one per line) next to the dll.
/// </summary>
public sealed class Probe : CoreMod
{
    private int _frames;
    private int _runs;

    public override void OnUpdate()
    {
        if (++_frames % 900 != 0 || _runs >= 3) return;   // ~15 s, 30 s, 45 s at 60 fps
        _runs++;

        var file = Path.Combine(Directory, "ReflectionProbe.txt");
        var keywords = File.Exists(file)
            ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray()
            : new[] { "gold", "hp" };

        // "!builtin arg" lines log a builtin's raw result for one numeric argument.
        foreach (var line in keywords.Where(k => k.StartsWith('!')))
        {
            var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !double.TryParse(parts[1], out var arg)) continue;
            try
            {
                var r = Game.CallBuiltin(parts[0], arg);
                Log.Info($"run {_runs}: {parts[0]}({arg}) -> kind {r.Kind}, real {r.Real}, text \"{r}\"");
            }
            catch (Exception ex) { Log.Info($"run {_runs}: {parts[0]}({arg}) threw {ex.Message}"); }
        }
        keywords = keywords.Where(k => !k.StartsWith('!')).ToArray();

        var objects = GmlObject.All();

        // "@name" lines dump every numeric/string variable of that object's first instance.
        foreach (var dump in keywords.Where(k => k.StartsWith('@')).Select(k => k[1..]))
        {
            var o = objects.FirstOrDefault(x => x.Name == dump);
            if (o.Name == null || o.InstanceCount == 0) { Log.Info($"run {_runs}: @{dump}: no live instance"); continue; }
            var inst = o.Instance(0);
            foreach (var name in inst.VariableNames().OrderBy(n => n))
            {
                var v = inst.Get(name);
                if (v.IsNumber || v.Kind == RValueKind.String) Log.Info($"run {_runs}: @{dump}.{name} = {Describe(v)}");
            }
        }
        keywords = keywords.Where(k => !k.StartsWith('@')).ToArray();

        int live = 0, hits = 0;
        foreach (var o in objects)
        {
            int n = o.InstanceCount;
            if (n == 0) continue;
            live++;
            var inst = o.Instance(0);
            foreach (var name in inst.VariableNames())
            {
                if (!keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                var v = inst.Get(name);
                Log.Info($"run {_runs}: {o.Name} (x{n}).{name} = {Describe(v)}");
                if (++hits >= 200) break;
            }
        }
        foreach (var g in Globals.Names())
        {
            if (!keywords.Any(k => g.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            var v = Globals.Get(g);
            if (v.IsNumber || v.Kind == RValueKind.String) Log.Info($"run {_runs}: global.{g} = {Describe(v)}");
        }
        Log.Info($"run {_runs}: {objects.Count} objects, {live} with live instances, {hits} matching variables");
    }

    private static string Describe(RValue v) =>
        v.IsNumber ? v.AsReal.ToString("G10")
        : v.Kind == RValueKind.String ? $"\"{v}\""
        : $"<{Gml.TypeOf(v)}>";
}
