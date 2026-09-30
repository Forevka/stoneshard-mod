using CoreLoader;

namespace TavernGames;

/// <summary>
/// Thimblerig, the cup-and-ball game, as a five-level ladder. The ball is
/// shown under a cup, the cups are shuffled, and the player picks one. Each
/// level is a round of its own at even money; winning one offers the next -
/// more cups, more swaps, faster hands, and a higher stake - and losing any
/// sends the ladder back to the first.
/// </summary>
internal sealed class Thimblerig : MiniGame
{
    /// <summary>One rung: cups on the table, swaps, frames per swap, stake multiple, and the odds that a swap moves two pairs at once.</summary>
    private readonly record struct Level(int Cups, int Swaps, int Frames, int Multiple, double DoubleSwap);

    private static readonly Level[] Levels =
    [
        new(3, 6, 30, 1, 0),
        new(3, 9, 22, 2, 0),
        new(4, 11, 17, 3, 0.15),
        new(5, 13, 13, 5, 0.35),
        new(6, 16, 10, 8, 0.5),
    ];

    // Frames: the ball shown, the cups coming down, a lift, and the pause before the result.
    private const int ShowFrames = 60, LowerFrames = 12, LiftFrames = 14, ResultFrames = 34;
    private const double CupScale = 2, CupW = 22, CupH = 24, BallSize = 8, Lift = 20, Arc = 22;
    private const int ShadowFrame = 1;

    private readonly Sprite _cup, _ball;

    private enum Step { Show, Lower, Shuffle, Choose, Reveal, Done }

    private Step _step;
    private int _timer;
    private int _level = 1;     // 1-based
    private int _base;          // the stake of level 1; each level plays for a multiple of it
    private int[] _slotOf = [];  // cup -> slot it stands on
    private int _ballCup;
    private int _picked = -1;    // the cup the player chose
    private readonly List<(int A, int B)[]> _moves = new();
    private int _move;           // index into _moves while shuffling

    public Thimblerig(Sprite cup, Sprite ball)
    {
        _cup = cup;
        _ball = ball;
    }

    public override string Id => "thimblerig";
    public override string Title => "Thimblerig";
    public override string Rules =>
        "Watch the ball go under a cup, follow the cups as they are shuffled, then ~y~click the cup~/~ that hides it. " +
        "Five levels: each one won lets you ~y~continue~/~ to the next - more cups, more and faster swaps - for a higher stake " +
        "(x1, x2, x3, x5, x8). A miss sends you back to the first.";

    private Level Current => Levels[_level - 1];

    protected override void Start()
    {
        if (Round.Continues && _level < Levels.Length) _level++;
        else
        {
            _level = 1;
            _base = Round.Stake;
        }
        int cups = Current.Cups;
        _slotOf = Enumerable.Range(0, cups).ToArray();
        _ballCup = Round.Rng.Next(cups);
        _picked = -1;
        PlanShuffle();
        Round.Say($"Level {_level} of {Levels.Length}: {cups} cups. Watch the ball.");
        _step = Step.Show;
        _timer = ShowFrames;
    }

    // The whole shuffle is drawn up front: a list of moves, each one pair of
    // slots trading cups (or two pairs at once, at the higher levels).
    private void PlanShuffle()
    {
        _moves.Clear();
        _move = 0;
        int cups = Current.Cups;
        for (int i = 0; i < Current.Swaps; i++)
        {
            int a = Round.Rng.Next(cups), b = (a + 1 + Round.Rng.Next(cups - 1)) % cups;
            if (cups >= 4 && Round.Rng.NextDouble() < Current.DoubleSwap)
            {
                var rest = Enumerable.Range(0, cups).Where(s => s != a && s != b).ToList();
                int c = rest[Round.Rng.Next(rest.Count)];
                rest.Remove(c);
                int d = rest[Round.Rng.Next(rest.Count)];
                _moves.Add([(a, b), (c, d)]);
            }
            else
            {
                _moves.Add([(a, b)]);
            }
        }
    }

    // ------------------------------------------------------------ flow

