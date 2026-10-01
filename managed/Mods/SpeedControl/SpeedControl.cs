using CoreLoader;

[assembly: CoreModInfo(typeof(SpeedControl.SpeedControlMod), "Speed Control", "1.0.0", "Lodestone")]
[assembly: CoreModAnyGame]

namespace SpeedControl;

/// <summary>
/// Runs any GameMaker game faster or slower by changing its target frame rate
/// with game_set_speed - the same knob the game itself uses. The chosen speed
/// is re-asserted if the game resets it (many do on room changes).
/// </summary>
public sealed class SpeedControlMod : CoreMod
{
    private const int GamespeedFps = 0;   // gamespeed_fps

    private double _baseFps;
    private float _multiplier = 1f;
    private bool _active;
    private string _error = "";

    private static readonly float[] Presets = { 0.5f, 1f, 2f, 3f, 5f, 10f };

    public override void OnInitialize()
    {
        _baseFps = ReadFps();
        _multiplier = Config.Get("multiplier", 1f);
        _active = _multiplier != 1f;
        Log.Info($"game runs at {_baseFps} fps; saved multiplier x{_multiplier}");
    }

    public override void OnUpdate()
    {
        if (!_active || _baseFps <= 0) return;
        double want = _baseFps * _multiplier;
        if (Math.Abs(ReadFps() - want) > 0.5) Apply(want);
    }

    public override void OnGUI()
    {
        if (_baseFps <= 0) _baseFps = ReadFps();
        UI.Text($"Game speed: {ReadFps():0} fps (normal {_baseFps:0})");
        if (_error.Length > 0) UI.TextColored(1f, 0.45f, 0.45f, _error);

        if (UI.SliderFloat("multiplier", ref _multiplier, 0.25f, 10f)) { _active = true; Config.Set("multiplier", _multiplier); }
        foreach (var p in Presets)
        {
            if (UI.Button($"{p:0.##}x")) { _multiplier = p; _active = true; Config.Set("multiplier", _multiplier); }
            UI.SameLine();
        }
        if (UI.Button("Reset"))
        {
            _multiplier = 1f;
            _active = false;
            Config.Set("multiplier", 1f);
            Apply(_baseFps);
        }
    }

    public override void OnShutdown()
    {
        if (_active) Apply(_baseFps);
    }

    // Arity differs between runtimes (1 in the manual, 2 in some registries);
    // the spare slot is harmless and simply unused where it is not expected.
    private double ReadFps()
    {
        try
        {
            var arity = Game.BuiltinArity("game_get_speed");
            return (arity == 2
                ? Game.CallBuiltin("game_get_speed", GamespeedFps, 0)
                : Game.CallBuiltin("game_get_speed", GamespeedFps)).AsReal;
        }
        catch (GmlException ex)
        {
            _error = ex.Message;
            return 0;
        }
    }

    private void Apply(double fps)
    {
        try
        {
            Game.CallBuiltin("game_set_speed", fps, GamespeedFps);
            _error = "";
        }
        catch (GmlException ex)
        {
            _error = ex.Message;
        }
    }
}
