using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The player and the other units (o_enemy: monsters and anything that can
/// fight; o_NPC: townsfolk, traders, animals), as the player would see them.
/// </summary>
/// <remarks>
/// Visibility is the game's own: is_visible(), run as a unit or an object, is
/// what the fog of war shows (it calls scr_checkUnitFogVisible).
/// </remarks>
internal static class Units
{
    // is_visible() alone also answers true for townsfolk walking between rooms,
    // parked far outside this one, so the instance must stand on the room's grid.
    // It reads unit variables (on a corpse it fails), so other things are
    // asked by their tile: scr_checkFogVisibleCoordinates(gx, gy).
    public static bool Visible(InstanceRef r)
    {
        var (w, h) = Grid.Size();
        var c = Grid.CellOf(r);
        if (w > 0 && (c.X < 0 || c.Y < 0 || c.X >= w || c.Y >= h)) return false;
        try
        {
            if (Gm.IsA(r, "o_unit")) return Gm.Truthy(Scripts.is_visible.CallAs(r));
            return Gm.Player is not { } me || Gm.Truthy(Scripts.scr_checkFogVisibleCoordinates.CallAs(me, c.X, c.Y));
        }
        catch (GmlException) { return true; }
    }

    public static double Atr(InstanceRef unit, string key)
    {
        try
        {
            var v = Scripts.scr_atr.CallAs(unit, key);
            return v.IsNumber ? Gm.Round(v.AsReal, 2) : double.NaN;
        }
        catch (GmlException) { return double.NaN; }
    }

    /// <summary>A unit's health: its HP variable, which damage lowers at once.</summary>
    public static double Hp(InstanceRef unit) => Gm.Round(Gm.Num(unit, "HP", Atr(unit, "HP")), 1);

    public static double HealthFraction(InstanceRef unit)
    {
        double max = Gm.Num(unit, "max_hp", 0);
        return max > 0 ? Hp(unit) / max : 1;
    }

    /// <summary>The player's crowns (scr_gold_count), or null when unreadable.</summary>
    public static double? Gold()
    {
        try { return Gm.Player is { } p && Scripts.scr_gold_count.CallAs(p) is { IsNumber: true } g ? g.AsReal : null; }
        catch (GmlException) { return null; }
    }

    /// <summary>
    /// Where an instance is: room x/y, its grid cell, and the desktop pixel to
    /// click it at - the middle of its bounding box, which is where its sprite
    /// is drawn (a barrel's x/y is at its foot, and the tile below it can be
    /// the void outside the room), or its cell's centre when it has no box.
    /// </summary>
    public static object Pos(InstanceRef r)
    {
        var cell = Grid.CellOf(r);
        var (sx, sy) = ClickPoint(r);
        return new { x = Gm.Round(Gm.Num(r, "x")), y = Gm.Round(Gm.Num(r, "y")), gx = cell.X, gy = cell.Y, sx, sy };
    }

    /// <summary>The desktop pixel to click an instance at (see <see cref="Pos"/>).</summary>
    public static (int X, int Y) ClickPoint(InstanceRef r)
    {
        var cell = Grid.CellOf(r);
        var (cx, cy) = Grid.Centre(cell.X, cell.Y);
        double l = Gm.Num(r, "bbox_left"), rt = Gm.Num(r, "bbox_right"), t = Gm.Num(r, "bbox_top"), b = Gm.Num(r, "bbox_bottom");
        if (rt > l && b > t && rt - l < 400 && b - t < 400) (cx, cy) = ((l + rt) / 2, (t + b) / 2);
        return Screen.FromRoom(cx, cy);
    }

    // ------------------------------------------------------------ others

    public static bool IsHostile(InstanceRef e) =>
        Gm.Flag(e, "is_player_enemy") || Gm.Flag(e, "is_agred") || Gm.Flag(e, "is_hostile");

    public static string DisplayName(InstanceRef u)
    {
        // Things on the ground are named as their nameplate names them.
        if (Gm.IsA(u, Objects.o_loot.Name))
        {
            try
            {
                if (Scripts.scr_loot_name.CallAs(u, u.Get("id")) is { Kind: RValueKind.String } n && n.ToString().Length > 0) return n.ToString();
            }
            catch (GmlException) { }
        }
        string name = Gm.Str(u, "name");
        if (name.Length == 0) name = Gm.Str(u, "id_name");
        return name.Length > 0 ? name : Gm.ObjectName(u);
    }

    /// <summary>
    /// Units that fight the player or could: every live o_enemy except the
    /// peaceful o_NPC ones (townsfolk and animals are o_enemy children too),
    /// which count only once they turn hostile. Optionally only the visible ones.
    /// </summary>
    public static List<InstanceRef> Enemies(bool all) =>
        Gm.All(Objects.o_enemy.Name)
          .Where(e => Gm.Num(e, "HP", 1) > 0 && (!Gm.IsA(e, Objects.o_NPC.Name) || IsHostile(e)) &&
                      (all || (Visible(e) && !Ambient(e))))
          .ToList();

    /// <summary>
    /// Birds (o_bird_parent) are o_enemy units, but their click event is empty:
    /// they cannot be attacked and only fly off. Listed only with "all".
    /// </summary>
    public static bool Ambient(InstanceRef e) => Gm.IsA(e, "o_bird_parent");

    public static object DescribeEnemy(InstanceRef e, InstanceRef player, Weapon weapon)
    {
        var me = Grid.CellOf(player);
        var at = Grid.CellOf(e);
        int dist = Gm.Tiles(me, at);
        bool visible = Visible(e);
        var can = new List<string>();
        if (dist <= 1) can.Add("melee");
        if (weapon.Ranged && visible && dist > 1 && dist <= weapon.Range) can.Add("shoot");
        if (dist > 1) can.Add("approach");
        return new
        {
            id = Gm.Id(e),
            obj = Gm.ObjectName(e),
            name = DisplayName(e),
            faction = Gm.Str(e, "faction") is { Length: > 0 } f ? f : Gm.Str(e, "faction_key"),
            tier = Gm.NumOrNull(e, "Tier"),
            level = Gm.NumOrNull(e, "LVL"),
            hp = Gm.Round(Gm.Num(e, "HP")),
            maxHp = Gm.Round(Gm.Num(e, "max_hp")),
            hostile = IsHostile(e),
            aware = Gm.Flag(e, "is_agred") || Gm.IdKey(e.Get("target")) == Gm.Id(player),
            state = Gm.Str(e, "state"),
            visible,
            dist,
            pos = Pos(e),
            can,
        };
    }

    public static List<InstanceRef> Npcs(bool all) =>
        Gm.All(Objects.o_NPC.Name).Where(n => all || Visible(n)).ToList();

    // ------------------------------------------------------------ player

    /// <summary>What the player's equipped weapon allows at range.</summary>
    public readonly record struct Weapon(string Name, bool Ranged, int Range);

    // A bow, crossbow or sling in hand shoots as far as the player sees (VSN);
    // anything else reaches the next tile. Items carry their kind in type
    // ("Bow", "Crossbow") and type_text ("common crossbow").
    public static Weapon Wielded(InstanceRef player)
    {
        foreach (var item in Bag.Items())
        {
            if (!Gm.Flag(item, "equipped")) continue;
            string kind = (Gm.Str(item, "type") + " " + Gm.Str(item, "type_text")).ToLowerInvariant();
            if (kind.Contains("bow") || kind.Contains("sling"))
                return new Weapon(Bag.Name(item), true, Math.Max(2, (int)Gm.Num(player, "VSN", 8)));
        }
        return new Weapon("", false, 1);
    }
}
