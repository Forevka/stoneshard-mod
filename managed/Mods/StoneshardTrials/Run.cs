using System.Text.Json;
using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>One character's trials: how far they have come and the trial under way.</summary>
internal sealed class Run
{
    /// <summary>Empty until the character's first trial starts; nothing is stored before then.</summary>
    public string Id { get; set; } = "";
    /// <summary>The trial number the next door leads to (or the one under way).</summary>
    public int Level { get; set; } = 1;
    public int TrialsWon { get; set; }
    public int GoldEarned { get; set; }
    public World.Dungeon? Trial { get; set; }
    /// <summary>The trial under way has paid out its ticket.</summary>
    public bool Won { get; set; }
}

/// <summary>
/// A run is kept on its character, as a player attribute holding the run as
/// JSON, which the game saves with the character. So it rolls back with the
/// save: a reload from before a boss fell finds that boss alive and the trial
/// not yet won, and a reload from before a reward finds neither the crowns
/// nor the trial counted. A copy also goes to
/// <c>Mods/StoneshardTrials/characters/&lt;id&gt;.json</c>, for reading, not
/// for loading.
/// </summary>
internal sealed class RunStore
{
    public const string Attribute = "trialsRun";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Compact = new();

    private readonly string _dir;
    private readonly Logger _log;

    public RunStore(string modDirectory, Logger log)
    {
        _dir = Path.Combine(modDirectory, "StoneshardTrials", "characters");
        _log = log;
    }

    /// <summary>The run as the character carries it ("" when it carries none).</summary>
    public static string Stored(InstanceRef player)
    {
        var v = Scripts.scr_atr.CallAs(player, Attribute);
        return v.Kind == RValueKind.String ? v.ToString() : "";
    }

    /// <summary>The character's run; a fresh, unsaved one when it has none yet.</summary>
    public Run Read(string stored)
    {
        if (stored.Length == 0) return new Run();
        try
        {
            if (JsonSerializer.Deserialize<Run>(stored, Compact) is { } run) return run;
        }
        catch (JsonException ex)
        {
            _log.Warning($"the character's run could not be read ({ex.Message}); starting it over");
        }
        return new Run();
    }

    /// <summary>Writes the run onto the character (giving it an id the first time) and mirrors it to disk.</summary>
    public string Save(InstanceRef player, Run run)
    {
        if (run.Id.Length == 0)
        {
            run.Id = Guid.NewGuid().ToString("N");
            _log.Info($"new run {run.Id}");
        }
        string stored = JsonSerializer.Serialize(run, Compact);
        Scripts.scr_atr_set_simple.CallAs(player, Attribute, stored);
        try
        {
            Directory.CreateDirectory(_dir);
            string path = PathOf(run.Id), tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(run, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"could not write the copy of run {run.Id}: {ex.Message}");
        }
        return stored;
    }

    // The id comes back from a save file: only its letters and digits make the
    // name, and one with none (an edited save) gets a name of its own.
    private string PathOf(string id)
    {
        string safe = string.Concat(id.Where(char.IsLetterOrDigit));
        if (safe.Length == 0 || safe.Length > 64) safe = "run" + (uint)id.GetHashCode();
        return Path.Combine(_dir, safe + ".json");
    }
}
