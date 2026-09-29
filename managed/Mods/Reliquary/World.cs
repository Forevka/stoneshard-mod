using CoreLoader;
using StoneShard;

namespace Reliquary;

/// <summary>
/// What the relics read and change in the live game. Every script, object and
/// variable name comes from the generated interop, so a game update that renames
/// one breaks the build rather than a player's run.
/// </summary>
/// <remarks>
/// Facts this rests on, each established on the running game through the test
/// host rather than assumed:
///   * a step moves the player 26 units, so a tile is 26 world units;
///   * scr_atr_calc rebuilds a unit's stats from scratch (a hand-written
///     Weapon_Damage of 500 was back to 100 after one turn), so adding a bonus
///     after every recalculation never compounds;
///   * an enemy whose HP is written to 0 dies on the next frame, drop and all;
///   * a status is an instance: create it, give it owner/target/duration and put
///     it in the unit's `buffs` list (stone and stun both behave);
///   * scr_actionsLogAddMessage(text) appends a line to the game's own log.
/// </remarks>
internal static partial class World
{
    public const double Tile = 26;

    // Variable names, all from the generated interop.
    private const string VarX = Objects.o_player.Vars.x;
    private const string VarY = Objects.o_player.Vars.y;
    private const string VarHp = Objects.o_player.Vars.HP;
    private const string VarMaxHp = Objects.o_player.Vars.max_hp;
    private const string VarMp = Objects.o_player.Vars.MP;
    private const string VarMaxMp = Objects.o_player.Vars.max_mp;
    private const string VarVision = Objects.o_player.Vars.VSN;
    private const string VarBuffs = Objects.o_player.Vars.buffs;
    private const string VarBuffsChanged = Objects.o_player.Vars.buffs_is_change;
    private const string VarPsyche = Objects.o_player.Vars.psyData;
    private const string VarBody = Objects.o_player.Vars.Body_Parts_map;
    private const string VarHostile = Objects.o_enemy.Vars.is_hostile;

    public static InstanceRef? Player => Objects.o_player.First is { } p && p.Exists ? p : null;

    public static InstanceRef RequirePlayer() =>
        Player ?? throw new InvalidOperationException("no player: load a save and let the game run");

    /// <summary>Instance ids compare by their low 32 bits: numbers on older runtimes, references on newer ones.</summary>
    public static long IdKey(RValue v) =>
        v.IsNumber ? (long)v.AsReal : v.Kind == RValueKind.Reference ? v.Int64 & 0xFFFFFFFF : -1;

    public static bool IsPlayer(RValue id) => Player is { } p && IdKey(id) >= 0 && IdKey(id) == IdKey(p.Id);

    public static double Num(InstanceRef r, string name, double fallback = 0)
    {
        var v = r.Get(name);
        return v.IsNumber ? v.AsReal : fallback;
    }

    public static (double X, double Y) Position(InstanceRef r) => (Num(r, VarX), Num(r, VarY));

