using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Vessels: sworn against sleep, which is a shorter oath than it sounds.
/// </summary>
/// <remarks>
/// Sleep is read from the player's own statuses: a sleeping unit carries the
/// game's sleep status (o_db_sleep). Checked every turn - sleeping passes turns
/// like anything else - which needs no guess at a sleep script's arguments.
/// If a patch ever stops using that status, the oath simply never breaks: the
/// stone keeps its fill rather than punishing a sleep it cannot see.
///
/// "Armor" is the game's defence per body part (Head_DEF, Body_DEF, Arms_DEF,
/// Legs_DEF, and the DEF total): the bonus and the Withdrawal scale all five.
/// </remarks>
internal sealed class OathStone : Relic
{
    private const int FullAt = 500, WithdrawalTurns = 200;
    private const double Bonus = 25, Withdrawal = 20;

    private static readonly string Sleep = Objects.o_db_sleep.Name;

    private static readonly string[] Armor =
    {
        Objects.o_player.Vars.DEF, Objects.o_player.Vars.Head_DEF, Objects.o_player.Vars.Body_DEF,
        Objects.o_player.Vars.Arms_DEF, Objects.o_player.Vars.Legs_DEF,
    };

    public override string Id => "oath_stone";
    public override string Name => "Oath-Stone of the Deep Road";
    public override string Family => "Vessels";
    public override string Flavor => "Sworn against sleep, which is a shorter oath than it sounds.";
    public override string Boon =>
        $"Fills the longer you go without sleeping. At ~y~{FullAt}~/~ turns unslept it holds ~lg~+{Bonus}%~/~ Weapon Damage, " +
        $"~lg~+{Bonus}%~/~ Magic Power and ~lg~+{Bonus}%~/~ Armor.";
    public override string Toll =>
        $"Sleeping empties it and brings ~r~Withdrawal~/~ for {WithdrawalTurns} turns: ~r~-{Withdrawal}%~/~ to all three. " +
        "It attacks the very loop the road is built on.";

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        if (!item.Carried) return;

        double withdrawal = item.Get("withdrawal");
        if (withdrawal > 0) item.Set("withdrawal", withdrawal - 1);

        bool asleep;
        try { asleep = World.HasStatus(player, Sleep); }
        catch (GmlException) { asleep = false; }

        if (asleep)
        {
            if (item.Get("unslept") > 0)
            {
                item.Set("unslept", 0);
                item.Set("withdrawal", WithdrawalTurns);
                World.Recalculate(player);
                World.Say("~r~Oath-Stone of the Deep Road~/~: the oath is broken. Withdrawal sets in.");
            }
            return;
        }

        double unslept = Math.Min(FullAt, item.Get("unslept") + 1);
        item.Set("unslept", unslept);
        if (unslept == FullAt && item.Get("announced") <= 0)
        {
            item.Set("announced", 1);
            World.Say("~y~Oath-Stone of the Deep Road~/~ is full: the oath holds.");
        }
        else if (unslept < FullAt) item.Set("announced", 0);
    }

    public override void OnStats(RelicItem item, Stats stats)
    {
        if (!item.Carried) return;
        double percent = item.Get("withdrawal") > 0
            ? -Withdrawal
            : Bonus * Math.Min(1, item.Get("unslept") / FullAt);
        if (percent == 0) return;
        stats.Add(Objects.o_player.Vars.Weapon_Damage, percent);
        stats.Add(Objects.o_player.Vars.Magic_Power, percent);
        // Armor is a plain value, not a percentage: scale it.
        foreach (var a in Armor) stats.Add(a, stats.Get(a) * percent / 100);
    }

    public override string Status(RelicItem item)
    {
        double withdrawal = item.Get("withdrawal");
        if (withdrawal > 0) return $"Withdrawal: {withdrawal:0} turns";
        double unslept = item.Get("unslept");
        return $"Unslept: {unslept:0}/{FullAt} turns (+{Bonus * Math.Min(1, unslept / FullAt):0.#}%)";
    }
}
