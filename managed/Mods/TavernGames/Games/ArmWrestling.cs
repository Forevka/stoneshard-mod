using CoreLoader;

namespace TavernGames;

/// <summary>
/// Arm wrestling as a timing game. The locked hands sit on a tug meter the
/// opponent's strength drags towards their side all the time (with a heave now
/// and then); the player drags it back by pressing Space, or clicking, while
/// the cursor on the bar below is inside the zone - dead centre for a perfect,
/// a slip for a miss. Pin their hand to win, lose yours to lose. The zone is as
/// wide as the player's strength (STR) against theirs, the cursor quickens as
/// the match goes on, and either way it costs real Fatigue.
/// </summary>
internal sealed class ArmWrestling : MiniGame
{
    // Frames of "ready... go", and of rest after a press before the next counts.
    private const int CountdownFrames = 100, PressRest = 36;
    // Played by hand, a good push has to outpace the cursor's trip back to the
    // zone (a second or so of their pressure); a perfect clearly does.
    private const double Perfect = 0.2, Good = 0.13, Slip = 0.08, Heave = 0.07, Pressure = 0.001;
    // ArmLength: the sprite's elbow (its origin, row 43) to the hand.
    private const double ArmScale = 2, ArmLength = 43, Reach = 78;
    private const int PlayerSleeve = 0, TheirSleeve = 1;

    private readonly Sprite _arm, _fists;
    private readonly SkillBar _bar = new();

    private enum Step { Countdown, Pull, Done }

    private Step _step;
    private int _timer, _rest, _heaveIn, _hits;
    private double _meter;       // -1 their win, +1 the player's
    private double _myStrength, _theirStrength;
    private string _shout = "";
    private int _shoutFrames;

    public ArmWrestling(Sprite arm, Sprite fists)
    {
        _arm = arm;
        _fists = fists;
    }

    public override string Id => "arm-wrestling";
    public override string Title => "Arm Wrestling";
    public override string Rules =>
        "Elbows on the table. Their strength pushes the hands towards their side all the time; push back with ~y~Space~/~ (or a click) " +
        "while the cursor is in the ~lg~green zone~/~ - the pale centre is a perfect. A miss slips you back. The zone is as wide as your " +
        "STR against theirs. Win or lose, it is ~r~tiring~/~.";

    /// <summary>How strong an opponent is, by trade: sellswords and innkeepers are big, serving girls are not.</summary>
    private static double StrengthOf(InstanceRef npc)
    {
        string obj = Tavern.ObjectName(npc);
        if (Tavern.HasWord(obj, "merc") || Tavern.HasWord(obj, "leif") || Tavern.HasWord(obj, "darrel")) return 15;
        if (Tavern.HasWord(obj, "innkeeper") && !Tavern.HasWord(obj, "wife") && !Tavern.HasWord(obj, "daughter")) return 14;
        if (Tavern.HasWord(obj, "smith") || Tavern.HasWord(obj, "butcher")) return 14;
        if (obj.Contains("drunk", StringComparison.OrdinalIgnoreCase)) return 12;
        if (Tavern.HasWord(obj, "wife") || Tavern.HasWord(obj, "daughter") || Tavern.HasWord(obj, "hostess*") || Tavern.HasWord(obj, "servant")) return 9;
        return 11;
    }

    protected override void Start()
    {
        _meter = 0;
        _hits = 0;
        _rest = 0;
        _shoutFrames = 0;
        _myStrength = World.Player is { } p ? Body.Strength(p) : 10;
        _theirStrength = StrengthOf(Round.Opponent.Npc) + Round.Rng.Next(-1, 2);
        _bar.ZoneWidth = Math.Clamp(0.24 + (_myStrength - _theirStrength) * 0.025, 0.1, 0.4);
        _bar.Speed = 0.011;
        _bar.Cursor = 0;
        _bar.MoveZone(Round.Rng);
        _heaveIn = NextHeave();
        _step = Step.Countdown;
        _timer = CountdownFrames;
        Round.Say($"Your STR {_myStrength:0} against about {_theirStrength:0}.");
    }

    private int NextHeave() => Round.Opponent.Temper == Temperament.Reckless ? Round.Rng.Next(90, 170) : Round.Rng.Next(140, 260);

    public override void Tick()
    {
        if (_shoutFrames > 0) _shoutFrames--;
        _bar.Step();
        if (_step == Step.Countdown)
        {
            if (--_timer <= 0)
            {
                _step = Step.Pull;
                Shout("Pull!");
                Sfx.Play("snd_button_click");
            }
            return;
        }
        if (_step != Step.Pull) return;

        _bar.Sweep();
        if (_rest > 0) _rest--;
        // Their arm never stops pushing, harder the stronger they are.
        _meter -= Pressure * _theirStrength / 11;
        if (--_heaveIn <= 0)
        {
            _meter -= Heave;
            _heaveIn = NextHeave();
            Shout("Hnnngh!");
        }
        if (_meter <= -1) End(false);
        else if (_meter >= 1) End(true);
    }

    public override void OnAction() => Push();

    public override void OnClick(double x, double y) => Push();

