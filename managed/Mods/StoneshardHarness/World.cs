using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// Where the player is and what is open.
/// </summary>
/// <remarks>
/// GML's current room is a built-in the runtime does not let a mod read, so
/// the room is tracked: every door, stair and border crossing goes through
/// scr_smoothRoomChange(room, ...), whose first argument is recorded (see
/// HarnessMod). Until the first change after the mod starts, the room is
/// unknown, except that a new Adventure starts in the Osbrook tavern
/// (r_taverninside1floor). The world cell (global.playerGridX/Y) and the floor
/// (global.locationFloor, above 0 in a dungeon) are always read live.
/// </remarks>
internal static class World
{
    // Kept in a game global rather than a field, so a hot reload of the mod
    // (a new copy of this class) still knows the room.
    private const string RoomGlobal = "__harness_room";

    /// <summary>The room last changed to, by index; -1 until one is seen.</summary>
    public static int TrackedRoom
    {
        get => Globals.Get(RoomGlobal) is { IsNumber: true } v ? (int)v.AsReal : -1;
        set => Globals.Set(RoomGlobal, value);
    }

    public static string? RoomName =>
        TrackedRoom >= 0 && Builtins.room_exists(TrackedRoom).AsBool ? Builtins.room_get_name(TrackedRoom).ToString() : null;

    public static (int X, int Y) Cell => ((int)Num(Globals.Get("playerGridX")), (int)Num(Globals.Get("playerGridY")));

    public static int Floor => (int)Num(Globals.Get("locationFloor"));

    public static bool InDungeon
    {
        get
        {
            try { return Gm.Player is { } p && Gm.Truthy(Scripts.scr_is_in_dungeon.CallAs(p)); }
            catch (GmlException) { return Floor > 0; }
        }
    }

    private static double Num(RValue v) => v.IsNumber ? v.AsReal : -1;

    /// <summary>Which windows and menus are open, by name.</summary>
    public static List<string> OpenMenus()
    {
        var open = new List<string>();
        if (Objects.o_modificatorsMenu.First is { } m)
        {
            foreach (var (flag, name) in new[]
                     {
                         ("inventoryMenuActive", "inventory"), ("characterMenuActive", "character"), ("skillMenuActive", "skills"),
                         ("journalActive", "journal"), ("mapActive", "map"), ("tradeMenuActive", "trade"),
                         ("escMenuActive", "pause"), ("stashLeftMenuActive", "stash"), ("stashRightMenuActive", "stash-right"),
                         ("cookingMenuActive", "cooking"), ("exploreMenuActive", "explore"), ("bookActive", "book"),
                     })
                if (Gm.Flag(m, flag)) open.Add(name);
        }
        if (Dialogue.Open != null) open.Add("dialogue");
        foreach (var (obj, name) in new[]
                 {
                     ("o_container", "loot"), ("o_container_inventory", "loot"), ("o_reward_container", "reward"), ("o_context_button", "context-menu"),
                     ("o_globalmap_controller", "world-map"), ("o_inventory_trader", "trade"), ("o_confirm_button", "confirm"),
                     ("o_dead_panel", "dead"),
                 })
            if (Gm.Count(obj) > 0 && !open.Contains(name)) open.Add(name);
        return open;
    }
}
