using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// Runs the player's actions through the game's own scripts and reports what
/// each one did.
/// </summary>
/// <remarks>
/// An action takes frames: the player walks, swings, the other units take
/// their turns. A test command cannot wait, so starting one answers its number
/// at once, and hx.result answers how it went once the game has settled: the
/// player's turn is back, nobody is moving, for a few frames in a row. Only one
/// action runs at a time; starting another while one runs is refused.
///
/// The scripts, as a real click runs them (watched with ScriptSpy):
///   * a floor click calls scr_player_move(x, y) with the cell's room
///     coordinates; run as the player it walks there, pathfinding as a click does;
///   * a click on a unit or an object calls scr_player_move(its x, its y) as
///     that instance: the player walks up to it and then talks, attacks, opens
///     or enters as the game decides for that thing;
///   * scr_skip_turn as the player passes a turn.
/// </remarks>
internal sealed class Act
{
    private sealed class Pending
    {
        public required int Seq;
        public required string Kind;
        public required string What;
        public long Start;
        // Where settling is counted from: the start, or the frame the last step finished.
        public long SettleFrom;
        public int MinFrames = 6;
        public int MaxFrames = 60 * 20;
        public int Settled;
        public int WaitsLeft;
        public int WaitsDone;
        public double TurnAtSkip;
        public long SkippedAt;
        public bool SkipPending;
        public int SkipRetries;
        public int Absent;
        public int Failures;
        // When the room last started changing during this action (-1: it has not).
        public long RoomChangeAt = -1;
        // A door or the area's edge was used: the result says when it led nowhere.
        public bool Door;
        public long PlayerId = -1;
        public double Hp, Mp;
        public double? Gold;
        public (int X, int Y) Cell;
        public double Turn;
        public long TargetId = -1;
        public string? TargetName;
        public double? TargetHp;
        public Dictionary<long, string> LogSeen = [];
        public string? Note;
        public string? Error;
        public object? Extra;
        // Work that has to span frames, run in order before the action can settle; each step answers whether it is done.
        public readonly Queue<Func<Pending, bool>> Steps = new();
    }

    private readonly Scheduler _at;
    private readonly Logger _log;
    private Pending? _current;
    private int _seq;
    private readonly Dictionary<int, object> _results = [];

    public Act(Scheduler at, Logger log)
    {
        _at = at;
        _log = log;
    }

    public bool Busy => _current != null;

    // ------------------------------------------------------------ starting

    // Conversations, keys and clicks also work where there is no player yet
    // (the intro, menus): such an action settles on frames alone.
    private Pending Begin(string kind, string what, InstanceRef? target = null, bool needsPlayer = true)
    {
        if (_current is { } running)
            throw new InvalidOperationException($"action {running.Seq} ({running.Kind}) is still running; poll hx.result {running.Seq}");
        var player = needsPlayer ? Gm.RequirePlayer() : Gm.Player;
        var p = new Pending
        {
            Seq = _seq + 1,
            Kind = kind,
            What = what,
            Start = _at.Frame,
            SettleFrom = _at.Frame,
            Gold = Units.Gold(),
            Turn = Turn(),
            LogSeen = ActionLog.Snapshot(),
        };
        if (player is { } me)
        {
            p.PlayerId = Gm.Id(me);
            p.Hp = Units.Hp(me);
            p.Mp = Units.Atr(me, "MP");
            p.Cell = Grid.CellOf(me);
        }
        if (target is { } t)
        {
            p.TargetId = Gm.Id(t);
            p.TargetName = Units.DisplayName(t);
            p.TargetHp = Gm.NumOrNull(t, "HP");
        }
        return p;
    }

    // The number is taken only once the action has really started, so a start
    // that failed leaves no gap for hx.result to stumble on.
    private object Started(Pending p)
    {
        _seq = p.Seq;
        _current = p;
        return new { seq = p.Seq, kind = p.Kind, what = p.What, poll = $"hx.result {p.Seq}", extra = p.Extra };
    }

    /// <summary>The game's turn counter (o_controller.turns), for counting the turns an action took.</summary>
    public static double Turn() => Objects.o_controller.First is { } c ? Gm.Num(c, "turns", -1) : -1;

