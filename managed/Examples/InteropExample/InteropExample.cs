using CoreLoader;
using Dwarf_Eats_Mountain;   // the generated interop for Dwarf Eats Mountain

[assembly: CoreModInfo(typeof(InteropExample.InteropExampleMod), "Interop Example", "1.0.0", "CoreLoader")]
[assembly: CoreModGame("Dwarf Eats Mountain")]

namespace InteropExample;

/// <summary>
/// A mod written against the generated interop instead of strings: scripts,
/// objects, events and builtins are compiler-checked members, and IntelliSense
/// lists everything the game has.
/// </summary>
public sealed class InteropExampleMod : CoreMod
{
    private long _hits;
    private long _unitSteps;
    private string _status = "";

    public override void OnInitialize()
    {
        // A script hook through the generated ref...
        Scripts.dealDamage.After(_ => _hits++);
        // ...and an object event hook, Objects.<object>.<Event>_<n>.
        Objects.oMiner.Step_0.Before(_ => _unitSteps++);
        Log.Info("hooked dealDamage and oMiner.Step_0 through the generated interop");
    }

    public override void OnGUI()
    {
        UI.Text($"dealDamage calls: {_hits:N0}");
        UI.Text($"oMiner Step events: {_unitSteps:N0}");

        // A typed builtin wrapper: the arity comes from this game's own registry.
        if (UI.Button("Builtins.irandom(6) + 1"))
            _status = $"rolled {Builtins.irandom(6).AsReal + 1}";

        // Objects resolve by name at runtime, so an index shuffle in an update is
        // harmless; variable names are harvested constants, not magic strings.
        if (Objects.oSys.First is { } sys)
            UI.Text($"gold (oSys.gold): {sys[Objects.oSys.Vars.gold].AsReal:N0}");

        // Scripts whose argument count was read from their code get a typed
        // Invoke: key_to_string reads exactly one argument.
        if (UI.Button("Scripts.key_to_string.Invoke(32)"))
            _status = $"key 32 is {Scripts.key_to_string.Invoke(32)}";

        if (_status.Length > 0) UI.Text(_status);
    }
}