    /// <summary>
    /// A push at the cursor's current place (Space, a click). The test host may
    /// first put the cursor on the zone's centre (aim +1) or well off it (aim -1).
    /// </summary>
    public bool Push(int aim = 0)
    {
        if (_step != Step.Pull || _rest > 0) return false;
        if (aim != 0) _bar.Cursor = aim > 0 ? _bar.ZoneCenter : (_bar.ZoneCenter > 0.5 ? 0 : 1);
        _rest = PressRest;
        var hit = _bar.Press();
        switch (hit)
        {
            case Hit.Perfect:
                _meter += Perfect;
                Shout("Perfect!");
                break;
            case Hit.Good:
                _meter += Good;
                break;
            default:
                _meter -= Slip;
                Shout("Slipped!");
                break;
        }
        if (hit != Hit.Miss)
        {
            // Every clean push quickens the cursor and moves the zone: the
            // longer it lasts, the harder it gets. A miss leaves the zone where
            // it was, for another try.
            _hits++;
            _bar.Speed = 0.011 + 0.0012 * _hits;
            _bar.MoveZone(Round.Rng);
            Sfx.Play("snd_button_enter");
        }
        if (_meter >= 1) End(true);
        else if (_meter <= -1) End(false);
        return true;
    }

    private void Shout(string text)
    {
        _shout = text;
        _shoutFrames = 40;
    }

    public override void Conclude()
    {
        // Mid-pull the match is still in the player's hands: walking away forfeits it.
    }

    private void End(bool won)
    {
        _step = Step.Done;
        _meter = Math.Clamp(_meter, -1, 1);
        // Win or lose, it takes it out of you.
        if (World.Player is { } p) Body.Tire(p, won ? 3 : 4);
        if (won) Round.Finish(Outcome.Win, $"You pin {Round.Opponent.Name}'s hand to the table.");
        else Round.Finish(Outcome.Loss, $"{Round.Opponent.Name} slams your hand down.");
    }

    public override IEnumerable<ButtonSpec> Buttons() => [];

    public override void OnButton(string id) { }

    // ------------------------------------------------------------ drawing

    public override void Draw(Canvas c, Area area)
    {
        double cx = area.CenterX, table = area.Y + 150;
        c.Text("~y~You~/~", area.X + 40, area.Y + 4);
        c.Text($"~y~{Round.Opponent.Name}~/~", area.X + area.W - 40, area.Y + 4, align: 1);

        // The tug meter: the hands' place between the two of you. Your side
        // (left) is where you lose, theirs where you win.
        var meter = new Area(cx - 180, area.Y + 24, 360, 8);
        c.Fill(meter, 0x201A16, 0.95);
        c.Fill(new Area(meter.X, meter.Y, meter.W / 2, meter.H), 0x2A2A6A, 0.5);
        c.Fill(new Area(cx, meter.Y, meter.W / 2, meter.H), 0x2A5A2A, 0.5);
        c.Outline(meter, 0x5A4A3A);
        double mx = cx + _meter * meter.W / 2;
        c.Fill(new Area(mx - 2, meter.Y - 3, 4, meter.H + 6), 0xF0F0F0);

        // The table edge, the two elbows on it, the hands locked above.
        c.Fill(new Area(cx - 160, table, 320, 6), 0x2A4A6A, 0.9);
        // The player's win leans the hands over to their (right) side; the
        // elbows and the pivot give a little the same way, which keeps the two
        // forearms close in length.
        double angle = _meter * 50 * Math.PI / 180, lean = _meter * 14;
        double hx = cx + lean + Reach * Math.Sin(angle), hy = table - Reach * Math.Cos(angle);
        DrawArm(c, cx - 46 + lean, table, hx, hy, PlayerSleeve);
        DrawArm(c, cx + 46 + lean, table, hx, hy, TheirSleeve);
        c.SpriteRotated(_fists, 0, hx, hy, 0, ArmScale);

        if (_step == Step.Countdown)
            c.Text(_timer > CountdownFrames / 2 ? "~y~Ready...~/~" : "~y~Set...~/~", cx, area.Y + 60, align: 0);
        if (_shoutFrames > 0) c.Text($"~w~{_shout}~/~", _shout == "Hnnngh!" ? cx + 110 : _shout == "Pull!" ? cx : cx - 110, _shout == "Pull!" ? area.Y + 60 : hy - 10, align: 0);

        // The timing bar, and what to do.
        var bar = new Area(cx - 170, table + 18, 340, 14);
        _bar.Draw(c, bar);
        if (_step == Step.Pull)
            c.Text(_rest > 0 ? "~w~...~/~" : "~y~Space~/~ or click when the cursor is in the zone", cx, bar.Y + 20, align: 0);
    }

    // A forearm from its elbow on the table to the hands: turned to point at them, stretched to reach.
    private void DrawArm(Canvas c, double ex, double ey, double hx, double hy, int sleeve)
    {
        double dx = hx - ex, dy = hy - ey, len = Math.Sqrt(dx * dx + dy * dy);
        double degrees = Math.Atan2(-dx, -dy) * 180 / Math.PI;
        c.SpriteRotated(_arm, sleeve, ex, ey, degrees, ArmScale, len / (ArmLength * ArmScale));
    }

    /// <summary>The match, for the test host.</summary>
    public object State() => new
    {
        step = _step.ToString(),
        meter = Math.Round(_meter, 3),
        cursor = Math.Round(_bar.Cursor, 3),
        zone = Math.Round(_bar.ZoneCenter, 3),
        zoneWidth = Math.Round(_bar.ZoneWidth, 3),
        resting = _rest > 0,
        mine = _myStrength,
        theirs = _theirStrength,
    };
}
