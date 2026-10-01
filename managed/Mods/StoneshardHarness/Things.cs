using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The interactive things around the player: doors and stairs, containers,
/// items on the ground, people, corpses, and anything else the game lets the
/// cursor act on.
/// </summary>
internal static class Things
{
    // GML's `all` keyword: instance_find(all, n) walks every live instance.
    private const int All = -3;

    /// <summary>Every live instance within <paramref name="tiles"/> of the cell (room objects, not GUI ones).</summary>
    public static IEnumerable<InstanceRef> Near((int X, int Y) at, int tiles)
    {
        int n = (int)Builtins.instance_number(All).AsReal;
        var (cx, cy) = Grid.Centre(at.X, at.Y);
        double reach = (tiles + 1) * Grid.Cell;
        for (int i = 0; i < n; i++)
        {
            var r = new InstanceRef(Builtins.instance_find(All, i));
            var x = r.Get("x");
            var y = r.Get("y");
            if (!x.IsNumber || !y.IsNumber) continue;
            // GUI instances live around (-5000, -5000), far outside any room.
            if (Math.Abs(x.AsReal - cx) > reach || Math.Abs(y.AsReal - cy) > reach) continue;
            yield return r;
        }
    }

    public enum Kind { None, Door, Border, Container, Item, Npc, Corpse, Interactive }

    public static Kind Classify(InstanceRef r)
    {
        var lineage = Gm.Lineage(r).ToList();
        if (lineage.Count == 0) return Kind.None;
        // Drawing helpers that sit on a tile (a corpse's blood, o_corpseBloodRender) are nothing to act on.
        if (lineage[0].EndsWith("Render", StringComparison.Ordinal)) return Kind.None;
        bool Has(string part) => lineage.Any(n => n.Contains(part, StringComparison.OrdinalIgnoreCase));
        // People and animals are o_enemy children too: told apart first.
        if (lineage.Contains(Objects.o_NPC.Name)) return Kind.Npc;
        if (lineage.Contains(Objects.o_player.Name) || lineage.Contains(Objects.o_enemy.Name)) return Kind.None;
        if (Has("corpse")) return Kind.Corpse;
        // A room's edge is a row of o_tile_transition "doors" to the next world cell.
        if (lineage[0] == "o_tile_transition") return Kind.Border;
        // Only o_transitions_door children change room when used (hx.interact
        // replays their click); other things called doors are just furniture.
        if (lineage.Contains(Objects.o_transitions_door.Name)) return Kind.Door;
        if (lineage.Contains("c_container") || Has("chest")) return Kind.Container;
        if (lineage.Contains(Objects.o_loot.Name)) return Kind.Item;
        if (Gm.Flag(r, "c_cursor_active") || r.Get("interract_event").IsNumber) return Kind.Interactive;
        return Kind.None;
    }

    /// <summary>The interactive things within reach of the player, nearest first.</summary>
    public static List<(InstanceRef Thing, Kind Kind, int Dist)> Around(InstanceRef player, int tiles, bool all)
    {
        var me = Grid.CellOf(player);
        var list = new List<(InstanceRef, Kind, int)>();
        foreach (var r in Near(me, tiles))
        {
            var kind = Classify(r);
            if (kind == Kind.None) continue;
            if (!all && !Units.Visible(r)) continue;
            list.Add((r, kind, Gm.Tiles(me, Grid.CellOf(r))));
        }
        // The edge is dozens of border tiles: only the nearest one is listed.
        var sorted = list.OrderBy(t => t.Item3).ToList();
        int firstBorder = sorted.FindIndex(t => t.Item2 == Kind.Border);
        return sorted.Where((t, i) => t.Item2 != Kind.Border || i == firstBorder).ToList();
    }

    public static object Describe(InstanceRef r, Kind kind, int dist)
    {
        var actions = new List<string>();
        string? exitText = null;
        switch (kind)
        {
            // What hx.interact does with it (see Act.Click): enter, pick up and
            // talk are the game's own click replayed; the rest is walked up to.
            case Kind.Door:
            case Kind.Border:
                exitText = Gm.Str(r, "exit_text") is { Length: > 0 } t ? t : null;
                actions.Add("enter");
                break;
            case Kind.Item:
                actions.Add("pickup");
                break;
            case Kind.Container:
                actions.Add("open");
                break;
            case Kind.Npc:
                actions.Add("talk");
                break;
            default:
                actions.Add("walk-to");
                break;
        }
        return new
        {
            id = Gm.Id(r),
            kind = kind.ToString().ToLowerInvariant(),
            obj = Gm.ObjectName(r),
            name = kind == Kind.Border ? "Edge of the area" : Units.DisplayName(r),
            exit = exitText,
            dist,
            pos = Units.Pos(r),
            actions,
        };
    }
}
