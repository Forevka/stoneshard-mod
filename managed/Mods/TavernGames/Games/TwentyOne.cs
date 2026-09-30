using CoreLoader;

namespace TavernGames;

/// <summary>
/// Twenty-One against the NPC as dealer: get closer to 21 than they do
/// without going over. Aces count 1 or 11, faces 10. A natural (21 on the
/// first two cards) is paid half as much again. The dealer draws to a total
/// that depends on its nerve: a cautious one stops at 15, a reckless one pushes
/// on to 18.
/// </summary>
internal sealed class TwentyOne : MiniGame
{
    private const int DealFrames = 18, DealerFrames = 32;
    private const double CardScale = 2, CardStep = 32;

    // cards.png: 13 ranks (ace first) for each suit - clubs, diamonds, hearts,
    // spades - then the back.
    private const int Back = 52;

    private readonly Sprite _cards;
    private readonly List<int> _deck = new(), _mine = new(), _theirs = new();

    private enum Step { Dealing, Play, Dealer, Done }

    private Step _step;
    private int _timer;
    private bool _hidden;
    private bool _doubled;

    public TwentyOne(Sprite cards) => _cards = cards;

    public override string Id => "twenty-one";
    public override string Title => "Twenty-One";
    public override string Rules =>
        "Beat the dealer's hand without going over ~y~21~/~. Aces count 1 or 11, faces 10. ~y~Hit~/~ for a card, ~y~stand~/~ to stop, " +
        "or ~y~double~/~ your stake on two cards for exactly one more. A natural 21 pays half again.";

    protected override void Start()
    {
        _deck.Clear();
        _deck.AddRange(Enumerable.Range(0, 52));
        // Fisher-Yates, with the round's (possibly seeded) generator.
        for (int i = _deck.Count - 1; i > 0; i--)
        {
            int j = Round.Rng.Next(i + 1);
            (_deck[i], _deck[j]) = (_deck[j], _deck[i]);
        }
        _mine.Clear();
        _theirs.Clear();
        _hidden = true;
        _doubled = false;
        _mine.Add(Deal());
        _theirs.Add(Deal());
        _mine.Add(Deal());
        _theirs.Add(Deal());
        Sfx.Play("snd_page_turn_1");
        _step = Step.Dealing;
        _timer = DealFrames;
    }

    private int Deal()
    {
        int card = _deck[^1];
        _deck.RemoveAt(_deck.Count - 1);
        return card;
    }

    /// <summary>A hand's best total: aces count 11 while that does not bust it.</summary>
    public static int Total(IEnumerable<int> hand)
    {
        int total = 0, aces = 0;
        foreach (int card in hand)
        {
            int rank = card % 13;
            if (rank == 0) { aces++; total += 11; }
            else total += Math.Min(10, rank + 1);
        }
        while (total > 21 && aces-- > 0) total -= 10;
        return total;
    }

    private static bool Natural(List<int> hand) => hand.Count == 2 && Total(hand) == 21;

    private int DealerStandsOn => Round.Opponent.Temper switch
    {
        Temperament.Cautious => 15,
        Temperament.Reckless => 18,
        _ => 17,
    };

    public override void Tick()
    {
        if (_step is not (Step.Dealing or Step.Dealer) || --_timer > 0) return;
        if (_step == Step.Dealing)
        {
            _step = Step.Play;
            if (Natural(_mine) || Natural(_theirs)) Reveal();
            return;
        }
        // The dealer's turn: one card at a time, so the player sees each one land.
        if (Total(_theirs) < DealerStandsOn && Total(_mine) <= 21)
        {
            _theirs.Add(Deal());
            Sfx.Play("snd_page_turn_2");
            _timer = DealerFrames;
            return;
        }
        Settle();
    }

    public override IEnumerable<ButtonSpec> Buttons()
    {
        if (_step != Step.Play) yield break;
        int s = Round.Stake;
        yield return new("hit", "Hit");
        yield return new("stand", "Stand");
        yield return new("double", $"Double ~y~{s}~/~",
                         _mine.Count == 2 && !_doubled && Round.CanPlayerRaise(s) && Round.CanOpponentRaise(s));
    }

