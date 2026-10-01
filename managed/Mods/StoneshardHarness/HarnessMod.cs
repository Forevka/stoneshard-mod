using System.Text.Json;
using CoreLoader;
using StoneShard;

[assembly: CoreModInfo(typeof(StoneshardHarness.HarnessMod), "Stoneshard Harness", "0.1.0", "Lodestone")]
[assembly: CoreModGame("StoneShard")]

namespace StoneshardHarness;

/// <summary>
/// Lets a script or an agent play Stoneshard over the test host: hx.* commands
/// describe what the player sees (the room, the player, enemies, things to act
/// on, the bag, the action log, a conversation) as JSON with desktop-pixel
/// positions, and act through the game's own scripts, with a real click or key
/// only as the fallback. Does nothing unless the game runs with the test host
/// on (CORELOADER_TEST=1).
/// </summary>
public sealed class HarnessMod : CoreMod
{
    private readonly Scheduler _at = new();
    private Act _act = null!;

    // How far hx.objects looks, in tiles, when not told.
    private const int DefaultReach = 12;

    public override void OnInitialize()
    {
        if (!TestHost.Enabled)
        {
            Log.Info("idle: the harness only runs with the test host on (CORELOADER_TEST=1)");
            return;
        }
        _act = new Act(_at, Log);
        // After, not before: a mod may point the change elsewhere in its own
        // Before hook (StoneshardTrials sends the tavern door into a dungeon),
        // and the argument the original ran with is the room that comes.
        Scripts.scr_smoothRoomChange.After(OnRoomChange);
        Scripts.scr_mouse_on_unit.Before(OnMouseOnUnit);
        // Actions advance in the game's own step, not at Present: a turn skip
        // sent from the frame's end was found to be dropped now and then.
        Objects.o_controller.Step_0.After(_ => TickActions());
        Commands();
        Log.Info("ready: hx.* commands are on the test host (list-commands)");
    }

    public override void OnUpdate()
    {
        if (!TestHost.Enabled) return;
        _at.Tick(Log);
        // Menus and the title screen have no o_controller to step: actions
        // there (a button, a key) advance from the frame instead.
        if (Objects.o_controller.First != null) return;
        TickActions();
        // Back at the menus the tracked room is over: a save loaded from
        // there need not pass through scr_smoothRoomChange.
        try
        {
            if (Gm.Player == null && World.TrackedRoom >= 0) World.TrackedRoom = -1;
        }
        catch (GmlException) { }
    }

    // A throw from a hook faults the whole mod, so this is the harness's fault
    // barrier: a failed tick is logged and counted, and Act ends an action
    // whose ticks keep failing.
    private void TickActions()
    {
        try { _act.Tick(); }
        catch (Exception ex) { _act.TickFailed(ex); }
    }

    // An unload or hot reload between a press and its release must not leave
    // the mouse button or a key held down.
    public override void OnShutdown()
    {
        _at.Clear();
        if (TestHost.Enabled) Pointer.ReleaseAll();
    }

    // While the harness runs a unit's click event, the unit "is under the mouse".
    // The answer is the unit's own id value, read now: the game compares it
    // with instance references.
    private static void OnMouseOnUnit(HookCall c)
    {
        if (Act.Pointing < 0) return;
        try
        {
            if (Gm.ById(Act.Pointing) is not { } unit) return;
            c.SkipOriginal();
            c.Result = unit.Get("id");
        }
        catch (GmlException) { }
    }

