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
    /// <summary>The trial under way has an elite master (the game does not save that on the unit).</summary>
    public bool Elite { get; set; }
    /// <summary>The kind of dungeon the last trial was in, so the next can be another.</summary>
    public string? LastKind { get; set; }
    /// <summary>The last trial's dungeon ("x_y"), so an endless run does not take it twice running.</summary>
    public string? LastCell { get; set; }
    /// <summary>Every dungeon of the world has been won: the run is over and the world is open again.</summary>
    public bool Completed { get; set; }
    /// <summary>The danger tier of the last trial won (the traders stock for it).</summary>
    public int LastWonTier { get; set; } = 1;
    /// <summary>How many trials were won when the tavern traders were last stocked; 0 before their first stock.</summary>
    public int StockedAt { get; set; }
    /// <summary>The tier the traders' current stock was made for.</summary>
    public int StockTier { get; set; } = 1;
    /// <summary>Counts the traders' stocks; each trader notes which one it has rolled.</summary>
    public int StockSerial { get; set; }
    /// <summary>The cards taken so far, oldest first.</summary>
    public List<Boon> Boons { get; set; } = new();
    /// <summary>
    /// The cards offered for the last won trial, until one is taken or all are
    /// discarded. Kept on the run so a load or a reload offers the same ones.
    /// </summary>
    public Offer? Offer { get; set; }
}

/// <summary>A card taken: which one, at what tier, after which trial, and what it chose (an item, a skill tree).</summary>
internal sealed class Boon
{
    public string Id { get; set; } = "";
    public int Tier { get; set; } = 1;
    public int Trial { get; set; }
    public string? Detail { get; set; }
    /// <summary>Its cost ends once this many trials are won (0: it has none, or it lasts).</summary>
    public int CostUntil { get; set; }
}

/// <summary>Cards on the table after a won trial.</summary>
internal sealed class Offer
{
    public int Trial { get; set; }
    public int Tier { get; set; } = 1;
    public List<string> Cards { get; set; } = new();
    /// <summary>What each card chose when dealt (same order as <see cref="Cards"/>).</summary>
    public List<string?> Details { get; set; } = new();
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
        Mirror(run);
        return stored;
    }

    /// <summary>
    /// Writes the readable copy. Also called when a load brings back an older
    /// run, so the copy follows the save rather than the last thing written.
    /// </summary>
    public void Mirror(Run run)
    {
        if (run.Id.Length == 0) return;
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