    public override void OnButton(string id)
    {
        switch (id)
        {
            case "hit":
                _mine.Add(Deal());
                Sfx.Play("snd_page_turn_3");
                if (Total(_mine) > 21) Reveal();
                else if (Total(_mine) == 21) DealerTurn();
                break;
            case "stand":
                DealerTurn();
                break;
            case "double":
                Round.PlayerRaise(Round.Stake);
                Round.OpponentRaise(Round.Stake);
                _doubled = true;
                Round.Say($"You double to {Round.PlayerIn}.");
                _mine.Add(Deal());
                Sfx.Play("snd_page_turn_3");
                if (Total(_mine) > 21) Reveal();
                else DealerTurn();
                break;
        }
    }

    private void DealerTurn()
    {
        _hidden = false;
        _step = Step.Dealer;
        _timer = DealerFrames;
        Round.Say($"{Round.Opponent.Name} turns the hidden card: {Total(_theirs)}.");
    }

    // Straight to the result: a bust or a natural needs no more cards.
    private void Reveal()
    {
        _hidden = false;
        Settle();
    }

    private void Settle()
    {
        _step = Step.Done;
        int me = Total(_mine), them = Total(_theirs);
        string name = Round.Opponent.Name;
        if (me > 21) { Round.Finish(Outcome.Loss, $"Bust at {me}."); return; }
        bool myNatural = Natural(_mine), theirNatural = Natural(_theirs);
        if (myNatural && theirNatural) { Round.Finish(Outcome.Push, "Two naturals - a draw."); return; }
        if (myNatural)
        {
            // Half again, as far as the opponent's purse goes.
            int bonus = Math.Min(Round.Stake / 2, Round.Opponent.Bankroll - Round.OpponentIn);
            if (bonus > 0) Round.OpponentRaise(bonus);
            Round.Finish(Outcome.Win, "A natural twenty-one!");
            return;
        }
        if (theirNatural) { Round.Finish(Outcome.Loss, $"{name} has a natural twenty-one."); return; }
        if (them > 21) Round.Finish(Outcome.Win, $"{name} busts at {them}.");
        else if (me > them) Round.Finish(Outcome.Win, $"{me} beats {them}.");
        else if (me < them) Round.Finish(Outcome.Loss, $"{them} beats {me}.");
        else Round.Finish(Outcome.Push, $"{me} each - a draw.");
    }

    // ------------------------------------------------------------ drawing

    public override void Draw(Canvas c, Area area)
    {
        bool dealing = _step == Step.Dealing;
        // While dealing, cards appear one by one in the order they were dealt.
        int shown = dealing ? 4 - (int)Math.Ceiling(_timer / (DealFrames / 4.0)) : int.MaxValue;
        c.Text($"~y~{Round.Opponent.Name}~/~ deals", area.X, area.Y + 4);
        c.Text("~y~You~/~", area.X, area.Y + 112);
        DrawHand(c, area, _theirs, area.Y + 20, _hidden, dealing ? shown / 2 : int.MaxValue);
        DrawHand(c, area, _mine, area.Y + 128, false, dealing ? (shown + 1) / 2 : int.MaxValue);
        if (!dealing)
        {
            string theirs = _hidden ? "?" : Total(_theirs).ToString();
            c.Text($"Total: ~y~{theirs}~/~", area.X + area.W, area.Y + 4, align: 1);
            int me = Total(_mine);
            c.Text($"Total: {(me > 21 ? "~r~" : "~y~")}{me}~/~", area.X + area.W, area.Y + 112, align: 1);
        }
        if (_step == Step.Play) c.Text("~w~Hit, stand - or double on your first two cards.~/~", area.CenterX, area.Y + 96, align: 0);
    }

    private void DrawHand(Canvas c, Area area, List<int> hand, double y, bool hideSecond, int count)
    {
        double w = 26 * CardScale;
        int n = Math.Min(count, hand.Count);
        double x0 = area.CenterX - (CardStep * (hand.Count - 1) + w) / 2;
        for (int i = 0; i < n; i++)
            c.Sprite(_cards, hideSecond && i == 1 ? Back : hand[i], x0 + i * CardStep, y, CardScale);
    }

    /// <summary>The cards on the table, for the test host (the hidden one stays hidden).</summary>
    public object State() => new
    {
        step = _step.ToString(),
        mine = _mine.ToArray(),
        myTotal = Total(_mine),
        theirs = _hidden ? _theirs.Take(1).ToArray() : _theirs.ToArray(),
        theirTotal = _hidden ? (int?)null : Total(_theirs),
    };
}
