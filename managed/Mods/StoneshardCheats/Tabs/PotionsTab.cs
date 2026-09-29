using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// Potion construction: write the effect list, let the game derive the rest.
/// </summary>
/// <remarks>
/// There is no o_inv_potion_healing object to spawn - there is exactly one potion
/// object, o_inv_bottle, and every potion in the game is an instance of it whose
/// `data` map carries an effect list. The game builds one like this:
///
///     o_inv_bottle Create  -> sets alarm[0]
///     o_inv_bottle Alarm 0 -> scr_roll_potion   (the whole event; nothing else)
///     scr_roll_potion      -> rolls the effects, then calls
///                             scr_potion_set_param, which derives everything
///                             else from them - Name, Colour, quality
///
/// A potion is therefore its effect list, `atrdlist` (a plain ds_list of tag
/// strings), plus whatever the game derives from it. So building one means:
/// take one bottle, let the game roll it normally, then write the chosen tags
/// into `atrdlist` and have scr_potion_set_param re-derive the rest. Any
/// combination works, including ones no roll table produces.
///
/// Established by observation rather than assumption:
///   * scr_potion_set_param takes argc = 0 and works on the bottle as `self`,
///     read off a captured call. There are no argument semantics to guess.
///   * neither it nor scr_roll_potion survives being called from outside its
///     own event frame (the runtime throws 0xE06D7363), which is why the build
///     happens in an After hook on the bottle's Alarm 0 and not in the UI.
///   * a potion cannot be re-rolled: called on an already-rolled bottle,
///     scr_roll_potion changes nothing, and neither blanking `Name` nor emptying
///     `atrdlist` lifts that. Rolling fresh bottles until one matches can only
///     reach combinations the game rolls itself, and disposing of the misses
///     needs an aimed instance_destroy. Building outright avoids both.
/// </remarks>
internal sealed class PotionsTab : Tab
{
    // The one potion object; its asset index is looked up at every use, never
    // compiled in.
    private const string BottleObject = "o_inv_bottle";
    private const string AlarmEvent = "gml_Object_o_inv_bottle_Alarm_0";

    // The give is expected to trigger the bottle's alarm on the next step. If
    // nothing fires by then the request is dropped, so a give that failed
    // quietly cannot rewrite the next bottle the game makes on its own.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private string? _hookError;

    // The armed request: a one-shot hook on the bottle's Alarm 0.
    private NextCall? _request;
    // The bottle the give created, or -1 when it could not be singled out.
    private long _armedId = -1;
    private List<string> _wantTags = new();

    private bool Armed => _request is { IsPending: true };

    // The outcome of the last request, posted by the hook and collected once by
    // Draw, so it is reported on the frame it arrives rather than every frame.
    private bool _outcomeNew, _outcomeOk;
    private string _outcomeMessage = "";

    // What the tab shows until the next outcome.
    private bool _lastOk;
    private string _lastMessage = "";

    private bool[] _picked = Array.Empty<bool>();

    public override string Name => "Potions";

    public override void Initialize(CheatsMod mod)
    {
        Catalogue.Start();
        // The hook itself is only installed while a build is armed; checked up
        // front so a game without the event says so instead of offering a button.
        if (Game.FindSymbol(AlarmEvent) == 0)
        {
            _hookError = $"{AlarmEvent} does not exist in {Game.Name}";
            Actions.Log.Warning($"potions: {_hookError}");
        }
    }

    // Which bottle alarm is ours. The whole event body is one call to
    // scr_roll_potion, so the hook sees one call per potion the game creates -
    // not one per step.
    private bool IsOurBottle(HookCall c)
    {
        // A mod skipped the game's own roll: there is no rolled bottle to build on.
        if (c.Self.IsNull || c.OriginalSkipped) return false;
        // Aimed at the bottle the give created: any other bottle rolling in the
        // meantime (loot, a trader restock) is left exactly as the game made it.
        if (_armedId < 0) return true;
        try { return IdKey(c.Self.Get("id")) == _armedId; }
        catch (Exception) { return false; }
    }

    // After the game's own roll, which is untouched and always first: it is what
    // makes the bottle a valid potion in the first place; the build only rewrites it.
    private void OnBottleAlarm(HookCall c)
    {
        // This runs inside the game's event: a failure is turned into the outcome
        // message here rather than faulting the mod mid-event.
        try
        {
            Finish(true, Build(c.Self));
        }
        catch (Exception ex)
        {
            Finish(false, ex.Message);
        }
    }

