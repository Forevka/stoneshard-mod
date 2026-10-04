using CoreLoader;

[assembly: CoreModInfo(typeof(ObjectTypeProbe.Probe), "Object type probe", "1.0.0", "Lodestone tests")]
[assembly: CoreModAnyGame]

namespace ObjectTypeProbe;

/// <summary>
/// Regression fixture for runtime-defined objects (<see cref="ObjectTypes"/>).
/// Defines a base type with C# Create and Step events and a child type that
/// only has its own Create (calling the inherited one), spawns one of each,
/// lets them run for a second, and checks through the game's own builtins that
/// the objects exist, are named and parented, count each other's instances
/// and ran their events. Logs PASSED or FAILED with the failed checks.
/// </summary>
public sealed class Probe : CoreMod
{
    private const string BaseName = "o_lodestone_probe";
    private const string ChildName = "o_lodestone_probe_child";

    private ObjectType? _base, _child;
    private InstanceRef _b, _c;
    private int _frame, _startFrame = -1;
    private bool _done;

    public override void OnUpdate()
    {
        if (_done) return;
        _frame++;
        if (_base == null)
        {
            string status = ObjectTypes.Status;
            if (status.StartsWith("not proven", StringComparison.Ordinal)) return;
            if (!ObjectTypes.Available)
            {
                Log.Warning($"object types unavailable here: {status}");
                _done = true;
                return;
            }
            Define();
            return;
        }
        if (_frame - _startFrame == 60) Check();
    }

    private void Define()
    {
        _base = ObjectTypes.Define(BaseName, visible: false)
            .On(GameEvent.Create, e =>
            {
                e.Self.Set("probe_created", 1);
                e.Self.Set("probe_steps", 0);
            })
            .On(GameEvent.Step, e => e.Self.Set("probe_steps", e.Self.Get("probe_steps").AsReal + 1));

        _child = ObjectTypes.Define(ChildName, parent: BaseName, visible: false)
            .On(GameEvent.Create, e =>
            {
                bool ran = e.CallInherited();
                e.Self.Set("probe_child_created", ran ? 1 : -1);
            });

        _b = _base.Create(0, 0);
        _c = _child.Create(0, 0);
        _startFrame = _frame;
        Log.Info($"defined {_base} and {_child}; spawned one of each");
    }

    // An asset index from a builtin's answer: a number, or a 2024+ typed reference.
    private static int Index(RValue v) => v.IsNumber ? (int)v.AsReal : v.Kind == RValueKind.Reference ? (int)(v.Int64 & 0xFFFFFFFF) : -1;

    private void Check()
    {
        _done = true;
        var failures = new List<string>();
        void Expect(bool ok, string what)
        {
            if (!ok) failures.Add(what);
        }

        int b = _base!.Index, c = _child!.Index;
        Expect(Game.CallBuiltin("object_exists", c).AsBool, "object_exists(child)");
        Expect(Game.CallBuiltin("object_get_name", c).ToString() == ChildName, "object_get_name(child)");
        Expect(Index(Game.CallBuiltin("object_get_parent", c)) == b, "object_get_parent(child) == base");
        Expect(Index(Game.CallBuiltin("asset_get_index", BaseName)) == b, "asset_get_index(base)");
        Expect(Game.CallBuiltin("object_is_ancestor", c, b).AsBool, "object_is_ancestor(child, base)");
        Expect(_b.Exists && _c.Exists, "both instances exist");
        Expect(Index(_c.Get("object_index")) == c, "child instance's object_index");
        Expect(_base.InstanceCount == 2, $"instance_number(base) == 2 (got {_base.InstanceCount})");
        Expect(_child.InstanceCount == 1, $"instance_number(child) == 1 (got {_child.InstanceCount})");

        Expect(_b.Get("probe_created").AsReal == 1, "base Create ran");
        Expect(_c.Get("probe_created").AsReal == 1, "child's inherited Create ran (CallInherited)");
        Expect(_c.Get("probe_child_created").AsReal == 1, "child Create ran and found its parent's");
        double bs = _b.Get("probe_steps").AsReal, cs = _c.Get("probe_steps").AsReal;
        Expect(bs >= 30, $"base Step ran (steps {bs})");
        Expect(cs >= 30, $"child inherited Step ran (steps {cs})");

        Log.Info(failures.Count == 0
            ? $"PASSED: {BaseName} = {b}, {ChildName} = {c}; steps {bs} / {cs}"
            : $"FAILED: {string.Join("; ", failures)}");
    }
}
