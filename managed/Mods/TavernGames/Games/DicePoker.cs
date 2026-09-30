using CoreLoader;

namespace TavernGames;

/// <summary>
/// Poker dice. Both sides throw five dice, face up; one round of betting (the
/// player raises and the opponent calls or folds, or the player checks and the
/// opponent may raise, to be called or folded to); then each side may throw any
/// of its dice again, once; the better hand takes the pot.
/// </summary>
internal sealed class DicePoker : MiniGame
{
    private const int RollFrames = 36, ThinkFrames = 40;
    private const double DieScale = 2, DieStep = 52;

    private readonly Sprite _dice;
    private readonly int[] _mine = new int[5], _theirs = new int[5];
    private readonly bool[] _pick = new bool[5], _theirPick = new bool[5];

    private enum Step { Rolling, Bet, TheyRaised, Thinking, Reroll, Rerolling, Done }

    private Step _step;
    private int _timer;
    // What happens when the current wait ends.
    private Action? _then;
    private bool _theyRaised;

    public DicePoker(Sprite dice) => _dice = dice;

    public override string Id => "dice-poker";
    public override string Title => "Poker Dice";
    public override string Rules =>
        "Five dice each. After the first throw, ~y~raise~/~ the stake or ~y~check~/~; your opponent may call, fold or raise. " +
        "Then pick the dice to throw again - once - and the better hand wins: five of a kind, four, full house, " +
        "straights (six-high over five-high), three, two pair, a pair.";

    protected override void Start()
    {
        Array.Clear(_pick);
        Array.Clear(_theirPick);
        _theyRaised = false;
        Roll(_mine, null);
        Roll(_theirs, null);
        Sfx.Play("snd_npc_man1_play_dice");
        Wait(Step.Rolling, RollFrames, () => _step = Step.Bet);
    }

    // ------------------------------------------------------------ flow

    private void Wait(Step step, int frames, Action then)
    {
        _step = step;
        _timer = frames;
        _then = then;
    }

    public override void Tick()
    {
        if (_step is not (Step.Rolling or Step.Thinking or Step.Rerolling)) return;
        if (--_timer > 0) return;
        var then = _then;
        _then = null;
        then?.Invoke();
    }

    public override IEnumerable<ButtonSpec> Buttons()
    {
        int s = Round.Stake;
        switch (_step)
        {
            case Step.Bet:
                yield return new("raise", $"Raise ~y~{s}~/~", Round.CanPlayerRaise(s) && Round.CanOpponentRaise(s));
                yield return new("check", "Check");
                break;
            case Step.TheyRaised:
                yield return new("call", $"Call ~y~{s}~/~", Round.CanPlayerRaise(s));
                yield return new("fold", "~r~Fold~/~");
                break;
            case Step.Reroll:
                int n = _pick.Count(p => p);
                yield return new("reroll", n == 0 ? "Stand" : $"Throw {n}");
                break;
        }
    }

    public override void OnButton(string id)
    {
        int s = Round.Stake;
        switch (id)
        {
            case "raise":
                Round.PlayerRaise(s);
                Round.Say($"You raise {s}.");
                Wait(Step.Thinking, ThinkFrames, () =>
                {
                    if (OpponentCalls())
                    {
                        Round.OpponentRaise(s);
                        Round.Bark(Round.Opponent.Line(Mood.Call, Round.Rng));
                        _step = Step.Reroll;
                    }
                    else
                    {
                        Round.Bark(Round.Opponent.Line(Mood.Fold, Round.Rng));
                        Round.Finish(Outcome.Win, $"{Round.Opponent.Name} folds.");
                        _step = Step.Done;
                    }
                });
                break;
            case "check":
                Round.Say("You check.");
                Wait(Step.Thinking, ThinkFrames, () =>
                {
                    // Never a raise the player cannot afford to call: that would be a forced fold.
                    if (Round.CanOpponentRaise(s) && Round.CanPlayerRaise(s) && OpponentRaises())
                    {
                        Round.OpponentRaise(s);
                        _theyRaised = true;
                        Round.Bark(Round.Opponent.Line(Mood.Raise, Round.Rng));
                        _step = Step.TheyRaised;
                    }
                    else
                    {
                        Round.Say($"{Round.Opponent.Name} checks.");
                        _step = Step.Reroll;
                    }
                });
                break;
            case "call":
                Round.PlayerRaise(s);
                Round.Say($"You call {s}.");
                _step = Step.Reroll;
                break;
            case "fold":
                Round.Forfeit("You fold.");
                _step = Step.Done;
                break;
            case "reroll":
                ThrowAgain();
                break;
        }
    }

