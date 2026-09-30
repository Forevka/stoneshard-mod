using CoreLoader;

namespace TavernGames;

/// <summary>
/// Twenty-One against the NPC as dealer: get closer to 21 than they do
/// without going over. Aces count 1 or 11, faces 10. A natural (21 on the
/// first two cards) is paid half as much again. The dealer draws to a total
/// that depends on its nerve: a cautious one stops at 15, a reckless one pushes
/// on to 18.
/// </summary>
/// <remarks>
/// Every card is dealt from a deck on the table: it slides face down from the
/// deck to its place, then turns over (the dealer's second card stays down
/// until the dealer's turn). A hand re-centres smoothly as it grows, and a
/// result waits for the last card to land.
/// </remarks>
internal sealed class TwentyOne : MiniGame
{
    // Frames: between cards of the opening deal, a card's slide, its turn,
    // and between the dealer's draws.
    private const int DealGap = 9, Fly = 14, Turn = 10, DealerFrames = 34;
    private const double CardScale = 2, CardStep = 32, CardW = 26, CardH = 36;

    // cards.png: 13 ranks (ace first) for each suit - clubs, diamonds, hearts,
    // spades - then the back.
    private const int Back = 52;

    /// <summary>A card on the table, with where it is in its deal and its turn.</summary>
    private sealed class Dealt
    {
        public Dealt(int card, long dealAt, bool faceUp)
        {
            Card = card;
            DealAt = dealAt;
            TurnAt = faceUp ? dealAt + Fly : long.MaxValue;
        }

        public int Card { get; }

        /// <summary>The frame it leaves the deck.</summary>
        public long DealAt { get; }

        /// <summary>The frame it starts to turn face up; MaxValue while it stays down.</summary>
        public long TurnAt { get; set; }

        // Where it was last drawn (NaN until it lands), eased towards its place.
        public double X = double.NaN, Y;
        public bool Sounded;
    }

    private readonly Sprite _cards;
    private readonly List<int> _deck = new();
    private readonly List<Dealt> _mine = new(), _theirs = new();

    private enum Step { Dealing, Play, Dealer, Resolving, Done }

    private Step _step;
    private int _timer;
    private bool _doubled;

    // Frames since the round began, counted by Tick: the cards' clock.
    private long _clock;

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
        _doubled = false;
        _clock = 0;
        // You, them, you, them - the dealer's second card face down.
        _mine.Add(new Dealt(Deal(), 0, true));
        _theirs.Add(new Dealt(Deal(), DealGap, true));
        _mine.Add(new Dealt(Deal(), 2 * DealGap, true));
        _theirs.Add(new Dealt(Deal(), 3 * DealGap, false));
        _step = Step.Dealing;
        _timer = 3 * DealGap + Fly + Turn;
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

    private static int Total(List<Dealt> hand) => Total(hand.Select(d => d.Card));

    private static bool Natural(List<Dealt> hand) => hand.Count == 2 && Total(hand) == 21;

    private bool Hidden => _theirs.Count > 1 && _theirs[1].TurnAt == long.MaxValue;

    private int DealerStandsOn => Round.Opponent.Temper switch
    {
        Temperament.Cautious => 15,
        Temperament.Reckless => 18,
        _ => 17,
    };

    // ------------------------------------------------------------ flow

    // The table calls Tick only while the round is in play, so every
    // animation must be over before Settle: the dealer's pause between draws
    // and Resolving's wait are both longer than a card's slide and turn.
    public override void Tick()
    {
        _clock++;
        if (_step is Step.Play or Step.Done || --_timer > 0) return;
        switch (_step)
        {
            case Step.Dealing:
                _step = Step.Play;
                if (Natural(_mine) || Natural(_theirs)) Reveal();
                return;
            case Step.Resolving:
                Settle();
                return;
        }
        // The dealer's turn: one card at a time, so the player sees each one land.
        if (Total(_theirs) < DealerStandsOn && Total(_mine) <= 21)
        {
            _theirs.Add(new Dealt(Deal(), _clock, true));
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
                _mine.Add(new Dealt(Deal(), _clock, true));
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
                _mine.Add(new Dealt(Deal(), _clock, true));
                if (Total(_mine) > 21) Reveal();
                else DealerTurn();
                break;
        }
    }

    public override void Conclude()
    {
        switch (_step)
        {
            // Nothing left for the player to choose: play it out at once.
            case Step.Resolving:
                Settle();
                break;
            case Step.Dealer:
                while (Total(_theirs) < DealerStandsOn && Total(_mine) <= 21) _theirs.Add(new Dealt(Deal(), _clock, true));
                Settle();
                break;
            // Still dealing: a natural on either side decides it, anything else waits on the player.
            case Step.Dealing when Natural(_mine) || Natural(_theirs):
                Settle();
                break;
        }
    }