    private static void Walk(InstanceRef self, double x, double y) => Scripts.scr_player_move.CallAs(self, x, y);

    /// <summary>
    /// The unit the harness is clicking, while its click event runs: what
    /// scr_mouse_on_unit answers then (HarnessMod hooks it), by id; -1 otherwise.
    /// </summary>
    public static long Pointing { get; private set; } = -1;

    private static bool ClickUnit(InstanceRef unit)
    {
        Pointing = Gm.Id(unit);
        try { return Gm.RunEvent(unit, "Mouse_4"); }
        finally { Pointing = -1; }
    }

    // A left click on a unit runs its Left Pressed event (Mouse_4, e.g.
    // o_NPC_Mouse_4, o_enemy_Mouse_4), which picks the tile to walk to - an
    // innkeeper is talked to across his counter - and calls scr_player_move as
    // the unit. Running that event is the click without the mouse. An enemy's
    // click first asks scr_mouse_on_unit who is under the mouse; while the
    // harness runs it, that answers the target (see Pointing), and the game's
    // own combat code then steps towards it, swings, or shoots.
    //
    // A click on an object is the object's Mouse_4 plus the floor cursor's
    // own click (o_floor_target, whose click event reads the real mouse, so it
    // cannot be run as it is). Watched on a real door click, the cursor's part
    // is: target_id = the object, then scr_player_move(the tile beside it) run
    // as the cursor; the game walks the player there and, arriving, runs the
    // object's User Event 0 - for a door its way out (alarm 7 changes room).
    // Those are the calls made here; the rest is the game's.
    private void Click(Pending p, InstanceRef target)
    {
        if (Gm.IsA(target, Objects.o_enemy.Name))
        {
            if (ClickUnit(target)) return;
            p.Note = "it has no click event: walked to it with scr_player_move";
            Walk(target, Gm.Num(target, "x"), Gm.Num(target, "y"));
            return;
        }

        var player = Gm.RequirePlayer();
        var at = Grid.CellOf(target);
        var me = Grid.CellOf(player);
        // The cursor's way is only proven for doors and containers (a barrel
        // opened the same way): its user event reads variables they have, and
        // on a corpse it stopped the game with a GML error. Anything else is
        // only walked up to.
        if (Gm.IsA(target, Objects.o_loot.Name))
        {
            PickUp(p, target, at);
            return;
        }
        if (!Gm.IsA(target, Objects.o_transitions_door.Name) && !Gm.IsA(target, "c_container"))
        {
            UseOnArrival(p, target, at);
            return;
        }
        var cursor = Gm.Exact("o_floor_target") ?? throw new InvalidOperationException("no floor cursor (o_floor_target) in this room");
        // A door's way out starts in its alarm after the player has arrived:
        // give it time to begin, so the action ends in the next room (and a
        // container's window time to open).
        p.MinFrames = 90;
        p.Door = Gm.IsA(target, Objects.o_transitions_door.Name);
        cursor.Set("target_id", target.Get("id"));
        Gm.RunEvent(target, "Mouse_4", cursor);
        // The object is used on arriving, so a player already beside it steps
        // to another free tile beside it (a real click from there does nothing).
        var stand = Grid.Beside(at, me) ?? at;
        if (stand == me && Grid.Beside(at, me, avoid: me) is { } other) stand = other;
        else if (stand == me) p.Note = "already beside it with no other tile to arrive from: the game may not use it";
        var (x, y) = Grid.Centre(stand.X, stand.Y);
        Walk(cursor, x, y);
    }

    // Something on the ground (an o_loot child: drops, forage, sticks) is
    // picked up in a chain the click sets up: the item's User Event 3
    // (Other_13) calls scr_take_loot, which runs the floor cursor's User Event 0
    // with state from the click. Replaying the events without it stopped the
    // game with a GML error (object_is_ancestor on an unset value in
    // scr_guiCreateInteractive), so this is the one action that falls back to
    // the mouse: walk next to it by script, then a real click on it, which
    // needs the game in the foreground.
    private void PickUp(Pending p, InstanceRef item, (int X, int Y) at)
    {
        WalkBeside(p, at);
        p.Steps.Enqueue(_ =>
        {
            if (!item.Exists) throw new InvalidOperationException("it is gone");
            var (sx, sy) = Units.ClickPoint(item);
            p.Extra = Pointer.Click(_at, sx, sy, right: false);
            p.Note = "picked up with a mouse click (the fallback): the game's pick-up cannot be run without one";
            p.MinFrames = 30;
            return true;
        });
    }

