using CoreLoader;

[assembly: CoreModInfo(typeof(VarProbeMod.VarProbe), "Var probe", "1.0.0", "CoreLoader tests")]

namespace VarProbeMod;

/// <summary>
/// Regression fixture for instance-variable names. Creates a variable the game
/// has never seen, forces a full compacting GC so any managed buffer the name
/// travelled in is moved or freed, then reads the variable back by name.
/// Expected: the value round-trips (and keeps doing so on later presses).
/// </summary>
public sealed class VarProbe : CoreMod
{
    private int _counter;
    private string _result = "";

    public override void OnGUI()
    {
        var self = Game.CurrentSelf;
        UI.Text($"current self: {self}");
        if (!UI.Button("create var, force GC, read back")) { UI.Text(_result); return; }

        try
        {
            var name = $"coreloader_probe_{_counter}";
            double value = 1000 + _counter++;
            self.Set(name, value);

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            var back = self.Get(name);
            _result = back.AsReal == value ? $"OK: {name} = {back}" : $"MISMATCH: {name} = {back} (wrote {value})";
        }
        catch (Exception ex)
        {
            _result = $"failed: {ex.Message}";
        }
        Log.Info(_result);
        UI.Text(_result);
    }
}