    private void OnRoomChange(HookCall c)
    {
        // A Before hook that refused the change leaves the room as it was.
        if (c.ArgCount < 1 || c.OriginalSkipped) return;
        try
        {
            var room = c.GetArg(0);
            if (room.IsNumber && room.AsReal >= 0) World.TrackedRoom = (int)room.AsReal;
            _act.RoomChanging();
        }
        catch (GmlException ex) { Log.Warning($"room tracking failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------ commands

    private void Commands()
    {
        TestHost.Register("hx.state", _ => State(),
            "hx.state: one-call summary {room, cell, floor, dungeon, turn, playerTurn, menus, player {hp, mp, level, xp, cell, screen}, gold, enemiesVisible, busy}");
        TestHost.Register("hx.player", _ => PlayerView(),
            "hx.player: position (room, grid, screen), vitals, equipped items, and possible actions (free moves, hotbar, menus)");
        TestHost.Register("hx.enemies", a => Enemies(Flag(a, 0, "all")),
            "hx.enemies [all]: hostile or potentially hostile units the player can see (all: every live one), nearest first, with what the player can do to each");
        TestHost.Register("hx.npcs", a => Npcs(Flag(a, 0, "all")),
            "hx.npcs [all]: people and animals the player can see (all: every one), nearest first");
        TestHost.Register("hx.objects", a => Objects_(a),
            "hx.objects [reach=12] [all]: doors, containers, items, NPCs, corpses and other interactive things in view (all: in the fog too), nearest first");
        TestHost.Register("hx.inventory", _ => Inventory(),
            "hx.inventory: the bag and the worn items {open, items [{id, name, kind, equipped, slot, stack, charges, screen}]}");
        TestHost.Register("hx.actions", a => _act.Menu(Long(a, 0, "id")),
            "hx.actions <id>: reads an item's or an object's context menu (opens and closes it); poll hx.result: extra.actions [{action, label}]");
        TestHost.Register("hx.log", a => ActionLog.Read().TakeLast(a.Count > 0 ? (int)Num(a, 0, "n") : 10).Select(l => l.Text).ToList(),
            "hx.log [n=10]: the last n lines of the action log (bottom left), oldest first");
        TestHost.Register("hx.dialogue", _ => Dialogue.Describe() ?? new { open = false },
            "hx.dialogue: the open conversation {speaker, text, options [{n, key, text, enabled, screen}]}");
        TestHost.Register("hx.screen", a => ScreenOf(a),
            "hx.screen <gx> <gy> | room <x> <y> | at <sx> <sy>: a grid cell or room position as desktop pixels, or desktop pixels as room and grid");

        TestHost.Register("hx.move", a => _act.Move((int)Num(a, 0, "dx"), (int)Num(a, 1, "dy")),
            "hx.move <dx> <dy>: one step (-1..1 each), as a click on that tile; answers {seq}, then poll hx.result");
        TestHost.Register("hx.goto", a => _act.GoTo((int)Num(a, 0, "gx"), (int)Num(a, 1, "gy")),
            "hx.goto <gx> <gy>: walks to a grid cell, pathfinding as a click does; poll hx.result");
        TestHost.Register("hx.attack", a => _act.Attack(Long(a, 0, "enemy id"), a.Count > 1 ? (int)Num(a, 1, "turns") : 1),
            "hx.attack <enemyId> [turns=1]: clicks the enemy once per turn - a step towards it, a swing or a shot, as the game's own click decides - until it dies or the turns run out; poll hx.result");
        TestHost.Register("hx.interact", a => _act.Interact(Long(a, 0, "id"), a.Count > 1 ? Text(a, 1, "action") : null),
            "hx.interact <id> [action]: the default action (talk, open, enter, pick up), as a click does; or a context-menu action by name (hx.actions lists them); poll hx.result");
        TestHost.Register("hx.use", a => _act.Use(Long(a, 0, "item id"), a.Count > 1 ? Text(a, 1, "action") : null),
            "hx.use <itemId> [action]: an item's context-menu action (default: its first, e.g. Use/Eat/Equip); poll hx.result");
        TestHost.Register("hx.wait", a => _act.Wait(a.Count > 0 ? (int)Num(a, 0, "turns") : 1),
            "hx.wait [turns=1]: skips turns (scr_skip_turn); poll hx.result");
        TestHost.Register("hx.say", a => _act.Say(Text(a, 0, "option")),
            "hx.say <n|key|text>: picks a conversation option (the option button's click event); poll hx.result, then hx.dialogue");
        TestHost.Register("hx.buttons", _ => Gui.Buttons().Select(Gui.Describe).ToList(),
            "hx.buttons: the window buttons on screen (CONFIRM, CANCEL...) [{id, text, enabled, screen}]");
        TestHost.Register("hx.press", a => _act.Raw("press", Text(a, 0, "button"), () => new { pressed = Gui.Press(Text(a, 0, "button")) }, 4),
            "hx.press <id|text>: presses a window button (its User Event 0, as its click does); poll hx.result");
        TestHost.Register("hx.click", a => _act.Raw("click", $"{Num(a, 0, "x")},{Num(a, 1, "y")}",
                () => Pointer.Click(_at, (int)Num(a, 0, "x"), (int)Num(a, 1, "y"), Flag(a, 2, "right")), 12),
            "hx.click <sx> <sy> [right]: a real mouse click at desktop pixels (the fallback; refused unless the game is in the foreground); poll hx.result");
        TestHost.Register("hx.key", a => _act.Raw("key", Text(a, 0, "key"), () => Pointer.Key(_at, Text(a, 0, "key")), 2),
            "hx.key <key>: presses a key through keyboard_key_press (i, esc, space, 1-9, f1...); no focus needed; poll hx.result");
        TestHost.Register("hx.result", a => _act.Result(a.Count > 0 ? (int)Num(a, 0, "seq") : null),
            "hx.result [seq]: how an action went {done, turns, player {hp, mp, from, to}, target {hpBefore, hpAfter, dead}, log, menus} (done:false while it runs)");
        TestHost.Register("hx.cancel", _ => _act.Cancel(), "hx.cancel: stops tracking the running action");
        TestHost.Register("hx.close", _ => CloseAll(), "hx.close: closes context menus, then presses Esc once (closes the top window)");
    }

    // ------------------------------------------------------------ views

    private object State()
    {
        var p = Gm.Player;
        var cell = World.Cell;
        object? player = null;
        int enemies = 0;
        if (p is { } me)
        {
            var at = Grid.CellOf(me);
            var (sx, sy) = Screen.FromRoom(Grid.Centre(at.X, at.Y).X, Grid.Centre(at.X, at.Y).Y);
            player = new
            {
                name = Gm.Str(me, "name"),
                hp = Units.Hp(me), maxHp = Gm.Num(me, "max_hp"),
                mp = Units.Atr(me, "MP"), maxMp = Gm.Num(me, "max_mp"),
                level = Units.Atr(me, "LVL"), xp = Units.Atr(me, "XP"), maxXp = Gm.NumOrNull(me, "max_xp"),
                x = Gm.Round(Gm.Num(me, "x")), y = Gm.Round(Gm.Num(me, "y")), gx = at.X, gy = at.Y, sx, sy,
            };
            enemies = Units.Enemies(all: false).Count;
        }
        return new
        {
            room = World.RoomName,
            cell = new { x = cell.X, y = cell.Y },
            floor = World.Floor,
            dungeon = World.InDungeon,
            turn = Act.Turn(),
            playerTurn = p is { } t && Gm.Flag(t, "turn_available"),
            menus = World.OpenMenus(),
            player,
            gold = Units.Gold(),
            enemiesVisible = enemies,
            busy = _act.Busy,
        };
    }

    private static object PlayerView()
    {
        var me = Gm.RequirePlayer();
        var at = Grid.CellOf(me);
        var moves = Grid.Directions.Select(d =>
        {
            int gx = at.X + d.Dx, gy = at.Y + d.Dy;
            string? blocked = Grid.Blocked(gx, gy);
            long who = blocked == "unit" ? Grid.Occupant(gx, gy) : -1;
            return new { dir = d.Name, dx = d.Dx, dy = d.Dy, gx, gy, free = blocked == null, blocked, who = who >= 0 ? who : (long?)null };
        }).ToList();
        var weapon = Units.Wielded(me);
        return new
        {
            id = Gm.Id(me),
            name = Gm.Str(me, "name"),
            obj = Gm.ObjectName(me),
            pos = Units.Pos(me),
            turn = Gm.Flag(me, "turn_available"),
            state = Gm.Str(me, "state"),
            vitals = new
            {
                hp = Units.Hp(me), maxHp = Gm.Num(me, "max_hp"),
                mp = Units.Atr(me, "MP"), maxMp = Gm.Num(me, "max_mp"),
                level = Units.Atr(me, "LVL"), xp = Units.Atr(me, "XP"),
                hunger = Units.Atr(me, "Hunger"), thirst = Units.Atr(me, "Thirsty"), fatigue = Units.Atr(me, "Fatigue"),
                pain = Units.Atr(me, "Pain"), sanity = Units.Atr(me, "Sanity"), morale = Units.Atr(me, "Morale"),
                intoxication = Units.Atr(me, "Intoxication"), immunity = Units.Atr(me, "Immunity"),
                vision = Gm.NumOrNull(me, "VSN"),
            },
            weapon = new { name = weapon.Name, ranged = weapon.Ranged, range = weapon.Range },
            equipped = Bag.Items().Where(i => Gm.Flag(i, "equipped")).Select(i => Bag.Describe(i, withScreen: false)).ToList(),
            hotbar = Hotbar.Read(),
            moves,
            actions = new[] { "hx.move <dx> <dy>", "hx.goto <gx> <gy>", "hx.wait [turns]", "hx.key i (inventory)", "hx.attack <id>", "hx.interact <id> [action]", "hx.use <itemId> [action]" },
        };
    }

    private static object Enemies(bool all)
    {
        var me = Gm.RequirePlayer();
        var weapon = Units.Wielded(me);
        var mine = Grid.CellOf(me);
        return Units.Enemies(all)
            .OrderBy(e => Gm.Tiles(mine, Grid.CellOf(e)))
            .Select(e => Units.DescribeEnemy(e, me, weapon))
            .ToList();
    }

    private static object Npcs(bool all)
    {
        var me = Gm.RequirePlayer();
        var mine = Grid.CellOf(me);
        return Units.Npcs(all)
            .Select(n => (n, d: Gm.Tiles(mine, Grid.CellOf(n))))
            .OrderBy(t => t.d)
            .Take(40)
            .Select(t => new
            {
                id = Gm.Id(t.n), obj = Gm.ObjectName(t.n), name = Units.DisplayName(t.n), dist = t.d,
                hostile = Units.IsHostile(t.n), visible = Units.Visible(t.n), pos = Units.Pos(t.n),
            })
            .ToList();
    }

    private static object Objects_(IReadOnlyList<JsonElement> a)
    {
        int reach = DefaultReach;
        bool all = false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].ValueKind == JsonValueKind.Number) reach = Math.Clamp((int)a[i].GetDouble(), 1, 60);
            else if (a[i].ValueKind == JsonValueKind.String && a[i].GetString() == "all") all = true;
            else throw new ArgumentException($"unknown option {a[i].GetRawText()} (a reach in tiles, or all)");
        }
        var me = Gm.RequirePlayer();
        return Things.Around(me, reach, all).Select(t => Things.Describe(t.Thing, t.Kind, t.Dist)).ToList();
    }

