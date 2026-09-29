using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// Needs, vitals and XP through the game's attribute scripts; statuses applied
/// by creating their instances; the psyche map; the ability locks.
/// </summary>
internal sealed class CharacterTab : Tab
{
    private sealed record Attr(string Key, string Label, int Soft);

    private sealed record Condition(string Name, string Display, int Index, bool Positive);

    private sealed class PsyField
    {
        public required string Key;
        public double Value;
        public bool IsString;
        public string Text = "";
    }

    private static readonly Attr[] Needs =
    {
        new("Hunger", "Hunger", 100),
        new("Thirsty", "Thirst", 100),
        new("Intoxication", "Intoxication", 100),
        new("Immunity", "Immunity", 100),
        new("Fatigue", "Fatigue", 100),
        new("Pain", "Pain", 100),
    };

    private static readonly Attr[] Vitals =
    {
        new("HP", "Health", 500),
        new("MP", "Mana", 500),
        new("XP", "XP", 5000),
        new("LVL", "Level", 30),
    };

    private static readonly string[] Locks =
    {
        "lock_skills", "lock_spells", "lock_attack",
        "lock_regen", "lock_mana_regen", "lock_turn", "lock_items",
    };

    // The object table is walked a slice per frame: every index costs one or two
    // builtin calls and the game has thousands of objects, which in one frame
    // would be a visible hitch.
    private const int ScanPerFrame = 400;

    private readonly List<Condition> _found = new();
    private List<Condition>? _conditions;
    private int _scanAt;
    private int _scanMisses;

    private double _xpAmount = 100;
    private string _condFilter = "";
    private int _condChoice = -1;
    private int _condDuration = 200;
    private readonly List<PsyField> _psy = new();

    public override string Name => "Character";

    public override void Draw()
    {
        if (!Player.Available)
        {
            UI.TextColored(0.95f, 0.8f, 0.35f, "Waiting for the player instance - load a character, then move a step.");
            return;
        }

        // Read live rather than cached. The game moves these constantly, and a
        // stale slider would quietly write an old value back over a newer one.
        UI.SeparatorText("Needs");
        foreach (var a in Needs) AttrRow(a);

        UI.SeparatorText("Vitals and progression");
        foreach (var a in Vitals) AttrRow(a);

        // scr_get_XP is NOT a setter - it runs the real level-up path, so asking
        // for more than the threshold levels you up instead of overshooting. That
        // is why it sits apart from the raw XP slider.
        UI.SetNextItemWidth(160f);
        UI.InputDouble("##xpamt", ref _xpAmount, 10, 100, "%.0f");
        UI.SameLine();
        double xp = _xpAmount;
        Actions.CallRow("Grant XP", "scr_get_XP", $"scr_get_XP {xp:0}", () => Player.Call("scr_get_XP", xp));
        UI.TextDisabled("levels you up if it crosses the threshold");

        DrawConditions();
        DrawPsyche();
        DrawLocks();
    }

    // Every attribute call runs AS the player: scr_atr resolves its attribute
    // against `self`, and whatever instance the game last ran is not reliably
    // the player.
    private static void AttrRow(Attr a)
    {
        RValue r;
        try { r = Player.Call("scr_atr", a.Key); }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException) { r = RValue.Undefined; }
        if (!r.IsNumber)
        {
            UI.TextDisabled($"{a.Label}: unreadable");
            return;
        }

