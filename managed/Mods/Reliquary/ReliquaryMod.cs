using System.Text.Json;
using CoreLoader;
using Reliquary.Relics;
using StoneShard;

[assembly: CoreModInfo(typeof(Reliquary.ReliquaryMod), "Reliquary", "0.1.0", "CoreLoader")]
[assembly: CoreModGame("StoneShard")]

namespace Reliquary;

/// <summary>
/// Stoneshard Reliquary: artifacts that ask for something back. Six relics,
/// one from each family of the design, built only from the generated interop
/// and what the running game showed through the test host.
/// </summary>
/// <remarks>
/// Three hooks carry everything, each found by watching the live game:
///   * scr_atr_calc (after, as the player) - the stat layer;
///   * scr_global_turn_end (after, once per player turn, as the player) - the tick;
///   * scr_save_damage_received (after, as whoever was hit, args attacker and
///     amount) - damage both ways.
/// Activation is hover-and-key: an inventory item under the mouse has
/// inmouse = true, and vanilla offers passive items no "Use" entry to hang one on.
/// </remarks>
public sealed class ReliquaryMod : CoreMod, IRelicHost
{
    // A scan walks the carriers and the player's gear; every two seconds keeps
    // a dropped, sold or loaded relic from going unnoticed for long.
    private const int ScanEveryFrames = 120;

    internal static string ActivateKeyName { get; private set; } = "U";

    /// <summary>Whether the wielded weapon is a staff, from the last scan.</summary>
    internal static bool WieldingStaff { get; private set; }

    /// <summary>Hands the wielded weapon takes (1 or 2), 0 with none, from the last scan.</summary>
    internal static int WeaponHands { get; private set; }

    private readonly List<Relic> _relics = new()
    {
        new StaveboundEmber(),
        new Gorgoneion(),
        new WolfsHeart(),
        new CopperRing(),
        new GraftedHand(),
        new PilgrimsMillstone(),
        // Riders and other damage-path relics.
        new FacelessMirror(), new SplitQuiver(), new CinderRosary(), new EchoingBell(), new DebtorsKnot(), new WeepingCandle(),
        // Panic buttons and scaling passives.
        new PallbearersCoin(), new VesselOfBorrowedYears(), new ReliquaryOfSaintMardun(), new UsurersScale(), new SunderedGate(),
        // Auras, grafts and needs.
        new Censer(), new LodestoneIdol(), new SurveyorsChain(), new IronLung(), new OathStone(), new SatedWorm(),
    };

    private Carriers _carriers = null!;
    private int _sinceScan = ScanEveryFrames;
    private string _lastMessage = "";
    private long _turns;
    private long _playerKey = -1;

    public override void OnInitialize()
    {
        ActivateKeyName = Config.Get("activateKey", "U").Trim().ToUpperInvariant();
        if (ActivateKeyName.Length != 1) ActivateKeyName = "U";

        var sprites = new Dictionary<string, Sprite>();
        foreach (var r in _relics)
        {
            // 27 px per inventory cell, drawn from the top-left like the game's own icons.
            try { sprites[r.Id] = Content.AddSprite($"assets/{r.Id}.png"); }
            catch (Exception ex) { Log.Warning($"{r.Name}: no placeholder sprite ({ex.Message}), it keeps the carrier's icon"); }
        }
        _carriers = new Carriers(_relics, sprites, Log);

        Scripts.scr_atr_calc.After(OnStatsCalculated);
        Scripts.scr_global_turn_end.After(OnTurnEnd);
        Scripts.scr_save_damage_received.After(OnDamage);

        foreach (var relic in _relics) Guard(relic, "install", () => relic.Install(this));

        if (TestHost.Enabled) RegisterCommands();
        Log.Info($"{_relics.Count} relics ready; activate with [{ActivateKeyName}] over a relic in the inventory");
    }