    public override void Tick()
    {
        if (_step is Step.Choose or Step.Done || --_timer > 0) return;
        switch (_step)
        {
            case Step.Show:
                _step = Step.Lower;
                _timer = LowerFrames;
                return;
            case Step.Lower:
                _step = Step.Shuffle;
                _timer = Current.Frames;
                Sfx.Play("snd_button_enter");
                return;
            case Step.Shuffle:
                // The move just drawn lands: its cups now stand on each other's slots.
                foreach (var (a, b) in _moves[_move]) SwapSlots(a, b);
                if (++_move < _moves.Count)
                {
                    _timer = Current.Frames;
                    if (_move % 2 == 0) Sfx.Play("snd_button_enter");
                    return;
                }
                _step = Step.Choose;
                Round.Say("Where is the ball?");
                return;
            case Step.Reveal:
                Finish();
                return;
        }
    }

    private void SwapSlots(int a, int b)
    {
        int ca = Array.IndexOf(_slotOf, a), cb = Array.IndexOf(_slotOf, b);
        _slotOf[ca] = b;
        _slotOf[cb] = a;
    }

    public override IEnumerable<ButtonSpec> Buttons() => [];

    public override void OnButton(string id) { }

    public override void OnClick(double x, double y)
    {
        if (_step != Step.Choose) return;
        for (int cup = 0; cup < _slotOf.Length; cup++)
        {
            if (!CupRect(_lastArea, _slotOf[cup], 0).Contains(x, y)) continue;
            PickCup(cup);
            return;
        }
    }

    /// <summary>Picks whatever cup stands on <paramref name="slot"/> (0 = leftmost), as a click on it would (test host).</summary>
    public bool Pick(int slot)
    {
        if (_step != Step.Choose || (uint)slot >= (uint)_slotOf.Length) return false;
        PickCup(Array.IndexOf(_slotOf, slot));
        return true;
    }

    private void PickCup(int cup)
    {
        _picked = cup;
        _step = Step.Reveal;
        _timer = LiftFrames + ResultFrames;
        Sfx.Play("snd_button_click");
    }

    public override void Conclude()
    {
        // A cup already chosen decides the level; before that the player is
        // still playing and the table folds it - the cups are put away either way.
        if (_step == Step.Reveal) Finish();
        else _step = Step.Done;
    }

    private void Finish()
    {
        _step = Step.Done;
        if (_picked == _ballCup)
        {
            string next = _level < Levels.Length ? "" : " The whole ladder!";
            Round.Finish(Outcome.Win, $"Found it - level {_level} won.{next}");
        }
        else
        {
            Round.Finish(Outcome.Loss, $"Empty. The ball was under another cup.");
        }
    }

    // ------------------------------------------------------------ ladder

    public override IEnumerable<(ButtonSpec Button, int Stake)> Continuations(Session finished)
    {
        if (finished.Result != Outcome.Win || _level >= Levels.Length) yield break;
        int stake = _base * Levels[_level].Multiple;
        yield return (new ButtonSpec("continue", $"~lg~Level {_level + 1}~/~ for ~y~{stake}~/~"), stake);
    }

    public override string AgainLabel(Session finished) =>
        finished.Result == Outcome.Win ? "Start over" : "~lg~Again~/~";

    // ------------------------------------------------------------ drawing

    private Area _lastArea;

    private double SlotX(Area area, int slot)
    {
        int cups = _slotOf.Length;
        double w = CupW * CupScale, gap = Math.Min(92, (area.W - 80) / cups);
        double x0 = area.CenterX - (gap * (cups - 1) + w) / 2;
        return x0 + slot * gap;
    }

    private double Floor(Area area) => area.Y + 150;

    private Area CupRect(Area area, int slot, double lift) =>
        new(SlotX(area, slot), Floor(area) - CupH * CupScale - lift, CupW * CupScale, CupH * CupScale);