    private void WalkBeside(Pending p, (int X, int Y) at)
    {
        long walkedAt = 0;
        p.Steps.Enqueue(_ =>
        {
            var player = Gm.RequirePlayer();
            var me = Grid.CellOf(player);
            var cell = Grid.Beside(at, me) ?? throw new InvalidOperationException("no free tile beside it to stand on");
            if (cell != me)
            {
                var (x, y) = Grid.Centre(cell.X, cell.Y);
                Walk(player, x, y);
            }
            walkedAt = _at.Frame;
            return true;
        });
        int settled = 0;
        p.Steps.Enqueue(_ =>
        {
            var player = Gm.RequirePlayer();
            settled = Ready(player) && _at.Frame - walkedAt > 3 ? settled + 1 : 0;
            if (settled < 3) return false;
            if (Gm.Tiles(Grid.CellOf(player), at) > 1)
                throw new InvalidOperationException($"could not get next to it (stopped at {Grid.CellOf(player)}, it is at {at})");
            return true;
        });
    }

    // Walks next to the object (if not there already), waits to arrive, then
    // runs its User Event 0 (Other_10), or its click (Mouse_4) when it has none,
    // with the player as other.
    private void UseOnArrival(Pending p, InstanceRef target, (int X, int Y) at)
    {
        WalkBeside(p, at);
        // Running an object's events by guess can stop the game with a GML
        // error (it happened twice while building this), so only the kinds
        // whose click was watched and replayed are used; anything else is only
        // walked up to.
        p.Note = $"walked up to it; using a {Gm.ObjectName(target)} is not scripted - hx.actions {Gm.Id(target)} lists its menu, hx.click its screen position is the fallback";
    }

    public object Move(int dx, int dy)
    {
        if (dx is < -1 or > 1 || dy is < -1 or > 1 || (dx == 0 && dy == 0)) throw new ArgumentException("a step is -1, 0 or 1 on each axis, not both 0");
        var player = Gm.RequirePlayer();
        var at = Grid.CellOf(player);
        return GoTo(at.X + dx, at.Y + dy, "move");
    }

    public object GoTo(int gx, int gy, string kind = "goto")
    {
        var player = Gm.RequirePlayer();
        string? blocked = Grid.Blocked(gx, gy);
        if (blocked is "wall" or "edge") throw new InvalidOperationException($"cell {gx},{gy} is blocked ({blocked})");
        var p = Begin(kind, $"to {gx},{gy}");
        if (blocked == "unit" && (gx, gy) != Grid.CellOf(player)) p.Note = "someone stands there: the game will act on them (attack or talk), as a click would";
        var (x, y) = Grid.Centre(gx, gy);
        Walk(player, x, y);
        return Started(p);
    }

