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
    /// <summary>The trial under way has an elite master (the game does not save that on the unit), and its prefix.</summary>
    public bool Elite { get; set; }
    public string? EliteAffix { get; set; }
    /// <summary>The trial's master died, but not by the player's hand: it pays half.</summary>
    public bool WonByOther { get; set; }
    /// <summary>Endless trials at tier 5 won so far: each makes the next ones' enemies stronger.</summary>
    public int Tier5Wins { get; set; }
    /// <summary>Crowns settled but not yet handed over, and what the innkeeper says: paid once the player stands in the tavern.</summary>
    public int PendingGold { get; set; }
    public string? PendingNote { get; set; }
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
    /// <summary>The rare chance added to the current stock (Merchant's Favour).</summary>
    public int StockRareBonus { get; set; }
    /// <summary>Counts the traders' stocks; each trader notes which one it has rolled.</summary>
    public int StockSerial { get; set; }
    /// <summary>The character has seen (or skipped) the lore intro.</summary>
    public bool IntroSeen { get; set; }
    /// <summary>The run's starting points (3 ability, 3 attribute) have been given.</summary>
    public bool StartPoints { get; set; }
    /// <summary>Extra cards in the next offer (Second Look).</summary>
    public int ExtraCards { get; set; }
    /// <summary>Blood Money: the next trial's added danger, and what its reward is multiplied by (1: none).</summary>
    public double BloodShift { get; set; }
    public double BloodPay { get; set; } = 1;
    /// <summary>Merchant's Favour: how many more trader refreshes come a tier higher and rarer.</summary>
    public int FavourRefreshes { get; set; }
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
    /// <summary>The tier its cost was paid at (the offer's; the reward's may be higher by its rarity).</summary>
    public int CostTier { get; set; }
    /// <summary>The cost it came with (a CostDef id), and what that cost chose.</summary>
    public string? Cost { get; set; }
    public string? CostDetail { get; set; }
    /// <summary>Its cost ends once this many trials are won (0: it has none, or it lasts).</summary>
    public int CostUntil { get; set; }
    /// <summary>The cost was really paid (a card that failed first never paid it, and nothing is undone).</summary>
    public bool CostPaid { get; set; }
}

/// <summary>Cards on the table after a won trial.</summary>
internal sealed class Offer
{
    public int Trial { get; set; }
    public int Tier { get; set; } = 1;
    public List<string> Cards { get; set; } = new();
    /// <summary>What each card chose when dealt (same order as <see cref="Cards"/>).</summary>
    public List<string?> Details { get; set; } = new();
    /// <summary>Each card's cost (a CostDef id, or null) and what it chose.</summary>
    public List<string?> Costs { get; set; } = new();
    public List<string?> CostDetails { get; set; } = new();
    /// <summary>Each card's rarity: 0 common, 1 rare (its reward a tier higher), 2 legendary (two higher, no drawn cost).</summary>
    public List<int> Rarities { get; set; } = new();
    /// <summary>How many times this offer was rerolled (each reroll costs twice the last).</summary>
    public int Rerolls { get; set; }

    public int RarityAt(int i) => i < Rarities.Count ? Rarities[i] : 0;

    /// <summary>The tier a card's reward is given at: the offer's, raised by its rarity.</summary>
    public int CardTier(int i) => Math.Clamp(Tier + RarityAt(i), 1, 5);
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
