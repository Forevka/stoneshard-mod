using CoreLoader;

namespace TavernGames;

/// <summary>
/// A drinking contest, for real. Round after round both sides down a mug; the
/// first to fall under the table loses. The player downs each mug by stopping a
/// swaying marker in the middle of the bar (Space or a click) - and the sway
/// grows with how drunk the character really is, so every mug makes the next
/// one harder. Each mug is real Drunkenness in the game; a spill is about half
/// as much, and the mug is poured again - you still owe it. Three spills and
/// you are out, and deep in the drink you may pass out
/// on the spot - a real Sleep. Afterwards the game's own Drunkenness does the
/// rest: confusion, vomiting, and at its worst stage, sleep.
/// </summary>
internal sealed class DrinkingContest : MiniGame
{
    // Turns of Drunkenness a mug is worth (an ale is 20; the contest brew is
    // stronger). The stage climbs about one per 100 turns: from sober, stage IV
    // - and the risk of passing out - comes around the ninth mug.
    private const double MugTurns = 35, SpillTurns = 17;
    private const int PourFrames = 45, DrinkLimit = 300, TheirFrames = 60, MaxSpills = 3, PassOutTurns = 8;
    private const double MugScale = 2;

    private readonly Sprite _mug;
    private readonly SkillBar _bar = new() { ZoneCenter = 0.5, ZoneWidth = 0.22, PerfectShare = 0.35 };

    private enum Step { Pour, YourDrink, TheirDrink, Done }

    private Step _step;
    private int _timer, _clock;
    private int _mine, _theirs, _spills, _tolerance;
    private int _stage;   // the character's real Drunkenness stage, read once a frame in Tick
    private double _phase;
    private string _note = "";

    public DrinkingContest(Sprite mug) => _mug = mug;

    public override string Id => "drinking";
    public override string Title => "Drinking Contest";
    public override string Rules =>
        "Mug for mug until one of you is under the table. Down yours with ~y~Space~/~ (or a click) while the swaying marker is in the " +
        "~lg~middle~/~ - it sways more the drunker you are. Three spills and you are out. ~r~Every mug is real drink~/~: you will be drunk " +
        "afterwards, and deep in it you may pass out.";

    /// <summary>How many mugs an opponent holds, by trade: a drunkard has had practice.</summary>
    private int ToleranceOf(InstanceRef npc)
    {
        string obj = Tavern.ObjectName(npc);
        int t = obj.Contains("drunk", StringComparison.OrdinalIgnoreCase) ? 7
              : Tavern.HasWord(obj, "merc") || Tavern.HasWord(obj, "leif") || Tavern.HasWord(obj, "darrel") ? 7
              : Tavern.HasWord(obj, "innkeeper") && !Tavern.HasWord(obj, "wife") && !Tavern.HasWord(obj, "daughter") ? 6
              : Tavern.HasWord(obj, "wife") || Tavern.HasWord(obj, "daughter") || Tavern.HasWord(obj, "hostess*") ? 4
              : 5;
        return t + Round.Rng.Next(-1, 2);
    }

    protected override void Start()
    {
        _mine = _theirs = _spills = 0;
        _tolerance = ToleranceOf(Round.Opponent.Npc);
        _note = "";
        ReadStage();
        Pour();
        if (_stage > 0) Round.Say($"You come to the table already drunk (stage {_stage}).");
    }

    private void Pour()
    {
        _step = Step.Pour;
        _timer = PourFrames;
        Sfx.Play("snd_gui_pick_gold");
    }

    // How far the marker swings, and how fast, for the character as drunk as they are now.
    private (double Amplitude, double Speed) Sway() =>
        (0.12 + 0.07 * _stage + 0.012 * _mine, 0.045 + 0.012 * _stage + 0.002 * _mine);

    private void ReadStage() => _stage = World.Player is { } p ? Body.Drunkenness(p).Stage : 0;

    public override void Tick()
    {
        _clock++;
        _bar.Step();
        ReadStage();
        switch (_step)
        {
            case Step.Pour:
                if (--_timer > 0) return;
                _step = Step.YourDrink;
                _timer = DrinkLimit;
                _phase = Round.Rng.NextDouble() * Math.PI * 2;
                return;
            case Step.YourDrink:
            {
                // Two swings out of step: the drunker, the wider and quicker.
                var (a, w) = Sway();
                _phase += w;
                _bar.Cursor = Math.Clamp(0.5 + a * Math.Sin(_phase) + a * 0.45 * Math.Sin(_phase * 2.3 + 1.7), 0, 1);
                if (--_timer <= 0) Spill("You hesitate, and the mug slips.");
                return;
            }
            case Step.TheirDrink:
                if (--_timer > 0) return;
                TheyDrink();
                return;
        }
    }

    public override void OnAction() => Gulp();

    public override void OnClick(double x, double y) => Gulp();

    /// <summary>
    /// Downs the mug at the marker's current place (Space, a click). The test
    /// host may first put the marker in the middle (aim +1) or off it (aim -1).
    /// </summary>
    public bool Gulp(int aim = 0)
    {
        if (_step != Step.YourDrink) return false;
        if (aim != 0) _bar.Cursor = aim > 0 ? 0.5 : 0.05;
        var hit = _bar.Press();
        if (hit == Hit.Miss)
        {
            Spill("Ale down your chin and on the table.");
            return true;
        }
        _mine++;
        if (World.Player is { } p) Body.Drink(p, MugTurns);
        _note = hit == Hit.Perfect ? "Bottoms up!" : "Down it goes.";
        Sfx.Play("snd_button_enter");
        if (PassesOut()) return true;
        TheirTurn();
        return true;
    }

