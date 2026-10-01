using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// The Trial Ticket: a vanilla paper map tagged in its saved data, renamed and
/// re-described, whose Use takes the player back to the hub instead of revealing
/// the map.
/// </summary>
/// <remarks>
/// Established on the running game:
///   * scr_inventory_add_item(objectIndex), run as the exact o_inventory, puts an
///     item in the bag and returns it (Reliquary's path);
///   * a map's Use (its context menu, or right-click) runs its user event 14
///     (o_inv_map_Other_24), which inherits o_inv_consum's: skipping the map's
///     event skips both;
///   * the tooltip's title is data.Name, its body the item's mid_text and desc,
///     which the game re-derives on load - hence Refresh;
///   * Use is offered only while the map has a charge and its idName is not in
///     the character's studied maps (the Osbrook map is studied early on);
///   * an extra key in `data` survives a save and load.
/// </remarks>
internal static class Ticket
{
    public const string Tag = "trials";
    public const string Value = "ticket";
    public const string Name = "Trial Ticket";
    private const string IdName = "trials_ticket";
    private const string Carrier = Objects.o_inv_map_osbrook.Name;
    private const string Body = "Proof that you have survived a trial.\n\n~lg~Use~/~ it to return to the tavern.";
    private const string Flavor = "A strip of vellum stamped with the tavern's mark. The road home is shorter for those who carry one.";

    public static bool Is(InstanceRef item)
    {
        var data = new DsMap(item.Get("data"));
        return data.Exists && data.Get(Tag).ToString() == Value;
    }

    /// <summary>Puts a new ticket in the bag; null when the bag would not take it.</summary>
    public static InstanceRef? Give()
    {
        var inventory = Inventory() ?? throw new InvalidOperationException("no o_inventory");
        var obj = GmlObject.Find(Carrier) ?? throw new InvalidOperationException($"no {Carrier}");
        var made = Scripts.scr_inventory_add_item.CallAs(inventory, obj.Index);
        var item = new InstanceRef(made);
        if (!IsInstance(made) || !item.Exists) return null;
        // Already in the bag: a map that cannot be made a ticket must not stay
        // there as a free Osbrook map, or every retry would add another.
        try
        {
            var data = new DsMap(item.Get("data"));
            if (!data.Exists) throw new InvalidOperationException("the new map has no data map");
            data.Set(Tag, Value);
            Refresh(item);
        }
        catch
        {
            if (item.Resolve() is { } inst) Scripts.scr_item_destroy.CallAs(inst, inst);
            throw;
        }
        return item;
    }

    /// <summary>Every ticket the game has loaded, in the bag or not.</summary>
    public static IEnumerable<InstanceRef> All()
    {
        if (Objects.o_inv_map.Object is not { } maps) yield break;
        foreach (var r in maps.Instances())
        {
            bool tagged;
            try { tagged = Is(r); }
            catch (GmlException) { continue; }
            if (tagged) yield return r;
        }
    }

    /// <summary>Puts the ticket's name and text back when the game has re-derived its own.</summary>
    public static void Refresh(InstanceRef item)
    {
        var data = new DsMap(item.Get("data"));
        if (data.Get("Name").ToString() != Name) data.Set("Name", Name);
        if (item.Get("name").ToString() != Name) item.Set("name", Name);
        // The context menu offers Use only for a map whose idName is not among
        // the maps already studied, and only while it has a charge left.
        if (item.Get("idName").ToString() != IdName) item.Set("idName", IdName);
        if (World.Num(item, "charge") < 1) item.Set("charge", 1);
        if (item.Get("mid_text").ToString() != Body) item.Set("mid_text", Body);
        if (item.Get("desc").ToString() != Flavor) item.Set("desc", Flavor);
    }

    // The bag itself: instance_find(o_inventory) also returns its children
    // (other containers), so the exact object is picked out by name.
    private static InstanceRef? Inventory()
    {
        if (Objects.o_inventory.Object is not { } obj) return null;
        foreach (var r in obj.Instances())
            if (Builtins.object_get_name(r.Get("object_index")).ToString() == Objects.o_inventory.Name) return r;
        return null;
    }

    private static bool IsInstance(RValue v) =>
        v.IsNumber ? v.AsReal >= 0 : v.Kind == RValueKind.Reference && (v.Int64 & 0xFFFFFFFF) < 0x80000000;
}
