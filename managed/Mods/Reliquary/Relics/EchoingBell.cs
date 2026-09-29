using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Riders: every blow rings twice - the second time on the tile, not the target.
/// </summary>
/// <remarks>
/// Built on the idea of the game's seals and tile marks (a mark on a tile that
/// acts on whoever stands there when it fires): every hit the player lands
/// leaves an Echo Seal on the victim's tile, and three turns later the seal
/// discharges half of that hit into whatever stands on the tile then - a foe,
/// or you. The seal does not re-aim.
///
/// The game's own seal and tile-mark objects (o_seal_of_*, c_tile_mark) are in
/// the interop, but none has been seen live and their set-up is not known:
/// creating one blind could leave a half-built mark whose own events fail
/// every frame. So the relic keeps its seals itself, and drawing a native seal
/// over them is an opt-in experiment for the test host
/// (reliq.bell.native o_seal_of_reflection), switched off the moment a
/// creation fails.
/// </remarks>
internal sealed class EchoingBell : Relic
{
    private const double Share = 0.5;
    private const int Delay = 3, MaxSeals = 12;

    public override string Id => "echoing_bell";
    public override string Name => "The Echoing Bell";
    public override string Family => "Riders";
    public override string Flavor => "It rings once when struck, and once more when you've stopped listening.";
    public override string Boon =>
        $"Every hit you land leaves an ~y~Echo Seal~/~ on the target's tile. ~y~{Delay}~/~ turns later it rings again, " +
        $"dealing ~lg~{Share * 100:0}%~/~ of that hit to whatever stands there.";
    public override string Toll =>
        "The echo does not re-aim. It lands on the tile, whatever is standing there now - including you, if you've stepped " +
        "into it. It will not quite finish you.";

    private sealed class Seal
    {
        public int X, Y, TurnsLeft;
        public double Amount;
        public InstanceRef? Visual;
    }

    private readonly List<Seal> _seals = new();
    private long _playerKey = -1;
    private string? _nativeObject;

    public override void OnEnemyDamaged(RelicItem item, InstanceRef player, InstanceRef victim, double amount)
    {
        if (!item.Carried || amount <= 0) return;
        Track(player);
        var (tx, ty) = World.TileOf(victim);
        var seal = new Seal { X = tx, Y = ty, TurnsLeft = Delay, Amount = Math.Round(amount * Share, 1) };
        seal.Visual = TryDrawNative(victim);
        _seals.Add(seal);
        // The oldest seal fades first when too many are ringing at once.
        while (_seals.Count > MaxSeals)
        {
            Erase(_seals[0]);
            _seals.RemoveAt(0);
        }
    }

    // Seals already cast ring whether or not the bell is still carried.
    public override void OnPlayerTurn(InstanceRef player)
    {
        Track(player);
        foreach (var seal in _seals.ToList())
        {
            if (--seal.TurnsLeft > 0) continue;
            _seals.Remove(seal);
            Erase(seal);
            var struck = World.UnitsNear(player, seal.X, seal.Y, 0);
            if (struck.Count == 0)
            {
                World.Say("~y~The Echoing Bell~/~ rings over an empty tile.");
                continue;
            }
            foreach (var u in struck)
            {
                bool you = World.IdKey(u.Id) == World.IdKey(player.Id);
                double dealt = World.HurtAny(u, World.Resisted(u, seal.Amount, Objects.o_player.Vars.Arcane_Resistance), player);
                World.Say(you
                    ? $"~r~The Echoing Bell~/~ rings under your feet: {dealt:0.#} damage."
                    : $"~y~The Echoing Bell~/~ rings again: {dealt:0.#} damage.");
            }
        }
    }

    // A new character or a load: tiles and ids from before mean nothing.
    private void Track(InstanceRef player)
    {
        long key = World.IdKey(player.Id);
        if (key == _playerKey) return;
        _playerKey = key;
        _seals.Clear();
    }

    private InstanceRef? TryDrawNative(InstanceRef at)
    {
        if (_nativeObject is null) return null;
        try
        {
            var obj = GmlObject.Find(_nativeObject) ?? throw new InvalidOperationException($"no object {_nativeObject}");
            var (x, y) = World.Position(at);
            var inst = Builtins.instance_create_depth(x, y, 0, obj.Index);
            return inst.IsUndefined ? null : new InstanceRef(inst);
        }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException)
        {
            _host?.Log($"Echoing Bell: native seal {_nativeObject} failed ({ex.Message}); drawing switched off");
            _nativeObject = null;
            return null;
        }
    }

    private static void Erase(Seal seal)
    {
        if (seal.Visual is { } v && v.Exists) World.DestroyInstance(v);
    }

    public override string Status(RelicItem item) =>
        _seals.Count == 0 ? "Silent" : $"{_seals.Count} seal(s) ringing, next in {_seals.Min(s => s.TurnsLeft)} turn(s)";

    private IRelicHost? _host;

    public override void Install(IRelicHost host)
    {
        _host = host;
        if (!TestHost.Enabled) return;
        TestHost.Register("reliq.bell.seals", _ =>
            _seals.Select(s => new { x = s.X, y = s.Y, turns = s.TurnsLeft, amount = s.Amount, native = s.Visual is not null }).ToArray(),
            "reliq.bell.seals: pending Echo Seals {x, y, turns, amount, native}");
        TestHost.Register("reliq.bell.native", args =>
        {
            string name = args.Count > 0 ? args[0].GetString() ?? "off" : "off";
            _nativeObject = name == "off" ? null : name;
            return _nativeObject ?? "off";
        }, "reliq.bell.native <object|off>: also draws each new seal as that game object (an experiment; off by default)");
    }
}
