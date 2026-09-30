namespace TavernGames;

/// <summary>A button a game offers at the bottom of the table.</summary>
/// <param name="Id">What <see cref="MiniGame.OnButton"/> is handed when it is pressed.</param>
/// <param name="Label">Colour-text label (~lg~ green, ~y~ yellow, ~r~ red, ~/~ ends).</param>
/// <param name="Enabled">A disabled button is drawn greyed and ignores clicks.</param>
internal readonly record struct ButtonSpec(string Id, string Label, bool Enabled = true);

/// <summary>A rectangle in the table's design space (960 x 540, scaled to the screen).</summary>
internal readonly record struct Area(double X, double Y, double W, double H)
{
    public bool Contains(double x, double y) => x >= X && x < X + W && y >= Y && y < Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
}

/// <summary>
/// One tavern game. The table owns everything around it - the stake, the
/// opponent, the gold, the window, input - and hands a round to the game once
/// the stake is agreed. A game only plays: it draws its part of the table,
/// offers buttons, reacts to them, and ends the round with
/// <see cref="Session.Finish"/>.
/// </summary>
/// <remarks>
/// Every method runs on the game thread, from the table's per-frame update or
/// its draw. A round must be able to end at any time: the player may walk away
/// (<see cref="Session.Forfeit"/> is then called for them), and a hot reload
/// drops the round without paying anyone.
/// </remarks>
internal abstract class MiniGame
{
    /// <summary>Stable id, used by the test host and the config ("dice-poker").</summary>
    public abstract string Id { get; }

    /// <summary>The name on the table's tab.</summary>
    public abstract string Title { get; }

    /// <summary>One or two lines of rules shown while the stake is chosen.</summary>
    public abstract string Rules { get; }

    /// <summary>The round in play; set by <see cref="Begin"/>.</summary>
    protected Session Round { get; private set; } = null!;

    /// <summary>A new round: both stakes are in. Deal, roll, reset.</summary>
    public void Begin(Session round)
    {
        Round = round;
        Start();
    }

    protected abstract void Start();

    /// <summary>Once a frame while the round runs (after that frame's drawing): animations, the opponent's moves.</summary>
    public virtual void Tick() { }

    /// <summary>Draws the play area. Coordinates are design units inside <paramref name="area"/>.</summary>
    public abstract void Draw(Canvas canvas, Area area);

    /// <summary>The buttons to offer right now. Empty while the game animates or waits on the opponent.</summary>
    public abstract IEnumerable<ButtonSpec> Buttons();

    /// <summary>One of <see cref="Buttons"/> was pressed.</summary>
    public abstract void OnButton(string id);

    /// <summary>A click in the play area that was not on a button, in design units.</summary>
    public virtual void OnClick(double x, double y) { }

    /// <summary>
    /// Ways to carry on from a finished round besides starting afresh, each
    /// with the stake the next round would be played for - a ladder's
    /// "Continue to level 3". Pressing one starts a round with
    /// <see cref="Session.Continues"/> set. Empty by default.
    /// </summary>
    public virtual IEnumerable<(ButtonSpec Button, int Stake)> Continuations(Session finished) => [];

    /// <summary>The label of the button that starts a fresh round after one ends.</summary>
    public virtual string AgainLabel(Session finished) => "~lg~Again~/~";

    /// <summary>
    /// The player is leaving the table mid-round. A round whose outcome no
    /// longer depends on the player - the dice already in the air, a hand
    /// already won, a dealer left to draw - ends here as it would have; one
    /// still waiting on the player is left alone, and the table folds it.
    /// </summary>
    public virtual void Conclude() { }
}
