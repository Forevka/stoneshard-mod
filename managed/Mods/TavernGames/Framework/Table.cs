using CoreLoader;
using StoneShard;

namespace TavernGames;

/// <summary>
/// The table: a modal window over the game where the player picks a game and a
/// stake, plays a round against an NPC, and is paid or pays. It owns what every
/// game shares - the frame, the purses, the chatter, the buttons, input - and
/// hands the round to a <see cref="MiniGame"/> once the stake is agreed.
/// </summary>
/// <remarks>
/// Input: while the table is open, pick mode is kept armed, so every click is
/// swallowed before the game sees it (the player cannot walk off or attack
/// mid-hand) and comes back here as a point on the GUI layer.
///
/// Gold is held in escrow: the player's crowns leave the purse as they are
/// committed (the ante, each raise), so a loss needs no further transfer and
/// can never be dodged. A win pays back the escrow plus the opponent's share, a
/// draw the escrow alone. A round that is dropped unfinished - a hot reload,
/// the opponent gone, a room change, a fault - gives the escrow back. A payout
/// the game refuses outright is kept as a debt, retried, and blocks the next
/// round until paid.
/// </remarks>
internal sealed class Table
{
    private static readonly int[] Stakes = [5, 10, 25, 50, 100, 200, 500];
    private const int RetryEveryFrames = 60;

    private readonly Canvas _canvas = new();
    private readonly Logger _log;
    private readonly Ledger _ledger;
    private readonly IReadOnlyList<MiniGame> _games;
    private readonly List<string> _chatter = new();
    private Random _rng = new();

    private enum Phase { Choosing, Playing, Settled }

    private Phase _phase;
    private int _gameIndex;
    private int _stakeIndex = 1;
    private Opponent? _opponent;
    private Session? _round;
    private string _error = "";

    // Crowns taken from the player for the round in play, and crowns the
    // table owes the player (a payout the game did not take).
    private int _escrow, _owed, _sinceRetry;

    // The purse as the Draw pass read it, once per frame.
    private int? _gold;

    // Esc, as seen before the game cleared it (see CaptureKeys).
    private bool _escPressed;

    public Table(IReadOnlyList<MiniGame> games, Ledger ledger, Logger log)
    {
        _games = games;
        _ledger = ledger;
        _log = log;
    }

    public bool IsOpen => _opponent != null;

    public Opponent? Opponent => _opponent;

    public MiniGame CurrentGame => _games[_gameIndex];

    public Session? Round => _round;

    public string PhaseName => _phase.ToString();

    public int Stake => Stakes[_stakeIndex];

    public int Escrow => _escrow;

    public int Owed => _owed;

    /// <summary>
    /// Keeps the keyboard from the game while the table is up: at the start of
    /// every step (o_controller's Begin Step, which runs before any key event
    /// or Step reads the keys) the table notes Esc for itself and clears all
    /// input, so Esc does not also open the pause menu and no hotkey reaches
    /// the game underneath. Mouse clicks are already the table's through pick
    /// mode, which works below GameMaker's input and is not affected.
    /// </summary>
    public void CaptureKeys() =>
        Objects.o_controller.Step_1.Before(_ =>
        {
            if (!IsOpen) return;
            try
            {
                if (Builtins.keyboard_check_pressed(27).AsBool) _escPressed = true;
                Builtins.io_clear();
            }
            catch (GmlException) { }
        });

    /// <summary>Replaces the dice and the deck with a seeded sequence (test host).</summary>
    public void Seed(int seed) => _rng = new Random(seed);

    // ------------------------------------------------------------ open / close

    /// <summary>Sits down with <paramref name="npc"/>, standing up from any other table first.</summary>
    public void Open(InstanceRef npc, string? gameId = null)
    {
        if (IsOpen) Close();
        var (temper, purse) = Tavern.Classify(npc);
        string key = Tavern.Key(npc);
        var opp = new Opponent(npc, key, Tavern.DisplayName(npc), temper, _ledger.For(key, purse));
        _opponent = opp;
        _chatter.Clear();
        _round = null;
        _error = "";
        _escPressed = false;
        _phase = Phase.Choosing;
        if (gameId != null) SelectGame(gameId);
        ClampStake();
        Chat(opp.Bankroll < Stakes[0] ? Line(Mood.Broke) : Line(Mood.Greet));
        Input.ArmPick();
        Sfx.Play("snd_ui_open_window_st");
        _log.Info($"sat down with {opp.Name} ({Tavern.ObjectName(npc)}, {temper}, purse {opp.Bankroll})");
    }

