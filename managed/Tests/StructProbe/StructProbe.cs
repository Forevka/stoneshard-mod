using CoreLoader;

[assembly: CoreModInfo(typeof(StructProbe.Probe), "Struct probe", "1.0.0", "Lodestone tests")]

namespace StructProbe;

/// <summary>
/// Regression fixture for structs kept across frames. Structs are garbage
/// collected, not reference counted, so a struct only C# knows about would be
/// collected under it. It makes a struct at Present (outside any GML event),
/// keeps it, copies it, then for ten seconds churns garbage and forces
/// collections while checking the struct still reads back. Finally it frees it
/// and checks the loader's root array is empty again.
/// </summary>
public sealed class Probe : CoreMod
{
    private int _frame;
    private RValue _kept = RValue.Undefined;
    private RValue _copy = RValue.Undefined;
    private int _checks, _failures;

    public override void OnUpdate()
    {
        _frame++;
        if (_frame == 120) Start();
        if (_kept.Kind != RValueKind.Object) return;

        // Garbage for the collector: fresh structs nobody keeps.
        for (int i = 0; i < 500; i++) Game.CallBuiltin("json_parse", "{\"junk\": 1}");

        if (_frame % 60 != 0) return;
        if (Game.BuiltinArity("gc_collect") != null) Game.CallBuiltin("gc_collect");

        double a = Game.CallBuiltin("variable_struct_get", _kept, "a").AsReal;
        double b = Game.CallBuiltin("variable_struct_get", _copy, "a").AsReal;
        _checks++;
        if (a != 5 || b != 5) _failures++;
        Log.Info($"check {_checks}: kept.a = {a}, copy.a = {b}, rooted {Values.RootedStructs}");

        if (_checks == 10) Finish();
    }

    private void Start()
    {
        _kept = Values.Keep(Game.CallBuiltin("json_parse", "{\"a\": 5, \"b\": [1, 2, 3]}"));
        _copy = Values.Copy(_kept);
        Log.Info($"kept a struct (kind {_kept.Kind}); copy {(_copy.Pointer == _kept.Pointer ? "shares it" : "differs")}; " +
                 $"rooted {Values.RootedStructs}");
    }

    private void Finish()
    {
        Values.Free(ref _kept);
        Values.Free(ref _copy);
        int left = Values.RootedStructs;
        Log.Info(_failures == 0 && left == 0
            ? $"PASSED: the struct survived {_checks} forced collections; roots released ({left} left)"
            : $"FAILED: {_failures} bad read(s), {left} root(s) left");
    }
}