        int value = (int)Math.Round(r.AsReal);
        UI.SetNextItemWidth(220f);
        if (UI.SliderInt($"{a.Label}##atr_{a.Key}", ref value, 0, a.Soft))
        {
            int v = value;
            Actions.Run($"scr_atr_set \"{a.Key}\" {v}", () => Player.Call("scr_atr_set", a.Key, v));
        }
        UI.SameLine();
        UI.TextDisabled(a.Key);
    }

    // ------------------------------------------------------------ conditions

    private void DrawConditions()
    {
        UI.SeparatorText("Conditions");

        if (_conditions == null)
        {
            StepScan();
            if (_conditions == null)
            {
                UI.TextDisabled($"Reading the object table for statuses... {_scanAt} objects, {_found.Count} found.");
                return;
            }
        }

        if (_conditions.Count == 0)
        {
            UI.TextDisabled("No condition catalogue (no o_db_* or o_b_* objects found).");
            return;
        }

        UI.TextWrapped(
            "A status is an instance, not a script call. The status object is created and " +
            "given the three fields its own Create chain leaves blank - owner, target and " +
            "duration - and the game's recalculation, which runs every step and every " +
            "turn, picks it up from there.");
        UI.TextDisabled(
            $"{_conditions.Count} statuses, read from the object table. Behavioural ones (stun, bleeding, " +
            "poison) work; pure stat buffs apply with no magnitude - see the note below.");
        UI.SetNextItemWidth(-1f);
        UI.InputTextWithHint("##condfilter", "filter conditions...", ref _condFilter, 96);

        var shown = new List<int>();
        for (int i = 0; i < _conditions.Count; i++)
        {
            var c = _conditions[i];
            if (_condFilter.Length == 0 ||
                c.Display.Contains(_condFilter, StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains(_condFilter, StringComparison.OrdinalIgnoreCase))
                shown.Add(i);
        }

        var conds = _conditions;
        UI.BeginChild("##condlist", 200f);
        UI.Clipped(shown.Count, row =>
        {
            int i = shown[row];
            var c = conds[i];
            // Buffs and debuffs go through the same call, so colour is the only
            // clue about which one you are about to inflict on yourself.
            if (c.Positive) UI.PushTextColor(0.55f, 0.9f, 0.55f);
            else UI.PushTextColor(0.95f, 0.65f, 0.55f);
            if (UI.Selectable($"{c.Display}##cond{i}", _condChoice == i)) _condChoice = i;
            UI.PopTextColor();
            UI.SameLine(250f);
            UI.TextDisabled(c.Name);
        });
        UI.EndChild();

        UI.SetNextItemWidth(220f);
        UI.SliderInt("duration##conddur", ref _condDuration, 1, 2000, "%d ticks");
        UI.SameLine();
        UI.TextDisabled("a potion's buff read 199");

        UI.BeginDisabled(_condChoice < 0 || _condChoice >= conds.Count);
        if (UI.Button("Apply condition", 200f)) ApplyClicked(conds[_condChoice], _condDuration);
        UI.EndDisabled();
        UI.SameLine();
        UI.TextDisabled("creates the instance and points it at you");

        UI.TextDisabled(
            "Note: `data`, the stat-modifier map, is filled by whoever applies a buff and " +
            "there is no shared call that does it, so a pure stat buff lands with no " +
            "magnitude. Making numbers up here is what broke this last time.");
    }

    // Walks the next slice of object indices, the way GmlObject.All does (stop
    // after 64 missing indices in a row), keeping only the status objects.
    private void StepScan()
    {
        int end = _scanAt + ScanPerFrame;
        while (_scanAt < end)
        {
            if (_scanMisses >= 64 || _scanAt >= 100_000)
            {
                _found.Sort((a, b) => string.CompareOrdinal(a.Display, b.Display));
                _conditions = new List<Condition>(_found);
                Actions.Log.Info($"character: {_conditions.Count} statuses found in {_scanAt} object indices");
                return;
            }

            int i = _scanAt++;
            if (!Game.CallBuiltin("object_exists", i).AsBool) { _scanMisses++; continue; }
            _scanMisses = 0;

            string name = Game.CallBuiltin("object_get_name", i).ToString();
            bool debuff = name.StartsWith("o_db_", StringComparison.Ordinal);
            bool buff = name.StartsWith("o_b_", StringComparison.Ordinal);
            if (!debuff && !buff) continue;
            // The family parents are abstract: nothing of theirs is a status on its own.
            if (name.Length > 7 && name.EndsWith("_parent", StringComparison.Ordinal)) continue;

            string display = name[(debuff ? 5 : 4)..].Replace('_', ' ');
            if (display.Length > 0) display = char.ToUpperInvariant(display[0]) + display[1..];
            _found.Add(new Condition(name, display, i, buff));
        }
    }

    private static void ApplyClicked(Condition c, int duration)
    {
        // Count before and after: the apply can report success because the
        // instance was created and written, while the game still declines to
        // take it up. Showing both numbers makes that visible instead of
        // leaving a button that lies.
        int before = ActiveConditionCount();
        if (!Actions.Run($"apply {c.Name} ({c.Display}) for {duration} ticks", () => ApplyCondition(c, duration)))
            return;
        int after = ActiveConditionCount();

        if (before < 0 || after < 0)
            // Not the same as a count of zero, and saying so matters: it means
            // the check itself failed, not that the status did.
            Actions.Report($"{c.Name} ({c.Display}) -> applied, but the player's buffs list could not be read - unverified", false);
        else if (after > before)
            Actions.Report($"{c.Name} ({c.Display}) -> applied; player now carries {after} status(es), was {before}");
        else
            Actions.Report($"{c.Name} ({c.Display}) -> created and registered, but the list still reads {after} - the game dropped it", false);
    }

    // Applying a status is not a script call at all - it is creating an instance.
    //
    // An earlier version called scr_buff_change(index) as the player and killed
    // the character outright. That script is a buff's own tick handler: all four
    // of its call sites in the exe are buff Alarm events, so it expects to run AS
    // a live buff, and it reads more argument slots than it was given.
    //
    // Breakpointing gml_Object_c_buff_Create_0 while drinking a potion showed what
    // really happens: three instances appear, each running the inherited Create
    // chain (o_condition_debuff : o_debuff : c_buff). No applying script is on the
    // stack, because instance creation goes through a runtime builtin and the
    // engine dispatches Create itself.
    //
    // Reading one of those live buffs back, then creating one by hand and diffing
    // the two, established the division of labour exactly: the Create chain fills
    // in everything structural on its own - type, stack, stage, LVL, source, and
    // an allocated (empty) data map - and leaves precisely three fields for
    // whoever is applying it: `owner` (the unit, as an instance reference),
    // `target` (that unit's OBJECT index) and `duration` (ticks remaining; a buff
    // created with 0 never retires). Set those and the game takes it from there:
    // scr_atr_calc runs every step and every turn, and scr_player_buff_buffer
    // recomputes the attribute buffer from whatever buffs are live.
    //
    // KNOWN LIMIT: `data`, the ds_map of stat modifiers, is written by whoever
    // applies the buff - the potion code computes its own rolled values - and
    // there is no shared call that fills it (scr_buff_param only READS it, once
    // per attribute, during recalculation). Statuses whose effect lives in their
    // own events - stun, bleeding, poison, coma, most of o_db_* - do not need it.
    // Pure stat-modifier buffs apply with no magnitude until the numbers come
    // from somewhere real. Inventing them here is how the last bug happened.
    private static void ApplyCondition(Condition c, int duration)
    {
        var player = Player.Require();

        // Everything the apply needs is resolved before the instance is created,
        // so a failure does not leave an ownerless status lying in the room.
        var owner = player.Get("id");
        if (owner.IsUndefined)
            throw new InvalidOperationException("no instance reference for the player - cannot set the buff's owner");

        // `target` is an object index. Resolved by name rather than pinning the
        // number it happened to read, so a patch that renumbers objects costs nothing.
        var playerObject = GmlObject.Find("o_player")
            ?? throw new InvalidOperationException("asset_get_index(\"o_player\") failed");

        // A buff does not register itself. The whole Create chain - c_buff,
        // o_buff/o_debuff and the family object above them - makes no script
        // calls at all; it only initialises fields. So the unit's own buffs list
        // is the registry, and whoever applies a status has to put it there.
        // Confirmed rather than assumed: drinking a potion took the player's list
        // from 0 to 3 entries, one instance reference per buff it created.
        var list = player.Get("buffs");
        if (!Ds.ListExists(list))
            throw new InvalidOperationException("the player's buffs list is not readable");

        // A buff is an instance and has to be born somewhere; the player's own
        // position keeps it with the character it belongs to.
        var (x, y) = Player.Position;
        var inst = Player.Builtin("instance_create_depth", x, y, 0, c.Index);
        if (inst.IsUndefined)
            throw new InvalidOperationException($"instance_create_depth failed for {c.Name} (index {c.Index})");

        var buff = new InstanceRef(inst);
        buff.Set("owner", owner);
        buff.Set("target", playerObject.Index);
        buff.Set("duration", Math.Max(1, duration));

        // The reference itself goes in the list, not a decoded id.
        Ds.Add(list, inst);

        // The unit's own dirty flag, raised when its buff set changes and cleared
        // once the attributes have been recomputed. Setting it asks for that pass
        // rather than waiting for something else to trigger one. Written as a
        // real bool because that is the kind the game keeps there.
        try
        {
            player.Set("buffs_is_change", new RValue { Real = 1, Kind = RValueKind.Bool });
        }
        catch (GmlException ex)
        {
            Actions.Report($"buffs_is_change not set: {ex.Message}", false);
        }

        Actions.Log.Info($"character: applied {c.Name} (index {c.Index}) for {Math.Max(1, duration)} ticks " +
                         $"(target={playerObject.Index}, buffs list {list.AsReal})");
    }

    // How many entries the player's `buffs` ds_list holds, or -1 if it cannot
    // be read at all. -1 ("not readable") is a different answer from 0
    // ("readable, and empty"); an earlier version conflated them and made a bad
    // apply look like a clean one.
    private static int ActiveConditionCount()
    {
        try
        {
            var list = Player.Require().Get("buffs");
            return Ds.ListExists(list) ? Ds.Count(list) : -1;
        }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException)
        {
            return -1;
        }
    }

    // ---------------------------------------------------------------- psyche

    // psyData IS a live ds_map on the player, so unlike the needs these are
    // written directly instead of through a script.
    private void DrawPsyche()
    {
        if (!UI.CollapsingHeader("Psyche - Sanity, Morale, Panic, Bless...")) return;

        if (UI.Button("Read psyData", 160f))
        {
            try { ReadPsyche(); }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException)
            {
                Actions.Report($"psyData: {ex.Message}", false);
            }
        }
        UI.SameLine();
        UI.TextDisabled($"{_psy.Count} field(s)");

        UI.BeginChild("##psy", 220f);
        for (int i = 0; i < _psy.Count; i++)
        {
            var f = _psy[i];
            if (f.IsString)
            {
                UI.TextDisabled($"{f.Key} = {f.Text}");
                continue;
            }
            UI.SetNextItemWidth(180f);
            if (UI.InputDouble($"{f.Key}##psy{i}", ref f.Value, 1, 10, "%.2f"))
            {
                string key = f.Key;
                double v = f.Value;
                Actions.Run($"psyData.{key} = {v:0.##}", () => Ds.Set(PsyMap(), key, v));
            }
        }
        UI.EndChild();
    }

    private static RValue PsyMap()
    {
        var map = Player.Require().Get("psyData");
        if (!Ds.MapExists(map)) throw new InvalidOperationException("psyData is not readable");
        return map;
    }

    private void ReadPsyche()
    {
        _psy.Clear();
        foreach (var (key, value) in Ds.Entries(PsyMap()))
        {
            // Converted here: strings from the game are pooled and gone next frame.
            if (value.IsNumber) _psy.Add(new PsyField { Key = key, Value = value.AsReal });
            else if (value.Kind == RValueKind.String) _psy.Add(new PsyField { Key = key, IsString = true, Text = value.ToString() });
        }
        if (_psy.Count == 0) Actions.Report("psyData: the map is empty", false);
    }

    // ----------------------------------------------------------------- locks

    private static void DrawLocks()
    {
        if (!UI.CollapsingHeader("Ability locks")) return;

        UI.TextWrapped(
            "The game's own way of taking abilities away: ds_lists on the player. " +
            "Read-only for now - what a locked entry actually looks like is not " +
            "established, and writing a guess into these would break the character.");
        foreach (var l in Locks)
        {
            int n = LockCount(l);
            if (n < 0) UI.TextDisabled($"   {l,-18} unreadable");
            else UI.Text($"   {l,-18} {n} entr{(n == 1 ? "y" : "ies")}");
        }
    }

    private static int LockCount(string listName)
    {
        try
        {
            var list = Player.Require().Get(listName);
            return Ds.ListExists(list) ? Ds.Count(list) : -1;
        }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException)
        {
            return -1;
        }
    }
}
