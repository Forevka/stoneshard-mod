using CoreLoader;
using StoneShard;

namespace FastTravel;

/// <summary>
/// Moves the party to another cell of the world map the way a border
/// crossing does, then makes sure it stands somewhere it can walk away from.
/// </summary>
/// <remarks>
/// A border crossing (o_tile_transition) stores where the player will stand as
/// the player's attributes localX/localY, points global.playerGridX/Y at the
/// next cell and calls scr_smoothRoomChange(room, [4], -1, false). The new
/// room's own start code places the player at localX/localY; nothing checks
/// that the spot is free (live: arriving on a market stall or inside a house
/// left the player there). So after arrival the room's wall and position
/// grids (o_controller.wallgrid: 0 is open ground; posgrid: the instance on a
/// cell, or a negative "noone") are read, and a player on a blocked cell, or on
/// one walled off from the room's edges, is moved to the nearest cell that is
/// free and connected to them, with the game's own scr_invisible_teleport,
/// which keeps those grids in step.
/// </remarks>
internal sealed class Traveller
{
    private const int Cell = 26;
    // Every overworld room seen live is 90x90 cells (field, forest, Osbrook,
    // the brewery); a building's inside is smaller (the Osbrook tavern: 39x29).
    private const int RoomCells = 90;
    private const int ArrivalTimeoutFrames = 60 * 20;

    private readonly Logger _log;
    private (int X, int Y)? _pending;
    private (int X, int Y) _from;
    private long _oldPlayer = -1;
    private int _frames;
    private int _settle;

    public Traveller(Logger log) => _log = log;

    public bool Busy => _pending is not null;

    /// <summary>Why travel is refused right now, or null when it can go.</summary>
    public string? Refusal(InstanceRef player)
    {
        if (Busy) return "you are already on your way";
        // Only from the open world: leaving a building or a dungeon by any
        // other way than its door is not something the game ever does.
        if (Grids() is not { } g || g.W != RoomCells || g.H != RoomCells) return "step outside first";
        double range = Num(player, Objects.o_player.Vars.VSN, 8) * Cell;
        var (px, py) = (Num(player, "x"), Num(player, "y"));
        if (Objects.o_enemy.Object is { } enemies)
        {
            foreach (var e in enemies.Instances())
            {
                if (!e.Exists || Num(e, Objects.o_enemy.Vars.is_hostile) <= 0 || Num(e, Objects.o_enemy.Vars.HP) <= 0) continue;
                if (Math.Max(Math.Abs(Num(e, "x") - px), Math.Abs(Num(e, "y") - py)) <= range)
                    return "there are enemies nearby";
            }
        }
        return null;
    }

    // The arrival point is the room's middle unless given (the test host gives
    // one to try the blocked-arrival fix).
    public void Go(InstanceRef player, (int X, int Y) cell, (double X, double Y)? arrival = null)
    {
        int room = WorldMap.RoomOf(cell.X, cell.Y);
        double centre = RoomCells / 2 * Cell + Cell / 2;
        var (ax, ay) = arrival ?? (centre, centre);
        _from = WorldMap.PlayerCell;
        try
        {
            // The same three things a border crossing sets, in its order.
            Scripts.scr_atr_set_simple.CallAs(player, "localX", ax);
            Scripts.scr_atr_set_simple.CallAs(player, "localY", ay);
            SetCell(cell);
            _oldPlayer = IdKey(player.Id);
            Scripts.scr_smoothRoomChange.CallAs(player, room, Builtins.array_create(1, 4), -1, false);
        }
        catch (GmlException)
        {
            // Still in the old room: the map must not think otherwise.
            SetCell(_from);
            throw;
        }
        _pending = cell;
        _frames = 0;
        _settle = 0;
        _log.Info($"travelling to {cell.X},{cell.Y} (room {Builtins.room_get_name(room)})");
    }

    private static void SetCell((int X, int Y) cell)
    {
        Globals.Set("playerGridX", cell.X);
        Globals.Set("playerGridY", cell.Y);
    }