    /// <summary>
    /// Clicks the enemy as many times as asked, one turn each (a step towards
    /// it, a swing or a shot, as the game decides), stopping early when it dies.
    /// </summary>
    public object Attack(long id, int clicks)
    {
        if (clicks is < 1 or > 200) throw new ArgumentException("attack 1 to 200 times");
        var enemy = Gm.RequireById(id, "unit");
        if (!Gm.IsA(enemy, Objects.o_enemy.Name)) throw new ArgumentException($"{id} is a {Gm.ObjectName(enemy)}, not an o_enemy");
        var p = Begin("attack", clicks > 1 ? $"{Units.DisplayName(enemy)} (up to {clicks} turns)" : Units.DisplayName(enemy), enemy);
        p.MaxFrames = 60 * 20 * clicks;
        int done = 0, settled = 0, retries = 0;
        double turn = Turn();
        p.Steps.Enqueue(_ =>
        {
            if (Gm.ById(id) is not { } live || Gm.Num(live, "HP", 1) <= 0) return true;
            if (done >= clicks) return true;
            var player = Gm.RequirePlayer();
            // Repeating is for finishing a fight, not for dying in one: below a
            // quarter of the player's health it stops and leaves the call to the caller.
            if (done > 0 && Units.HealthFraction(player) < LowHealth)
            {
                p.Note = $"stopped after {done} turn(s): the player's health is below {LowHealth:P0}";
                return true;
            }
            // The next click once the last turn has been played out.
            settled = Ready(player) ? settled + 1 : 0;
            // A click sent while the last turn is still being shown can be
            // ignored by the game: after a second with no turn taken it is
            // sent again, a few times, before giving up.
            bool ignored = done > 0 && settled >= 60 && Turn() == turn;
            if (ignored && ++retries > 3)
            {
                p.Note = $"click {done} took no turn (out of reach, or nothing to do): stopped";
                return true;
            }
            if (done > 0 && !ignored && (settled < 3 || Turn() == turn)) return false;
            turn = Turn();
            settled = 0;
            if (!ignored) { done++; retries = 0; }
            if (!ClickUnit(live)) throw new InvalidOperationException($"{Units.DisplayName(live)} has no click event");
            p.Extra = new { clicks = done };
            return false;
        });
        return Started(p);
    }

    public object Interact(long id, string? action)
    {
        var thing = Gm.RequireById(id, "instance");
        var p = Begin("interact", $"{Units.DisplayName(thing)}{(action != null ? " / " + action : "")}", thing);
        if (action == null || action.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            Click(p, thing);
        }
        else
        {
            ThroughMenu(p, thing, action);
        }
        return Started(p);
    }

    public object Use(long id, string? action)
    {
        var item = Gm.RequireById(id, "item");
        if (!Gm.IsA(item, Objects.o_inv_slot.Name)) throw new ArgumentException($"{id} is a {Gm.ObjectName(item)}, not an item");
        var p = Begin("use", $"{Bag.Name(item)}{(action != null ? " / " + action : "")}");
        ThroughMenu(p, item, action);
        return Started(p);
    }

    /// <summary>Reads an instance's context menu (opening and closing it); the actions are in the result's extra.</summary>
    public object Menu(long id)
    {
        var thing = Gm.RequireById(id, "instance");
        var p = Begin("menu", Units.DisplayName(thing));
        p.MinFrames = 2;
        OpenMenuSteps(p, thing, buttons =>
        {
            p.Extra = new { actions = Bag.DescribeMenu(buttons) };
            Bag.CloseMenus();
        });
        return Started(p);
    }

    // The context menu's buttons appear a frame or two after the right-click
    // event that asks for them, so pressing one is three steps: open, wait for
    // the buttons, press. No action given presses the first (Use, Equip...).
    private void ThroughMenu(Pending p, InstanceRef target, string? action) =>
        OpenMenuSteps(p, target, buttons =>
        {
            string pick = action ?? Gm.Str(buttons[0], "func");
            p.Extra = new { pressed = Bag.Press(buttons, pick), offered = Bag.DescribeMenu(buttons) };
        });

    private const int MenuWaitFrames = 30;
    private const int AbsentFrames = 300;
    private const double LowHealth = 0.25;
    private const int RoomChangeFrames = 45;

    /// <summary>From the room-change hook: the running action now waits for the new room.</summary>
    public void RoomChanging()
    {
        if (_current is { } p) p.RoomChangeAt = _at.Frame;
    }

    private void OpenMenuSteps(Pending p, InstanceRef target, Action<List<InstanceRef>> then)
    {
        long id = Gm.Id(target);
        long asked = 0;
        p.Steps.Enqueue(_ =>
        {
            Bag.CloseMenus();
            if (!Gm.RunEvent(target, "Mouse_5")) throw new InvalidOperationException($"{Units.DisplayName(target)} has no context menu");
            asked = _at.Frame;
            return true;
        });
        p.Steps.Enqueue(_ =>
        {
            var buttons = Bag.MenuButtons(id);
            if (buttons.Count > 0)
            {
                then(buttons);
                return true;
            }
            if (_at.Frame - asked > MenuWaitFrames) throw new InvalidOperationException($"{Units.DisplayName(target)} opened no context menu (it may need to be in reach, or the inventory open)");
            return false;
        });
    }

