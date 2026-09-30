using System.Text.Json;
using CoreLoader;
using StoneShard;

[assembly: CoreModInfo(typeof(TavernGames.TavernGamesMod), "Tavern Games", "0.1.0", "Lodestone")]
[assembly: CoreModGame("StoneShard")]

namespace TavernGames;

/// <summary>
/// Gambling with the locals: stand next to someone in a tavern and press the
/// play key (G by default) to sit down to Poker Dice or Twenty-One for crowns.
/// The table is a small framework - <see cref="Table"/> runs the stake, the
/// purses, the window and input; each game is a <see cref="MiniGame"/> - so a
/// new game is one class added to the list below.
/// </summary>
public sealed class TavernGamesMod : CoreMod
{
    // Scanning every NPC is cheap but not free: the prompt is refreshed four times a second.
    private const int PromptEveryFrames = 15;

    private readonly Ledger _ledger = new();
    private readonly Canvas _promptCanvas = new();
    private Table _table = null!;
    private List<MiniGame> _games = null!;
    private string _key = "G";
    private double _reach = 1.5;
    private int _sincePrompt = PromptEveryFrames;
    private InstanceRef? _candidate;
    private string _candidateName = "";
    private string _lastDrawError = "", _lastUpdateError = "";
    private long _playerKey = -1;

    public override void OnInitialize()
    {
        _key = Config.Get("playKey", "G").Trim().ToUpperInvariant();
        if (_key.Length != 1) _key = "G";
        _reach = Math.Clamp(Config.Get("reachTiles", 1.5), 1, 4);
        Tavern.Anywhere = Config.Get("anywhere", false);

        var dice = Content.AddSprite("assets/dice.png", frames: 6);
        var cards = Content.AddSprite("assets/cards.png", frames: 53);
        _games = [new DicePoker(dice), new TwentyOne(cards)];
        _table = new Table(_games, _ledger, Log);
        _table.CaptureKeys();

        GameDraw.OnGui(DrawGui);
        // Purses refill as turns pass; the tick runs as whoever ends a turn, the player's is ours.
        Scripts.scr_global_turn_end.After(c =>
        {
            try
            {
                if (!c.Self.IsNull && World.Player is { } p && Tavern.IdKey(c.Self.Get("id")) == Tavern.IdKey(p.Get("id"))) _ledger.Turn();
            }
            catch (GmlException) { }
        });
        if (TestHost.Enabled) RegisterCommands();
        Log.Info($"ready: stand by someone in a tavern and press [{_key}] to play ({string.Join(", ", _games.Select(g => g.Title))})");
    }

    public override void OnShutdown()
    {
        _table?.Drop();
        _promptCanvas.Clear();
    }

    // Frames in a row the open table has failed to update. Past the limit it
    // is closed: the table holds every key and click, so a table that keeps
    // failing would otherwise leave the player unable to do anything.
    private const int MaxFailedFrames = 30;
    private int _failedFrames;

    public override void OnUpdate()
    {
        // The table and the prompt read the live game every frame: a refused
        // call costs that frame (logged once), not the mod. Anything else
        // would fault the mod, and a faulted mod gets no OnShutdown - so it is
        // caught too, and an open table is closed with the stake handed back.
        try
        {
            Step();
            _failedFrames = 0;
        }
        catch (GmlException ex)
        {
            if (ex.Message != _lastUpdateError) Log.Warning($"update: {ex.Message}");
            _lastUpdateError = ex.Message;
            if (_table.IsOpen && ++_failedFrames >= MaxFailedFrames) CloseAfterFault("the table keeps failing");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("update", ex);
            if (_table.IsOpen) CloseAfterFault(ex.GetType().Name);
        }
    }

