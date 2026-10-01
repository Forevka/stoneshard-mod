using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// Makes sure a trial starts at the dungeon's entrance. The generator puts the
/// player at the stairs whose position_tag matches the arrival tag, and the
/// mod sets the one a dungeon entrance would - but on the first trial after a
/// new game or a load the player still arrived at the room's default spot
/// (481, 507), in the dark inside a wall. So once the dungeon has loaded, a
/// player not beside its stairs up is moved to the free cell nearest them,
/// with the game's own scr_invisible_teleport (FastTravel's arrival check).
/// </summary>
internal sealed class Arrival
{
    private const int Cell = 26;
    private const int TimeoutFrames = 60 * 20;
    private const int SettleFrames = 10;
    private const int NearCells = 3;

    private readonly Logger _log;
    private readonly Action _arrived;
    private long _oldPlayer = -1;
    private int _frames, _settle;

    /// <param name="log">The mod's log.</param>
    /// <param name="arrived">Run once the player stands at the entrance (moved or not).</param>
    public Arrival(Logger log, Action arrived)
    {
        _log = log;
        _arrived = arrived;
    }

    public bool Pending { get; private set; }

    /// <summary>The trial's room change has started from the hub.</summary>
    public void Expect()
    {
        Pending = true;
        _oldPlayer = World.Player is { } p ? World.IdKey(p.Id) : -1;
        _frames = _settle = 0;
    }

    public void Cancel() => Pending = false;

    /// <summary>Every frame while pending: once the dungeon's player and grids exist, check where it stands.</summary>
    public void Tick()
    {
        if (!Pending) return;
        if (++_frames > TimeoutFrames)
        {
            Pending = false;
            _log.Warning("no arrival in the trial's dungeon seen; skipping the arrival check");
            // The trial still gets what it adds, if the player is in it after all.
            _arrived();
            return;
        }
        if (World.Player is not { } player || World.IdKey(player.Id) == _oldPlayer || !World.InDungeon) return;
        if (Grids() is not { } g) return;
        // A few frames for the room's own start code to place the player and fill the grids.
        if (++_settle < SettleFrames) return;
        Pending = false;
        try { Settle(player, g); }
        finally { _arrived(); }
    }

    private readonly record struct RoomGrids(double Walls, double Positions, int W, int H);

    private static RoomGrids? Grids()
    {
        if (Objects.o_controller.First is not { } controller) return null;
        var walls = controller.Get("wallgrid");
        var positions = controller.Get("posgrid");
        if (!walls.IsNumber || !positions.IsNumber) return null;
        return new RoomGrids(walls.AsReal, positions.AsReal,
            (int)Builtins.ds_grid_width(walls).AsReal, (int)Builtins.ds_grid_height(walls).AsReal);
    }

    private void Settle(InstanceRef player, RoomGrids g)
    {
        if (Objects.o_dungeon_stairs_up.First is not { } stairs)
        {
            _log.Warning("the trial's dungeon has no stairs up; leaving the player where they are");
            return;
        }
        int sx = (int)Math.Floor(World.Num(stairs, "x") / Cell), sy = (int)Math.Floor(World.Num(stairs, "y") / Cell);
        int px = (int)Math.Floor(World.Num(player, "x") / Cell), py = (int)Math.Floor(World.Num(player, "y") / Cell);
        if (Math.Max(Math.Abs(px - sx), Math.Abs(py - sy)) <= NearCells) return;

        long me = World.IdKey(player.Id);
        bool Free(int x, int y)
        {
            if (x <= 0 || y <= 0 || x >= g.W - 1 || y >= g.H - 1) return false;
            if (Builtins.ds_grid_get(g.Walls, x, y).AsReal != 0) return false;
            long who = World.IdKey(Builtins.ds_grid_get(g.Positions, x, y));
            return who < 0 || who == me;
        }
        // Rings out from the stairs, below them first (the stairs stand in a wall).
        for (int r = 1; r < 12; r++)
        {
            foreach (var (dx, dy) in Ring(r))
            {
                if (!Free(sx + dx, sy + dy)) continue;
                Scripts.scr_invisible_teleport.CallAs(player, (sx + dx) * Cell + Cell / 2, (sy + dy) * Cell + Cell / 2);
                _log.Info($"arrived at {px},{py}, away from the entrance at {sx},{sy}; moved to {sx + dx},{sy + dy}");
                return;
            }
        }
        _log.Warning($"no free cell near the entrance at {sx},{sy}; leaving the player at {px},{py}");
    }

    private static IEnumerable<(int, int)> Ring(int r)
    {
        var cells = new List<(int, int)>();
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r) cells.Add((dx, dy));
        return cells.OrderByDescending(c => c.Item2).ThenBy(c => Math.Abs(c.Item1));
    }
}
