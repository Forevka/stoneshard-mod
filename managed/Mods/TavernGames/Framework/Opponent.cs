using CoreLoader;

namespace TavernGames;

/// <summary>How an opponent bets and plays: the same rules, different nerve.</summary>
internal enum Temperament
{
    /// <summary>Innkeepers and staff: bets on sure things, folds early.</summary>
    Cautious,
    /// <summary>Bards, townsfolk: plays the odds.</summary>
    Steady,
    /// <summary>Drunks and sellswords: raises on nothing, calls everything.</summary>
    Reckless,
}

/// <summary>
/// Whoever sits across the table: a live NPC, with a purse and a temper read
/// from what kind of NPC it is. The purse is the mod's own bookkeeping (see
/// <see cref="Ledger"/>); vanilla gives most townsfolk no money at all.
/// </summary>
internal sealed class Opponent
{
    public Opponent(InstanceRef npc, string key, string name, Temperament temper, Ledger.Entry books)
    {
        Npc = npc;
        Key = key;
        Name = name;
        Temper = temper;
        Books = books;
    }

    public InstanceRef Npc { get; }

    /// <summary>Stable across rooms and loads: the NPC's id_name, or its object name.</summary>
    public string Key { get; }

    public string Name { get; }

    public Temperament Temper { get; }

    public Ledger.Entry Books { get; }

    /// <summary>Crowns the opponent can still lose.</summary>
    public int Bankroll => Books.Bankroll;

    /// <summary>A line for the moment, picked at random from the temperament's lines.</summary>
    public string Line(Mood mood, Random rng)
    {
        var lines = Barks.For(Temper, mood);
        return lines.Length == 0 ? "" : lines[rng.Next(lines.Length)];
    }
}

/// <summary>What an opponent is reacting to.</summary>
internal enum Mood
{
    Greet,
    Raise,
    Call,
    Fold,
    Win,
    Lose,
    Broke,
}

/// <summary>What opponents say. Short, and in the game's voice.</summary>
internal static class Barks
{
    public static string[] For(Temperament t, Mood m) => (t, m) switch
    {
        (Temperament.Reckless, Mood.Greet) => ["Sit, sit! Put your coin where your mouth is.", "Another fool with a full purse. Good.", "Loser buys the next round."],
        (Temperament.Reckless, Mood.Raise) => ["Double it. I feel lucky.", "More! Let's make it hurt.", "Bah, raise."],
        (Temperament.Reckless, Mood.Call) => ["I'm in, whatever you've got.", "Call. Never backed down in my life."],
        (Temperament.Reckless, Mood.Fold) => ["...Fine. Take it.", "Curse these bones."],
        (Temperament.Reckless, Mood.Win) => ["Ha! Pay up!", "The drink's on you, friend.", "Told you. Lucky."],
        (Temperament.Reckless, Mood.Lose) => ["Gah! Again. We go again.", "You cheat, I swear it.", "Hmph. Beginner's luck."],
        (Temperament.Cautious, Mood.Greet) => ["A small game, then. Nothing foolish.", "House rules: no knives on the table.", "One round, while it's quiet."],
        (Temperament.Cautious, Mood.Raise) => ["I'll raise. Carefully.", "A little more, I think."],
        (Temperament.Cautious, Mood.Call) => ["Very well. I'll see it.", "Hm. Call."],
        (Temperament.Cautious, Mood.Fold) => ["Not worth it. I'm out.", "No, no. Keep it."],
        (Temperament.Cautious, Mood.Win) => ["The house thanks you.", "Better luck next time."],
        (Temperament.Cautious, Mood.Lose) => ["Well played. Don't make a habit of it.", "Hm. So it goes."],
        (Temperament.Steady, Mood.Greet) => ["A game? Why not.", "Let's see what the dice think of you."],
        (Temperament.Steady, Mood.Raise) => ["Raise.", "I'll add to it."],
        (Temperament.Steady, Mood.Call) => ["Call.", "I'll see that."],
        (Temperament.Steady, Mood.Fold) => ["Not this hand.", "I fold."],
        (Temperament.Steady, Mood.Win) => ["Mine, I think.", "Good game."],
        (Temperament.Steady, Mood.Lose) => ["Yours. Fairly won.", "Good hand."],
        (_, Mood.Broke) => ["I'm cleaned out. Come back another day.", "Not a crown left to lose. Go on."],
        _ => [],
    };
}
