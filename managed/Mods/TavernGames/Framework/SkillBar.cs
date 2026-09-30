namespace TavernGames;

/// <summary>How a press on a <see cref="SkillBar"/> landed.</summary>
internal enum Hit
{
    Miss,
    Good,
    Perfect,
}

/// <summary>
/// The timing check the physical games share: a cursor runs along a bar, and
/// the player presses (Space, or a click) while it is inside the marked zone -
/// dead centre for a perfect. The cursor either sweeps end to end at
/// <see cref="Speed"/> or is placed by the game each frame (a drunk's sway).
/// All positions are fractions of the bar, 0 to 1.
/// </summary>
internal sealed class SkillBar
{
    private int _direction = 1;

    /// <summary>Where the cursor is.</summary>
    public double Cursor { get; set; } = 0.5;

    public double ZoneCenter { get; set; } = 0.5;

    public double ZoneWidth { get; set; } = 0.25;

    /// <summary>The perfect band, as a share of the zone around its centre.</summary>
    public double PerfectShare { get; set; } = 0.3;

    /// <summary>Bar lengths per frame, for a sweeping cursor.</summary>
    public double Speed { get; set; } = 0.012;

    /// <summary>A flash of the last press's verdict, counted down in frames by <see cref="Step"/>.</summary>
    public int Flash { get; private set; }

    public Hit LastHit { get; private set; }

    /// <summary>A sweeping cursor's move for this frame: end to end and back.</summary>
    public void Sweep()
    {
        Cursor += Speed * _direction;
        if (Cursor >= 1) { Cursor = 1; _direction = -1; }
        else if (Cursor <= 0) { Cursor = 0; _direction = 1; }
    }

    /// <summary>Once a frame, whoever moves the cursor: lets the verdict flash fade.</summary>
    public void Step()
    {
        if (Flash > 0) Flash--;
    }

    /// <summary>Judges a press at the cursor's current place.</summary>
    public Hit Press()
    {
        double off = Math.Abs(Cursor - ZoneCenter);
        LastHit = off <= ZoneWidth * PerfectShare / 2 ? Hit.Perfect : off <= ZoneWidth / 2 ? Hit.Good : Hit.Miss;
        Flash = 24;
        return LastHit;
    }

    /// <summary>Moves the zone somewhere new, whole inside the bar.</summary>
    public void MoveZone(Random rng) =>
        ZoneCenter = ZoneWidth / 2 + rng.NextDouble() * (1 - ZoneWidth);

    /// <summary>The bar, its zone, the perfect band and the cursor, inside <paramref name="a"/>.</summary>
    public void Draw(Canvas c, Area a)
    {
        c.Fill(a, 0x201A16, 0.95);
        c.Outline(a, 0x5A4A3A);
        double zx = a.X + (ZoneCenter - ZoneWidth / 2) * a.W, zw = ZoneWidth * a.W;
        // The zone flashes with the last verdict: green for a hit, red for a miss.
        int zone = Flash > 0 ? (LastHit == Hit.Miss ? 0x3030A0 : 0x40A040) : 0x3A6A3A;
        c.Fill(new Area(zx, a.Y + 2, zw, a.H - 4), zone, 0.9);
        double pw = zw * PerfectShare;
        c.Fill(new Area(a.X + ZoneCenter * a.W - pw / 2, a.Y + 2, pw, a.H - 4), 0x50C8E8, 0.95);
        double cx = a.X + Math.Clamp(Cursor, 0, 1) * a.W;
        c.Fill(new Area(cx - 1.5, a.Y - 4, 3, a.H + 8), 0xF0F0F0);
    }
}
