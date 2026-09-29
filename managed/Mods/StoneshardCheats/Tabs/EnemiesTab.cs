using CoreLoader;

namespace StoneshardCheats;

/// <summary>The enemy roster, one Remove button per row, and a variable probe.</summary>
/// <remarks>
/// Every enemy is an instance of o_enemy or one of its children, so the roster
/// is a walk over live instances (instance_number + instance_find) rather than a
/// table of monster names, and cannot go out of date. That walk answers with
/// instance references rather than CInstance pointers: enough to read and write
/// fields and to destroy the instance, which is all the tab needs.
/// </remarks>
internal sealed class EnemiesTab : Tab
{
    private const string ObjectName = "o_enemy";

    // The probe logs at most this many variables; the on-screen list is
    // clipped instead, so it shows all.
    private const int LogLimit = 300;

    /// <summary>
    /// One row, kept between refreshes as plain C# data. The id is a number or a
    /// kind-15 reference, neither of which is pooled, so it survives the frame;
    /// whether the instance behind it still does is checked with Exists.
    /// </summary>
    internal sealed record Enemy(
        InstanceRef Ref, string Key, string Name, string Race, string Type,
        double? Hp, double? MaxHp, double? Level, double? X, double? Y, double? Dist);

    private List<Enemy> _roster = new();
    private int _reported = -1;
    private string _error = "";

    private string? _selectedKey;
    private List<string> _probeNames = new();
    private string? _probeKey;

    private bool _autoRefresh = true;
    private int _refreshEvery = 15;   // frames
    private int _sinceRefresh;
    private string _filter = "";

    public override string Name => "Enemies";

    public override void Draw()
    {
        UI.TextWrapped(
            "Everything hostile standing in the room with you. Enemies are all instances of one " +
            "object, so this is a walk over live instances rather than a list of monster names - " +
            "which is why it needs no table to maintain and cannot go out of date.");

        UI.Spacing();
        if (UI.Button("Refresh", 110f)) Refresh();
        UI.SameLine();
        UI.Checkbox("auto", ref _autoRefresh);
        UI.SameLine();
        UI.SetNextItemWidth(150f);
        UI.SliderInt("##rate", ref _refreshEvery, 5, 120, "every %d frames");
        UI.SameLine();
        UI.SetNextItemWidth(-1f);
        UI.InputTextWithHint("##filter", "filter by name...", ref _filter, 64);

        // Reading a dozen fields per enemy is cheap but not free, and the roster
        // only changes as fast as the game's turns do.
        if (_autoRefresh && ++_sinceRefresh >= _refreshEvery)
        {
            _sinceRefresh = 0;
            Refresh();
        }

        UI.Text($"listed {_roster.Count}");
        UI.SameLine();
        UI.TextDisabled(_reported < 0
            ? "| the game could not be asked | source: instance_find"
            : $"| the game reports {_reported} | source: instance_find");

        if (_roster.Count == 0)
        {
            UI.Spacing();
            UI.TextDisabled(_reported == 0 ? "Nothing hostile here." : "Nothing listed yet - hit Refresh.");
        }

        // Removing inside the loop would change the list under it, so the click
        // is remembered and acted on once the child is closed.
        Enemy? remove = null;
        UI.Spacing();
        UI.BeginChild("##roster", 230f);
        foreach (var e in _roster)
        {
            if (!Matches(e)) continue;

            // The button comes first because the Selectable spans the rest of
            // the row. allowOverlap keeps it from swallowing clicks meant for a
            // widget on the same row, which is how the native row lost its
            // buttons before the flag was added.
            if (UI.SmallButton($"Remove###rm{e.Key}")) remove = e;
            UI.SameLine();
            if (UI.Selectable($"{RowText(e)}###row{e.Key}", e.Key == _selectedKey, allowOverlap: true))
                _selectedKey = e.Key;
        }
        UI.EndChild();

        if (remove != null) Remove(remove);

        var selected = _roster.Find(e => e.Key == _selectedKey);
        if (selected != null) DrawSelected(selected);

        UI.Spacing();
        UI.TextWrapped(
            "Remove destroys the instance: the drop still happens, because that lives in the " +
            "Destroy event, but nothing on the damage path does. The variable list below the " +
            "selected enemy (and Log vars, which writes it to the log) is how the field names " +
            "above were settled and how they get re-checked after a patch.");
        UI.TextWrapped(
            "That is the only action on purpose. Writing an enemy's HP - to kill it, or " +
            "partway to wound it - went through the game's own attribute setter run as that " +
            "enemy, and it did not take: nothing died and nothing was hurt. Writing the variable " +
            "directly moved the number without the game reacting, which is worse than no button, " +
            "because then the row reads a lie. The kill-everything variants could not be aimed " +
            "either, so one misclick emptied the room.");

        if (_error.Length > 0) UI.TextColored(0.95f, 0.45f, 0.45f, _error);
    }

