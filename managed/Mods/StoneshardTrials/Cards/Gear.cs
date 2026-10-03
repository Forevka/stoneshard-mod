using CoreLoader;
using StoneShard;

namespace StoneshardTrials.Cards;

/// <summary>
/// The game's gear, for cards that give a piece of it.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25): global.weapons_stat is a
/// ds_map of every weapon, armour piece and jewel by its display name (795),
/// each a map with Tier (1-5), Slot ("axe", "Chest", "Ring"...), rarity
/// ("Common" for the base items, "Unique" and such for named ones), Price and
/// id. scr_inventory_add_weapon(name, rarity) run as the exact o_inventory
/// makes that piece in the bag at a rarity (1 Common, 2 Uncommon, 3 Rare,
/// 4 Epic, 5 Cursed, 6 Unique, 7 Treasure) and returns it, unidentified (its data's identified 0); noone
/// when the bag is full.
/// </remarks>
internal static class Gear
{
    public readonly record struct Piece(string Name, int Tier, string Slot);

    private static List<Piece>? _pieces;

    /// <summary>The base pieces (rarity Common in the table) of a tier.</summary>
    public static List<Piece> OfTier(int tier)
    {
        _pieces ??= Read();
        return _pieces.Where(p => p.Tier == Math.Clamp(tier, 1, 5)).ToList();
    }

    // Some rows are unfinished pieces with no sprites (global.weapons_asset_data
    // gives them inv_sprite -4); giving one stops the game with a message box.
    private static List<Piece> Read()
    {
        var list = new List<Piece>();
        var table = new DsMap(Globals.Get("weapons_stat"));
        var assets = new DsMap(Globals.Get("weapons_asset_data"));
        if (!table.Exists || !assets.Exists) throw new InvalidOperationException("no global.weapons_stat or weapons_asset_data");
        foreach (var (name, value) in table.Entries())
        {
            var row = new DsMap(value);
            if (!row.Exists || row.Get("rarity").ToString() != "Common") continue;
            if (new DsMap(assets.Get(name)) is not { Exists: true } art || art.Get("inv_sprite") is not { IsNumber: true } sprite || sprite.AsReal < 0) continue;
            if (row.Get("Tier") is not { IsNumber: true } tier) continue;
            list.Add(new Piece(name, (int)tier.AsReal, row.Get("Slot").ToString()));
        }
        return list;
    }

    /// <summary>
    /// Makes the piece in the bag at that rarity, identified (the card has
    /// already said what it is). A full bag makes the game throw it on the
    /// ground at the player's feet (unidentified), which is said.
    /// </summary>
    public static void Give(string name, int rarity)
    {
        var inventory = World.Inventory() ?? throw new InvalidOperationException("no o_inventory");
        var made = new InstanceRef(Scripts.scr_inventory_add_weapon.CallAs(inventory, name, rarity));
        // Identified is the item data's flag (the instance variable of that name is not what shows).
        if (World.IdKey(made.Id) < 0 || !made.Exists) World.Say($"~y~Your bag is full~/~: the {name} lies at your feet.");
        else if (new DsMap(made.Get("data")) is { Exists: true } data) data.Set("identified", 1);
    }
}