    public override void Draw(Canvas c, Area area)
    {
        _lastArea = area;
        c.Text($"Level ~y~{_level}~/~ of {Levels.Length}  -  {Current.Cups} cups, {Current.Swaps} moves", area.X, area.Y + 4);
        // The cloth the cups stand on.
        c.Fill(new Area(area.X + 20, Floor(area) - 58, area.W - 40, 76), 0x28402E, 0.9);
        c.Outline(new Area(area.X + 20, Floor(area) - 58, area.W - 40, 76), 0x3A5A40);

        int cups = _slotOf.Length;
        var pos = new (double X, double Lift, bool Front)[cups];
        for (int cup = 0; cup < cups; cup++) pos[cup] = (SlotX(area, _slotOf[cup]), 0, false);

        switch (_step)
        {
            case Step.Show:
                pos[_ballCup].Lift = Lift;
                break;
            case Step.Lower:
                pos[_ballCup].Lift = Lift * _timer / LowerFrames;
                break;
            case Step.Shuffle:
            {
                // Progress through the move, eased in and out; one cup of each
                // pair arcs over the table, the other passes in front of it.
                double t = 1 - (double)_timer / Current.Frames, e = t * t * (3 - 2 * t);
                foreach (var (a, b) in _moves[_move])
                {
                    int ca = Array.IndexOf(_slotOf, a), cb = Array.IndexOf(_slotOf, b);
                    double xa = SlotX(area, a), xb = SlotX(area, b), hop = Arc * Math.Sin(Math.PI * e);
                    pos[ca] = (xa + (xb - xa) * e, hop, false);
                    pos[cb] = (xb + (xa - xb) * e, -hop * 0.35, true);
                }
                break;
            }
            case Step.Choose:
                for (int cup = 0; cup < cups; cup++)
                    if (CupRect(area, _slotOf[cup], 0).Contains(c.Mouse.X, c.Mouse.Y)) pos[cup].Lift = 3;
                break;
            case Step.Reveal or Step.Done:
            {
                int elapsed = LiftFrames + ResultFrames - Math.Max(0, _timer);
                double up = Lift * Math.Min(1, (double)elapsed / LiftFrames);
                pos[_picked].Lift = _step == Step.Done ? Lift : up;
                // A miss: the right cup lifts too, a moment later, to show where the ball was.
                if (_picked != _ballCup)
                {
                    double late = Lift * Math.Clamp((elapsed - LiftFrames - 8) / (double)LiftFrames, 0, 1);
                    pos[_ballCup].Lift = _step == Step.Done ? Lift : late;
                }
                break;
            }
        }

        double floor = Floor(area), cupH = CupH * CupScale;
        // Shadows, then the ball, then the cups - the ones passing in front last.
        for (int cup = 0; cup < cups; cup++) c.Sprite(_cup, ShadowFrame, pos[cup].X, floor - cupH, CupScale);
        bool ballVisible = _step is Step.Show or Step.Lower || (_step is Step.Reveal or Step.Done && pos[_ballCup].Lift > 4);
        if (ballVisible)
        {
            double bx = pos[_ballCup].X + (CupW * CupScale - BallSize * CupScale) / 2;
            c.Sprite(_ball, 0, bx, floor - BallSize * CupScale - 2, CupScale);
        }
        foreach (bool front in new[] { false, true })
            for (int cup = 0; cup < cups; cup++)
            {
                if (pos[cup].Front != front) continue;
                bool chosen = _step is Step.Reveal or Step.Done && cup == _picked;
                int tint = chosen ? 0xC0E8FF : 0xFFFFFF;
                c.Sprite(_cup, 0, pos[cup].X, floor - cupH - pos[cup].Lift, CupScale, tint);
            }

        string hint = _step switch
        {
            Step.Show => "~w~The ball is here...~/~",
            Step.Lower or Step.Shuffle => "~w~Keep your eyes on it.~/~",
            Step.Choose => "~y~Click the cup that hides the ball.~/~",
            _ => "",
        };
        if (hint.Length > 0) c.Text(hint, area.CenterX, floor + 26, align: 0);
    }

    /// <summary>The table, for the test host. It gives the ball away: the test host is a development tool.</summary>
    public object State() => new
    {
        level = _level,
        step = _step.ToString(),
        cups = _slotOf.Length,
        ballSlot = _slotOf.Length > 0 ? _slotOf[_ballCup] : -1,
        picked = _picked >= 0 ? _slotOf[_picked] : -1,
        baseStake = _base,
    };
}