    private void DrawSelected(Enemy e)
    {
        UI.SeparatorText($"{e.Name}###selected");
        string race = e.Race.Length == 0 ? "?" : e.Race;
        string type = e.Type.Length == 0 ? "?" : e.Type;
        UI.TextDisabled(e.X is { } x && e.Y is { } y
            ? $"race {race}   type {type}   at ({x:0}, {y:0})"
            : $"race {race}   type {type}   position unreadable");

        // The probe reads live, so an enemy that died since the last refresh
        // must not be asked for its variables.
        if (!e.Ref.Exists)
        {
            UI.TextDisabled("This enemy is gone; the roster catches up on the next refresh.");
            return;
        }

        if (_probeKey != e.Key)
        {
            _probeKey = e.Key;
            _probeNames = e.Ref.VariableNames().OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        UI.TextDisabled($"{_probeNames.Count} instance variables");
        UI.SameLine();
        if (UI.SmallButton("Log vars###logvars")) LogVars(e);

        UI.BeginChild("##probe", 220f);
        UI.Clipped(_probeNames.Count, i =>
        {
            string n = _probeNames[i];
            UI.Text($"{n} = {Format(e.Ref.Get(n))}");
        });
        UI.EndChild();
    }

    // -------------------------------------------------------------- roster

    private void Refresh()
    {
        _error = "";
        _probeKey = null;   // names can change as the enemy's state does

        try
        {
            _roster = ReadRoster(out _reported);
        }
        catch (InvalidOperationException ex)
        {
            _roster.Clear();
            _reported = -1;
            _error = ex.Message;
        }
        catch (GmlException ex)
        {
            _error = $"refresh failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Every live enemy, nearest first, and how many the game itself reports.
    /// Shared by the tab and the test host's cheats.enemies.
    /// </summary>
    public static List<Enemy> ReadRoster(out int reported)
    {
        if (GmlObject.Find(ObjectName) is not { } obj)
            throw new InvalidOperationException($"unknown object '{ObjectName}'");

        // Ask the game how many there are first: it is one call, it is the
        // honest answer, and it is what the listed count gets compared against.
        reported = obj.InstanceCount;

        (double X, double Y)? player = Player.Available ? Player.Position : null;

        var built = new List<Enemy>(Math.Max(0, reported));
        foreach (var r in obj.Instances())
        {
            if (r.Id.IsUndefined || !r.Exists) continue;
            built.Add(Describe(r, player));
        }

        // Nearest first: the one you care about is the one about to hit you.
        // Rows with no known distance sink to the bottom rather than sorting as zero.
        return built.OrderBy(e => e.Dist ?? double.MaxValue).ToList();
    }

    // Field names taken from a live o_player dump: o_player and o_enemy are both
    // units, so they share the unit-level fields. Every one is probed rather than
    // assumed - a missing field leaves the column blank instead of failing the
    // row, which is what makes this survive a patch that renames one of them.
    private static Enemy Describe(InstanceRef r, (double X, double Y)? player)
    {
        string race = Text(r, "race");
        string type = Text(r, "type");
        string name = Text(r, "name");
        if (name.Length == 0) name = race;
        if (name.Length == 0) name = type;
        if (name.Length == 0) name = "(unnamed)";

        double? x = Number(r, "x"), y = Number(r, "y");
        double? dist = null;
        if (x is { } ex && y is { } ey && player is { } p)
            dist = Math.Sqrt((ex - p.X) * (ex - p.X) + (ey - p.Y) * (ey - p.Y));

        return new Enemy(r, Key(r.Id), name, race, type,
            Number(r, "HP"), Number(r, "max_hp") ?? Number(r, "bmax_hp"), Number(r, "LVL"),
            x, y, dist);
    }

    // A stable identity for UI ids and the selection: the id's bits and kind
    // are the same on every refresh for as long as the instance lives.
    private static string Key(RValue id) => $"{(int)id.Kind}:{id.Int64:x}";

    private static double? Number(InstanceRef r, string name)
    {
        var v = r.Get(name);
        return v.IsNumber ? v.AsReal : null;
    }

    // Converted at once: a string from the game is pooled and gone at the end
    // of the frame, and the roster lives across many.
    private static string Text(InstanceRef r, string name)
    {
        var v = r.Get(name);
        return v.Kind == RValueKind.String ? v.ToString() : "";
    }

    private bool Matches(Enemy e)
    {
        if (_filter.Length == 0) return true;
        return e.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
               e.Race.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
               e.Type.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string RowText(Enemy e)
    {
        string hp = e.Hp is not { } h ? "hp ?"
                  : e.MaxHp is { } m && m > 0 ? $"{h:0}/{m:0} hp"
                  : $"{h:0} hp";
        string lvl = e.Level is { } l && l >= 0 ? $"  lvl {l:0}" : "";
        string dist = e.Dist is { } d ? $"  {d:0} px" : "";
        return $"{e.Name,-22} {hp}{lvl}{dist}";
    }

    private static string Format(RValue v)
    {
        string s = v.Kind == RValueKind.String ? $"\"{v}\"" : v.ToString();
        return s.Length > 160 ? s[..160] + "..." : s;
    }

    // ------------------------------------------------------------- actions

    private void Remove(Enemy e)
    {
        Actions.Run($"instance_destroy {e.Name} ({e.Key})", () => Destroy(e));
        if (e.Key == _selectedKey) _selectedKey = null;
        Refresh();
    }

    /// <summary>Destroys the enemy; returns whether it is really gone.</summary>
    public static bool Destroy(Enemy e)
    {
        if (!e.Ref.Exists) throw new InvalidOperationException($"{e.Name} is already gone");

        // The reference form: destroys the instance named by the argument.
        // The player is passed as self because builtins are handed one even
        // when they ignore it, and a null self is not worth the risk - so
        // with no player there is no remove (Require throws "no player").
        Game.CallBuiltinAs(Player.Require(), "instance_destroy", e.Ref.Id);

        // instance_exists skips an instance that has been destroyed, so a
        // survivor here means the call was accepted but did nothing.
        if (!e.Ref.Exists) return true;
        Actions.Report($"{e.Name} still exists after instance_destroy", ok: false);
        return false;
    }

    private void LogVars(Enemy e)
    {
        var log = Actions.Log;
        log.Info($"=== {e.Name} ({e.Key}) ===");
        int shown = Math.Min(_probeNames.Count, LogLimit);
        for (int i = 0; i < shown; i++)
            log.Info($"    {_probeNames[i]} = {Format(e.Ref.Get(_probeNames[i]))}");
        log.Info($"    {_probeNames.Count} instance variables in total");
    }
}
