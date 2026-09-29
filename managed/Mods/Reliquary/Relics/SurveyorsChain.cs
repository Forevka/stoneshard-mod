using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Auras: measured ground is owned ground.
/// </summary>
/// <remarks>
/// The chain remembers where the player stood: the tile from five turns ago
/// becomes a mark, and marks last a while. The mod keeps them itself (tile
/// coordinates in memory), so they reset when the game is loaded or a new
/// character appears - a mark is a short-lived thing anyway.
///
/// Two parts of the design are out of reach and are dropped: the mod cannot
/// steer enemy pathfinding ("the AI paths around them"), and the marks are not
/// drawn on the floor (the game's own tile marks, c_tile_mark, are created by
/// its skills with state the mod cannot fill in reliably). Crossing a mark is
/// announced in the game's log instead.
/// </remarks>
internal sealed class SurveyorsChain : Relic
{
    private const int Lag = 5, MarkTurns = 30, MaxMarks = 12;
    // Held for 2: a 1-turn status applied at the end of a turn can expire in
    // that same turn-end pass before it ever holds anyone.
    private const int Slow = 2, Bleed = 3;
    private const double Heal = 0.02;

    private static readonly string Immob = Objects.o_db_immob.Name;
    private static readonly string BleedLegs = Objects.o_db_bleed_legs.Name;

    // Where the player stood, newest last, and the standing marks with the
    // turns each has left. Tile coordinates only: plain C# data, nothing held
    // from the game across frames.
    private readonly Queue<(int X, int Y)> _trail = new();
    private readonly Dictionary<(int X, int Y), int> _marks = new();

    public override string Id => "surveyors_chain";
    public override string Name => "The Surveyor's Chain";
    public override string Family => "Auras";
    public override string Flavor => "Measured ground is owned ground.";
    public override string Boon =>
        $"The tile you stood on ~y~{Lag}~/~ turns ago stays marked for {MarkTurns} turns. A hostile that stands on a mark is " +
        $"~lg~held~/~ for {Slow} turn and ~lg~bleeds~/~; you standing on one heal ~lg~{Heal * 100:0}%~/~ Max Health.";
    public override string Toll =>
        "It defends the ground you have already left, never the ground you are standing on, and ranged enemies simply never " +
        "cross it. It rewards patience and ~r~punishes chasing~/~.";

    // The trail belongs to the character: it advances once per game turn (the
    // mod's own turn clock, which multi-turn rests advance properly) and only
    // for the copy that counts. A load or new character starts it afresh.
    private long _lastTurn = -1, _playerKey = -1;

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        if (!item.Carried || !item.IsActive || ReliquaryMod.Turns == _lastTurn) return;
        _lastTurn = ReliquaryMod.Turns;
        long playerKey = World.IdKey(player.Id);
        if (playerKey != _playerKey)
        {
            _playerKey = playerKey;
            _trail.Clear();
            _marks.Clear();
        }

        // Age the marks, then lay the one from Lag turns ago.
        foreach (var key in _marks.Keys.ToList())
            if (--_marks[key] <= 0) _marks.Remove(key);
        var here = World.TileOf(player);
        bool arrived = _trail.Count == 0 || _trail.Last() != here;
        _trail.Enqueue(here);
        if (_trail.Count > Lag)
        {
            var old = _trail.Dequeue();
            // Never the ground you are standing on: waiting in place for five
            // turns must not mark your own tile.
            if (old != here) _marks[old] = MarkTurns;
            while (_marks.Count > MaxMarks) _marks.Remove(_marks.MinBy(m => m.Value).Key);
        }
        item.Set("marks", _marks.Count);
        if (_marks.Count == 0) return;

        // Only on stepping onto a mark, not for standing on one: otherwise
        // resting on old ground would be a free regeneration engine.
        if (arrived && _marks.ContainsKey(here)) World.Heal(player, World.MaxHp(player) * Heal);

        int caught = 0;
        foreach (var e in World.Hostiles(player))
        {
            if (!_marks.ContainsKey(World.TileOf(e))) continue;
            try
            {
                World.RefreshStatus(e, Immob, Slow);
                World.RefreshStatus(e, BleedLegs, Bleed);
                caught++;
            }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException) { }
        }
        if (caught > 0) World.Say($"~y~The Surveyor's Chain~/~ catches {caught} foe(s) on measured ground.");
    }

    public override string Status(RelicItem item) => $"Marked tiles: {item.Get("marks"):0}";
}
