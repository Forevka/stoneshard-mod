using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// The places the trials use, as the game keeps them. Found on the running game
/// (Stoneshard 0.9.4.25):
///   * a new Adventure starts in the Osbrook tavern, room r_taverninside1floor,
///     world cell 32,10. Its street door is an o_Doors_all_small_exit whose
///     position_tag is "r_OSbrooktavern" and whose target is r_Osbrook;
///   * a door leaves in its alarm 7: it sets global.floor_counter to
///     locationFloor + its dungeon_level_incr and global.position_tag to its own
///     position_tag, then calls scr_smoothRoomChange(target, [..], -1, false) as
///     itself. The next room's controller makes floor_counter the new
///     global.locationFloor (above 0 is a dungeon: scr_is_in_dungeon) and puts
///     the player at the door whose position_tag matches;
///   * a dungeon entrance (a c_dungeon_enter, dungeon_level_incr 1) targets
///     r_dungeon_generate, which builds the dungeon of the world cell at
///     global.playerGridX/Y;
///   * scr_glmap_getLocationBySubType("Crypt" | "Catacombs" | "Bastion") lists
///     every such dungeon as {x, y, name, ...}; scr_globaldungeonTierGet(x, y)
///     is its tier (1 near Osbrook, up to 5) and
///     scr_globaltile_dungeon_get("boss_alive", x, y) whether its boss still lives.
/// </summary>
internal static class World
{
    public const string HubRoom = "r_taverninside1floor";
    public const string HubDoorTag = "r_OSbrooktavern";
    public const string DungeonRoom = "r_dungeon_generate";
    public const string StreetRoom = "r_Osbrook";
    /// <summary>The arrival tag a dungeon entrance sets, matched by the stairs up inside.</summary>
    public const string DungeonArrivalTag = "NA";
    public static readonly (int X, int Y) HubCell = (32, 10);
    public static readonly string[] DungeonKinds = { "Crypt", "Catacombs", "Bastion" };

    /// <summary>A world dungeon. Floors defaults to 1 so runs saved before it existed still read.</summary>
    public readonly record struct Dungeon(string Kind, int X, int Y, string Name, int Tier, bool BossAlive, int Floors = 1);

    public static InstanceRef? Player => Objects.o_player.First;

    public static bool InDungeon => Run(Scripts.scr_is_in_dungeon).AsBool;

    /// <summary>The hub's street door, when the player stands in the hub.</summary>
    public static InstanceRef? HubDoor()
    {
        if (Objects.o_Doors_all_small_exit.Object is not { } doors) return null;
        foreach (var d in doors.Instances())
            if (IsHubDoor(d.Get, d.Get("object_index"))) return d;
        return null;
    }

    public static bool IsHubDoor(Instance self) => !self.IsNull && IsHubDoor(self.Get, self.Get("object_index"));

    // Instance lists include children, so the door is told apart by its exact
    // object first - whatever else calls a room change (the player, at every
    // border crossing) has no door variables to read - then by its tag and by
    // leading out to Osbrook rather than in from it.
    private static bool IsHubDoor(Func<string, RValue> get, RValue objectIndex)
    {
        if (objectIndex.AsReal != Objects.o_Doors_all_small_exit.Object?.Index) return false;
        var target = get("target");
        return get("position_tag").ToString() == HubDoorTag && target.IsNumber && (int)target.AsReal == Room(StreetRoom);
    }

    public static List<Dungeon> Dungeons()
    {
        var list = new List<Dungeon>();
        foreach (var kind in DungeonKinds)
        {
            var arr = Run(Scripts.scr_glmap_getLocationBySubType, kind);
            int n = Gml.ArrayLength(arr);
            for (int i = 0; i < n; i++)
            {
                var s = Gml.ArrayGet(arr, i);
                int x = (int)Gml.StructGet(s, "x").AsReal, y = (int)Gml.StructGet(s, "y").AsReal;
                list.Add(new Dungeon(kind, x, y, Gml.StructGet(s, "name").ToString(),
                    Math.Max(1, (int)Run(Scripts.scr_globaldungeonTierGet, x, y).AsReal),
                    Truthy(Run(Scripts.scr_globaltile_dungeon_get, "boss_alive", x, y)),
                    Run(Scripts.scr_globaltile_dungeon_get, "dungeon_amountFloors", x, y) is { IsNumber: true } floors ? Math.Max(1, (int)floors.AsReal) : 1));
            }
        }
        return list;
    }

    public static int Room(string name) => (int)Builtins.asset_get_index(name).AsReal;

    /// <summary>
    /// The player's bag. instance_find(o_inventory) also returns its children
    /// (other containers), so the exact object is picked out by name.
    /// </summary>
    public static InstanceRef? Inventory()
    {
        if (Objects.o_inventory.Object is not { } obj) return null;
        foreach (var r in obj.Instances())
            if (Builtins.object_get_name(r.Get("object_index")).ToString() == Objects.o_inventory.Name) return r;
        return null;
    }

    // Inside a hook the loader has no current instance to run a script as and
    // refuses the call, so world queries run as the player.
    private static RValue Run(ScriptRef script, params RValue[] args) =>
        Player is { } p ? script.CallAs(p, args) : script.Call(args);

    public static void SetCell((int X, int Y) cell)
    {
        Globals.Set("playerGridX", cell.X);
        Globals.Set("playerGridY", cell.Y);
    }

    public static (int X, int Y) Cell =>
        ((int)Globals.Get("playerGridX").AsReal, (int)Globals.Get("playerGridY").AsReal);

    /// <summary>An instance id as a number: the game hands them out as numbers or as references.</summary>
    public static long IdKey(RValue v) =>
        v.IsNumber ? (long)v.AsReal : v.Kind == RValueKind.Reference ? v.Int64 & 0xFFFFFFFF : -1;

    // The game stores flags as 0/1, true/false or undefined depending on who wrote them.
    public static bool Truthy(RValue v) => v.IsNumber ? v.AsReal > 0 : v.Kind != RValueKind.Undefined && v.AsBool;

    public static double Num(InstanceRef r, string name, double fallback = 0)
    {
        var v = r.Get(name);
        return v.IsNumber ? v.AsReal : fallback;
    }

    /// <summary>A line in the game's own action log.</summary>
    public static void Say(string text)
    {
        try
        {
            if (Player is { } p) Scripts.scr_actionsLogAddMessage.CallAs(p, text);
        }
        catch (GmlException) { }
    }
}