    // Rewrites the freshly rolled bottle into the potion that was asked for.
    private string Build(Instance bottle)
    {
        var id = bottle.Get("data");
        if (!id.IsNumber) throw new InvalidOperationException("the bottle has no data map");
        var data = new DsMap(id);

        // `atrdlist` is a plain ds_list of tag strings - [ "good_pt_rage" ] - so
        // it is edited IN PLACE. Its id is never written back with
        // ds_map_replace, which would destroy the nesting.
        var list = new DsList(data.Get("atrdlist"));
        if (!list.Exists) throw new InvalidOperationException("could not write the effect list: no atrdlist");
        list.Clear();
        foreach (var tag in _wantTags) list.Add(tag);

        // argc = 0, self = the bottle: exactly how scr_roll_potion calls it.
        try
        {
            Game.CallScriptAs(bottle, bottle, "scr_potion_set_param");
        }
        catch (GmlException)
        {
            throw new InvalidOperationException(
                "scr_potion_set_param rejected the call - the effect list is written but the name and colour are stale");
        }

        var name = data.Get("Name");
        if (name.Kind != RValueKind.String) return "built it, but the result could not be read back";
        string text = name.ToString();
        return text.Length == 0 ? "built it" : $"built {text}";
    }

    // Called by the loader once the timeout passes with no matching alarm - on
    // time, even if no bottle alarm ever fires.
    private void TimedOut() =>
        Finish(false, $"no bottle alarm within {Timeout.TotalSeconds:0} s - the bottle was not given; nothing was built");

    private void Finish(bool ok, string message)
    {
        _outcomeNew = true;
        _outcomeOk = ok;
        _outcomeMessage = message;
        _outcomeSeq++;
    }

    // Counts outcomes, so a test client can tell the one it armed for from an
    // earlier one: the build that BuildPotion arms finishes as number _outcomeSeq + 1.
    private int _outcomeSeq;

    /// <summary>
    /// The state of the last build, for the test host: still pending, or its
    /// outcome and which build it was.
    /// </summary>
    public object Outcome() => Armed
        ? new { pending = true, seq = _outcomeSeq }
        : _outcomeNew
            ? new { pending = false, seq = _outcomeSeq, ok = _outcomeOk, message = _outcomeMessage }
            : (object)new { pending = false, seq = _outcomeSeq, ok = _lastOk, message = _lastMessage };

    /// <summary>
    /// Validates the tags against the effect table, then arms the build (see
    /// BuildPotion). Returns the outcome number the build will finish as.
    /// </summary>
    public int Arm(List<string> tags)
    {
        if (_hookError != null) throw new InvalidOperationException(_hookError);
        if (Armed) throw new InvalidOperationException("a potion build is already armed");
        if (tags.Count == 0) throw new ArgumentException("no effects given");
        if (!Catalogue.Done) throw new InvalidOperationException($"the effect table is still loading ({Catalogue.Status})");
        var unknown = tags.Where(t => !Catalogue.PotionEffects.Any(e => e.Tag == t)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"unknown effect tag(s): {string.Join(", ", unknown)}");
        BuildPotion(tags);
        return _outcomeSeq + 1;
    }

    private void BuildPotion(List<string> tags)
    {
        // The object's own asset index, looked up by name rather than a constant.
        int index = GmlObject.Find(BottleObject)?.Index
                    ?? throw new InvalidOperationException($"{BottleObject} is not in the object table");

        // Arm first, give the bottle second: the alarm fires on a later step, and
        // the hook does the building then - inside the event frame, the only
        // place the potion scripts run.
        //
        // The bottles that exist before the give are noted, so the one the give
        // creates can be told apart by diffing afterwards, and the hook then acts
        // on that instance only. If the give does not create its bottle
        // synchronously (no new instance, or more than one), the request stays
        // unaimed and the first bottle alarm within the timeout is built - the
        // same behaviour as before the aiming existed.
        var before = BottleIds(index);
        _wantTags = tags;
        _armedId = -1;
        _outcomeNew = false;
        _request = Hooks.NextAfter(AlarmEvent, IsOurBottle, OnBottleAlarm, Timeout, TimedOut);
        try
        {
            Player.Call("scr_dialogue_reward_add_item", index);
        }
        catch
        {
            _request.Dispose();
            throw;
        }

        var added = BottleIds(index);
        added.ExceptWith(before);
        if (added.Count == 1) _armedId = added.First();
        Actions.Log.Info(added.Count == 1
            ? $"potions: aimed at bottle {_armedId}"
            : $"potions: {added.Count} new bottle(s) after the give - building the first bottle alarm instead");
    }