    private void DealerTurn()
    {
        TurnHidden();
        _step = Step.Dealer;
        // The hidden card turns first; the dealer draws once everyone has seen it.
        _timer = Fly + DealerFrames;
        Round.Say($"{Round.Opponent.Name} turns over the hidden card.");
    }

    private void TurnHidden()
    {
        if (Hidden) _theirs[1].TurnAt = _clock + Fly;
    }

    // Straight to the result - a bust or a natural needs no more cards - once
    // the last card has landed and the hidden one has turned.
    private void Reveal()
    {
        TurnHidden();
        _step = Step.Resolving;
        _timer = Fly + Turn + 6;
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

    // Where the deck sits: right of the play area, between the two hands.
    private static (double X, double Y) DeckAt(Area area) => (area.X + area.W - CardW * CardScale - 6, area.Y + 58);

    public override void Draw(Canvas c, Area area)
    {
        c.Text($"~y~{Round.Opponent.Name}~/~ deals", area.X, area.Y + 4);
        c.Text("~y~You~/~", area.X, area.Y + 112);

        // The deck: a few backs, stacked a pixel apart.
        var (dx, dy) = DeckAt(area);
        for (int i = 3; i >= 0; i--) c.Sprite(_cards, Back, dx - i, dy - i, CardScale);

        DrawHand(c, area, _theirs, area.Y + 20);
        DrawHand(c, area, _mine, area.Y + 128);

        if (_step != Step.Dealing)
        {
            string theirs = Hidden ? "?" : Shown(_theirs).ToString();
            c.Text($"Total: ~y~{theirs}~/~", dx - 12, area.Y + 4, align: 1);
            int me = Shown(_mine);
            c.Text($"Total: {(me > 21 ? "~r~" : "~y~")}{me}~/~", dx - 12, area.Y + 112, align: 1);
        }
        if (_step == Step.Play) c.Text("~w~Hit, stand - or double on your first two cards.~/~", area.CenterX - 30, area.Y + 96, align: 0);
    }

    // The total of the cards that have landed face up, so the number never runs ahead of the table.
    private int Shown(List<Dealt> hand) => Total(hand.Where(d => _clock >= d.TurnAt + Turn / 2).Select(d => d.Card));

    private void DrawHand(Canvas c, Area area, List<Dealt> hand, double y)
    {
        var (dx, dy) = DeckAt(area);
        double w = CardW * CardScale;
        // Centred on what has left the deck, left of the deck itself.
        int count = hand.Count(d => _clock >= d.DealAt);
        double centre = area.X + (dx - area.X) / 2;
        double x0 = centre - (CardStep * (count - 1) + w) / 2;
        int slot = 0;
        foreach (var card in hand)
        {
            if (_clock < card.DealAt) continue;
            double tx = x0 + slot++ * CardStep;
            long t = _clock - card.DealAt;
            if (!card.Sounded)
            {
                card.Sounded = true;
                Sfx.Play("snd_page_turn_" + (1 + card.Card % 3));
            }

            double x, cy;
            if (t < Fly)
            {
                // Slides out of the deck, fast then settling (ease-out).
                double p = (double)t / Fly, e = 1 - (1 - p) * (1 - p);
                x = dx + (tx - dx) * e;
                cy = dy + (y - dy) * e;
            }
            else
            {
                // Landed: follows its place as the hand re-centres.
                x = double.IsNaN(card.X) ? tx : card.X + (tx - card.X) * 0.3;
                cy = y;
            }
            card.X = t < Fly ? double.NaN : x;
            card.Y = cy;

            // Turning over: the back narrows to nothing, then the face widens.
            double turn = _clock < card.TurnAt ? 0 : Math.Min(1, (double)(_clock - card.TurnAt) / Turn);
            bool face = turn >= 0.5;
            double squeeze = turn <= 0 ? 1 : Math.Abs(1 - 2 * turn);
            c.SpriteTurned(_cards, face ? card.Card : Back, x, cy, CardW, Math.Max(0.04, squeeze), CardScale);
        }
    }

    /// <summary>The cards on the table, for the test host (the hidden one stays hidden).</summary>
    public object State() => new
    {
        step = _step.ToString(),
        mine = _mine.Select(d => d.Card).ToArray(),
        myTotal = Total(_mine),
        theirs = Hidden ? _theirs.Take(1).Select(d => d.Card).ToArray() : _theirs.Select(d => d.Card).ToArray(),
        theirTotal = Hidden ? (int?)null : Total(_theirs),
    };
}