    /// <summary>Distance in tiles, the way a grid game counts it: diagonals are one step.</summary>
    public static double Tiles(InstanceRef a, InstanceRef b)
    {
        var (ax, ay) = Position(a);
        var (bx, by) = Position(b);
        return Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by)) / Tile;
    }

    public static double Vision(InstanceRef player) => Num(player, VarVision, 8);

    /// <summary>
    /// Living hostiles within <paramref name="radius"/> tiles of the player
    /// (their vision by default). Townsfolk are o_enemy too, which is why
    /// is_hostile is checked: a rat reads 1, the magistrate 0.
    /// </summary>
    public static List<InstanceRef> Hostiles(InstanceRef player, double? radius = null)
    {
        var found = new List<InstanceRef>();
        if (Objects.o_enemy.Object is not { } enemies) return found;
        double r = radius ?? Vision(player);
        foreach (var e in enemies.Instances())
        {
            if (!e.Exists || Num(e, VarHostile) <= 0 || Num(e, VarHp) <= 0) continue;
            if (Tiles(player, e) <= r) found.Add(e);
        }
        return found;
    }

    // ------------------------------------------------------------ health

    /// <summary>Takes HP off a unit. At zero the game's own death follows on the next frame.</summary>
    public static double Hurt(InstanceRef unit, double amount)
    {
        double hp = Num(unit, VarHp);
        double after = Math.Max(0, hp - amount);
        unit.Set(VarHp, after);
        return hp - after;
    }

    public static void Heal(InstanceRef unit, double amount)
    {
        double max = Num(unit, VarMaxHp, 1);
        unit.Set(VarHp, Math.Min(max, Num(unit, VarHp) + amount));
    }

    public static void RestoreEnergy(InstanceRef unit, double amount)
    {
        double max = Num(unit, VarMaxMp, 1);
        unit.Set(VarMp, Math.Min(max, Num(unit, VarMp) + amount));
    }

    public static double MaxHp(InstanceRef unit) => Num(unit, VarMaxHp, 1);

    public static double MaxEnergy(InstanceRef unit) => Num(unit, VarMaxMp, 1);

    // ------------------------------------------------------------ psyche

    /// <summary>
    /// Sanity is the sum of the psyche map's parts (Situational, Lifestyle,
    /// Rest); a loss comes off the situational part, as a kill's would.
    /// </summary>
    public static void LoseSanity(InstanceRef player, double amount)
    {
        var psy = new DsMap(player.Get(VarPsyche));
        if (!psy.Exists) return;
        var cur = psy.Get("SanitySituational");
        if (cur.IsNumber) psy.Set("SanitySituational", Math.Max(0, cur.AsReal - amount));
    }

    public static double Sanity(InstanceRef player)
    {
        var psy = new DsMap(player.Get(VarPsyche));
        var v = psy.Exists ? psy.Get("Sanity") : RValue.Undefined;
        return v.IsNumber ? v.AsReal : 100;
    }

    // ------------------------------------------------------------ body

    public static DsMap Body(InstanceRef player) => new(player.Get(VarBody));

    // ------------------------------------------------------------ statuses

    /// <summary>
    /// Puts a status (o_db_stone, o_db_stun...) on a unit for
    /// <paramref name="turns"/> turns. The status's own Create chain fills in
    /// everything structural; whoever applies it supplies owner, target and
    /// duration, and registers it in the unit's buffs list.
    /// </summary>
    public static void ApplyStatus(InstanceRef unit, string statusObject, int turns)
    {
        var obj = GmlObject.Find(statusObject) ?? throw new InvalidOperationException($"no status object {statusObject}");
        var buffs = new DsList(unit.Get(VarBuffs));
        if (!buffs.Exists) throw new InvalidOperationException("the unit has no buffs list");
        var target = unit.Get(Objects.o_player.Vars.object_index);
        var (x, y) = Position(unit);
        var inst = Builtins.instance_create_depth(x, y, 0, obj.Index);
        if (inst.IsUndefined) throw new InvalidOperationException($"could not create {statusObject}");
        var status = new InstanceRef(inst);
        try
        {
            status.Set(Objects.c_buff.Vars.owner, unit.Id);
            status.Set(Objects.c_buff.Vars.target, target);
            status.Set(Objects.c_buff.Vars.duration, Math.Max(1, turns));
            buffs.Add(inst);
        }
        catch
        {
            Destroy(inst);
            throw;
        }
        Recalculate(unit);
    }

    /// <summary>Whether the unit carries a live status of this object.</summary>
    public static bool HasStatus(InstanceRef unit, string statusObject)
    {
        var buffs = new DsList(unit.Get(VarBuffs));
        if (!buffs.Exists) return false;
        for (int i = 0; i < buffs.Count; i++)
        {
            var s = new InstanceRef(buffs.At(i));
            if (s.Exists && Builtins.object_get_name(s.Get(Objects.o_player.Vars.object_index)).ToString() == statusObject)
                return true;
        }
        return false;
    }

    /// <summary>Destroys every status a unit carries; returns how many there were.</summary>
    public static int StripStatuses(InstanceRef unit)
    {
        var buffs = new DsList(unit.Get(VarBuffs));
        if (!buffs.Exists || buffs.Count == 0) return 0;
        int n = buffs.Count;
        // Collected first: a status's Destroy event may edit the list under us.
        var all = new List<InstanceRef>(n);
        for (int i = 0; i < n; i++) all.Add(new InstanceRef(buffs.At(i)));
        foreach (var s in all)
            if (s.Exists) Destroy(s.Id);
        buffs.Clear();
        Recalculate(unit);
        return n;
    }

    // instance_destroy is handed the player as self, as the game's own callers
    // have one; a null self is not worth the risk.
    private static void Destroy(RValue id)
    {
        if (Player?.Resolve() is { } self) Game.CallBuiltinAs(self, "instance_destroy", id);
    }

    public static void DestroyInstance(InstanceRef r) => Destroy(r.Id);

    /// <summary>
    /// Asks the game to recalculate the player's stats at its next chance: the
    /// unit's own dirty flag, raised by the game when its buffs change. Written
    /// as a real bool, the kind the game keeps there.
    /// </summary>
    public static void Recalculate(InstanceRef player) =>
        player.Set(VarBuffsChanged, new RValue { Real = 1, Kind = RValueKind.Bool });

    // ------------------------------------------------------------ feedback

    /// <summary>A line in the game's own action log. Accepts its colour tags (~y~ ... ~/~).</summary>
    public static void Say(string text)
    {
        try { Scripts.scr_actionsLogAddMessage.Call(text); }
        catch (GmlException) { /* the log is cosmetic; a relic never fails over it */ }
    }
}
