using CoreLoader;
using StoneShard;

namespace Reliquary;

/// <summary>
/// Relics live inside vanilla items. This finds them, keeps them looking like
/// relics, and knows which ones the player is carrying.
/// </summary>
/// <remarks>
/// Established on the running game:
///   * scr_inventory_add_item(objectIndex), run as o_inventory, puts an item in
///     the bag and returns it; scr_inventory_add_weapon(name, rarity) does the
///     same for gear (the StoneshardCheats constructor's path);
///   * the tooltip title is data.Name, the body is mid_text and desc, and the
///     icon is s_index (sprite_index is only the cell background);
///   * an extra key in `data` survives a save and load, but the game re-derives
///     Name, mid_text, desc and s_index on load - hence Refresh, which puts them
///     back whenever they drift;
///   * an item in the bag has o_inventory as its owner; gear worn in a slot
///     also has equipped = true.
/// </remarks>
internal sealed class Carriers
{
    public const string Tag = "reliquary";

    // The two carriers. The agraffe is a plain valuable (no effect of its own);
    // every gear item, the ring included, is an o_inv_slot.
    private const string TallObject = Objects.o_inv_bird_agraphe.Name;
    private const string GearObject = Objects.o_inv_slot.Name;
    private const string RingName = "Copper Ring";
    private const int Common = 1;

    private readonly Dictionary<string, Relic> _relics;
    private readonly Dictionary<string, Sprite> _sprites;
    private readonly Logger _log;

    /// <summary>Every relic item found by the last scan, carried or not.</summary>
    public List<RelicItem> Items { get; private set; } = new();

    public Carriers(IEnumerable<Relic> relics, Dictionary<string, Sprite> sprites, Logger log)
    {
        _relics = relics.ToDictionary(r => r.Id);
        _sprites = sprites;
        _log = log;
    }

    public IEnumerable<RelicItem> Carried => Items.Where(i => i.Carried);

    // ------------------------------------------------------------ giving

