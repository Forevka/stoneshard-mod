using CoreLoader;
//#if (hasInterop)
using INTEROP_PLACEHOLDER;   // generated: Scripts.*, Objects.*, Builtins.*, Assets.*
//#endif

[assembly: CoreModInfo(typeof(MyMod.MyModMod), "MyMod", "0.1.0", "AUTHOR_PLACEHOLDER")]
//#if (hasGame)
[assembly: CoreModGame("GAME_NAME_PLACEHOLDER")]
//#endif

namespace MyMod;

public sealed class MyModMod : CoreMod
{
    private long _frames;
    private float _multiplier = 1f;

    public override void OnInitialize()
    {
        _multiplier = Config.Get("multiplier", 1f);
        Log.Info($"MyMod loaded in {Game.Name} ({Game.Symbols.Count} GML functions)");
//#if (hasInterop)

        // A hook through the generated interop - pick any script from Scripts.*:
        // Scripts.some_script.Before(call => call.SetArg(0, call.GetArg(0).AsReal * _multiplier));
//#endif
    }

    public override void OnUpdate() => _frames++;

    public override void OnGUI()
    {
        UI.Text($"Frames: {_frames:N0}");
        if (UI.SliderFloat("multiplier", ref _multiplier, 1f, 10f)) Config.Set("multiplier", _multiplier);
    }
}
