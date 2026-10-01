using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The room's tile grids: o_controller.wallgrid (walls and solid furniture) and
/// posgrid (who stands where), one cell per 26-pixel tile. A unit's grid_x and
/// grid_y are its cell; the cell's centre is grid * 26 + 13 in room pixels.
/// </summary>
internal static class Grid
{
    public const int Cell = 26;

    public static readonly (int Dx, int Dy, string Name)[] Directions =
    [
        (0, -1, "n"), (1, -1, "ne"), (1, 0, "e"), (1, 1, "se"),
        (0, 1, "s"), (-1, 1, "sw"), (-1, 0, "w"), (-1, -1, "nw"),
    ];

    public static (double X, double Y) Centre(int gx, int gy) => (gx * Cell + Cell / 2.0, gy * Cell + Cell / 2.0);

    public static (int X, int Y) CellOf(double x, double y) => ((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell));

    /// <summary>The cell an instance stands on: its grid_x/grid_y when it keeps them, else from its position.</summary>
    public static (int X, int Y) CellOf(InstanceRef r)
    {
        var gx = r.Get("grid_x");
        var gy = r.Get("grid_y");
        if (gx.IsNumber && gy.IsNumber && gx.AsReal >= 0 && gy.AsReal >= 0) return ((int)gx.AsReal, (int)gy.AsReal);
        return CellOf(Gm.Num(r, "x"), Gm.Num(r, "y"));
    }

    private static InstanceRef? Controller => Objects.o_controller.First;

    private static RValue? GridOf(string name)
    {
        if (Controller is not { } c) return null;
        var g = c.Get(name);
        return g.IsNumber && g.AsReal >= 0 && Builtins.ds_exists(g, 5 /* ds_type_grid */).AsBool ? g : (RValue?)null;
    }

    public static (int W, int H) Size()
    {
        if (GridOf("wallgrid") is not { } g) return (0, 0);
        return ((int)Builtins.ds_grid_width(g).AsReal, (int)Builtins.ds_grid_height(g).AsReal);
    }

    private static RValue At(RValue grid, int x, int y) => Builtins.ds_grid_get(grid, x, y);

    /// <summary>
    /// What blocks a step onto the cell: "wall", "unit" (someone stands there),
    /// "edge" (off the grid), or null when it is free.
    /// </summary>
    public static string? Blocked(int x, int y)
    {
        var (w, h) = Size();
        if (w == 0) return "unknown";
        if (x < 0 || y < 0 || x >= w || y >= h) return "edge";
        if (!Walkable(x, y)) return "wall";
        if (Occupant(x, y) >= 0) return "unit";
        return null;
    }

    /// <summary>
    /// Whether the pathfinder can step on the cell. o_controller.cleangrid is
    /// the room's A* grid (a struct), and astar_get_cell(grid, x, y) answers 0
    /// for a walkable cell and -1 for anything else - walls, furniture such as
    /// a counter, and the void outside the room, which wallgrid leaves at 0.
    /// Falls back to wallgrid when the A* grid is missing.
    /// </summary>
    public static bool Walkable(int x, int y)
    {
        if (Controller is { } c && c.Get("cleangrid") is { Kind: RValueKind.Object } astar)
        {
            try { return Scripts.astar_get_cell.CallAs(c, astar, x, y) is { IsNumber: true } v && v.AsReal >= 0; }
            catch (GmlException) { }
        }
        return GridOf("wallgrid") is not { } walls || At(walls, x, y) is not { IsNumber: true } wall || wall.AsReal == 0;
    }

    /// <summary>
    /// Where to stand to use something on <paramref name="target"/>: the player's
    /// own cell when it is already next to it, else the free cell beside it
    /// nearest the player (its own cell when that is free and nothing beside
    /// is). With <paramref name="avoid"/>, never that cell. Null when nothing
    /// around it is free.
    /// </summary>
    public static (int X, int Y)? Beside((int X, int Y) target, (int X, int Y) player, (int X, int Y)? avoid = null)
    {
        if (avoid == null && Gm.Tiles(target, player) <= 1) return player;
        (int X, int Y)? best = null;
        int bestDist = int.MaxValue;
        foreach (var (dx, dy, _) in Directions)
        {
            var c = (target.X + dx, target.Y + dy);
            if (c == avoid || Blocked(c.Item1, c.Item2) != null) continue;
            int d = Gm.Tiles(c, player);
            if (d < bestDist) { best = c; bestDist = d; }
        }
        if (best == null && Blocked(target.X, target.Y) == null) return target;
        return best;
    }

    /// <summary>
    /// The unit standing on the cell, or -1. posgrid names whatever the tile
    /// holds - noone (-4), a unit, but also things one walks under or past,
    /// such as a hanging sign that covers several tiles - so only a unit counts.
    /// </summary>
    public static long Occupant(int x, int y)
    {
        var (w, h) = Size();
        if (x < 0 || y < 0 || x >= w || y >= h || GridOf("posgrid") is not { } units) return -1;
        long id = Gm.IdKey(At(units, x, y));
        if (id <= 0 || Gm.ById(id) is not { } who) return -1;
        return Gm.IsA(who, Objects.o_enemy.Name) || Gm.IsA(who, Objects.o_player.Name) ? id : -1;
    }
}