    public override void OnClick(double x, double y)
    {
        if (_step != Step.Reroll) return;
        int i = DieAt(_lastArea, false, x, y);
        if (i < 0) return;
        _pick[i] = !_pick[i];
        Sfx.Play("snd_button_enter");
    }

    /// <summary>Marks die <paramref name="index"/> (0-4) to be thrown again, as a click would (test host).</summary>
    public bool Toggle(int index)
    {
        if (_step != Step.Reroll || (uint)index >= 5) return false;
        _pick[index] = !_pick[index];
        return true;
    }

    private void ThrowAgain()
    {
        Array.Copy(DiceHand.RerollAdvice(_theirs), _theirPick, 5);
        Roll(_mine, _pick);
        Roll(_theirs, _theirPick);
        int mine = _pick.Count(p => p), theirs = _theirPick.Count(p => p);
        Round.Say(mine == 0 ? "You stand." : $"You throw {mine} again.");
        Round.Say(theirs == 0 ? $"{Round.Opponent.Name} stands." : $"{Round.Opponent.Name} throws {theirs} again.");
        if (mine + theirs > 0) Sfx.Play("snd_npc_man2_play_dice");
        Wait(Step.Rerolling, mine + theirs > 0 ? RollFrames : 1, Showdown);
    }

    private void Showdown()
    {
        _step = Step.Done;
        var me = DiceHand.Of(_mine);
        var them = DiceHand.Of(_theirs);
        int cmp = me.CompareTo(them);
        if (cmp > 0) Round.Finish(Outcome.Win, $"{me.Name} beats {LowerFirst(them.Name)}.");
        else if (cmp < 0) Round.Finish(Outcome.Loss, $"{them.Name} beats {LowerFirst(me.Name)}.");
        else Round.Finish(Outcome.Push, $"{me.Name} each - a draw.");
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private void Roll(int[] dice, bool[]? which)
    {
        for (int i = 0; i < 5; i++)
            if (which == null || which[i]) dice[i] = Round.Rng.Next(1, 7);
    }

    // ------------------------------------------------------------ opponent

    private int TheirRank => DiceHand.Of(_theirs).Rank;

    // The dice lie face up, so the opponent sees the player's hand as well as
    // its own: ahead, level or behind decides most of it, temper the rest. A
    // reroll is still to come, so being behind is not hopeless - just worse.
    private int Standing => DiceHand.Of(_theirs).CompareTo(DiceHand.Of(_mine));

    /// <summary>Whether the opponent calls the player's raise.</summary>
    private bool OpponentCalls()
    {
        if (!Round.CanOpponentRaise(Round.Stake)) return false;
        int standing = Standing, rank = TheirRank;
        double chance = Round.Opponent.Temper switch
        {
            Temperament.Reckless => standing >= 0 ? 0.95 : 0.55,
            Temperament.Steady => standing > 0 ? 0.95 : standing == 0 ? 0.7 : rank >= DiceHand.TwoPair ? 0.4 : 0.15,
            _ => standing > 0 ? 0.9 : standing == 0 ? 0.45 : 0.05,
        };
        return Round.Rng.NextDouble() < chance;
    }

    /// <summary>Whether the opponent raises after the player checks (a bluff now and then, if reckless).</summary>
    private bool OpponentRaises()
    {
        int standing = Standing, rank = TheirRank;
        double chance = Round.Opponent.Temper switch
        {
            Temperament.Reckless => standing > 0 ? 0.85 : standing == 0 ? 0.4 : 0.15,
            Temperament.Steady => standing > 0 && rank >= DiceHand.Pair ? 0.7 : 0.05,
            _ => standing > 0 && rank >= DiceHand.TwoPair ? 0.6 : 0,
        };
        return Round.Rng.NextDouble() < chance;
    }

    // ------------------------------------------------------------ drawing

    private Area _lastArea;

    // Die i of a row: rows are centred; the opponent's sits on top.
    private static Area DieRect(Area area, bool theirs, int i)
    {
        double size = 20 * DieScale;
        double x0 = area.CenterX - (DieStep * 4 + size) / 2;
        double y = theirs ? area.Y + 22 : area.Y + 130;
        return new Area(x0 + i * DieStep, y, size, size);
    }

    private static int DieAt(Area area, bool theirs, double x, double y)
    {
        for (int i = 0; i < 5; i++)
        {
            var r = DieRect(area, theirs, i);
            // Picked dice sit higher; count the gap too.
            if (x >= r.X && x < r.X + r.W && y >= r.Y - 10 && y < r.Y + r.H) return i;
        }
        return -1;
    }

    public override void Draw(Canvas c, Area area)
    {
        _lastArea = area;
        bool rolling = _step is Step.Rolling or Step.Rerolling;
        c.Text($"~y~{Round.Opponent.Name}~/~", area.X, area.Y + 4);
        c.Text("~y~You~/~", area.X, area.Y + 112);
        for (int i = 0; i < 5; i++)
        {
            DrawDie(c, area, true, i, rolling && (_step == Step.Rolling || _theirPick[i]));
            DrawDie(c, area, false, i, rolling && (_step == Step.Rolling || _pick[i]));
        }
        if (!rolling)
        {
            c.Text(DiceHand.Of(_theirs).Name, area.CenterX, area.Y + 70, align: 0);
            c.Text(DiceHand.Of(_mine).Name, area.CenterX, area.Y + 178, align: 0);
        }
        string hint = _step switch
        {
            Step.Bet => "Raise, or check and see what they do.",
            Step.TheyRaised => $"{Round.Opponent.Name} raises. Call or fold?",
            Step.Thinking => $"{Round.Opponent.Name} thinks it over...",
            Step.Reroll => "Click the dice to throw again, then throw - or stand.",
            _ => "",
        };
        if (hint.Length > 0) c.Text($"~w~{hint}~/~", area.CenterX, area.Y + 96, align: 0);
    }

    private void DrawDie(Canvas c, Area area, bool theirs, int i, bool tumbling)
    {
        var r = DieRect(area, theirs, i);
        int face = (theirs ? _theirs : _mine)[i];
        double lift = !theirs && _pick[i] && _step == Step.Reroll ? -8 : 0;
        if (tumbling)
        {
            // A die in the air shows a different face every few frames, and jitters.
            face = 1 + (int)((_timer / 3 + i * 5 + (theirs ? 2 : 0)) % 6);
            lift = -((_timer + i * 3) % 6);
        }
        bool hover = !theirs && _step == Step.Reroll && r.Contains(c.Mouse.X, c.Mouse.Y + (_pick[i] ? 8 : 0));
        int tint = !theirs && _pick[i] && _step == Step.Reroll ? 0x80E0FF : hover ? 0xD0F0FF : 0xFFFFFF;
        c.Sprite(_dice, face - 1, r.X, r.Y + lift, DieScale, tint);
        if (!theirs && _pick[i] && _step == Step.Reroll) c.Outline(new Area(r.X - 2, r.Y + lift - 2, r.W + 4, r.H + 4), 0x40C0FF);
    }

    /// <summary>The dice on the table, for the test host.</summary>
    public object State() => new
    {
        step = _step.ToString(),
        mine = _mine.ToArray(),
        theirs = _theirs.ToArray(),
        picked = _pick.ToArray(),
        myHand = DiceHand.Of(_mine).Name,
        theirHand = DiceHand.Of(_theirs).Name,
        theyRaised = _theyRaised,
    };
}
