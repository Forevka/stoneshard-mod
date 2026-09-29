using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Vessels: it eats first.
/// </summary>
/// <remarks>
/// Only food counts (a child of o_inv_food_parent); potions, bandages and the
/// rest pass by untouched. Eating from the bag runs the food's use event
/// (o_inv_food_parent Other_24), which logs the meal, calls scr_consum_use as
/// the food item and spends it. While the worm holds a charge, that event is
/// skipped - the meal is refused and the food stays in the bag. Otherwise the
/// meal's Hunger value is read in scr_consum_use and becomes the charge.
///
/// The design turns fullness into temporary Max Health. Raising max_hp from
/// the stat layer is avoided on purpose: the game rescales current HP when its
/// maximum moves, and a bonus re-added after every recalculation could shave
/// HP each time. The worm's charge is an absorption reserve instead - worth up
/// to a third of your Health bar, it hands back what a hit takes until it runs
/// dry - the same extra bar, spent the same way.
/// </remarks>
internal sealed class SatedWorm : Relic
{
    private const double Full = 100, ReserveShare = 1.0 / 3, FillPerHunger = 2.5, FallbackHunger = 15;
    // A full worm fasts you for Full * DrainEvery turns if nothing hits you.
    private const int DrainEvery = 3;

    private static readonly string FoodParent = Objects.o_inv_food_parent.Name;

    // Filled in the Before hook and spent in the After hook of the same call:
    // the food item may be gone (eaten) by the time the call returns.
    private double _pendingFill;

    public override string Id => "sated_worm";
    public override string Name => "The Sated Worm";
    public override string Family => "Vessels";
    public override int DamageOrder => Negates;
    public override string Flavor => "It eats first.";
    public override string Boon =>
        "Eat with it in your bag and it takes the meal as a charge. A full worm is worth ~lg~a third of your Health bar~/~ " +
        "again: while charged, it hands back what hits take, until the charge runs out.";
    public override string Toll =>
        "While it holds any charge you ~r~cannot eat at all~/~, and Hunger drains at the normal rate underneath. Every charge " +
        "is a fasting window you have chosen to enter.";

    public override void Install(IRelicHost host)
    {
        // Eating from the bag runs the food's use event, which logs the meal,
        // calls scr_consum_use and then spends the item. Refusing here keeps
        // the food in the bag; refusing only inside scr_consum_use (live) left
        // the meal logged and the item gone with nothing eaten.
        host.Before(this, Objects.o_inv_food_parent.Other_24, c =>
        {
            if (host.Active(this) is not { } item || item.Get("fill") <= 0 || !IsFood(c.Self)) return;
            c.SkipOriginal();
            World.Say("~r~The Sated Worm~/~ will not let you eat: it is still full.");
        });
        host.Before(this, Scripts.scr_consum_use, c =>
        {
            _pendingFill = 0;
            if (host.Active(this) is not { } item || !IsFood(c.Self)) return;
            // Any other way food reaches scr_consum_use is still refused; the
            // food may be spent anyway on that path, so say why nothing happened.
            if (item.Get("fill") > 0)
            {
                c.SkipOriginal();
                World.Say("~r~The Sated Worm~/~ takes the meal from your mouth: it is still full.");
                return;
            }
            _pendingFill = Math.Min(Full, HungerValue(c.Self) * FillPerHunger);
        });
        host.After(this, Scripts.scr_consum_use, c =>
        {
            if (_pendingFill <= 0 || c.OriginalSkipped || host.Active(this) is not { } item) return;
            item.Set("fill", _pendingFill);
            World.Say($"~y~The Sated Worm~/~ takes the meal: charged {_pendingFill:0}/{Full:0}.");
            _pendingFill = 0;
        });
    }

    private static bool IsFood(Instance self)
    {
        if (self.IsNull) return false;
        try
        {
            string name = Builtins.object_get_name(self.Get(Objects.o_player.Vars.object_index)).ToString();
            return GmlObject.Find(name)?.IsA(FoodParent) == true;
        }
        catch (GmlException) { return false; }
    }

    // How much the meal feeds, from the item's data map: the consumable table's
    // Hunger column (negative: it lowers Hunger). Unknown shapes feed a default.
    private static double HungerValue(Instance food)
    {
        try
        {
            var data = new DsMap(food.Get("data"));
            if (data.Exists)
                foreach (var key in new[] { "Hunger", "Hunger_Change" })
                {
                    var v = data.Get(key);
                    if (v.IsNumber && v.AsReal != 0) return Math.Abs(v.AsReal);
                }
        }
        catch (GmlException) { }
        return FallbackHunger;
    }

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        if (!item.Carried) return;
        double fill = item.Get("fill");
        if (fill <= 0) return;
        double age = item.Get("age") + 1;
        if (age >= DrainEvery)
        {
            age = 0;
            fill = Math.Max(0, fill - 1);
            item.Set("fill", fill);
            if (fill <= 0) World.Say("~y~The Sated Worm~/~ is empty. You may eat again.");
        }
        item.Set("age", age);
    }

    // Each point of fill is worth (a third of max HP) / Full of absorbed damage.
    public override void OnPlayerDamaged(RelicItem item, InstanceRef player, double amount, RValue attacker)
    {
        if (!item.Carried) return;
        double fill = item.Get("fill");
        if (fill <= 0) return;
        double perPoint = World.MaxHp(player) * ReserveShare / Full;
        double reserve = fill * perPoint;
        // A hit arrives with HP already down and clamped at zero: hand back no
        // more than was really lost (ReliquaryMod.LastHp is the HP before it).
        double now = World.Num(player, Objects.o_player.Vars.HP);
        double lost = Math.Max(0, ReliquaryMod.LastHp - now);
        double absorbed = Math.Min(Math.Min(amount, lost), reserve);
        if (absorbed <= 0) return;
        World.Heal(player, absorbed);
        fill = Math.Max(0, fill - absorbed / perPoint);
        item.Set("fill", fill);
        if (fill <= 0) World.Say("~y~The Sated Worm~/~ is spent. You may eat again.");
    }

    public override string Status(RelicItem item)
    {
        double fill = item.Get("fill");
        return fill > 0 ? $"Charged: {fill:0}/{Full:0} - you cannot eat" : "Empty: it waits for a meal";
    }
}
