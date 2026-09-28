using System.Diagnostics;
using CoreLoader;

[assembly: CoreModInfo(typeof(ValueProbe.Probe), "Value probe", "1.0.0", "CoreLoader tests")]

namespace ValueProbe;

/// <summary>
/// Regression fixture for value lifetime. Every frame it makes ~2,000 fresh GML
/// strings (FromString, object_get_name, string concatenation through the
/// runtime) and throws them away. With the autorelease pool freeing them at
/// the end of each frame, the game's memory stays flat; without it, it climbs
/// by megabytes per second. Logs private memory every 10 s for a minute.
/// </summary>
public sealed class Probe : CoreMod
{
    private int _frame;
    private long _baseline;
    private long _made;

    public override void OnUpdate()
    {
        _frame++;
        if (_frame < 300) return;   // let the game settle first

        for (int i = 0; i < 1000; i++)
        {
            var s = RValue.FromString($"probe-{_made}");              // new string every time
            var joined = Game.CallBuiltin("string_upper", s);           // runtime-made string
            var name = Game.CallBuiltin("object_get_name", i % 20);    // runtime-made string
            _made += 3;
            if (joined.Kind != RValueKind.String || name.Kind != RValueKind.String) break;
        }

        if (_frame == 300) _baseline = Process.GetCurrentProcess().PrivateMemorySize64;
        if (_frame % 600 == 0 && _frame <= 300 + 600 * 6)
        {
            long now = Process.GetCurrentProcess().PrivateMemorySize64;
            Log.Info($"{_made:N0} strings made; pool drained each frame (pending now {Values.Pending}); " +
                     $"private memory {now / 1048576.0:0.0} MB, {(now - _baseline) / 1048576.0:+0.0;-0.0} MB since start");
        }
    }
}
