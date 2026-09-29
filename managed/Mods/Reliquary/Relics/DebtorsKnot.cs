using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Panic buttons: ten turns of no damage at all, bought on credit.
/// </summary>
/// <remarks>
/// A hit arrives with HP already down, so "no damage" is the loss handed
/// straight back and written into the debt - no more than was really lost,
/// capped by the HP the player had at the end of the last frame (the same
/// ceiling Gorgoneion uses). Who hit you is remembered by instance for the
/// settlement; that memory does not survive a load, and a debt reloaded
/// without it is settled in full.
/// </remarks>
internal sealed class DebtorsKnot : Relic
{
    private const int Term = 10;

    public override string Id => "debtors_knot";
    public override string Name => "The Debtor's Knot";
    public override string Family => "Panic buttons";
    public override string Flavor => "Nothing is forgiven. It is only postponed.";
    public override string Boon =>
        $"Activate: for ~y~{Term}~/~ turns you take no damage whatsoever. Every point that would have landed is banked instead.";
    public override string Toll =>
        $"When the term ends the whole debt lands at once - at ~y~half~/~ if every enemy that hit you is dead by then. " +
        "It is a bet that you can clear the room in time. The Knot itself leaves you one breath; what comes next may not.";
    public override bool Activatable => true;

    private readonly Dictionary<long, InstanceRef> _creditors = new();

    public override string Activate(RelicItem item, InstanceRef player)
    {
        if (item.Get("term") > 0) throw new InvalidOperationException("the Knot is already tied");
        item.Set("term", Term);
        item.Set("debt", 0);
        item.Set("creditors", 0);
        _creditors.Clear();
        return $"~y~The Debtor's Knot~/~ tightens. For {Term} turns, nothing lands.";
    }

    public override void OnPlayerDamaged(RelicItem item, InstanceRef player, double amount, RValue attacker)
    {
        if (!item.Carried || item.Get("term") <= 0) return;
        double now = World.Num(player, Objects.o_player.Vars.HP);
        double restored = Math.Min(now + amount, Math.Max(now, ReliquaryMod.LastHp)) - now;
        if (restored <= 0) return;
        World.Heal(player, restored);
        item.Set("debt", item.Get("debt") + restored);

        long key = World.IdKey(attacker);
        if (key >= 0 && !World.IsPlayer(attacker) && _creditors.TryAdd(key, new InstanceRef(attacker)))
            item.Set("creditors", item.Get("creditors") + 1);
    }

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        double term = item.Get("term");
        if (term <= 0) return;
        item.Set("term", term - 1);
        if (term - 1 > 0) return;

        double debt = item.Get("debt");
        // Settled in full unless every creditor is known and dead: after a load
        // the names are gone, and the Knot does not give the benefit of the doubt.
        bool known = _creditors.Count > 0 && _creditors.Count >= item.Get("creditors");
        bool allDead = known && _creditors.Values.All(c => !World.Alive(c));
        double due = allDead ? debt / 2 : debt;
        item.Set("debt", 0);
        item.Set("creditors", 0);
        _creditors.Clear();
        if (due <= 0)
        {
            World.Say("~y~The Debtor's Knot~/~ loosens. Nothing was owed.");
            return;
        }
        double paid = World.HurtPlayer(player, due);
        World.Say(allDead
            ? $"~y~The Debtor's Knot~/~ comes due at half: your creditors are dead. {paid:0.#} damage lands."
            : $"~r~The Debtor's Knot~/~ comes due: {paid:0.#} damage lands at once.");
    }

    public override string Status(RelicItem item)
    {
        double term = item.Get("term");
        return term > 0
            ? $"Tied: {term:0} turn(s) left, {item.Get("debt"):0.#} owed to {item.Get("creditors"):0} creditor(s)"
            : "Untied";
    }
}
