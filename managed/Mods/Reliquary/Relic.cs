using CoreLoader;

namespace Reliquary;

/// <summary>Which vanilla item a relic is carried as.</summary>
internal enum CarrierKind
{
    /// <summary>A 1x2 valuable (the Bird Agraffe): no effect of its own, sells, stacks never.</summary>
    Tall,
    /// <summary>A real, equippable ring from the game's armor table.</summary>
    Ring,
}

/// <summary>
/// One relic from the Stoneshard Reliquary design. A relic is behaviour only;
/// the item it lives in is a vanilla carrier tagged <c>data.reliquary = Id</c>,
/// and whatever it has to remember goes in that same data map, which the game
/// saves and loads with the item.
/// </summary>
internal abstract class Relic
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Family { get; }
    public abstract string Flavor { get; }
    /// <summary>What it gives, in the game's tooltip markup (~lg~ green, ~y~ yellow, ~/~ ends).</summary>
    public abstract string Boon { get; }
    /// <summary>What it costs, same markup (~r~ red).</summary>
    public abstract string Toll { get; }

    public virtual CarrierKind Carrier => CarrierKind.Tall;

    /// <summary>
    /// Where this relic stands when several react to the same hit: negators
    /// first (they hand the damage back), then lethal savers (only if the hit
    /// still kills), then observers (they see what really landed).
    /// </summary>
    public virtual int DamageOrder => 50;

    public const int Negates = 10, SavesLife = 20, Observes = 30;

    /// <summary>Whether hovering it and pressing the activate key does something.</summary>
    public virtual bool Activatable => false;

    /// <summary>Fresh item: set up its state keys.</summary>
    public virtual void Init(RelicItem item) { }

    /// <summary>
    /// Once, from OnInitialize: a relic that needs a hook beyond the three the
    /// mod installs (eating, sleeping, trading...) registers it here, through
    /// <paramref name="host"/> so it is guarded and can find its active item.
    /// </summary>
    public virtual void Install(IRelicHost host) { }

    /// <summary>Once per player turn, for every relic item the last scan found (check item.Carried).</summary>
    public virtual void OnTurn(RelicItem item, InstanceRef player) { }

    /// <summary>Once per player turn, item or no item: for effects that live on the character.</summary>
    public virtual void OnPlayerTurn(InstanceRef player) { }

    /// <summary>After every recalculation of the player's stats, item or no item.</summary>
    public virtual void OnPlayerStats(Stats stats) { }

    /// <summary>Every frame, for the copy that counts (see ReliquaryMod.Active). Keep it cheap.</summary>
    public virtual void OnFrame(RelicItem item, InstanceRef player) { }

    /// <summary>
    /// Right after the game recalculated the player's stats: add bonuses here.
    /// The game rebuilds stats from scratch every time, so these never stack up.
    /// </summary>
    public virtual void OnStats(RelicItem item, Stats stats) { }

    /// <summary>The player activated it. Returns what to log; throws to refuse, with the reason.</summary>
    public virtual string Activate(RelicItem item, InstanceRef player) =>
        throw new InvalidOperationException($"{Name} cannot be activated");

    /// <summary>The player lost <paramref name="amount"/> HP to <paramref name="attacker"/> (HP is already down).</summary>
    public virtual void OnPlayerDamaged(RelicItem item, InstanceRef player, double amount, RValue attacker) { }

    /// <summary>
    /// A tick of damage over time (a bleed) has just left the player's HP at
    /// zero, and the game has not acted on it yet (see ReliquaryMod.SaveIfDying).
    /// A save sets HP above zero.
    /// </summary>
    public virtual void OnPlayerDying(RelicItem item, InstanceRef player) { }

    /// <summary>The player took <paramref name="amount"/> HP off <paramref name="victim"/>.</summary>
    public virtual void OnEnemyDamaged(RelicItem item, InstanceRef player, InstanceRef victim, double amount) { }

    /// <summary>A short live line for the tooltip and the panel ("Fill 3/7").</summary>
    public virtual string Status(RelicItem item) => "";

    /// <summary>A live line about an effect that lives on the character, for the panel; "" for none.</summary>
    public virtual string PlayerStatus(InstanceRef player) => "";
}

/// <summary>What a relic's own hooks (see <see cref="Relic.Install"/>) can ask of the mod.</summary>
internal interface IRelicHost
{
    /// <summary>The copy of this relic that counts (the first carried one), or null when none is carried.</summary>
    RelicItem? Active(Relic relic);

    /// <summary>Runs a relic's code so that a throw is logged against it instead of faulting the mod.</summary>
    void Guard(Relic relic, string what, Action action);

    /// <summary>
    /// Registers a hook on a script or event, run through Guard. Use the
    /// interop: <c>host.After(this, Scripts.scr_x, c => ...)</c>.
    /// </summary>
    void Before(Relic relic, ScriptRef script, HookHandler handler);

    void After(Relic relic, ScriptRef script, HookHandler handler);

    /// <summary>The same, on an object event (<c>Objects.o_x.Other_24</c>).</summary>
    void Before(Relic relic, EventRef evt, HookHandler handler);

    void Log(string text);
}

/// <summary>A live relic item: the carrier instance and its data map.</summary>
internal sealed record RelicItem(Relic Relic, InstanceRef Ref)
{
    public DsMap Data => new(Ref.Get("data"));

    /// <summary>In the player's bag, or worn.</summary>
    public bool Carried { get; set; }

    /// <summary>Worn in an equipment slot (rings only).</summary>
    public bool Equipped { get; set; }

    /// <summary>
    /// The copy of its relic that counts (see ReliquaryMod.Active), set on
    /// every scan. Per-turn effects check it so a duplicate cannot apply twice.
    /// </summary>
    public bool IsActive { get; set; }

    // Loading a save destroys every item, and the stats of the new player are
    // calculated before the next scan: an item gone that way reads as fresh
    // and ignores writes, rather than failing every relic until then.
    public double Get(string key, double fallback = 0)
    {
        var data = Data;
        if (!data.Exists) return fallback;
        var v = data.Get("reliq_" + key);
        return v.IsNumber ? v.AsReal : fallback;
    }

    public void Set(string key, double value)
    {
        var data = Data;
        if (data.Exists) data.Set("reliq_" + key, value);
    }
}

/// <summary>Adds to the player's freshly calculated stats, inside the scr_atr_calc hook.</summary>
internal readonly ref struct Stats
{
    private readonly Instance _player;

    public Stats(Instance player) => _player = player;

    public double Get(string stat) => Read(stat) is { } v ? v : 0;

    // A stat the player does not carry (a per-part variant, say) is skipped:
    // one missing name must not cost the relic the rest of its bonuses.
    public void Add(string stat, double delta)
    {
        if (Read(stat) is { } v) _player.Set(stat, v + delta);
    }

    // The game keeps each resistance per body part as well, and a hit reads
    // the part it lands on; the character sheet shows the aggregate.
    private static readonly string[] Parts = { "_Head", "_Tors", "_Hands", "_Legs" };

    /// <summary>Adds to a resistance and to each body part's copy of it.</summary>
    public void AddResistance(string stat, double delta)
    {
        Add(stat, delta);
        foreach (var p in Parts) Add(stat + p, delta);
    }

    public void Cap(string stat, double max)
    {
        if (Read(stat) is { } v && v > max) _player.Set(stat, max);
    }

    private double? Read(string stat)
    {
        try
        {
            var v = _player.Get(stat);
            return v.IsNumber ? v.AsReal : null;
        }
        catch (GmlException) { return null; }
    }
}