    /// <summary>Every frame: once the new room has its player, check where it stands.</summary>
    public void Tick()
    {
        if (_pending is not { } cell) return;
        if (++_frames > ArrivalTimeoutFrames)
        {
            _pending = null;
            // The same player after all this time: the room never changed.
            if (Objects.o_player.First is { } same && IdKey(same.Id) == _oldPlayer)
            {
                SetCell(_from);
                _log.Warning($"the journey to {cell.X},{cell.Y} never started; the map is back at {_from.X},{_from.Y}");
            }
            else _log.Warning($"no arrival at {cell.X},{cell.Y} seen; skipping the arrival check");
            return;
        }
        if (Objects.o_player.First is not { } player || IdKey(player.Id) == _oldPlayer || WorldMap.PlayerCell != cell) return;
        if (Grids() is null) return;
        // A few frames for the room's own start code to fill its grids.
        if (++_settle < 10) return;
        _pending = null;
        try { Settle(player); }
        catch (GmlException ex) { _log.Warning($"arrival check failed: {ex.Message}"); }
    }

    private readonly record struct RoomGrids(double Walls, double Positions, int W, int H);

    private static RoomGrids? Grids()
    {
        if (Objects.o_controller.First is not { } controller) return null;
        var walls = controller.Get(Objects.o_controller.Vars.wallgrid);
        var positions = controller.Get(Objects.o_controller.Vars.posgrid);
        if (!walls.IsNumber || !positions.IsNumber) return null;
        return new RoomGrids(walls.AsReal, positions.AsReal, (int)Builtins.ds_grid_width(walls).AsReal, (int)Builtins.ds_grid_height(walls).AsReal);
    }

    private void Settle(InstanceRef player)
    {
        if (Grids() is not { } g) return;
        int w = g.W, h = g.H;
        long me = IdKey(player.Id);
        var open = new bool[w, h];
        var taken = new bool[w, h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                open[x, y] = Builtins.ds_grid_get(g.Walls, x, y).AsReal == 0;
                long who = IdKey(Builtins.ds_grid_get(g.Positions, x, y));
                taken[x, y] = who >= 0 && who != me;
            }

        // Open ground joined to the room's edges, where every border crossing
        // arrives: a cell cut off from them is a walled yard or an island.
        // Units standing about do not cut the ground; they only take a cell.
        var reach = new bool[w, h];
        var queue = new Queue<(int X, int Y)>();
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                if ((x == 0 || y == 0 || x == w - 1 || y == h - 1) && open[x, y]) { reach[x, y] = true; queue.Enqueue((x, y)); }
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || reach[nx, ny] || !open[nx, ny]) continue;
                reach[nx, ny] = true;
                queue.Enqueue((nx, ny));
            }
        }

        // Not on the edge itself: stepping there would be a border crossing.
        bool Good(int x, int y) => x > 0 && y > 0 && x < w - 1 && y < h - 1 && reach[x, y] && !taken[x, y];

        int cx = (int)Math.Floor(Num(player, "x") / Cell), cy = (int)Math.Floor(Num(player, "y") / Cell);
        if (Good(cx, cy)) return;
        for (int r = 1; r < Math.Max(w, h); r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || !Good(cx + dx, cy + dy)) continue;
                    Scripts.scr_invisible_teleport.CallAs(player, (cx + dx) * Cell + Cell / 2, (cy + dy) * Cell + Cell / 2);
                    _log.Info($"arrival cell {cx},{cy} was blocked or walled in; moved to {cx + dx},{cy + dy}");
                    return;
                }
            }
        }
        _log.Warning("no free cell joined to the room's edges");
    }

    internal static double Num(InstanceRef r, string name, double fallback = 0)
    {
        var v = r.Get(name);
        return v.IsNumber ? v.AsReal : fallback;
    }

    internal static long IdKey(RValue v) =>
        v.IsNumber ? (long)v.AsReal : v.Kind == RValueKind.Reference ? v.Int64 & 0xFFFFFFFF : -1;
}
