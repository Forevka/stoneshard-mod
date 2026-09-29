using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Vessels: it fills with every point of damage you take, and burning it gives
/// some of that back - at the price of the candle itself.
/// </summary>
/// <remarks>
/// The wax and the candle's capacity both live in the item's data map, so a
/// half-burnt candle is still half-burnt after a load, and a new candle is a
/// full-sized one.
/// </remarks>
internal sealed class WeepingCandle : Relic
{
    private const double StartCapacity = 100, Returned = 0.6, Shrink = 0.15;

    public override string Id => "weeping_candle";
    public override string Name => "The Weeping Candle";
    public override string Family => "Vessels";
    public override int DamageOrder => Observes;
    public override string Flavor => "It gives back what it was given, and gets shorter doing it.";
    public override string Boon =>
        $"It fills with every point of damage you take. Burn it to turn ~lg~{Returned * 100:0}%~/~ of what it holds into " +
        "healing, on the spot, with no ingredients and no bandage.";
    public override string Toll =>
        $"Each burn permanently shrinks it by ~r~{Shrink * 100:0}%~/~. It looks like an infinite resource and is a finite one.";
    public override bool Activatable => true;

    public override void Init(RelicItem item)
    {
        item.Set("capacity", StartCapacity);
        item.Set("wax", 0);
    }

    private static double Capacity(RelicItem item) => item.Get("capacity", StartCapacity);

    public override void OnPlayerDamaged(RelicItem item, InstanceRef player, double amount, RValue attacker)
    {
        if (!item.Carried) return;
        // What really landed: it runs after the negators (Gorgoneion, the Knot,
        // the Worm) have handed their share back, and overkill beyond the HP
        // the player had is not damage taken.
        double landed = Math.Max(0, ReliquaryMod.LastHp - World.Num(player, Objects.o_player.Vars.HP));
        if (landed <= 0) return;
        double cap = Capacity(item);
        item.Set("wax", Math.Min(cap, item.Get("wax") + Math.Min(landed, amount)));
    }

    public override string Activate(RelicItem item, InstanceRef player)
    {
        double wax = item.Get("wax");
        if (wax <= 0) throw new InvalidOperationException("there is nothing in it to burn");
        double heal = Math.Round(wax * Returned, 1);
        double cap = Math.Round(Capacity(item) * (1 - Shrink), 1);
        World.Heal(player, heal);
        item.Set("wax", 0);
        item.Set("capacity", cap);
        return $"~y~The Weeping Candle~/~ burns down: {heal:0.#} Health returns. It will hold only {cap:0.#} now.";
    }

    public override string Status(RelicItem item) => $"Wax: {item.Get("wax"):0.#}/{Capacity(item):0.#}";
}