    public object Wait(int turns)
    {
        if (turns is < 1 or > 100) throw new ArgumentException("wait 1 to 100 turns");
        var p = Begin("wait", $"{turns} turn(s)");
        p.WaitsLeft = turns;
        p.MaxFrames = 60 * 10 * turns;
        return Started(p);
    }

    public object Say(string which)
    {
        var p = Begin("say", which, needsPlayer: false);
        p.Extra = new { chose = Dialogue.Choose(which) };
        p.MinFrames = 4;
        return Started(p);
    }

    /// <summary>Wraps a fallback (a click or a key) so its effects are reported like any action's.</summary>
    public object Raw(string kind, string what, Func<object> run, int frames)
    {
        var p = Begin(kind, what, needsPlayer: false);
        p.Extra = run();
        p.MinFrames = frames + 4;
        return Started(p);
    }

    // ------------------------------------------------------------ settling

    public void Tick()
    {
        if (_current is not { } p) return;
        long frames = _at.Frame - p.Start;

        // A room change replaces the player for a moment; one missing for
        // seconds has died (or the game went back to its menu).
        p.Absent = Gm.Player == null && p.PlayerId >= 0 ? p.Absent + 1 : 0;
        if (p.Absent > AbsentFrames)
        {
            p.Note = World.OpenMenus().Contains("dead") ? "the player died" : "the player is gone (dead, or back at the menu)";
            p.Steps.Clear();
            Finish(p, timedOut: false);
            return;
        }

        if (p.Steps.Count > 0)
        {
            try
            {
                if (p.Steps.Peek()(p)) p.Steps.Dequeue();
            }
            catch (Exception ex) when (ex is GmlException or ArgumentException or InvalidOperationException)
            {
                // A step that cannot go on ends the action; what it did so far is still reported.
                p.Error = ex.Message;
                p.Steps.Clear();
            }
            if (p.Steps.Count > 0 && frames <= p.MaxFrames) return;
            p.SettleFrom = _at.Frame;
        }

        var player = Gm.Player;
        bool ready = player is { } pl ? Ready(pl) : p.PlayerId < 0;

        // Waiting skips one turn at a time, and a skip counts only once the turn
        // counter has moved: the game drops a skip that comes too soon after
        // the last turn (the player's turn reads as available a little before
        // it accepts the next one), so a skip with no effect after
        // three-quarters of a second is sent again, up to a few times.
        long sinceSkip = _at.Frame - p.SkippedAt;
        if (p.SkipPending)
        {
            if (Turn() != p.TurnAtSkip)
            {
                p.SkipPending = false;
                p.WaitsLeft--;
                p.WaitsDone++;
                p.SkipRetries = 0;
                p.Settled = 0;
            }
            else if (sinceSkip > 45)
            {
                p.SkipPending = false;
                if (++p.SkipRetries > 5)
                {
                    p.Note = $"the game did not take a turn skip ({p.WaitsDone} of {p.WaitsDone + p.WaitsLeft} done)";
                    p.WaitsLeft = 0;
                }
            }
        }
        else if (p.WaitsLeft > 0 && player is { } me && ready && p.Settled >= 3)
        {
            p.TurnAtSkip = Turn();
            p.SkippedAt = _at.Frame;
            p.SkipPending = true;
            Scripts.scr_skip_turn.CallAs(me);
            p.Settled = 0;
            return;
        }

        p.Settled = ready ? p.Settled + 1 : 0;
        bool timedOut = frames > p.MaxFrames;
        bool skipsDone = p.WaitsLeft == 0 && !p.SkipPending;
        // A door's way out changes room some frames after the player arrives:
        // the action ends in the new room, once the player there has settled.
        bool roomDone = p.RoomChangeAt < 0 || _at.Frame - p.RoomChangeAt > RoomChangeFrames;
        if ((_at.Frame - p.SettleFrom >= p.MinFrames && p.Settled >= 3 && skipsDone && roomDone) || timedOut)
            Finish(p, timedOut);
    }

    // The player's turn is back and nothing of theirs is in motion.
    private static bool Ready(InstanceRef player) =>
        Gm.Flag(player, "turn_available") && !Gm.Flag(player, "is_moving") && Gm.Num(player, "path", -1) < 0 &&
        Gm.Str(player, "state") is "idle" or "";