    public override void OnUpdate()
    {
        if (World.Player is not { } current) return;

        // A load (or a new character) replaces the player and every item: the
        // relics found before are gone, so look again at once.
        long key = World.IdKey(current.Id);
        if (key != _playerKey)
        {
            _playerKey = key;
            _sinceScan = ScanEveryFrames;
        }
        if (++_sinceScan >= ScanEveryFrames) Rescan();

        foreach (var item in Active())
            Guard(item.Relic, "frame", () => item.Relic.OnFrame(item, current));

        // What the player had when the frame's turn work was done: a hit
        // arrives with HP already down (and clamped at zero), so this is the
        // only honest answer to "how much did that really take".
        LastHp = World.Num(current, Objects.o_player.Vars.HP);

        // keyboard_check_pressed is true for the frame the key went down; this
        // runs in the same frame, after the game's step.
        if (Builtins.keyboard_check_pressed(Builtins.ord(ActivateKeyName)).AsBool)
        {
            var hovered = _carriers.Items.FirstOrDefault(i => i.Carried && i.Relic.Activatable &&
                                                               i.Ref.Exists && i.Ref.Get("inmouse").AsBool);
            // A refusal ("still recharging") is already in the game's log; it is
            // an answer, not a fault.
            if (hovered != null)
            {
                try { Activate(hovered); }
                catch (Exception ex) when (ex is InvalidOperationException or GmlException) { }
            }
        }
    }

    /// <summary>The player's HP at the end of the last frame (see OnUpdate).</summary>
    internal static double LastHp { get; private set; }

