namespace TavernGames;

/// <summary>How a round ended, from the player's side.</summary>
internal enum Outcome
{
    Win,
    Loss,
    Push,
}

/// <summary>
/// One round at the table: who sits across, how much each side has put in,
/// and what was said. The player's crowns leave the purse as they are
/// committed (the ante, then each raise) and are held by the table; the
/// opponent's are only counted, since its purse is the mod's own book.
/// </summary>
internal sealed class Session
{
    private readonly List<string> _log = new();
    private readonly Func<int, bool> _playerCanCover;
    private readonly Action<int> _takeFromPlayer;

    /// <param name="opponent">Who sits across.</param>
    /// <param name="stake">The ante, already taken from the player by the table.</param>
    /// <param name="rng">The table's dice and deck.</param>
    /// <param name="playerCanCover">Whether the player still carries this many crowns more.</param>
    /// <param name="takeFromPlayer">Takes more crowns from the player into the table's keeping; throws if it cannot.</param>
    public Session(Opponent opponent, int stake, Random rng, Func<int, bool> playerCanCover, Action<int> takeFromPlayer)
    {
        Opponent = opponent;
        Stake = stake;
        Rng = rng;
        PlayerIn = stake;
        OpponentIn = stake;
        _playerCanCover = playerCanCover;
        _takeFromPlayer = takeFromPlayer;
    }

    public Opponent Opponent { get; }

    /// <summary>
    /// Started from one of the game's continuations rather than afresh (see
    /// MiniGame.Continuations). It does not say which: a game offering more
    /// than one keeps track of what it offered itself.
    /// </summary>
    public bool Continues { get; init; }

    /// <summary>The agreed ante; raises are counted in multiples of it.</summary>
    public int Stake { get; }

    public Random Rng { get; }

    /// <summary>What the player has committed; lost in full on a loss.</summary>
    public int PlayerIn { get; private set; }

    /// <summary>What the opponent has committed; won in full on a win.</summary>
    public int OpponentIn { get; private set; }

    public int Pot => PlayerIn + OpponentIn;

    public bool Finished { get; private set; }

    public Outcome Result { get; private set; }

    /// <summary>What the result line says ("Full house beats two pair").</summary>
    public string Verdict { get; private set; } = "";

    /// <summary>Most recent lines last; the table shows the tail.</summary>
    public IReadOnlyList<string> Log => _log;

    public void Say(string line) => _log.Add(line);

    /// <summary>The opponent speaks, in their own colour.</summary>
    public void Bark(string line)
    {
        if (line.Length > 0) _log.Add($"~y~{Opponent.Name}~/~: {line}");
    }

    public bool CanPlayerRaise(int amount) => _playerCanCover(amount);

    public bool CanOpponentRaise(int amount) => Opponent.Bankroll >= OpponentIn + amount;

    /// <summary>
    /// The player puts <paramref name="amount"/> more in. The crowns leave the
    /// purse now; call this before changing any other state, since it throws
    /// (InvalidOperationException) when they cannot be taken.
    /// </summary>
    public void PlayerRaise(int amount)
    {
        _takeFromPlayer(amount);
        PlayerIn += amount;
    }

    public void OpponentRaise(int amount) => OpponentIn += amount;

    /// <summary>Ends the round. The table pays out as soon as the game hands control back.</summary>
    public void Finish(Outcome result, string verdict)
    {
        if (Finished) return;
        Finished = true;
        Result = result;
        Verdict = verdict;
    }

    /// <summary>The player walked away or folded: the opponent takes what the player put in.</summary>
    public void Forfeit(string verdict = "You fold.") => Finish(Outcome.Loss, verdict);
}