    // The result is always stored, even when reading the game for it fails:
    // a client polling hx.result must never wait for one that was lost.
    private void Finish(Pending p, bool timedOut)
    {
        _current = null;
        object result;
        try { result = Outcome(p, timedOut); }
        catch (Exception ex)
        {
            result = new { seq = p.Seq, done = true, kind = p.Kind, what = p.What, timedOut, error = $"could not read the outcome: {ex.Message}" };
        }
        _results[p.Seq] = result;
        _results.Remove(p.Seq - 30);
        _log.Info($"action {p.Seq} {p.Kind} {p.What}: done{(timedOut ? " (timed out)" : "")} after {_at.Frame - p.Start} frames");
    }

    private object Outcome(Pending p, bool timedOut)
    {
        if (p.Door && p.RoomChangeAt < 0 && p.Note == null && p.Error == null)
            p.Note = "the door did not lead anywhere: it is locked or barred, or a quest holds the player here (the action log may say why)";
        var player = Gm.Player;
        object? target = null;
        if (p.TargetId >= 0)
        {
            var t = Gm.ById(p.TargetId);
            double? hp = t is { } live ? Gm.NumOrNull(live, "HP") : null;
            target = new
            {
                id = p.TargetId, name = p.TargetName, hpBefore = p.TargetHp, hpAfter = hp,
                // Only a unit (it had HP) dies; an object that is gone was used up or left behind.
                gone = t == null, dead = p.TargetHp != null && (t == null || hp is <= 0),
            };
        }
        return new
        {
            seq = p.Seq,
            done = true,
            kind = p.Kind,
            what = p.What,
            timedOut,
            frames = _at.Frame - p.Start,
            // The counter belongs to the room's controller: across a room change it starts again.
            turns = p.RoomChangeAt < 0 && Turn() is var now && now >= 0 && p.Turn >= 0 ? now - p.Turn : (double?)null,
            waited = p.Kind == "wait" ? p.WaitsDone : (int?)null,
            player = player is { } me ? new
            {
                hp = new[] { p.Hp, Units.Hp(me) },
                mp = new[] { p.Mp, Units.Atr(me, "MP") },
                gold = new[] { p.Gold, Units.Gold() },
                from = new { gx = p.Cell.X, gy = p.Cell.Y },
                to = Grid.CellOf(me) is var c ? new { gx = c.X, gy = c.Y } : null,
                sameInstance = Gm.Id(me) == p.PlayerId,
            } : null,
            target,
            log = ActionLog.Since(p.LogSeen),
            menus = World.OpenMenus(),
            room = World.RoomName,
            note = p.Note,
            error = p.Error,
            extra = p.Extra,
        };
    }

    /// <summary>
    /// From the hook's fault barrier: a tick that threw. A bad frame is
    /// tolerated (the game may be mid-change); an action whose ticks keep
    /// failing for a second ends with the error, so nothing polls it forever.
    /// </summary>
    public void TickFailed(Exception ex)
    {
        if (_current is not { } p) return;
        if (++p.Failures == 1) _log.Warning($"action {p.Seq} {p.Kind}: tick failed: {ex.Message}");
        if (p.Failures < 60) return;
        p.Error ??= $"stopped: the game could not be read ({ex.Message})";
        p.Steps.Clear();
        Finish(p, timedOut: false);
    }

    public object Result(int? seq)
    {
        int s = seq ?? _seq;
        if (_current is { } p && p.Seq == s)
            return new { seq = s, done = false, kind = p.Kind, what = p.What, frames = _at.Frame - p.Start, waitsLeft = p.WaitsLeft };
        if (_results.TryGetValue(s, out var r)) return r;
        throw new ArgumentException(s == 0 ? "no action has run yet" : $"no action {s} (only the last 30 are kept)");
    }

    /// <summary>Gives up on the running action (it keeps whatever it did) and records it as cancelled.</summary>
    public object Cancel()
    {
        if (_current is not { } p) return new { cancelled = false };
        p.Note = "cancelled";
        Finish(p, timedOut: false);
        return new { cancelled = true, seq = p.Seq };
    }
}
