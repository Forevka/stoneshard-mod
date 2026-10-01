using CoreLoader;
using StoneShard;

[assembly: CoreModInfo(typeof(StoneshardTrials.TrialsMod), "Stoneshard Trials", "0.1.0", "Lodestone")]
[assembly: CoreModGame("StoneShard")]

namespace StoneshardTrials;

/// <summary>
/// A roguelike loop on top of Stoneshard: the Osbrook tavern, where a new
/// Adventure starts, is the hub; its street door leads into a random dungeon
/// whose boss still lives; that boss leaves a Trial Ticket, and using the
/// ticket brings the player back to the tavern.
/// </summary>
public sealed class TrialsMod : CoreMod
{
    private readonly Banner _banner = new();
    private readonly Random _rng = new();
    private RunStore _store = null!;
    // The character in play and its run (see RunStore); null at the title screen.
    private Run? _run;
    private long _runPlayer = -1;
    // The run as the character carried it when last read or written: a load
    // that restores the character's attributes after we first looked is seen
    // by this changing.
    private string _runStored = "";
    private ModSettings.Setting _enabledSetting = null!, _xpScale = null!, _goldScale = null!;
    // Chosen when the hub door starts to leave, used when it changes room.
    private World.Dungeon? _next;
    // A won trial whose ticket did not fit in the bag yet.
    private bool _ticketOwed;
    // The used ticket, destroyed once the way back has really started.
    private InstanceRef? _usedTicket;
    private bool _returnQueued;
    private string? _bannerText;
    private int _frames;
    private string _lastError = "";

    public override void OnInitialize()
    {
        _store = new RunStore(Directory, Log);
        _enabledSetting = ModSettings.Toggle(this, "enabled", "Trials",
            "Off: the tavern door leads to Osbrook and the world map opens again.", true);
        _xpScale = ModSettings.Slider(this, "xpScale", "Kill experience",
            "Experience from slain enemies; no other source gives any during the trials.", 1, 0, 3, 0.25, v => $"x{v:0.##}");
        _goldScale = ModSettings.Slider(this, "goldScale", "Trial reward",
            "Crowns paid for each trial won.", 1, 0, 3, 0.25, v => $"x{v:0.##}");

        Objects.o_transitions_door.Alarm_7.Before(c => Guard("door", () => OnDoorLeaving(c)));
        Scripts.scr_smoothRoomChange.Before(c => Guard("room change", () => OnRoomChange(c)));
        Objects.o_enemy.Destroy_0.Before(c => Guard("enemy death", () => OnEnemyDestroyed(c)));
        Objects.o_inv_map.Other_24.Before(c => Guard("map use", () => OnMapUsed(c)));
        Scripts.scr_globalmapCreate.Before(c => Guard("world map", () => OnWorldMap(c)));
        Scripts.scr_get_XP.Before(c => Guard("experience", () => OnExperience(c)));
        GameDraw.OnGui(DrawGui);
        if (TestHost.Enabled) RegisterCommands();
        Log.Info($"ready{(_enabled ? "" : " (turned off)")}");
    }

    public override void OnShutdown() => _banner.Clear();

    public override void OnUpdate() => Guard("update", Update);

    private bool _enabled => _enabledSetting.GetBool();

    /// <summary>
    /// The run of the character in play, read again whenever the player is a new
    /// instance (a load, a new game). Null without a player.
    /// </summary>
    private Run? CurrentRun()
    {
        if (World.Player is not { } player)
        {
            _run = null;
            _runPlayer = -1;
            return null;
        }
        long key = World.IdKey(player.Id);
        string stored = RunStore.Stored(player);
        if (_run == null || key != _runPlayer || stored != _runStored)
        {
            // Another character, or the same one loaded from a save: nothing
            // in flight belongs to it.
            if (key != _runPlayer)
            {
                _ticketOwed = false;
                _next = null;
                _returnQueued = false;
                _usedTicket = null;
                _bannerText = null;
            }
            _run = _store.Read(stored);
            _runPlayer = key;
            _runStored = stored;
            if (_run.Id.Length > 0) Log.Info($"run {_run.Id}: trial {_run.Level}, {_run.TrialsWon} won, {_run.GoldEarned} crowns earned");
        }
        return _run;
    }

    // Onto the character first (so it is saved and rolls back with the game's save), then its copy on disk.
    private void SaveRun(Run run)
    {
        if (World.Player is { } player) _runStored = _store.Save(player, run);
    }