    private void CloseAfterFault(string why)
    {
        _failedFrames = 0;
        Log.Warning($"closing the table ({why}); the stake goes back to the player");
        try { _table.Drop(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error("dropping the table", ex); }
        World.Say("~y~Tavern Games~/~: the game is called off; your stake is returned.");
    }

    private void Step()
    {
        // The player instance is replaced by a room change as well as a load.
        // Neither can happen with the table up (it holds every click and key),
        // but if one does, the round is called off and the stake returned.
        // The books are kept either way: clearing them on a room change would
        // refill a drained purse for walking out and back in.
        long key = World.Player is { } current ? Tavern.IdKey(current.Get("id")) : -1;
        if (key != _playerKey)
        {
            if (_table.IsOpen) _table.Drop();
            _playerKey = key;
        }

        if (_table.IsOpen)
        {
            _table.Update();
            return;
        }
        if (World.Player is not { } player)
        {
            _candidate = null;
            return;
        }
        if (++_sincePrompt >= PromptEveryFrames)
        {
            _sincePrompt = 0;
            _candidate = Tavern.GameBusy() ? null : Tavern.NearestPlayer(player, _reach, out _);
            _candidateName = _candidate is { } c ? Tavern.DisplayName(c) : "";
        }
        if (!Builtins.keyboard_check_pressed(Builtins.ord(_key)).AsBool || Tavern.GameBusy()) return;
        if (Tavern.NearestPlayer(player, _reach, out string? why) is not { } npc)
        {
            if (why != null) World.Say($"~y~Tavern Games~/~: {why}.");
            return;
        }
        _table.Open(npc);
    }

    private void DrawGui()
    {
        try
        {
            if (_table.IsOpen) _table.Draw();
            else if (_candidate is { Exists: true }) DrawPrompt();
        }
        // A bad frame costs that frame; the same error is logged once. Esc
        // and the buttons are handled by the update, not here, so a table that
        // fails to draw can still be left.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex.Message != _lastDrawError) Log.Warning($"drawing: {ex.GetType().Name}: {ex.Message}");
            _lastDrawError = ex.Message;
        }
    }

    // A small board above the action bar: who will play, and the key.
    private void DrawPrompt()
    {
        var c = _promptCanvas;
        c.Begin();
        var area = new Area(Canvas.DesignW / 2 - 110, Canvas.DesignH - 118, 220, 26);
        c.Board(area);
        c.Text($"[~lg~{_key}~/~] Play with ~y~{_candidateName}~/~", area.CenterX, area.Y + 7, align: 0);
    }

    // ------------------------------------------------------------ overlay

    public override void OnGUI()
    {
        UI.TextWrapped($"Stand next to someone in a tavern (innkeepers, drunks, sellswords and the like) and press [{_key}] " +
                       "to play. Esc or Leave stands up; leaving mid-round folds it. playKey, reachTiles and anywhere are in TavernGames.json.");
        bool anywhere = Tavern.Anywhere;
        if (UI.Checkbox("Play with anyone friendly, anywhere (not only in taverns)", ref anywhere))
        {
            Tavern.Anywhere = anywhere;
            Config.Set("anywhere", anywhere);
        }
        UI.Separator();
        if (_table.IsOpen && _table.Opponent is { } opp)
            UI.Text($"At the table with {opp.Name}: {_table.CurrentGame.Title}, {_table.PhaseName}");
        else
            UI.TextDisabled(_candidate != null ? $"Nearby: {_candidateName}" : "Nobody nearby wants to play.");
        UI.SeparatorText("Ledger");
        foreach (var (key, e) in _ledger.All)
            UI.Text($"{key}: purse {e.Bankroll}/{e.Purse}, rounds {e.Played}, your net {e.Net:+#;-#;0}");
    }

    // ------------------------------------------------------------ test host

