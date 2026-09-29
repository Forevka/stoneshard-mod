using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Scaling passives: crowns spent become XP, crowns gained cost Health.
/// </summary>
/// <remarks>
/// The game has several gold scripts in the interop (scr_gold_add,
/// scr_characterGoldSpend, scr_characterGoldIncome, scr_characterGoldIncomeBag),
/// but only scr_gold_add's argument has been seen live, and trading moves gold
/// through the trade window as well. Rather than guess which of them sees
/// every crown, the scale watches the one number that has been read back live:
/// scr_gold_count, the gold the player carries. A drop is a spend, a rise is a
/// gain, whatever caused it. It is sampled a few times a second rather than per
/// turn, because trading happens without turns passing.
///
/// Moving gold into a stash or onto the floor therefore reads as spending,
/// and taking it back as gaining - which costs exactly the Health it gave XP
/// for, so the loop is a trade, not a free lunch. The spy plan in the report
/// lists which scripts to watch live to narrow this to real trades later.
/// </remarks>
internal sealed class UsurersScale : Relic
{
    private const int SampleEveryFrames = 20;
    private const double XpPerCrown = 1, HpPerCrown = 1;

    private int _sinceSample;
    // The last count seen, or null until the first sample after picking it up (or a load).
    private double? _lastGold;
    // Which player that count belongs to: a load replaces the player, and the
    // count from before it must not be charged against the loaded game.
    private long _playerKey = -1;
    // OnFrame runs only while the scale is carried; a gap in the sampling means
    // it was put down, and what changed meanwhile is not its business.
    private long _lastSampleMs;
    private double? _candidate;
    private const long PutDownAfterMs = 3000;

    public override string Id => "usurers_scale";
    public override string Name => "The Usurer's Scale";
    public override string Family => "Scaling passives";
    public override string Flavor => "Balanced, and not in your favour.";
    public override string Boon =>
        $"Every crown you ~lg~spend~/~ grants ~lg~{XpPerCrown:0} XP~/~. Shopping becomes levelling; a 1200-crown purchase is " +
        "most of a level on its own.";
    public override string Toll =>
        $"Every crown you ~r~gain~/~ costs ~r~{HpPerCrown:0} Health~/~. Selling a full dungeon's haul can kill you outright, " +
        "and you will find yourself leaving loot on the floor to stay alive.";

    public override void OnFrame(RelicItem item, InstanceRef player)
    {
        if (++_sinceSample < SampleEveryFrames) return;
        _sinceSample = 0;
        long nowMs = Environment.TickCount64;
        if (nowMs - _lastSampleMs > PutDownAfterMs) _lastGold = _candidate = null;
        _lastSampleMs = nowMs;
        long key = World.IdKey(player.Id);
        if (key != _playerKey)
        {
            _playerKey = key;
            _lastGold = _candidate = null;
        }
        if (World.Gold(player) is not { } gold) return;
        // A baseline only from two equal samples in a row: right after a load
        // the purse can read 0 before its stacks are rebuilt, and a baseline
        // taken then would charge the whole purse as a gain.
        if (_lastGold is not { } before)
        {
            if (_candidate is { } c && c == gold) _lastGold = gold;
            _candidate = gold;
            return;
        }
        _lastGold = gold;
        double delta = gold - before;
        if (delta < 0) Spent(item, player, -delta);
        else if (delta > 0) Gained(item, player, delta);
    }

    private static void Spent(RelicItem item, InstanceRef player, double crowns)
    {
        World.GrantXp(player, crowns * XpPerCrown);
        item.Set("spent", item.Get("spent") + crowns);
        World.Say($"~y~The Usurer's Scale~/~ weighs {crowns:0} crowns spent: ~lg~+{crowns * XpPerCrown:0} XP~/~.");
    }

    // The design lets this kill. Until writing the player's HP to zero is shown
    // to run the game's own death, it stops at 1 HP like every self-harm path.
    private static void Gained(RelicItem item, InstanceRef player, double crowns)
    {
        double lost = World.HurtPlayer(player, crowns * HpPerCrown);
        item.Set("gained", item.Get("gained") + crowns);
        World.Say($"~y~The Usurer's Scale~/~ weighs {crowns:0} crowns gained: ~r~-{lost:0} Health~/~.");
    }

    public override string Status(RelicItem item) =>
        $"Weighed: {item.Get("spent"):0} spent, {item.Get("gained"):0} gained";
}
