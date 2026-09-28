using CoreLoader;

[assembly: CoreModInfo(typeof(CoexistProbe.Probe), "Coexist probe", "1.0.0", "CoreLoader tests")]
[assembly: CoreModGame("StoneShard")]

namespace CoexistProbe;

/// <summary>
/// Regression fixture for the hook-engine migration. The loader's own
/// Stoneshard tools watch o_player's and o_enemy's Step events natively; this
/// mod hooks the same two events from C#. Both must fire side by side (before
/// the migration, MinHook refused the second detour on the same target). After
/// counting for a while it unsubscribes, which must leave the native tools
/// running.
/// </summary>
public sealed class Probe : CoreMod
{
    private long _player, _enemy;
    private HookHandle? _hp, _he;
    private int _frames;

    public override void OnInitialize()
    {
        _hp = Hooks.Before("gml_Object_o_player_Step_0", _ => _player++);
        _he = Hooks.Before("gml_Object_o_enemy_Step_0", _ => _enemy++);
        Log.Info("hooked o_player and o_enemy Step alongside the native trackers");
    }

    public override void OnUpdate()
    {
        _frames++;
        if (_frames == 1800 && _hp != null)
        {
            Log.Info($"after 30 s: o_player Step x{_player:N0}, o_enemy Step x{_enemy:N0}; unsubscribing");
            _hp.Dispose();
            _he!.Dispose();
            _hp = _he = null;
        }
    }
}
