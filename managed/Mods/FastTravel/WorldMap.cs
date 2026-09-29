using CoreLoader;
using StoneShard;

namespace FastTravel;

/// <summary>
/// The world map as the game keeps it, read through the test host on the live
/// game rather than assumed:
///   * the world is a grid of cells; the player's is global.playerGridX/Y;
///   * a cell the player has entered is a key "x_y" in global.locationsRoomsDataMap
///     (the game saves each visited room's state there);
///   * scr_globaltile_get_room(x, y) is the room a cell loads: -1 off the
///     world, r_sea for open water;
///   * the game's own highlight of the hovered cell (o_globalmapHighlight)
///     exists only over cells the fog has lifted from, so the hovered cell is
///     computed from the map's layout instead (see CellAt).
/// </summary>
internal static class WorldMap
{
    public const int Width = 42, Height = 45;

    public static (int X, int Y) PlayerCell =>
        ((int)Globals.Get("playerGridX").AsReal, (int)Globals.Get("playerGridY").AsReal);

    /// <summary>Whether the map screen is open: its controls bar only exists then.</summary>
    public static bool IsOpen => Objects.o_globalmapControlsRender.Object is { InstanceCount: > 0 };

    /// <summary>The cell under the mouse on the open map, or null.</summary>
    /// <remarks>
    /// Computed rather than read from the game's own highlight, which exists
    /// only over cells the fog has lifted from, while a cell next to one you
    /// have visited may still be under it.
    /// </remarks>
    public static (int X, int Y)? HoveredCell =>
        CellAt(Builtins.device_mouse_x_to_gui(0).AsReal, Builtins.device_mouse_y_to_gui(0).AsReal);

    /// <summary>
    /// The cell at a point of the display GUI. The map (o_globalmap) lays its
    /// cells out in the GUI's 960-wide design space, each mapWidth / 42 across
    /// at mapScale, scrolled by mapOffsetX/Y - checked live against the game's
    /// highlight at zoom 1 and 0.8.
    /// </summary>
    public static (int X, int Y)? CellAt(double guiX, double guiY)
    {
        if (Layout() is not { } l) return null;
        int x = (int)Math.Floor((guiX / l.K + l.OffsetX) / l.Cell);
        int y = (int)Math.Floor((guiY / l.K + l.OffsetY) / l.Cell);
        return InBounds(x, y) ? (x, y) : null;
    }

    /// <summary>A cell's top-left corner and size on the display GUI, and the map's zoom.</summary>
    public static (double X, double Y, double Size, double Scale)? CellRect((int X, int Y) cell)
    {
        if (Layout() is not { } l) return null;
        return ((cell.X * l.Cell - l.OffsetX) * l.K, (cell.Y * l.Cell - l.OffsetY) * l.K, l.Cell * l.K, l.Scale);
    }

    private readonly record struct MapLayout(double K, double Cell, double Scale, double OffsetX, double OffsetY);

    private static MapLayout? Layout()
    {
        if (Objects.o_globalmap.First is not { } map) return null;
        var scale = map.Get("mapScale");
        var width = map.Get("mapWidth");
        if (!scale.IsNumber || !width.IsNumber || scale.AsReal <= 0) return null;
        return new MapLayout(MapUi.GuiScale, width.AsReal / Width * scale.AsReal, scale.AsReal,
                             map.Get("mapOffsetX").AsReal, map.Get("mapOffsetY").AsReal);
    }

    /// <summary>Whether the fog has lifted from a cell (the game highlights only those).</summary>
    public static bool Revealed((int X, int Y) cell)
    {
        var fog = Globals.Get("globalmapFogGrid");
        return fog.IsNumber && Builtins.ds_grid_get(fog, cell.X, cell.Y).AsReal > 0;
    }

    public static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>Every cell the player has entered.</summary>
    public static HashSet<(int X, int Y)> Visited()
    {
        var set = new HashSet<(int, int)>();
        var rooms = new DsMap(Globals.Get("locationsRoomsDataMap"));
        if (rooms.Exists)
        {
            foreach (var (key, _) in rooms.Entries())
            {
                var parts = key.Split('_');
                if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)) set.Add((x, y));
            }
        }
        set.Add(PlayerCell);
        return set;
    }

    public static int RoomOf(int x, int y) => (int)Scripts.scr_globaltile_get_room.Call(x, y).AsReal;

    /// <summary>Land the game can load: not off the world, not open sea.</summary>
    public static bool IsLand(int room) =>
        room >= 0 && Builtins.room_get_name(room).ToString() != "r_sea";

    public enum Verdict { Allowed, Here, Unknown, Water }

    /// <summary>
    /// A cell can be travelled to when it is land and the player has been in it
    /// or in one of its eight neighbours.
    /// </summary>
    public static Verdict Judge((int X, int Y) cell, HashSet<(int X, int Y)> visited)
    {
        if (cell == PlayerCell) return Verdict.Here;
        bool near = visited.Contains(cell);
        for (int dx = -1; dx <= 1 && !near; dx++)
            for (int dy = -1; dy <= 1 && !near; dy++)
                near = visited.Contains((cell.X + dx, cell.Y + dy));
        if (!near) return Verdict.Unknown;
        return IsLand(RoomOf(cell.X, cell.Y)) ? Verdict.Allowed : Verdict.Water;
    }
}
