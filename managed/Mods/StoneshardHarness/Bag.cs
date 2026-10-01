using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The player's items. Every item is an o_inv_slot child whose owner is the
/// bag (the exact o_inventory); worn ones have equipped set and an
/// equipped_slot. Items are GUI instances: their x/y are under the GUI camera,
/// and on screen only while the inventory is open.
/// </summary>
/// <remarks>
/// An item's actions are its context menu, as a right click opens it:
/// the item's Mouse_5 event calls scr_create_context_menu, which makes one
/// o_context_button per action (func = the action, e.g. "Use", name = its
/// label, e.g. "Eat"; interact_id = the item). A button's Mouse_4 event
/// performs its action. The same holds for world objects with a context menu.
/// </remarks>
internal static class Bag
{
    public static InstanceRef? Inventory => Gm.Exact(Objects.o_inventory.Name);

    public static List<InstanceRef> Items() => Inventory is { } inv ? Owned(Gm.Id(inv)) : [];

    public static List<InstanceRef> Owned(long owner) =>
        Gm.All(Objects.o_inv_slot.Name).Where(i => Gm.IdKey(i.Get("owner")) == owner).ToList();

    /// <summary>
    /// The other side of an open window: a trader's goods (o_trade_inventory),
    /// a container or corpse being looted (o_container_inventory), a quest
    /// reward (o_reward_container), the stash. Their items are o_inv_slot
    /// children owned by that window; a trader's offer Buy in their menu.
    /// </summary>
    public static IEnumerable<(string Kind, InstanceRef Owner)> Windows()
    {
        foreach (var (obj, kind) in new[]
                 {
                     ("o_trade_inventory", "trade"), ("o_container", "loot"), ("o_container_inventory", "loot"),
                     ("o_reward_container", "reward"), ("o_stash_inventory", "stash"),
                 })
            foreach (var w in Gm.All(obj).Where(w => Gm.ObjectName(w) == obj))
                yield return (kind, w);
    }

    public static bool IsOpen => Objects.o_modificatorsMenu.First is { } m && Gm.Flag(m, "inventoryMenuActive");

    public static object Describe(InstanceRef item, bool withScreen)
    {
        double x = Gm.Num(item, "x"), y = Gm.Num(item, "y");
        double w = Gm.Num(item, "width", Grid.Cell), h = Gm.Num(item, "height", Grid.Cell);
        object? screen = null;
        if (withScreen && Screen.FromGui(x + w / 2, y + h / 2) is { } p) screen = new { x = p.X, y = p.Y };
        var stack = item.Get("stack");
        var charge = item.Get("charge");
        return new
        {
            id = Gm.Id(item),
            obj = Gm.ObjectName(item),
            name = Name(item),
            kind = Gm.Str(item, "type_text"),
            equipped = Gm.Flag(item, "equipped"),
            // equipped_slot names the GUI slot object and is the same for most
            // worn items; slot is the body slot ("Back", "Ring", "Weapon"...).
            slot = Gm.Flag(item, "equipped") ? Gm.Str(item, "slot") : null,
            stack = stack.IsNumber && stack.AsReal > 0 ? stack.AsReal : (double?)null,
            charges = charge.IsNumber && charge.AsReal > 0 ? charge.AsReal : (double?)null,
            identified = Gm.Num(item, "identified", 1) > 0,
            price = Gm.NumOrNull(item, "price"),
            screen,
        };
    }

    // idName is the item's display name for gear; consumables keep their key
    // there ("dumpling"), and their shown name comes from the consumables table.
    public static string Name(InstanceRef item)
    {
        string id = Gm.Str(item, "idName");
        try
        {
            var shown = Scripts.scr_consum_get_name.CallAs(item, id);
            if (shown.Kind == RValueKind.String && shown.ToString() is { Length: > 0 } s && s != id) return s;
        }
        catch (GmlException) { }
        return id;
    }

    // ------------------------------------------------------- context menus

    public static List<InstanceRef> MenuButtons(long ownerId) =>
        Gm.All(Objects.o_context_button.Name)
          .Where(b => Gm.ObjectName(b) == Objects.o_context_button.Name && (ownerId < 0 || Gm.IdKey(b.Get("interact_id")) == ownerId))
          .ToList();

    public static List<object> DescribeMenu(IEnumerable<InstanceRef> buttons) =>
        buttons.Select(b => (object)new { action = Gm.Str(b, "func"), label = Gm.Str(b, "name") }).ToList();

    /// <summary>Presses the button for <paramref name="action"/> (its func or its label, any case).</summary>
    public static string Press(List<InstanceRef> buttons, string action)
    {
        int i = buttons.FindIndex(x => string.Equals(Gm.Str(x, "func"), action, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = buttons.FindIndex(x => string.Equals(Gm.Str(x, "name"), action, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            string have = string.Join(", ", buttons.Select(x => $"{Gm.Str(x, "func")} ({Gm.Str(x, "name")})"));
            CloseMenus();
            throw new ArgumentException($"no action '{action}'; it offers: {have}");
        }
        var button = buttons[i];
        string done = Gm.Str(button, "func");
        if (!Gm.RunEvent(button, "Mouse_4")) throw new InvalidOperationException("the menu button has no press event");
        return done;
    }

    // A context menu is an o_gui_context frame holding the o_context_button
    // rows and an o_context_button_close; all of them go.
    public static void CloseMenus()
    {
        foreach (var name in new[] { "o_context_button", "o_context_button_close", "o_gui_context" })
            foreach (var b in Gm.All(name).ToList())
                Builtins.instance_destroy(b.Id);
    }
}
