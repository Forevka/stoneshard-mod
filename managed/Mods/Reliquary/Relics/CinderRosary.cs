using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Riders: each hit threads a bead of fire onto the target; the fifth one is not a prayer.
/// </summary>
/// <remarks>
/// Cinders are the relic's own count per unit, kept in memory: a fight does not
/// outlast a session, and a unit's id says nothing after a load anyway. The
/// blast is HP taken off every hostile (and the player) within two tiles, less
/// their fire resistance, and every hostile it leaves standing gains a Cinder -
/// which is how a crowd chains. A Cindered foe that dies before it bursts hands
/// its Cinders to something adjacent, the player included.
/// </remarks>
internal sealed class CinderRosary : Relic
{
    private const int BurstAt = 5, Radius = 2, MaxBurstsPerHit = 16;
    private const double Blast = 20;

    public override string Id => "cinder_rosary";
    public override string Name => "Cinder Rosary";
    public override string Family => "Riders";
    public override string Flavor => "Fifty-nine beads. The last one is not a prayer.";
    public override string Boon =>
        $"Each hit places a ~y~Cinder~/~ on the target. At ~y~{BurstAt}~/~ Cinders it detonates for ~lg~{Blast:0}~/~ Fire damage " +
        $"in a ~y~{Radius}-tile~/~ radius, and every foe the blast leaves standing gains a Cinder. In a crowd this chains.";
    public override string Toll =>
        "The blast has no allegiance: inside the radius, you burn with them. A Cindered foe that dies before it bursts " +
        "passes its Cinders to whatever is adjacent. Which is often you.";

    private sealed class Stack
    {
        public required InstanceRef Unit;
        public int Cinders;
        public int X, Y;
    }

    private readonly Dictionary<long, Stack> _stacks = new();
    private IRelicHost? _host;
    private long _playerKey = -1;

    public override void OnEnemyDamaged(RelicItem item, InstanceRef player, InstanceRef victim, double amount)
    {
        if (!item.Carried || amount <= 0) return;
        Track(player);
        var pending = new Queue<long>();
        if (World.Alive(victim))
        {
            if (Add(victim, 1) >= BurstAt) pending.Enqueue(World.IdKey(victim.Id));
        }
        else
        {
            Died(player, World.IdKey(victim.Id), pending);
        }
        Resolve(player, pending);
    }

    // Foes that died of something else since: their Cinders still jump, from
    // where they were last seen.
    public override void OnPlayerTurn(InstanceRef player)
    {
        Track(player);
        if (_stacks.Count == 0 || _host?.Active(this) is null) return;
        var pending = new Queue<long>();
        foreach (var key in _stacks.Keys.ToList())
        {
            if (!_stacks.TryGetValue(key, out var s)) continue;
            if (World.Alive(s.Unit)) (s.X, s.Y) = World.TileOf(s.Unit);
            else Died(player, key, pending);
        }
        Resolve(player, pending);
    }

    // A new character or a load: every unit id before it is meaningless.
    private void Track(InstanceRef player)
    {
        long key = World.IdKey(player.Id);
        if (key == _playerKey) return;
        _playerKey = key;
        _stacks.Clear();
    }

    private int Add(InstanceRef unit, int cinders)
    {
        long key = World.IdKey(unit.Id);
        if (!_stacks.TryGetValue(key, out var s)) _stacks[key] = s = new Stack { Unit = unit };
        s.Cinders += cinders;
        (s.X, s.Y) = World.TileOf(unit);
        return s.Cinders;
    }

    private void Died(InstanceRef player, long key, Queue<long> pending)
    {
        if (!_stacks.Remove(key, out var dead) || dead.Cinders <= 0) return;
        var heirs = World.UnitsNear(player, dead.X, dead.Y, 1).Where(u => World.IdKey(u.Id) != key).ToList();
        if (heirs.Count == 0) return;
        var heir = heirs[Random.Shared.Next(heirs.Count)];
        bool you = World.IdKey(heir.Id) == World.IdKey(player.Id);
        World.Say(you
            ? $"~r~Cinder Rosary~/~: {dead.Cinders} Cinder(s) leap onto you."
            : $"~y~Cinder Rosary~/~: {dead.Cinders} Cinder(s) leap to the next body.");
        if (Add(heir, dead.Cinders) >= BurstAt) pending.Enqueue(World.IdKey(heir.Id));
    }

    // Bursts one after another, each one's blast possibly readying the next;
    // bounded so a packed room cannot spin a frame forever.
    private void Resolve(InstanceRef player, Queue<long> pending)
    {
        int bursts = 0;
        while (pending.Count > 0 && bursts < MaxBurstsPerHit)
        {
            long key = pending.Dequeue();
            if (!_stacks.Remove(key, out var s) || s.Cinders < BurstAt) continue;
            bursts++;
            var (tx, ty) = World.Alive(s.Unit) ? World.TileOf(s.Unit) : (s.X, s.Y);
            int hit = 0;
            bool burnedYou = false;
            foreach (var u in World.UnitsNear(player, tx, ty, Radius))
            {
                long uk = World.IdKey(u.Id);
                double dealt = World.HurtAny(u, World.Resisted(u, Blast, Objects.o_player.Vars.Fire_Resistance), player);
                if (dealt <= 0) continue;
                hit++;
                if (uk == World.IdKey(player.Id)) { burnedYou = true; continue; }
                if (!World.Alive(u)) Died(player, uk, pending);
                else if (uk != key && Add(u, 1) >= BurstAt) pending.Enqueue(uk);
            }
            World.Say($"~r~Cinder Rosary~/~: a burst of fire strikes {hit} bod{(hit == 1 ? "y" : "ies")}" +
                      (burnedYou ? ", you among them." : "."));
        }
    }

    public override string Status(RelicItem item)
    {
        int foes = _stacks.Count(kv => kv.Key != _playerKey && kv.Value.Cinders > 0);
        int mine = _stacks.TryGetValue(_playerKey, out var s) ? s.Cinders : 0;
        return (foes > 0 ? $"{foes} foe(s) Cindered" : "No foe Cindered") + (mine > 0 ? $", {mine} on you" : "");
    }

    /// <summary>Cinders per unit id, for the test host.</summary>
    public object Snapshot() => _stacks.Select(kv => new { id = kv.Key, cinders = kv.Value.Cinders, x = kv.Value.X, y = kv.Value.Y }).ToArray();

    public override void Install(IRelicHost host)
    {
        _host = host;
        if (TestHost.Enabled)
            TestHost.Register("reliq.cinder.stacks", _ => Snapshot(), "reliq.cinder.stacks: Cinders per unit {id, cinders, x, y}");
    }
}
