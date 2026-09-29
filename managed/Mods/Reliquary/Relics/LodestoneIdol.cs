using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Auras: it wants company and has no taste.
/// </summary>
/// <remarks>
/// The design drags every enemy in sight two tiles closer each turn. Writing a
/// unit's x/y does not stick - the game keeps units on their grid cells - so
/// real movement would have to go through one of the game's own movement
/// scripts (scr_unit_push_from_tile reads 3 arguments; scr_knockback and
/// scr_cast_knockback read an unknown number). Their arguments are not
/// established, so that path is an experiment, off unless switched on over the
/// test host (reliq.lode.push on).
///
/// What always runs is the part that needs no guessing: every hostile in sight
/// "hears" you each turn through the enemy AI's own noise fields, so the ones
/// you were retreating from, the ones asleep and the pack you were splitting
/// all come for you. Kiting stops working because nobody loses you.
/// </remarks>
internal sealed class LodestoneIdol : Relic
{
    private const int Pull = 2;

    // The movement experiment, off by default: a wrong guess at a movement
    // script's arguments must not move units somewhere odd in a real run.
    private static bool _pushEnabled;
    private static bool _pushFailedOnce;

    public override string Id => "lodestone_idol";
    public override string Name => "Lodestone Idol";
    public override string Family => "Auras";
    public override string Flavor => "It wants company and has no taste.";
    public override string Boon =>
        "Every turn, every hostile in sight is drawn to you: it knows exactly where you are. Archers and casters cannot " +
        "keep their distance from a target they never lose, and every area attack you own lands on a crowd.";
    public override string Toll =>
        "~r~Every hostile.~/~ The ones you were retreating from, the ones asleep, the pack you were carefully splitting. " +
        "You cannot disengage from anything while it is in your bag.";

    public override void Install(IRelicHost host)
    {
        if (!TestHost.Enabled) return;
        TestHost.Register("reliq.lode.push", args =>
        {
            string mode = args.Count > 0 ? args[0].GetString() ?? "" : "";
            if (mode is "on" or "off") _pushEnabled = mode == "on";
            _pushFailedOnce = false;
            return new { push = _pushEnabled };
        }, "reliq.lode.push [on|off]: the Lodestone's movement experiment (scr_unit_push_from_tile as each enemy)");
        TestHost.Register("reliq.lode.probe", args =>
        {
            // One enemy, one call, positions before and after: how the
            // arguments are learned without letting a guess loose every turn.
            if (args.Count < 1) throw new ArgumentException("reliq.lode.probe <enemy id> [a0 a1 a2]");
            var enemy = new InstanceRef(args[0].GetDouble());
            var player = World.RequirePlayer();
            var (px, py) = World.Position(player);
            RValue a0 = args.Count > 1 ? args[1].GetDouble() : px;
            RValue a1 = args.Count > 2 ? args[2].GetDouble() : py;
            RValue a2 = args.Count > 3 ? args[3].GetDouble() : Pull;
            var before = World.Position(enemy);
            var result = Scripts.scr_unit_push_from_tile.CallAs(enemy, a0, a1, a2);
            var after = World.Position(enemy);
            return new { before = new[] { before.X, before.Y }, after = new[] { after.X, after.Y }, result = result.ToString() };
        }, "reliq.lode.probe <enemy id> [a0 a1 a2]: runs scr_unit_push_from_tile as the enemy (default a0,a1 = the player's x,y; a2 = 2)");
    }

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        if (!item.Carried) return;
        int drawn = 0;
        foreach (var e in World.Hostiles(player))
        {
            try
            {
                World.MakeHeard(e, player);
                if (_pushEnabled) Push(e, player);
                drawn++;
            }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException) { }
        }
        item.Set("drawn", drawn);
    }

    private static void Push(InstanceRef enemy, InstanceRef player)
    {
        var (px, py) = World.Position(player);
        try
        {
            Scripts.scr_unit_push_from_tile.CallAs(enemy, px, py, Pull);
        }
        catch (GmlException ex)
        {
            // A wrong guess is reported once and the experiment switches itself off.
            if (!_pushFailedOnce) World.Say($"~y~Lodestone Idol~/~: the pull fails ({ex.Message}).");
            _pushFailedOnce = true;
            _pushEnabled = false;
        }
    }

    public override string Status(RelicItem item) => $"Drawing {item.Get("drawn"):0} hostile(s)";
}
