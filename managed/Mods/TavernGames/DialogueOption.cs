using CoreLoader;
using StoneShard;

namespace TavernGames;

/// <summary>
/// "Fancy a game?" as a line in the game's own conversations. Talking to
/// someone who would play adds one option, just above the goodbye; choosing it
/// ends the conversation the way "Until next time" does and sits down at the
/// table with them.
/// </summary>
/// <remarks>
/// How a conversation's options work, as watched live (Ram, Osbrook):
///   * the options are fragment keys ("trade", "learn", "leave"): a string
///     array that scr_dialogue_sort_options orders, as o_dialogue, just
///     before dialogue_create_option_buttons turns each into a button;
///   * dialogue_get_string(key, true) gives the line an option button shows
///     (scr_create_contract_button(text, key) then makes the button);
///   * choosing one calls scr_dialogue_advance(key), and leaving is
///     scr_dialogue_advance("@dialogue_end").
/// The option is a key of our own threaded through those three: added after
/// the sort, answered before dialogue_get_string looks it up, and caught before
/// scr_dialogue_advance would look for a fragment the game does not have.
///
/// One edge is left: a hot reload (or a fault) while a conversation shows the
/// option takes the hooks away but not the button, which then reads as the
/// bare key and ends nothing when chosen; the next conversation is normal.
/// </remarks>
internal sealed class DialogueOption
{
    public const string Key = "lodestone_tavern_play";
    private const string EndKey = "@dialogue_end";
    private const string LeaveKey = "leave";

    private readonly Func<InstanceRef, bool> _willPlay;
    private readonly Logger _log;
    private readonly string[] _lines =
    [
        "Fancy a game? Dice, or cards?",
        "Care to play for a few crowns?",
        "How about a game to pass the time?",
    ];

    /// <param name="willPlay">Whether this conversation's speaker would sit down to play.</param>
    /// <param name="log">Where a refused change to the options is reported.</param>
    public DialogueOption(Func<InstanceRef, bool> willPlay, Logger log)
    {
        _willPlay = willPlay;
        _log = log;
    }

    // The line for this conversation: picked when the option is added, so the
    // game's repeated lookups of it all get the same words.
    private string _line = "";

    // How long a request waits for the conversation to close. The dialogue
    // window fades out in well under a second; a request still waiting after
    // this was overtaken by something else (a trade screen, another talk).
    private const long PendingMs = 3000;
    private long _pendingSince;

    /// <summary>Who asked to play, until the conversation has closed and the table opens (see <see cref="TakePending"/>).</summary>
    public InstanceRef? Pending { get; private set; }

    public void Install()
    {
        Scripts.scr_dialogue_sort_options.After(AddOption);
        Scripts.dialogue_get_string.Before(Answer);
        Scripts.scr_dialogue_advance.Before(Choose);
    }

    /// <summary>The NPC who asked, once, if they asked recently enough; the request is forgotten either way.</summary>
    public InstanceRef? TakePending()
    {
        var p = Pending;
        Pending = null;
        return p != null && Environment.TickCount64 - _pendingSince <= PendingMs ? p : null;
    }

    public void Forget() => Pending = null;

    // The speaker of the conversation this o_dialogue holds: a person
    // (o_npc_*), from the first of these variables that names one.
    private static InstanceRef? Speaker(Instance dialogue)
    {
        if (dialogue.IsNull) return null;
        foreach (var name in new[] { "speaker_instance", "interact_id", "owner" })
        {
            try
            {
                var v = dialogue.Get(name);
                if (Tavern.IdKey(v) < 0) continue;
                var npc = new InstanceRef(v);
                if (npc.Exists && Tavern.ObjectName(npc).StartsWith("o_npc_", StringComparison.Ordinal)) return npc;
            }
            catch (GmlException) { }
        }
        return null;
    }

    private static bool IsKey(HookCall c, string key)
    {
        if (c.ArgCount < 1) return false;
        var a = c.GetArg(0);
        return a.Kind == RValueKind.String && a.ToString() == key;
    }

    private void AddOption(HookCall c)
    {
        try
        {
            if (Speaker(c.Self) is not { } npc || !_willPlay(npc)) return;
            var options = c.Result;
            if (Gml.TypeOf(options) != "array") return;
            int n = Gml.ArrayLength(options);
            var keys = Enumerable.Range(0, n).Select(i => Gml.ArrayGet(options, i)).Select(v => v.Kind == RValueKind.String ? v.ToString() : "").ToList();
            // Only on the conversation's main menu, the one that offers a way out.
            if (!keys.Contains(LeaveKey) || keys.Contains(Key)) return;
            _line = _lines[Random.Shared.Next(_lines.Length)];
            Builtins.array_insert(options, keys.IndexOf(LeaveKey), Key);
        }
        catch (GmlException ex)
        {
            _log.Warning($"could not add the play option: {ex.Message}");
        }
    }

    // dialogue_get_string runs for every line of every conversation: only a
    // string argument equal to our key is ours.
    private void Answer(HookCall c)
    {
        if (!IsKey(c, Key)) return;
        c.SkipOriginal();
        c.Result = _line.Length > 0 ? _line : _lines[0];
    }

    private void Choose(HookCall c)
    {
        if (!IsKey(c, Key)) return;
        // Our key has no fragment: end the conversation as leaving does - done
        // first, so nothing below can leave the game looking for one - and
        // take the table from there once the dialogue window is gone.
        c.SetArg(0, EndKey);
        Pending = Speaker(c.Self);
        _pendingSince = Environment.TickCount64;
    }
}