    private void Spill(string what)
    {
        _spills++;
        if (World.Player is { } p) Body.Drink(p, SpillTurns);
        _note = what;
        Round.Say($"Spilled ({_spills} of {MaxSpills}).");
        if (_spills >= MaxSpills)
        {
            End(false, "You can't keep another drop down.");
            return;
        }
        if (PassesOut()) return;
        // The mug is still owed: the opponent waits, and it is filled again.
        Pour();
    }

    // Deep in the drink, any mug may be the one that puts you on the floor.
    private bool PassesOut()
    {
        if (World.Player is not { } p) return false;
        ReadStage();   // this mug may have just tipped it over
        if (_stage < 4) return false;
        double chance = 0.25 + 0.05 * Math.Max(0, _mine - 8);
        if (Round.Rng.NextDouble() >= chance) return false;
        Body.PassOut(p, PassOutTurns);
        End(false, "The room tilts, and you slide under the table.");
        return true;
    }

    private void TheirTurn()
    {
        _step = Step.TheirDrink;
        _timer = TheirFrames;
    }

    private void TheyDrink()
    {
        _theirs++;
        // Past their limit they go down; right at it, it is a coin toss.
        bool down = _theirs > _tolerance || (_theirs == _tolerance && Round.Rng.NextDouble() < 0.5);
        if (down)
        {
            End(true, $"{Round.Opponent.Name} topples off the bench, out cold.");
            return;
        }
        if (_theirs == _tolerance - 1) Round.Bark("One... more... *hic*");
        Pour();
    }

    private void End(bool won, string verdict)
    {
        _step = Step.Done;
        Round.Finish(won ? Outcome.Win : Outcome.Loss, verdict);
    }

    public override IEnumerable<ButtonSpec> Buttons()
    {
        if (_step == Step.YourDrink) yield return new("yield", "Push the mug away");
    }

    public override void OnButton(string id)
    {
        if (id == "yield" && _step == Step.YourDrink) End(false, "You push the mug away. They win.");
    }

    public override void Conclude()
    {
        // Their mug is already at their lips: let them drink it before the table folds the rest.
        if (_step == Step.TheirDrink) TheyDrink();
    }

    // ------------------------------------------------------------ drawing

    private string TheirState() => (_tolerance - _theirs) switch
    {
        >= 4 => "steady as a rock",
        3 => "flushed",
        2 => "swaying",
        1 => "glassy-eyed",
        _ => "about to fall",
    };

    public override void Draw(Canvas c, Area area)
    {
        double left = area.X + 90, right = area.X + area.W - 90, row = area.Y + 70;
        c.Text("~y~You~/~", left, area.Y + 4, align: 0);
        c.Text($"~y~{Round.Opponent.Name}~/~", right, area.Y + 4, align: 0);
        c.Text(_stage == 0 ? "~w~sober~/~" : $"~w~drunk, stage {_stage}~/~", left, area.Y + 20, align: 0);
        c.Text($"~w~{TheirState()}~/~", right, area.Y + 20, align: 0);

        DrawMugs(c, left, row, _mine, _step is Step.Pour or Step.YourDrink);
        DrawMugs(c, right, row, _theirs, _step is Step.Pour or Step.YourDrink or Step.TheirDrink);
        // Spills so far.
        c.Text(_spills == 0 ? "" : $"~r~Spills: {_spills} of {MaxSpills}~/~", left, row + 74, align: 0);

        double cx = area.CenterX;
        if (_step == Step.YourDrink)
        {
            var bar = new Area(cx - 150, area.Y + 150, 300, 14);
            _bar.Draw(c, bar);
            c.Text($"~y~Space~/~ or click when the marker is in the middle  ({Math.Max(0, _timer / 60 + 1)}s)", cx, bar.Y + 20, align: 0);
        }
        else if (_step == Step.TheirDrink)
        {
            c.Text($"~w~{Round.Opponent.Name} drinks...~/~", cx, area.Y + 150, align: 0);
        }
        else if (_step == Step.Pour)
        {
            c.Text("~w~The mugs are filled.~/~", cx, area.Y + 150, align: 0);
        }
        if (_note.Length > 0 && _step != Step.Done) c.Text($"~w~{_note}~/~", cx, area.Y + 110, align: 0);
    }

    // A row of emptied mugs behind the full one in front of each drinker.
    private void DrawMugs(Canvas c, double x, double y, int emptied, bool fullOne)
    {
        double w = 18 * MugScale;
        int shown = Math.Min(emptied, 10);
        for (int i = 0; i < shown; i++)
            c.Sprite(_mug, 1, x - (shown * 9) + i * 18 - w / 2, y - 6, MugScale * 0.6);
        if (fullOne)
        {
            // The full mug sways a little on a drunk's side of the table.
            double wob = Math.Sin(_clock * 0.15) * Math.Min(4, emptied * 0.5);
            c.Sprite(_mug, 0, x - w / 2 + wob, y + 10, MugScale);
        }
        c.Text($"{emptied} {(emptied == 1 ? "mug" : "mugs")}", x, y + 56, align: 0);
    }

    /// <summary>The contest, for the test host (it gives the opponent's limit away: a development tool).</summary>
    public object State() => new
    {
        step = _step.ToString(),
        mine = _mine,
        theirs = _theirs,
        spills = _spills,
        tolerance = _tolerance,
        cursor = Math.Round(_bar.Cursor, 3),
        stage = _stage,
    };
}