    private static object Inventory()
    {
        bool open = Bag.IsOpen;
        return new
        {
            open,
            gold = Units.Gold(),
            items = Bag.Items()
                .OrderByDescending(i => Gm.Flag(i, "equipped"))
                .Select(i => Bag.Describe(i, withScreen: open))
                .ToList(),
            // An open trade, loot, reward or stash window and what it holds.
            windows = Bag.Windows().Select(w => new
            {
                kind = w.Kind,
                id = Gm.Id(w.Owner),
                items = Bag.Owned(Gm.Id(w.Owner)).Select(i => Bag.Describe(i, withScreen: true)).ToList(),
            }).ToList(),
        };
    }

    private static object ScreenOf(IReadOnlyList<JsonElement> a)
    {
        if (a.Count >= 3 && a[0].ValueKind == JsonValueKind.String)
        {
            string mode = a[0].GetString()!;
            double x = Num(a, 1, "x"), y = Num(a, 2, "y");
            if (mode == "room")
            {
                var (sx, sy) = Screen.FromRoom(x, y);
                return new { sx, sy, onScreen = Screen.OnScreen((sx, sy)) };
            }
            if (mode == "at")
            {
                var (rx, ry) = Screen.ToRoom((int)x, (int)y);
                var cell = Grid.CellOf(rx, ry);
                return new { x = Gm.Round(rx), y = Gm.Round(ry), gx = cell.X, gy = cell.Y, blocked = Grid.Blocked(cell.X, cell.Y) };
            }
            throw new ArgumentException($"unknown mode '{mode}' (room, at)");
        }
        int gx = (int)Num(a, 0, "gx"), gy = (int)Num(a, 1, "gy");
        var (cx, cy) = Grid.Centre(gx, gy);
        var p = Screen.FromRoom(cx, cy);
        return new { gx, gy, x = cx, y = cy, sx = p.X, sy = p.Y, onScreen = Screen.OnScreen(p), blocked = Grid.Blocked(gx, gy) };
    }