    /// <summary>Puts a new relic in the player's bag and returns it.</summary>
    public RelicItem Give(Relic relic)
    {
        var player = World.RequirePlayer();
        var inventory = Inventory() ?? throw new InvalidOperationException("no o_inventory: load a save first");

        RValue made;
        if (relic.Carrier == CarrierKind.Ring)
        {
            // Self is the inventory, other the player: what `with (o_inventory)`
            // in the player's own code gives the script.
            var inv = inventory.Resolve() ?? throw new InvalidOperationException("the inventory is gone");
            var pl = player.Resolve() ?? throw new InvalidOperationException("the player is gone");
            made = Scripts.scr_inventory_add_weapon.CallAs(inv, pl, RingName, Common);
        }
        else
        {
            var obj = GmlObject.Find(TallObject) ?? throw new InvalidOperationException($"no {TallObject}");
            made = Scripts.scr_inventory_add_item.CallAs(inventory, obj.Index);
        }
        if (World.IdKey(made) < 0) throw new InvalidOperationException("the inventory did not take the item (is the bag full?)");

        // In the bag by construction, so it counts from now, not from the next scan.
        var item = new RelicItem(relic, new InstanceRef(made)) { Carried = true };
        if (!item.Ref.Exists) throw new InvalidOperationException("the new item does not exist");
        item.Data.Set(Tag, relic.Id);
        relic.Init(item);
        Refresh(item, force: true);
        Items.Add(item);
        _log.Info($"gave {relic.Name} ({World.IdKey(made)})");
        return item;
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

    // ------------------------------------------------------------ scanning

    /// <summary>
    /// Finds every tagged item among the carriers and works out which ones the
    /// player holds. Cheap enough to run every couple of seconds: a few builtin
    /// calls per item the game has loaded.
    /// </summary>
    public void Scan()
    {
        var found = new List<RelicItem>();
        WeaponType = "";
        WeaponHands = 0;
        ScanObject(TallObject, exact: false, found);
        ScanObject(GearObject, exact: true, found);
        Items = found;
    }

    /// <summary>The wielded weapon's `type` ("bow", "staff"...), from the last scan; "" for none.</summary>
    public string WeaponType { get; private set; } = "";

    /// <summary>1 or 2 for the wielded weapon, 0 for none.</summary>
    public int WeaponHands { get; private set; }

    private void ScanObject(string name, bool exact, List<RelicItem> into)
    {
        if (GmlObject.Find(name) is not { } obj) return;
        foreach (var r in obj.Instances())
        {
            // One odd instance (destroyed mid-walk, a load in progress) costs
            // only itself; the rest of the scan still counts.
            try
            {
                // o_inv_slot's children are every other inventory item; only gear
                // (the object itself) can be a ring carrier.
                if (exact && World.IdKey(r.Get("object_index")) != obj.Index) continue;
                bool equipped = r.Get("equipped").AsBool;
                var data = new DsMap(r.Get("data"));
                if (!data.Exists) continue;

                // The same walk finds the wielded weapon: worn gear whose data says
                // Metatype "Weapon" (is_weapon is set on armor too). Its `type` is
                // the class - "bow", "2hStaff" - and a two-handed one fills both
                // hands, so whichever is seen first is the weapon.
                if (exact && equipped && WeaponHands == 0 && data.Get("Metatype").ToString() == "Weapon" && InBag(r))
                {
                    WeaponType = r.Get("type").ToString();
                    WeaponHands = (int)World.Num(r, "hands", 1);
                }

                var tag = data.Get(Tag);
                if (tag.Kind != RValueKind.String || !_relics.TryGetValue(tag.ToString(), out var relic)) continue;

                var item = new RelicItem(relic, r);
                bool inBag = InBag(r);
                item.Equipped = inBag && equipped;
                item.Carried = inBag;
                into.Add(item);
            }
            catch (GmlException) { }
        }
    }

    // The player's bag (worn gear included) owns the item; a chest, a trader or
    // the quest-reward window would be some other container.
    private static bool InBag(InstanceRef item)
    {
        var owner = item.Get("owner");
        return World.IdKey(owner) >= 0 && Builtins.instance_exists(owner).AsBool &&
               Builtins.object_get_name(new InstanceRef(owner).Get("object_index")).ToString() == Objects.o_inventory.Name;
    }

    // ------------------------------------------------------------ looks

    /// <summary>
    /// Makes the item read as its relic: name, tooltip, icon. Only writes when
    /// something drifted (after a load the game has put the carrier's own back).
    /// </summary>
    public void Refresh(RelicItem item, bool force = false)
    {
        var relic = item.Relic;
        var r = item.Ref;
        var data = item.Data;
        string body = Tooltip(item);

        if (force || data.Get("Name").ToString() != relic.Name) data.Set("Name", relic.Name);
        if (force || r.Get("mid_text").ToString() != body) r.Set("mid_text", body);
        if (force || r.Get("desc").ToString() != relic.Flavor) r.Set("desc", relic.Flavor);
        if (_sprites.TryGetValue(relic.Id, out var sprite) && (force || World.IdKey(r.Get("s_index")) != sprite.Index))
            r.Set("s_index", sprite.Index);
        // A ring stays a Ring (the equipment slot checks its type); the
        // agraffe is re-labelled from "valuable" to what it now is.
        if (relic.Carrier != CarrierKind.Ring && (force || r.Get("type").ToString() != "artifact"))
            r.Set("type", "artifact");
    }

    private static string Tooltip(RelicItem item)
    {
        var relic = item.Relic;
        string status = relic.Status(item);
        string text = $"{relic.Boon}\n\n{relic.Toll}";
        if (status.Length > 0) text += $"\n\n~y~{status}~/~";
        if (relic.Activatable) text += $"\n\nHover and press [{ReliquaryMod.ActivateKeyName}] to activate.";
        return text;
    }
}