    /// <summary>Stands up. A round still in play is folded - the player walked away from it.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        if (_round is { Finished: false } r)
        {
            // A round the player can no longer change is played out; only one
            // still waiting on them is folded.
            try { CurrentGame.Conclude(); }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException) { _log.Warning($"concluding the round: {ex.Message}"); }
            if (!r.Finished) r.Forfeit("You walk away from the table.");
            Settle();
        }
        if (_owed > 0) PayOwed();
        if (_owed > 0) _log.Warning($"left the table still owed {_owed} crowns the game would not take");
        _opponent = null;
        _round = null;
        if (Input.IsPicking) Input.CancelPick();
        Sfx.Play("snd_ui_close_window_st");
    }

    /// <summary>
    /// The round cannot go on (a hot reload, a room change, the opponent gone):
    /// nobody won, so the player gets back what the table holds for them.
    /// </summary>
    public void Drop()
    {
        int back = _escrow + _owed;
        if (back > 0 && World.Player is { } player)
        {
            try
            {
                int moved = Purse.Change(player, back);
                if (moved != back) _log.Warning($"returning {back} crowns from the table moved {moved}");
                else _log.Info($"round dropped: {back} crowns returned");
            }
            catch (Exception ex) when (ex is InvalidOperationException or GmlException)
            {
                _log.Warning($"could not return {back} crowns from the table: {ex.Message}");
            }
        }
        else if (back > 0)
        {
            _log.Warning($"{back} crowns at the table had no player to go back to");
        }
        _escrow = _owed = 0;
        _opponent = null;
        _round = null;
        if (Input.IsPicking) Input.CancelPick();
        _canvas.Clear();
    }


    private bool SelectGame(string id)
    {
        int i = _games.ToList().FindIndex(g => g.Id == id);
        if (i < 0) return false;
        _gameIndex = i;
        return true;
    }

    // ------------------------------------------------------------ per frame

    /// <summary>Once a frame, after Draw GUI: the round's own work, then the click the player made, if any.</summary>
    public void Update()
    {
        if (_opponent is not { } opp) return;
        if (World.Player is null || !opp.Npc.Exists)
        {
            _log.Info("the table was left behind (room change, or the opponent is gone)");
            Drop();
            return;
        }

        // Esc first: whatever else fails this frame, the player can always leave.
        if (_escPressed)
        {
            _escPressed = false;
            Close();
            return;
        }

        if (_phase == Phase.Playing && _round is { } r)
        {
            CurrentGame.Tick();
            if (r.Finished) Settle();
        }
        if (_owed > 0 && ++_sinceRetry >= RetryEveryFrames) PayOwed();

        // Keep every click for ourselves while the table is up.
        if (!Input.IsPicking) Input.ArmPick();
        if (!Input.TryTakePick(out var click)) return;
        Input.ArmPick();
        if (click.RightButton) return;
        // The game still tracks the mouse (only buttons are swallowed), and a
        // pick is taken the frame it happens, so this is where the click was.
        var (x, y) = _canvas.ToDesign(Builtins.device_mouse_x_to_gui(0).AsReal, Builtins.device_mouse_y_to_gui(0).AsReal);
        string? id = _canvas.ButtonAt(x, y);
        if (id == null)
        {
            if (_phase == Phase.Playing && PlayArea.Contains(x, y)) CurrentGame.OnClick(x, y);
            return;
        }
        if (id.Length > 0) Press(id);
    }

    /// <summary>
    /// Presses a button by id, as a click on it would: only the buttons the
    /// table shows in its current phase do anything (the test host drives the
    /// table this way).
    /// </summary>
    public bool Press(string id)
    {
        if (!IsOpen) return false;
        _error = "";
        try
        {
            bool done = PressIn(id);
            if (done) Sfx.Play("snd_button_click");
            return done;
        }
        catch (Exception ex) when (ex is InvalidOperationException or GmlException)
        {
            _error = ex.Message;
            return false;
        }
    }

    private bool PressIn(string id)
    {
        if (id == "leave")
        {
            Close();
            return true;
        }
        switch (_phase)
        {
            case Phase.Choosing:
                if (id.StartsWith("game:", StringComparison.Ordinal)) return SelectGame(id[5..]);
                switch (id)
                {
                    case "stake-" when _stakeIndex > 0:
                        _stakeIndex--;
                        return true;
                    case "stake+" when _stakeIndex < Stakes.Length - 1:
                        _stakeIndex++;
                        ClampStake();
                        return true;
                    case "play":
                        StartRound();
                        return true;
                }
                return false;
            case Phase.Settled:
                switch (id)
                {
                    case "again":
                        StartRound();
                        return true;
                    case "choose":
                        _phase = Phase.Choosing;
                        _round = null;
                        return true;
                }
                // One of the game's own ways to carry on, at the stake it names.
                if (_round is { } done)
                {
                    foreach (var (button, stake) in CurrentGame.Continuations(done))
                    {
                        if (button.Id != id || !button.Enabled) continue;
                        StartRound(stake, continues: true);
                        return true;
                    }
                }
                return false;
            default:
                if (_round is not { Finished: false } r) return false;
                if (!CurrentGame.Buttons().Any(b => b.Id == id && b.Enabled)) return false;
                CurrentGame.OnButton(id);
                if (r.Finished) Settle();
                return true;
        }
    }

    /// <summary>A round at the chosen stake, or at a continuation's stake (see MiniGame.Continuations).</summary>
    private void StartRound(int? stakeOverride = null, bool continues = false)
    {
        if (_opponent is not { } opp || World.Player is not { } player) return;
        if (_owed > 0) throw new InvalidOperationException($"the table still owes you {_owed} crowns");
        int stake = stakeOverride ?? Stake;
        if (opp.Bankroll < stake)
        {
            Chat(Line(Mood.Broke));
            throw new InvalidOperationException($"{opp.Name} cannot cover {stake} crowns");
        }
        Take(player, stake);
        _round = new Session(opp, stake, _rng,
            playerCanCover: more => (Purse.Count(player) ?? 0) >= more,
            takeFromPlayer: more => Take(player, more))
        {
            Continues = continues,
        };
        _phase = Phase.Playing;
        _chatter.Clear();
        CurrentGame.Begin(_round);
    }

    /// <summary>Takes crowns from the player into escrow; throws if the game will not give them up.</summary>
    private void Take(InstanceRef player, int amount)
    {
        int moved = Purse.Change(player, -amount);
        if (moved == -amount)
        {
            _escrow += amount;
            return;
        }
        // The purse moved by something else (or not at all): put it back as it was.
        if (moved != 0)
        {
            try { Purse.Change(player, -moved); }
            catch (Exception ex) when (ex is InvalidOperationException or GmlException)
            {
                _log.Warning($"taking {amount} crowns moved {moved} and could not be undone: {ex.Message}");
            }
        }
        throw new InvalidOperationException($"the game would not take {amount} crowns from you");
    }

    /// <summary>The round ended: pay out, update the books, let the opponent have a word.</summary>
    private void Settle()
    {
        if (_round is not { Finished: true } r || _phase == Phase.Settled || _opponent is not { } opp) return;
        _phase = Phase.Settled;
        // The player's side of it: what they win off the opponent, or lose to them.
        int delta = r.Result switch
        {
            Outcome.Win => r.OpponentIn,
            Outcome.Loss => -r.PlayerIn,
            _ => 0,
        };
        // What the table gives back: the escrow (which is PlayerIn: a take
        // either moves exactly its amount or nothing) and, on a win, the
        // opponent's share. A loss keeps it all.
        int payout = r.Result switch
        {
            Outcome.Win => _escrow + r.OpponentIn,
            Outcome.Push => _escrow,
            _ => 0,
        };
        _escrow = 0;
        _owed += payout;
        PayOwed();

        opp.Books.Bankroll -= delta;
        opp.Books.Net += delta;
        opp.Books.Played++;
        Chat(r.Result switch
        {
            Outcome.Win => Line(Mood.Lose),
            Outcome.Loss => Line(Mood.Win),
            _ => "",
        });
        Sfx.Play(r.Result == Outcome.Win ? "snd_gui_pick_gold" : r.Result == Outcome.Loss ? "snd_gui_drop_gold_1" : "snd_button_click");
        string money = delta > 0 ? $"~lg~+{delta}~/~ crowns" : delta < 0 ? $"~r~{delta}~/~ crowns" : "no crowns change hands";
        World.Say($"~y~{CurrentGame.Title}~/~ with {opp.Name}: {Strip(r.Verdict)} ({money}).");
        _log.Info($"{CurrentGame.Id} vs {opp.Name}: {r.Result}, {delta:+#;-#;0} crowns");
        ClampStake();
    }

    /// <summary>
    /// Pays what the table owes the player. A call that fails outright moved
    /// nothing and is tried again later. A call that runs but moves a different
    /// amount is never repeated: gold is coin items, and a bag with no room may
    /// have put them somewhere the count does not see (the floor), so a retry
    /// could hand them over twice. That case is reported instead.
    /// </summary>
    private void PayOwed()
    {
        _sinceRetry = 0;
        if (_owed <= 0 || World.Player is not { } player) return;
        int owed = _owed;
        try
        {
            int moved = Purse.Change(player, owed);
            _owed = 0;
            if (moved != owed)
            {
                _log.Warning($"paid {owed} crowns but the purse moved {moved}");
                World.Say($"~y~Tavern Games~/~: {owed} crowns were paid out, but only {Math.Max(0, moved)} reached your purse. Is your bag full?");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or GmlException)
        {
            _log.Warning($"paying {owed} crowns failed: {ex.Message}");
        }
        if (_owed > 0) _error = $"the table owes you {_owed} crowns; trying again";
    }

    private void ClampStake()
    {
        if (_opponent is not { } opp) return;
        int cap = opp.Bankroll;
        if (World.Player is { } p && Purse.Count(p) is { } gold) cap = Math.Min(cap, gold);
        while (_stakeIndex > 0 && Stakes[_stakeIndex] > cap) _stakeIndex--;
    }

    private string Line(Mood mood) => _opponent?.Line(mood, _rng) ?? "";

    private void Chat(string line)
    {
        if (line.Length == 0 || _opponent == null) return;
        _chatter.Add($"~y~{_opponent.Name}~/~: {line}");
        if (_chatter.Count > 8) _chatter.RemoveAt(0);
    }

    private static string Strip(string s) => s.Replace("~/~", "").Replace("~lg~", "").Replace("~y~", "").Replace("~r~", "");

    // ------------------------------------------------------------ drawing

    private static readonly Area Panel = new(160, 60, 640, 420);
    private static readonly Area PlayArea = new(184, 118, 592, 232);

    /// <summary>The Draw GUI pass: the whole table, if it is open.</summary>
    public void Draw()
    {
        if (_opponent is not { } opp) return;
        _canvas.Begin();
        var c = _canvas;
        _gold = World.Player is { } p ? Purse.Count(p) : null;
        // Darken the room behind the table.
        Builtins.draw_set_alpha(0.55);
        Builtins.draw_set_colour(0);
        Builtins.draw_rectangle(0, 0, GameDraw.GuiWidth, GameDraw.GuiHeight, false);
        Builtins.draw_set_alpha(1);

        c.Board(Panel);
        c.Text($"~y~{CurrentGame.Title}~/~ with ~y~{opp.Name}~/~", Panel.X + 24, Panel.Y + 18);
        c.Text($"You: ~y~{_gold?.ToString() ?? "?"}~/~ crowns    {opp.Name}: ~y~{opp.Bankroll}~/~", Panel.X + Panel.W - 24, Panel.Y + 18, align: 1);
        c.Fill(new Area(Panel.X + 20, Panel.Y + 44, Panel.W - 40, 1), 0x4A6A86, 0.8);

        if (_phase == Phase.Choosing) DrawChooser(c, opp);
        else DrawRound(c, opp);

        // The round's log, then what the opponent said about it; newest at the bottom.
        var lines = (_round?.Log ?? []).Concat(_chatter).TakeLast(4).ToList();
        double ly = 356;
        foreach (var line in lines)
        {
            c.Text(line, PlayArea.X, ly);
            ly += 14;
        }
        if (_error.Length > 0) c.Text($"~r~{_error}~/~", Panel.CenterX, 412, align: 0);
    }

    private bool CanStart(Opponent opp, int? stake = null) =>
        _owed == 0 && opp.Bankroll >= (stake ?? Stake) && (_gold ?? 0) >= (stake ?? Stake);

    private void DrawChooser(Canvas c, Opponent opp)
    {
        // One tab per game.
        double tx = PlayArea.X;
        for (int i = 0; i < _games.Count; i++)
        {
            string label = i == _gameIndex ? $"~y~{_games[i].Title}~/~" : _games[i].Title;
            c.Button("game:" + _games[i].Id, label, new Area(tx, PlayArea.Y, 130, 26));
            tx += 140;
        }
        c.Text(CurrentGame.Rules, PlayArea.X, PlayArea.Y + 44, wrap: PlayArea.W * c.K);
        c.Text($"Stake: ~y~{Stake}~/~ crowns a side", PlayArea.CenterX, PlayArea.Y + 170, align: 0);
        if (opp.Books.Played > 0)
        {
            string net = opp.Books.Net >= 0 ? $"~lg~+{opp.Books.Net}~/~" : $"~r~{opp.Books.Net}~/~";
            c.Text($"Rounds with {opp.Name}: {opp.Books.Played}, your winnings {net}", PlayArea.CenterX, PlayArea.Y + 190, align: 0);
        }

        double y = Panel.Y + Panel.H - 44;
        c.Button("stake-", "Lower", new Area(PlayArea.X, y, 100, 26), _stakeIndex > 0);
        c.Button("stake+", "Higher", new Area(PlayArea.X + 110, y, 100, 26),
                 _stakeIndex < Stakes.Length - 1 && Stakes[_stakeIndex + 1] <= Math.Min(opp.Bankroll, _gold ?? 0));
        c.Button("play", "~lg~Play~/~", new Area(PlayArea.X + PlayArea.W - 230, y, 110, 26), CanStart(opp));
        c.Button("leave", "Leave", new Area(PlayArea.X + PlayArea.W - 110, y, 110, 26));
    }

    private void DrawRound(Canvas c, Opponent opp)
    {
        if (_round is not { } r) return;
        CurrentGame.Draw(c, PlayArea);
        c.Text($"Pot: ~y~{r.Pot}~/~  (you {r.PlayerIn}, them {r.OpponentIn})", PlayArea.X + PlayArea.W, PlayArea.Y - 14, align: 1);

        double y = Panel.Y + Panel.H - 44;
        if (_phase == Phase.Settled)
        {
            string colour = r.Result == Outcome.Win ? "~lg~" : r.Result == Outcome.Loss ? "~r~" : "~y~";
            c.Text($"{colour}{r.Verdict}~/~", Panel.CenterX, PlayArea.Y + PlayArea.H - 18, align: 0);
            double bx = PlayArea.X;
            foreach (var (button, stake) in CurrentGame.Continuations(r))
            {
                c.Button(button.Id, button.Label, new Area(bx, y, 170, 26), button.Enabled && CanStart(opp, stake));
                bx += 178;
            }
            c.Button("again", CurrentGame.AgainLabel(r), new Area(bx, y, 120, 26), CanStart(opp));
            bx += 128;
            c.Button("choose", "Change game", new Area(bx, y, 130, 26));
            c.Button("leave", "Leave", new Area(PlayArea.X + PlayArea.W - 110, y, 110, 26));
            return;
        }
        double x = PlayArea.X;
        foreach (var b in CurrentGame.Buttons())
        {
            c.Button(b.Id, b.Label, new Area(x, y, 120, 26), b.Enabled);
            x += 128;
        }
        c.Button("leave", "~r~Fold & leave~/~", new Area(PlayArea.X + PlayArea.W - 130, y, 130, 26));
    }
}

/// <summary>The game's own sounds, by name; a missing one is silence, never an error.</summary>
internal static class Sfx
{
    private static readonly Dictionary<string, double> Cache = new(StringComparer.Ordinal);

    public static void Play(string name)
    {
        try
        {
            if (!Cache.TryGetValue(name, out var id))
            {
                var v = Builtins.asset_get_index(name);
                Cache[name] = id = v.IsNumber ? v.AsReal : -1;
            }
            if (id >= 0) Builtins.audio_play_sound(id, 5, false);
        }
        catch (GmlException) { }
    }
}

/// <summary>The player, and the game's action log.</summary>
internal static class World
{
    public static InstanceRef? Player => Objects.o_player.First is { } p && p.Exists ? p : null;

    /// <summary>A line in the game's own log, as the player (some callers have no current instance).</summary>
    public static void Say(string text)
    {
        try
        {
            if (Player is { } p) Scripts.scr_actionsLogAddMessage.CallAs(p, text);
        }
        catch (GmlException) { }
    }
}