    private void Update()
    {
        if (_returnQueued)
        {
            _returnQueued = false;
            var ticket = _usedTicket;
            _usedTicket = null;
            ReturnToHub(ticket);
        }
        // About once a second: work out the banner and keep tickets looking like tickets
        // (a load puts the map's own text back).
        if (++_frames % 60 != 0) return;
        if (CurrentRun() is null)
        {
            // Back at the menu: a debt belongs to the character that earned it.
            _ticketOwed = false;
            _bannerText = null;
            return;
        }
        bool carrying = false;
        foreach (var t in Ticket.All())
        {
            Ticket.Refresh(t);
            carrying = true;
        }
        if (_ticketOwed) TryGiveTicket(quiet: true);
        _bannerText = BannerText(carrying);
    }

    // A hook or update that throws would fault the whole mod; one bad call costs only itself.
    private void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex.Message != _lastError) Log.Warning($"{what}: {ex.Message}");
            _lastError = ex.Message;
        }
    }

    // ------------------------------------------------------------ into a trial

    // A door's alarm 7 is where it leaves: it reads its dungeon_level_incr (how
    // many floors down the next room is; 1 on a dungeon's entrance, 0 on the
    // tavern's door) and then calls scr_smoothRoomChange. The next room's
    // controller makes that the new floor, and a floor above 0 is what makes the
    // game treat a room as a dungeon. So the hub door is made a dungeon entrance
    // here, before it reads that, and only redirected in OnRoomChange.
    private void OnDoorLeaving(HookCall c)
    {
        if (!_enabled || !World.IsHubDoor(c.Self)) return;
        _next = null;
        c.Self.Set("dungeon_level_incr", 0);
        var pick = Pick();
        if (pick is null)
        {
            World.Say("~r~No trial is left to take~/~: every dungeon's master is dead.");
            return;
        }
        _next = pick;
        c.Self.Set("dungeon_level_incr", 1);
    }

    // The hub's street door leaves through scr_smoothRoomChange(r_Osbrook, ...)
    // as itself. Pointing that same call at the dungeon generator, with the
    // world cell set to a dungeon's, is what entering that dungeon's own door does.
    private void OnRoomChange(HookCall c)
    {
        if (!_enabled || !World.IsHubDoor(c.Self) || _next is not { } d || CurrentRun() is not { } run) return;
        _next = null;
        try
        {
            // The room first: if it cannot be found, nothing has been changed yet.
            int room = World.Room(World.DungeonRoom);
            if (room < 0) throw new InvalidOperationException($"no room {World.DungeonRoom}");
            c.SetArg(0, room);
            World.SetCell((d.X, d.Y));
        }
        catch
        {
            // Out into the street as the game meant; the door's alarm has
            // already read its floor step, so the street would count as floor 1.
            Globals.Set("floor_counter", 0);
            throw;
        }
        run.Trial = d;
        run.Won = false;
        _ticketOwed = false;
        SaveRun(run);
        Log.Info($"trial {run.Level}: {d.Name} ({d.Kind}, tier {d.Tier}) at {d.X},{d.Y}");
        World.Say($"~y~Trial {run.Level}~/~: the door opens onto {Describe(d)}.");
    }

    /// <summary>
    /// A dungeon whose boss still lives, as hard as the trial number allows:
    /// tier 1 for the first two trials, one tier more every two after that.
    /// </summary>
    private World.Dungeon? Pick()
    {
        var open = World.Dungeons().Where(d => d.BossAlive).ToList();
        if (open.Count == 0) return null;
        int cap = 1 + ((CurrentRun()?.Level ?? 1) - 1) / 2;
        var fitting = open.Where(d => d.Tier <= cap).ToList();
        if (fitting.Count == 0) fitting = open.Where(d => d.Tier == open.Min(o => o.Tier)).ToList();
        int top = fitting.Max(d => d.Tier);
        var best = fitting.Where(d => d.Tier == top).ToList();
        return best[_rng.Next(best.Count)];
    }

    private static string Describe(World.Dungeon d) => d.Kind switch
    {
        "Crypt" => "an abandoned crypt",
        "Catacombs" => "the catacombs under a ruined church",
        _ => "a bandit-held bastion",
    };

    private bool InTrial => _run?.Trial is { } t && World.InDungeon && World.Cell == (t.X, t.Y);

    // ------------------------------------------------------------ the world map

    // The world map opens through scr_globalmapCreate. The trials go only where
    // the tavern door sends them, and the map is also where travel mods
    // (FastTravel) work, so it stays shut. The HUD's map button and its key (M)
    // take noone back calmly; a paper map's Use does not (the GUI then indexes
    // the map it expected and the game stops with an error), so maps are
    // refused before their Use runs - see OnMapUsed.
    private DateTime _mapRefusedAt;

    private void OnWorldMap(HookCall c)
    {
        if (!_enabled) return;
        if (c.Self.IsNull || c.Self.Get("object_index").AsReal != Objects.o_gui_button_map.Object?.Index) return;
        c.SkipOriginal();
        c.Result = -4;
        SayMapRefused();
    }

    private void SayMapRefused()
    {
        if (DateTime.UtcNow - _mapRefusedAt < TimeSpan.FromSeconds(3)) return;
        _mapRefusedAt = DateTime.UtcNow;
        World.Say("The world map is of no use during the trials: ~y~the tavern door~/~ is the only road.");
    }

    // ------------------------------------------------------------ the boss and the ticket

    // o_enemy's Destroy is its death: it drops the loot and clears the dungeon's
    // boss_alive. A unit unloaded with its room still has its HP. Only the trial's
    // own dungeon pays, and only once: a miniboss and a boss do not make two.
    private void OnEnemyDestroyed(HookCall c)
    {
        if (!_enabled || CurrentRun() is not { Won: false } run) return;
        var self = c.Self;
        if (self.IsNull) return;
        bool boss = World.Truthy(self.Get("isBoss")) || World.Truthy(self.Get("isMiniboss"));
        var hp = self.Get("HP");
        if (!boss || !hp.IsNumber || hp.AsReal > 0 || !InTrial) return;
        Log.Info($"boss down: {Builtins.object_get_name(self.Get("object_index"))}");
        run.Won = true;
        _ticketOwed = true;
        SaveRun(run);
        TryGiveTicket(quiet: false);
    }

    private void TryGiveTicket(bool quiet)
    {
        if (Ticket.Give() is null)
        {
            if (!quiet) World.Say("~r~Your bag is full~/~: make room, and the Trial Ticket will find you.");
            return;
        }
        _ticketOwed = false;
        World.Say($"~lg~The trial is won.~/~ A ~y~{Ticket.Name}~/~ is in your bag: use it to return to the tavern.");
    }

    // ------------------------------------------------------------ back to the hub

    private void OnMapUsed(HookCall c)
    {
        if (!_enabled) return;
        var item = c.Self;
        if (item.IsNull) return;
        var r = new InstanceRef(item.Get("id"));
        c.SkipOriginal();
        if (!Ticket.Is(r))
        {
            // Any other map would open the world map (see OnWorldMap).
            SayMapRefused();
            return;
        }
        if (!World.InDungeon)
        {
            World.Say($"The {Ticket.Name} only works inside a trial.");
            return;
        }
        // The room change waits for the next frame, out of the inventory's own event.
        _usedTicket = r;
        _returnQueued = true;
    }

    private void ReturnToHub(InstanceRef? ticket)
    {
        if (World.Player is not { } player || CurrentRun() is not { } run) return;
        // What a door's alarm 7 sets before it changes room: the floor the next
        // room is on (0, out of the dungeon) and the tag of the door to arrive
        // at - the hub's own street door, as if walking in from Osbrook. Put back
        // if the change does not start, so the dungeon is not taken for the hub.
        var cell = World.Cell;
        var floor = Globals.Get("floor_counter");
        var tag = Globals.Get("position_tag");
        try
        {
            World.SetCell(World.HubCell);
            Globals.Set("floor_counter", 0);
            Globals.Set("position_tag", World.HubDoorTag);
            Scripts.scr_smoothRoomChange.CallAs(player, World.Room(World.HubRoom), Builtins.array_create(1, 4), -1, false);
        }
        catch (GmlException)
        {
            World.SetCell(cell);
            Globals.Set("floor_counter", floor);
            Globals.Set("position_tag", tag);
            World.Say("~r~The ticket stays cold~/~: the way back is barred for now.");
            throw;
        }
        // On its way. The run is settled and saved first, so nothing after
        // this can leave the trial half counted; then the ticket is spent and
        // the crowns paid.
        int gold = 0, finished = run.Level;
        if (run is { Won: true, Trial: { } won })
        {
            gold = Reward(won, run.Level);
            run.GoldEarned += gold;
            run.TrialsWon++;
            run.Level++;
        }
        run.Trial = null;
        run.Won = false;
        SaveRun(run);
        Log.Info($"back to the tavern; next is trial {run.Level}");
        if (ticket?.Resolve() is { } inst) Scripts.scr_item_destroy.CallAs(inst, inst);
        if (gold > 0)
        {
            Scripts.scr_gold_add.CallAs(player, gold);
            World.Say($"The innkeeper counts out ~y~{gold} crowns~/~ for trial {finished}.");
        }
    }

    /// <summary>
    /// Crowns for a trial won: 150 for a tier 1 dungeon, 100 more per tier above,
    /// a tenth more for every trial already behind, times the reward setting.
    /// </summary>
    private int Reward(World.Dungeon d, int level) =>
        (int)Math.Round((150 + 100 * (Math.Max(1, d.Tier) - 1)) * (1 + 0.1 * (level - 1)) * _goldScale.GetNumber());

    // ------------------------------------------------------------ experience

    // Every source of experience goes through scr_get_XP(amount); a kill is the
    // call made from o_enemy's Destroy, with the dying enemy as self. During the
    // trials only kills count (no XP for finding places, quests, books or
    // crafting), scaled by the setting.
    private void OnExperience(HookCall c)
    {
        if (!_enabled || c.ArgCount < 1) return;
        bool kill = !c.Self.IsNull && Objects.o_enemy.Object is { } enemy &&
                    (c.Self.Get("object_index").AsReal == enemy.Index ||
                     Builtins.object_is_ancestor(c.Self.Get("object_index"), enemy.Index).AsBool);
        if (!kill)
        {
            // The original returns the amount given: none.
            c.SkipOriginal();
            c.Result = 0;
            return;
        }
        var amount = c.GetArg(0);
        if (amount.IsNumber) c.SetArg(0, Math.Round(amount.AsReal * _xpScale.GetNumber()));
    }

    // ------------------------------------------------------------ the banner

    private string? BannerText(bool carryingTicket)
    {
        if (!_enabled) return null;
        // Not over the world map: its controls bar only exists while it is open.
        if (Objects.o_globalmapControlsRender.Object is { InstanceCount: > 0 }) return null;
        if (_run is not { } run) return null;
        if (World.HubDoor() is not null) return $"~y~Trial {run.Level}~/~  -  leave the tavern to start the next trial level";
        if (!InTrial) return null;
        if (carryingTicket) return $"~y~Trial {run.Level}~/~  -  use the ~y~{Ticket.Name}~/~ to return to the tavern";
        return run.Won ? $"~y~Trial {run.Level}~/~  -  make room in your bag for the ~y~{Ticket.Name}~/~"
                       : $"~y~Trial {run.Level}~/~  -  slay the master of this place to earn your way back";
    }

    // Drawing only: what to say is worked out in Update.
    private void DrawGui()
    {
        if (_bannerText is not { } text || World.Player is null) return;
        try { _banner.Draw(text); }
        catch (GmlException ex)
        {
            if (ex.Message != _lastError) Log.Warning($"banner: {ex.Message}");
            _lastError = ex.Message;
        }
    }

    private void RegisterCommands()
    {
        Probe.Register(Log);
        TestHost.Register("tr.state", _ => new
        {
            enabled = _enabled,
            run = _run?.Id,
            level = _run?.Level,
            trialsWon = _run?.TrialsWon,
            goldEarned = _run?.GoldEarned,
            trial = _run?.Trial?.Name,
            won = _run?.Won,
            ticketOwed = _ticketOwed,
            inHub = World.HubDoor() is not null,
            inDungeon = World.InDungeon,
            inTrial = InTrial,
            tickets = Ticket.All().Count(),
        }, "tr.state: the trial number, the current trial, tickets, where the player is");
        TestHost.Register("tr.give-ticket", _ =>
        {
            var t = Ticket.Give();
            return t is { } r ? r.Id.ToString() : "bag full";
        }, "tr.give-ticket: puts a Trial Ticket in the bag");
        TestHost.Register("tr.pick", _ => Pick()?.ToString() ?? "none", "tr.pick: the dungeon the next trial would take");
        TestHost.Register("tr.return", _ =>
        {
            _returnQueued = true;
            return "queued";
        }, "tr.return: goes back to the tavern as a used ticket does (no ticket spent)");
    }
}