    private object CloseAll()
    {
        int menus = Gm.Count(Objects.o_context_button.Name);
        Bag.CloseMenus();
        if (menus > 0) return new { closed = "context-menu" };
        // With nothing open, Esc would open the pause menu instead.
        if (World.OpenMenus().Count == 0) return new { closed = "nothing was open" };
        Pointer.Key(_at, "esc");
        return new { closed = "esc" };
    }

    // ------------------------------------------------------------ arguments

    private static JsonElement Arg(IReadOnlyList<JsonElement> a, int i, string what) =>
        i < a.Count ? a[i] : throw new ArgumentException($"missing argument {i + 1}: {what}");

    private static double Num(IReadOnlyList<JsonElement> a, int i, string what)
    {
        var v = Arg(a, i, what);
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
        throw new ArgumentException($"{what} must be a number, not {v.GetRawText()}");
    }

    private static long Long(IReadOnlyList<JsonElement> a, int i, string what) => (long)Num(a, i, what);

    private static string Text(IReadOnlyList<JsonElement> a, int i, string what)
    {
        var v = Arg(a, i, what);
        return v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
    }

    private static bool Flag(IReadOnlyList<JsonElement> a, int i, string name) =>
        i < a.Count && (a[i].ValueKind == JsonValueKind.True ||
                        (a[i].ValueKind == JsonValueKind.String && string.Equals(a[i].GetString(), name, StringComparison.OrdinalIgnoreCase)));
}
