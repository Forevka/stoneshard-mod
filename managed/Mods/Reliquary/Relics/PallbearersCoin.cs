using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Panic buttons: the blow that would kill you doesn't, and you pay for it in gear.
/// </summary>
/// <remarks>
/// The design has you wake ten tiles away. Units cannot be moved by writing
/// x/y (the grid puts them back), and the game's own scr_teleport has two
/// arguments whose meaning is not yet confirmed live, so the working default
/// is the closest native thing to "nothing in the room knows where you went":
/// the game's untargetable status for a few turns. The teleport is tried only
/// when <see cref="Experimental"/> is switched on (test host:
/// reliq.coin.teleport on), and the untargetable status is applied either way.
///
/// The toll's "one item destroyed outright" is likewise reduced to breaking it
/// the game's own way (durability 0, the same as everything else worn):
/// destroying a worn item's instance has not been proven safe for the
/// inventory, and a relic must never corrupt a save.
///
/// A killing hit is caught inside scr_save_damage_received (live: a 22-damage
/// bite at 3 HP, and the player lived). Bleeding never passes that hook; its
/// tick is caught as scr_pure_damage returns (see ReliquaryMod.SaveIfDying).
/// </remarks>
internal sealed class PallbearersCoin : Relic
{
    private const int HiddenTurns = 5, FleeTiles = 10;

    /// <summary>Try the game's teleport before falling back. Off until its arguments are confirmed.</summary>
    internal static bool Experimental { get; set; }

    private static readonly string Untargetable = Objects.o_b_untargetable.Name;

    public override string Id => "pallbearers_coin";
    public override string Name => "Pallbearer's Coin";
    public override string Family => "Panic buttons";
    public override int DamageOrder => SavesLife + 1;
    public override string Flavor => "Paid on arrival. Refused on the way back.";
    public override string Boon =>
        $"The blow that would kill you doesn't: you are left at ~lg~1~/~ Health, and for ~y~{HiddenTurns}~/~ turns nothing can " +
        "target you. No daily limit.";
    public override string Toll =>
        "Everything you wear ~r~breaks~/~ - durability 0, weapon included. You survive as a man with nothing that works, " +
        "which is its own kind of death sentence.";

    public override void Install(IRelicHost host)
    {
        if (!TestHost.Enabled) return;
        TestHost.Register("reliq.coin.teleport", args =>
        {
            Experimental = args.Count > 0 && args[0].GetString() is "on" or "true" or "1";
            return Experimental;
        }, "reliq.coin.teleport on|off: lets Pallbearer's Coin try scr_teleport before its fallback (EXPERIMENTAL)");
    }

    public override void OnPlayerDamaged(RelicItem item, InstanceRef player, double amount, RValue attacker) => Save(item, player);

    // Bleeding and other damage over time never pass the damage hook.
    public override void OnPlayerDying(RelicItem item, InstanceRef player) => Save(item, player);

    private static void Save(RelicItem item, InstanceRef player)
    {
        if (!item.Carried || World.Num(player, Objects.o_player.Vars.HP) > 0) return;
        // A player already at 0 at the end of the last frame is already dying: saving
        // them now would pull them back out of the game's death, not avoid it.
        if (ReliquaryMod.LastHp <= 0) return;

        // Life first, before anything below can fail.
        player.Set(Objects.o_player.Vars.HP, 1);

        int broken = 0;
        foreach (var gear in World.WornGear())
            if (World.BreakGear(gear)) broken++;

        bool fled = Experimental && TryFlee(player);
        try { World.RefreshStatus(player, Untargetable, HiddenTurns); }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException) { /* alive is what matters */ }

        item.Set("saves", item.Get("saves") + 1);
        World.Recalculate(player);
        World.Say(fled
            ? $"~y~Pallbearer's Coin~/~ pays your way out. You wake far off at 1 Health; {broken} worn item(s) broke."
            : $"~y~Pallbearer's Coin~/~ pays your way out. You cling on at 1 Health, unseen; {broken} worn item(s) broke.");
    }

    // Ten tiles straight away from the nearest foe; stays put if the game will not move you.
    private static bool TryFlee(InstanceRef player)
    {
        var (px, py) = World.Position(player);
        double dx = 1, dy = 0;
        var foes = World.Hostiles(player);
        if (foes.Count > 0)
        {
            var nearest = foes.OrderBy(f => World.Tiles(player, f)).First();
            var (fx, fy) = World.Position(nearest);
            dx = Math.Sign(px - fx);
            dy = Math.Sign(py - fy);
            if (dx == 0 && dy == 0) dx = 1;
        }
        return World.TryTeleport(player, px + dx * FleeTiles * World.Tile, py + dy * FleeTiles * World.Tile);
    }

    public override string Status(RelicItem item) =>
        item.Get("saves") > 0 ? $"Paid out {item.Get("saves"):0} time(s)" : "Unspent";
}
