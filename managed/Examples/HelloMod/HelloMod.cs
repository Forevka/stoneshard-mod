using CoreLoader;

[assembly: CoreModInfo(typeof(HelloMod.HelloMod), "Hello Mod", "0.2.0", "Lodestone")]

namespace HelloMod;

/// <summary>
/// The smallest useful mod, and deliberately game-agnostic: it only uses what
/// every YYC game has, so it runs unchanged in any of them.
/// </summary>
public sealed class HelloMod : CoreMod
{
    private long _frames;
    private string _filter = "";
    private string _lastResult = "";

    public override void OnInitialize()
    {
        Log.Info($"hello from {Game.Name}: {Game.Symbols.Count} GML functions, " +
                 $"{Game.Symbols.Count(s => s.IsScript)} scripts, " +
                 $"{Game.Symbols.Count(s => s.IsObjectEvent)} object events");
    }

    public override void OnUpdate() => _frames++;

    public override void OnGUI()
    {
        UI.Text($"Frames seen by this mod: {_frames:N0}");
        UI.Separator();

        UI.Text("Symbol search");
        UI.InputText("filter##sym", ref _filter, 128);
        if (_filter.Length >= 3)
        {
            int shown = 0;
            foreach (var s in Game.Symbols)
            {
                if (!s.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                UI.TextDisabled($"{s.Name}  @0x{s.Address:X}");
                if (++shown == 25) { UI.TextDisabled("..."); break; }
            }
        }
        UI.Separator();

        // A builtin call goes through the real GML runtime - the round trip the
        // rest of the API depends on.
        if (UI.Button("irandom(100) via the game's runtime"))
        {
            try { _lastResult = Game.CallBuiltin("irandom", 100).ToString(); }
            catch (GmlException ex) { _lastResult = "failed: " + ex.Message; }
        }
        if (_lastResult.Length > 0)
        {
            UI.SameLine();
            UI.Text(_lastResult);
        }
    }

    public override void OnShutdown() => Log.Info($"bye after {_frames:N0} frames");
}