    // A failed scan keeps the last good list rather than faulting the mod: one
    // odd item, or a scan caught mid-load, is not worth losing every relic over.
    private void Rescan()
    {
        _sinceScan = 0;
        string before = Signature();
        try { _carriers.Scan(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning($"scan failed, keeping the last one: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        WieldingStaff = _carriers.WeaponType.Contains("staff", StringComparison.OrdinalIgnoreCase);
        WeaponHands = _carriers.WeaponHands;
        // Tooltips last, once the wielded weapon is known: several status lines depend on it.
        foreach (var item in _carriers.Items)
            Guard(item.Relic, "refresh", () => _carriers.Refresh(item));
        // Picking up, dropping, wearing or swapping changes what the stat layer
        // adds; ask the game to recalculate rather than wait for its next reason.
        if (Signature() != before && World.Player is { } p) World.Recalculate(p);
    }

    private string Signature() =>
        $"{_carriers.WeaponType}|" + string.Join(",", _carriers.Items.Select(i => $"{i.Relic.Id}:{i.Carried}:{i.Equipped}"));

    /// <summary>
    /// The copy of each relic that counts: the first one carried. A second
    /// Millstone in the bag is ballast, not another +20%.
    /// </summary>
    private IEnumerable<RelicItem> Active() =>
        _carriers.Items.Where(i => i.Carried).GroupBy(i => i.Relic).Select(g => g.OrderByDescending(i => i.Equipped).First());

    // ------------------------------------------------------------ hooks

    private void OnStatsCalculated(HookCall c)
    {
        // Every unit recalculates through here; only the player's stats are ours.
        if (c.Self.IsNull || !World.IsPlayer(c.Self.Get("id"))) return;
        var stats = new Stats(c.Self);
        // Stats wraps the hook's own self, so it cannot go into Guard's lambda.
        foreach (var relic in _relics)
        {
            try { relic.OnPlayerStats(stats); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning($"{relic.Name} (stats): {ex.GetType().Name}: {ex.Message}");
            }
        }
        foreach (var item in Active())
        {
            try { item.Relic.OnStats(item, stats); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning($"{item.Relic.Name} (stats): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void OnTurnEnd(HookCall c)
    {
        if (c.Self.IsNull || !World.IsPlayer(c.Self.Get("id")) || World.Player is not { } player) return;
        _turns++;
        foreach (var relic in _relics)
            Guard(relic, "turn", () => relic.OnPlayerTurn(player));
        // Every item ticks (a recharge runs down in a chest too); the relics
        // themselves check whether the item is carried before doing anything.
        foreach (var item in _carriers.Items.ToList())
            Guard(item.Relic, "turn", () => item.Relic.OnTurn(item, player));
        // The rescan also refreshes the tooltips' live status lines.
        Rescan();
    }

    private void OnDamage(HookCall c)
    {
        if (c.Self.IsNull || c.ArgCount < 2 || World.Player is not { } player) return;
        var amountArg = c.GetArg(1);
        if (!amountArg.IsNumber) return;
        double amount = amountArg.AsReal;
        if (!(amount > 0)) return;
        var victimId = c.Self.Get("id");
        var attacker = c.GetArg(0);

        if (World.IsPlayer(victimId))
        {
            foreach (var item in Active())
                Guard(item.Relic, "damage taken", () => item.Relic.OnPlayerDamaged(item, player, amount, attacker));
        }
        else if (World.IsPlayer(attacker))
        {
            var victim = new InstanceRef(victimId);
            foreach (var item in Active())
                Guard(item.Relic, "damage dealt", () => item.Relic.OnEnemyDamaged(item, player, victim, amount));
        }
    }

    // ------------------------------------------------------------ IRelicHost

    RelicItem? IRelicHost.Active(Relic relic) => Active().FirstOrDefault(i => i.Relic == relic);

    void IRelicHost.Guard(Relic relic, string what, Action action) => Guard(relic, what, action);

    void IRelicHost.Before(Relic relic, ScriptRef script, HookHandler handler) =>
        script.Before(c => Guarded(relic, script, handler, c));

    void IRelicHost.After(Relic relic, ScriptRef script, HookHandler handler) =>
        script.After(c => Guarded(relic, script, handler, c));

    void IRelicHost.Log(string text) => Log.Info(text);

    // A relic's own hook, run so that a throw costs a log line, not the mod.
    private void Guarded(Relic relic, ScriptRef script, HookHandler handler, HookCall c)
    {
        try { handler(c); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning($"{relic.Name} ({script.Symbol}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // One relic going wrong must not take the others (or the mod) down with it.
    private void Guard(Relic relic, string what, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning($"{relic.Name} ({what}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ actions

    private string Activate(RelicItem item)
    {
        var player = World.RequirePlayer();
        try
        {
            string said = item.Relic.Activate(item, player);
            World.Say(said);
            _lastMessage = $"{item.Relic.Name}: activated";
            // Stats that depend on the new state apply from the next recalculation.
            World.Recalculate(player);
            if (item.Ref.Exists) _carriers.Refresh(item);
            Log.Info($"{item.Relic.Name} activated");
            return said;
        }
        catch (InvalidOperationException ex)
        {
            World.Say($"~y~{item.Relic.Name}~/~: {ex.Message}.");
            _lastMessage = $"{item.Relic.Name}: {ex.Message}";
            throw;
        }
        // The game refused a call part-way: say so, where the player looks and in the log.
        catch (GmlException ex)
        {
            World.Say($"~r~{item.Relic.Name}~/~ falters.");
            _lastMessage = $"{item.Relic.Name}: {ex.Message}";
            Log.Warning($"{item.Relic.Name} (activate): {ex.Message}");
            throw;
        }
    }

    private RelicItem Give(Relic relic)
    {
        var item = _carriers.Give(relic);
        _lastMessage = $"{relic.Name}: in your bag";
        World.Recalculate(World.RequirePlayer());
        return item;
    }

    // ------------------------------------------------------------ panel

    public override void OnGUI()
    {
        if (World.Player is null)
        {
            UI.TextColored(0.95f, 0.8f, 0.35f, "Load a save to see or give relics.");
            return;
        }

        UI.TextWrapped($"Relics work from the bag (the Copper Ring when worn). Hover one in the inventory and press " +
                       $"[{ActivateKeyName}] to activate it (activateKey in Reliquary.json).");
        if (_lastMessage.Length > 0) UI.TextDisabled(_lastMessage);
        UI.Separator();

        // The items are from the last scan, up to two seconds old: one sold or
        // destroyed since must cost a line of text, not the mod.
        foreach (var relic in _relics)
            UI.Guarded(() => DrawRelic(relic), ex => UI.TextColored(0.95f, 0.4f, 0.4f, $"{relic.Name}: {ex.Message}"));
    }

    private void DrawRelic(Relic relic)
    {
        UI.PushId(relic.Id);
        var mine = _carriers.Items.Where(i => i.Relic == relic).ToList();
        UI.SeparatorText($"{relic.Name}  ({relic.Family})###head");
        if (World.Player is { } player && relic.PlayerStatus(player) is { Length: > 0 } onYou) UI.TextColored(0.95f, 0.8f, 0.35f, onYou);
        if (UI.Button("Give", 80f))
        {
            try { Give(relic); }
            catch (Exception ex) when (ex is InvalidOperationException or GmlException) { _lastMessage = $"{relic.Name}: {ex.Message}"; }
        }
        foreach (var item in mine)
        {
            UI.SameLine();
            string where = item.Equipped ? "worn" : item.Carried ? "in bag" : "elsewhere";
            UI.TextDisabled($"[{where}] {relic.Status(item)}");
            if (relic.Activatable && item.Carried)
            {
                UI.SameLine();
                if (UI.SmallButton($"Activate###act{World.IdKey(item.Ref.Id)}"))
                {
                    try { Activate(item); }
                    catch (Exception ex) when (ex is InvalidOperationException or GmlException) { }
                }
            }
        }
        UI.PopId();
    }

    // ------------------------------------------------------------ test host

    private void RegisterCommands()
    {
        TestHost.Register("reliq.list", _ => _relics.Select(r => new { id = r.Id, name = r.Name, family = r.Family }).ToArray(),
            "reliq.list: every relic {id, name, family}");
        TestHost.Register("reliq.give", args =>
        {
            var item = Give(Find(args));
            return new { id = World.IdKey(item.Ref.Id), relic = item.Relic.Id };
        }, "reliq.give <id>: puts a new relic in the bag, answers {id, relic}");
        TestHost.Register("reliq.state", _ =>
        {
            Rescan();
            return new
            {
                turns = _turns,
                weapon = _carriers.WeaponType,
                hands = WeaponHands,
                onYou = _relics.Select(r => new { relic = r.Id, status = World.Player is { } p ? r.PlayerStatus(p) : "" })
                               .Where(s => s.status.Length > 0).ToArray(),
                items = _carriers.Items.Select(i => new
                {
                    id = World.IdKey(i.Ref.Id), relic = i.Relic.Id, carried = i.Carried, equipped = i.Equipped,
                    status = i.Relic.Status(i), data = i.Data.ToJson(),
                }).ToArray(),
            };
        }, "reliq.state: turns seen, wielded weapon, effects on the character, and every relic item {id, relic, carried, equipped, status, data}");
        TestHost.Register("reliq.activate", args =>
        {
            var relic = Find(args);
            Rescan();
            var item = _carriers.Items.FirstOrDefault(i => i.Relic == relic && i.Carried)
                       ?? throw new InvalidOperationException($"no {relic.Name} is carried");
            return Activate(item);
        }, "reliq.activate <id>: activates the carried relic, answers what it logged");
        TestHost.Register("reliq.hostiles", _ =>
        {
            var p = World.RequirePlayer();
            return World.Hostiles(p).Select(e => new { id = World.IdKey(e.Id), tiles = World.Tiles(p, e) }).ToArray();
        }, "reliq.hostiles: hostiles in the player's vision {id, tiles}");
    }

    private Relic Find(IReadOnlyList<JsonElement> args)
    {
        string id = args.Count > 0 ? args[0].GetString() ?? "" : throw new ArgumentException("which relic? see reliq.list");
        return _relics.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException($"no relic '{id}' (see reliq.list)");
    }
}