    private void RegisterCommands()
    {
        TestHost.Register("tg.npcs", _ =>
        {
            var p = World.Player ?? throw new InvalidOperationException("no player");
            bool tavern = Tavern.Anywhere || Tavern.InTavern(p);
            return (Objects.o_NPC.Object?.Instances() ?? []).Where(n => n.Exists).Select(n => new
            {
                id = Tavern.IdKey(n.Get("id")),
                obj = Tavern.ObjectName(n),
                name = Tavern.DisplayName(n),
                key = Tavern.Key(n),
                tiles = Tavern.Tiles(p, n),
                refusal = Tavern.Refusal(n, tavern),
            }).OrderBy(n => n.tiles).Take(20).ToArray();
        }, "tg.npcs: the 20 nearest NPCs {id, obj, name, key, tiles, refusal (\"\" = would play)}");
        TestHost.Register("tg.open", args =>
        {
            var p = World.Player ?? throw new InvalidOperationException("no player");
            string? game = args.Count > 0 ? args[0].GetString() : null;
            InstanceRef npc;
            if (args.Count > 1)
            {
                long id = args[1].GetInt64();
                var found = (Objects.o_NPC.Object?.Instances() ?? []).Where(n => n.Exists && Tavern.IdKey(n.Get("id")) == id).ToList();
                npc = found.Count > 0 ? found[0] : throw new ArgumentException($"no NPC {id}");
            }
            else
            {
                npc = Tavern.NearestPlayer(p, _reach, out string? why) ?? throw new InvalidOperationException(why ?? "nobody within reach");
            }
            _table.Open(npc, game);
            return Snapshot();
        }, "tg.open [game [npcId]]: sits down (with the nearest willing NPC, or that NPC regardless of where), answers tg.state");
        TestHost.Register("tg.state", _ => Snapshot(), "tg.state: the table {open, opponent, game, phase, stake, round, game state}");
        TestHost.Register("tg.press", args =>
        {
            string id = args.Count > 0 ? args[0].GetString() ?? "" : throw new ArgumentException("which button?");
            return new { pressed = _table.Press(id), state = Snapshot() };
        }, "tg.press <id>: presses a table button (play, again, leave, stake+, stake-, game:<id>, or the game's: raise, check, call, fold, reroll, hit, stand, double)");
        TestHost.Register("tg.toggle", args =>
            {
                if (args.Count == 0) throw new ArgumentException("which die (0-4)?");
                if (!_table.IsOpen || _table.CurrentGame is not DicePoker dice) throw new InvalidOperationException("no poker dice on the table");
                return dice.Toggle(args[0].GetInt32());
            },
            "tg.toggle <0-4>: poker dice - marks or unmarks one of your dice to throw again");
        TestHost.Register("tg.seed", args =>
        {
            _table.Seed(args[0].GetInt32());
            return true;
        }, "tg.seed <n>: makes the dice and the deck repeatable from the next round");
        TestHost.Register("tg.close", _ =>
        {
            _table.Close();
            return Snapshot();
        }, "tg.close: stands up (folding a round in play)");
    }

    private object Snapshot()
    {
        var r = _table.Round;
        object? game = _table.IsOpen && r != null
            ? _table.CurrentGame switch
            {
                DicePoker d => d.State(),
                TwentyOne t => t.State(),
                _ => null,
            }
            : null;
        return new
        {
            open = _table.IsOpen,
            opponent = _table.Opponent is { } o ? new { o.Name, o.Key, temper = o.Temper.ToString(), bankroll = o.Bankroll } : null,
            game = _table.CurrentGame.Id,
            phase = _table.PhaseName,
            stake = _table.Stake,
            gold = World.Player is { } p ? Purse.Count(p) : null,
            escrow = _table.Escrow,
            owed = _table.Owed,
            round = r == null ? null : new
            {
                r.PlayerIn, r.OpponentIn, r.Finished, result = r.Finished ? r.Result.ToString() : null, r.Verdict,
                buttons = r.Finished ? [] : _table.CurrentGame.Buttons().Select(b => $"{b.Id}{(b.Enabled ? "" : " (off)")}").ToArray(),
                log = r.Log.TakeLast(6).ToArray(),
            },
            state = game,
        };
    }
}
