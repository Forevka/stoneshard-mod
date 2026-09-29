using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Scaling passives: a bonus that reads live game state - lost Sanity.
/// </summary>
/// <remarks>
/// Sanity is the sum of three parts of the player's psyData: SanitySituational,
/// SanityLifestyle and SanityRest (100 = 50 + 35 + 15 on a fresh character).
/// The toll has no single game stat to hook, so it is enforced by watching
/// those parts turn by turn: half of any rise in the situational and
/// lifestyle parts is taken back ("regeneration halved"), and all of any rise
/// in the rest part ("rest restores none"). The baseline lives in C# only:
/// after a load the first turn just records it, which forgives at most one
/// turn's recovery; a relic put down and picked up again taxes, once, what
/// was recovered in between.
/// </remarks>
internal sealed class ReliquaryOfSaintMardun : Relic
{
    private const double PointsPerPercent = 3, MaxSanity = 100;

    private const string Situational = "SanitySituational", Lifestyle = "SanityLifestyle", Rest = "SanityRest";

    // What each part held at the end of the last turn, to tell recovery from loss.
    private readonly Dictionary<string, double> _last = new();
    // Whose parts those are: a load replaces the player, and its Sanity is not
    // a recovery to tax.
    private long _playerKey = -1;

    public override string Id => "reliquary_of_saint_mardun";
    public override string Name => "Reliquary of Saint Mardun";
    public override string Family => "Scaling passives";
    public override string Flavor => "Lucidity is a luxury of the safe.";
    public override string Boon =>
        $"~lg~+1%~/~ Weapon Damage and ~lg~+1%~/~ Magic Power for every ~y~{PointsPerPercent:0}~/~ points of Sanity below " +
        "your maximum - about +33% to both at full collapse.";
    public override string Toll =>
        "Sanity regenerates at ~r~half~/~ the rate, and rest restores ~r~none~/~ of it. You climb toward the bonus by " +
        "letting a character come apart.";

    private static double Bonus(InstanceRef player) =>
        Math.Floor(Math.Max(0, MaxSanity - World.Sanity(player)) / PointsPerPercent);

    public override void OnStats(RelicItem item, Stats stats)
    {
        if (!item.Carried || World.Player is not { } player) return;
        double bonus = Bonus(player);
        if (bonus <= 0) return;
        stats.Add(Objects.o_player.Vars.Weapon_Damage, bonus);
        stats.Add(Objects.o_player.Vars.Magic_Power, bonus);
    }

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        // Only the carried copy tolls. A copy lying in a chest must not touch
        // the baseline, or it would undo the carried one's every turn.
        if (!item.Carried) return;
        long key = World.IdKey(player.Id);
        if (key != _playerKey)
        {
            _playerKey = key;
            _last.Clear();
        }
        TakeBack(player, Situational, 0.5);
        TakeBack(player, Lifestyle, 0.5);
        TakeBack(player, Rest, 1.0);
        // The bonus follows Sanity; ask for a recalculation so it keeps up.
        World.Recalculate(player);
    }

    // Takes back `share` of whatever this part gained since last turn.
    private void TakeBack(InstanceRef player, string part, double share)
    {
        if (World.PsyNum(player, part) is not { } now) return;
        if (_last.TryGetValue(part, out var before) && now > before)
        {
            now -= (now - before) * share;
            World.PsySet(player, part, now);
        }
        _last[part] = now;
    }

    public override string Status(RelicItem item) =>
        World.Player is { } p ? $"Sanity {World.Sanity(p):0}: +{Bonus(p):0}% Weapon Damage and Magic Power" : "";
}