    // The ids of every live bottle, as numbers comparable across frames.
    private static HashSet<long> BottleIds(int index)
    {
        var ids = new HashSet<long>();
        int n = (int)Game.CallBuiltin("instance_number", index).AsReal;
        for (int i = 0; i < n; i++)
        {
            long id = IdKey(Game.CallBuiltin("instance_find", index, i));
            if (id >= 0) ids.Add(id);
        }
        return ids;
    }

    // Older runtimes hand out instance ids as plain numbers; newer ones as typed
    // references whose low 32 bits are the id.
    internal static long IdKey(RValue v) =>
        v.IsNumber ? (long)v.AsReal : v.Kind == RValueKind.Reference ? v.Int64 & 0xFFFFFFFF : -1;

    public override void Draw()
    {
        UI.TextWrapped(
            "Potions are the one thing the Items tab cannot list. There is no \"Potion of " +
            "Healing\" object in the game - there is one potion object, o_inv_bottle, and every " +
            "potion is an instance of it carrying a rolled set of effects. The name you read in " +
            "the inventory is assembled from those effects, so searching the catalogue for a " +
            "potion by name will never find one.");
        UI.Spacing();
        UI.TextWrapped(
            "So building one means taking a bottle, letting the game roll it, then replacing " +
            "its effect list with the one you picked and having the game re-derive the name, " +
            "colour and the rest. Any combination works - including ones no roll table produces.");

        UI.Separator();

        if (_hookError != null)
        {
            UI.TextColored(0.95f, 0.4f, 0.4f, $"Bottle recorder not installed: {_hookError}");
            return;
        }

        if (!Catalogue.Done)
        {
            UI.ProgressBar(Catalogue.Progress, 0f, Catalogue.Status);
            return;
        }

        var effects = Catalogue.PotionEffects;
        if (effects.Count == 0)
        {
            UI.TextColored(0.95f, 0.4f, 0.4f, $"No effect table found in the game image. ({Catalogue.Status})");
            return;
        }

        // Which effects the potion will carry. A set rather than one choice: the
        // whole point of building instead of rolling is combinations the roll
        // tables never produce.
        if (_picked.Length != effects.Count) _picked = new bool[effects.Count];

        var chosen = new List<string>();
        for (int i = 0; i < effects.Count; i++)
            if (_picked[i]) chosen.Add(effects[i].Tag);

        UI.Text($"{chosen.Count} effect(s) selected");
        UI.SameLine();
        if (UI.SmallButton("clear")) Array.Clear(_picked);

        UI.BeginChild("##effects", 220f);
        for (int i = 0; i < effects.Count; i++)
        {
            var e = effects[i];
            UI.PushId($"e{i}");
            if (e.Positive) UI.PushTextColor(0.55f, 0.9f, 0.55f);
            else UI.PushTextColor(0.95f, 0.6f, 0.55f);
            UI.Checkbox(e.Display, ref _picked[i]);
            UI.PopTextColor();
            UI.SameLine(220f);
            UI.TextDisabled(e.Tag);
            UI.PopId();
        }
        UI.EndChild();

        UI.BeginDisabled(Armed || chosen.Count == 0 || !Player.Available);
        if (UI.Button("Build this potion", 220f))
            Actions.Run($"build a potion with {chosen.Count} effect(s): {string.Join(", ", chosen)}",
                () => BuildPotion(chosen));
        UI.EndDisabled();
        UI.SameLine();
        UI.TextDisabled("takes one bottle and rewrites it");

        if (Armed) UI.TextColored(0.95f, 0.8f, 0.35f, "building...");

        if (_outcomeNew)
        {
            _outcomeNew = false;
            _lastOk = _outcomeOk;
            _lastMessage = _outcomeMessage;
            Actions.Report(_lastMessage, _lastOk);
        }

        if (_lastMessage.Length > 0)
        {
            if (_lastOk) UI.TextColored(0.45f, 0.9f, 0.45f, _lastMessage);
            else UI.TextColored(0.95f, 0.4f, 0.4f, _lastMessage);
        }
    }
}
