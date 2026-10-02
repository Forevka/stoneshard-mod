using CoreLoader;
using StoneShard;
using StoneshardTrials.Cards;

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
    private Arrival _arrival = null!;
    private CardWindow _cards = null!;
    private Merchants _merchants = null!;
    // The character in play and its run (see RunStore); null at the title screen.
    private Run? _run;
    private long _runPlayer = -1;
    // The run as the character carried it when last read or written: a load
    // that restores the character's attributes after we first looked is seen
    // by this changing.
    private string _runStored = "";
    // The run last seen, kept across rooms and menus (unlike _run).
    private string? _lastRunId;
    private ModSettings.Setting _enabledSetting = null!, _xpScale = null!, _goldScale = null!, _runMode = null!, _difficulty = null!;
    // Test host: the dungeon the next trial must take.
    private (int X, int Y)? _forcedNext;

    /// <summary>The next trial: its dungeon, whether it must be rewritten (endless), and the danger wanted.</summary>
    private readonly record struct Choice(World.Dungeon Dungeon, bool Rewrite, int Tier, double Target);

    // Chosen when the hub door starts to leave, used when it changes room.
    private Choice? _next;
    // What the trial under way adds to its dungeon, applied once the player has arrived.
    private Challenge.Plan _plan = Challenge.Plan.None;

    // Difficulty: Easy, Normal, Hard, Brutal - a shift of the wanted danger, and extra enemies.
    private static readonly string[] Difficulties = { "Easy", "Normal", "Hard", "Brutal" };
    private static readonly double[] DifficultyShift = { -0.5, 0, 0.5, 1.0 };
    private static readonly int[] DifficultyExtras = { 0, 0, 1, 2 };
    private int DifficultyIndex => (int)_difficulty.GetNumber();
    private bool EndlessRuns => _runMode.GetNumber() == 1;

    // A finished finite run is over; choosing Endless afterwards takes the trials up again.
    private bool RunOver(Run run) => run.Completed && !EndlessRuns;
    // A won trial whose ticket did not fit in the bag yet.
    private bool _ticketOwed;
    // A ticket was used: the way back starts on the next frame (Settle spends it).
    private bool _returnQueued;
    private string? _bannerText;
    private int _frames;
    private string _lastError = "";

    public override void OnInitialize()
    {
        _store = new RunStore(Directory, Log);
        _arrival = new Arrival(Log, OnArrived);
        _cards = new CardWindow(Log, i => Guard("card", () => TakeCard(i)), () => Guard("card", DiscardCards));
        _merchants = new Merchants(Log);
        _merchants.InstallHooks();
        _enabledSetting = ModSettings.Toggle(this, "enabled", "Trials",
            "Off: the tavern door leads to Osbrook and the world map opens again.", true);
        _xpScale = ModSettings.Slider(this, "xpScale", "Kill experience",
            "Experience from kills, the only source during the trials.", 1, 0, 3, 0.25, v => $"x{v:0.##}");
        _goldScale = ModSettings.Slider(this, "goldScale", "Trial reward",
            "Crowns paid for each trial won.", 1, 0, 3, 0.25, v => $"x{v:0.##}");
        _difficulty = ModSettings.Choice(this, "difficulty", "Difficulty",
            "Easy: half a tier lower. Hard: half higher, +1 enemy. Brutal: a tier higher, +2, elite master.",
            1, Difficulties);
        _runMode = ModSettings.Choice(this, "runMode", "Run",
            "Finite: every dungeon once, then the run ends. Endless: won dungeons are remade.",
            0, new[] { "Finite", "Endless" });

        Objects.o_transitions_door.Alarm_7.Before(c => Guard("door", () => OnDoorLeaving(c)));
        Scripts.scr_smoothRoomChange.Before(c => Guard("room change", () => OnRoomChange(c)));
        Objects.o_enemy.Destroy_0.Before(c => Guard("enemy death", () => OnEnemyDestroyed(c)));
        Objects.o_inv_map.Other_24.Before(c => Guard("map use", () => OnMapUsed(c)));
        Scripts.scr_globalmapCreate.Before(c => Guard("world map", () => OnWorldMap(c)));
        Scripts.scr_get_XP.Before(c => Guard("experience", () => OnExperience(c)));
        // A tree closed by a Forbidden Library card: its skills cannot be learned, nor its treatises read.
        Hooks.Before("gml_Object_o_skill_ico_Other_10", c => Guard("tree lock", () =>
        {
            if (CurrentRun() is not { Boons.Count: > 0 } run || !Trees.IsLockedSkill(c.Self, Catalog.LockedTrees(run))) return;
            c.SkipOriginal();
            World.Say("~r~That art is closed to you~/~ for these trials.");
        }));
        Scripts.scr_skill_branch_study.Before(c => Guard("tree lock", () =>
        {
            if (CurrentRun() is not { Boons.Count: > 0 } run || !Trees.IsLockedTreatise(c.Self, Catalog.LockedTrees(run))) return;
            c.SkipOriginal();
            c.Result = false;
            World.Say("~r~The treatise makes no sense to you~/~: that art is closed for these trials.");
        }));
        // The tavern traders are restocked by the trials only, never on the game's own timers.
        Scripts.scr_npc_restock.Before(c => Guard("restock", () =>
        {
            if (Merchants.IsOurs(c.Self)) c.SkipOriginal();
        }));
        Scripts.scr_atr_calc.Before(c => Guard("elite health", () => OnEliteCalc(c, after: false)));
        Scripts.scr_atr_calc.After(c => Guard("elite health", () => OnEliteCalc(c, after: true)));
        // While the card window is up the keyboard is its own: no hotkey reaches
        // the game (o_controller's Begin Step runs before any key is read).
        Objects.o_controller.Step_1.Before(_ =>
        {
            if (!_cards.IsOpen) return;
            try { Builtins.io_clear(); }
            catch (GmlException) { }
        });
        GameDraw.OnGui(DrawGui);
        if (TestHost.Enabled) RegisterCommands();
        Log.Info($"ready{(_enabled ? "" : " (turned off)")}");
    }

    public override void OnShutdown()
    {
        _cards.Close();
        _cards.Clear();
        _banner.Clear();
    }

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
            var read = _store.Read(stored);
            // Another character: nothing in flight belongs to it. (The player
            // is a new instance in every room, so that alone says nothing.)
            if (read.Id != _lastRunId)
            {
                _ticketOwed = false;
                _next = null;
                _returnQueued = false;
                _bannerText = null;
                _eliteKey = -1;
                _bagWhenFull = -1;
                _owedSeconds = 0;
                _plan = Challenge.Plan.None;
                _arrival.Cancel();
                _cards.Close();
            }
            // What the character carries changed under us (a load): the copy
            // follows, and boons the game does not save are put back.
            if (stored != _runStored) _store.Mirror(read);
            // A new room, a load or another character: the world read for the
            // banner may be another world's, or out of date (CompleteIfDone).
            _dungeonsSeen = null;
            _run = read;
            _lastRunId = read.Id;
            _runPlayer = key;
            _runStored = stored;
            if (_run.Id.Length > 0) Log.Info($"run {_run.Id}: trial {_run.Level}, {_run.TrialsWon} won, {_run.GoldEarned} crowns earned");
        }
        return _run;
    }

    // Onto the character first (so it is saved and rolls back with the game's save), then its copy on disk.
    private void SaveRun(Run run)
    {
        if (World.Player is not { } player) return;
        _runStored = _store.Save(player, run);
        _lastRunId = run.Id;
    }

    private void Update()
    {
        _arrival.Tick();
        _cards.Update();
        if (_returnQueued)
        {
            _returnQueued = false;
            ReturnToHub();
        }
        // About once a second: work out the banner and keep tickets looking like tickets
        // (a load puts the map's own text back).
        if (++_frames % 60 != 0) return;
        if (CurrentRun() is not { } run)
        {
            // Back at the menu: a debt belongs to the character that earned it.
            _ticketOwed = false;
            _bannerText = null;
            _cards.Close();
            return;
        }
        bool carrying = false;
        foreach (var t in Ticket.All())
        {
            Ticket.Refresh(t);
            carrying = true;
        }
        // Back in the tavern on foot. (While the door is leaving, the world cell
        // is already the dungeon's, so a trial just begun is not taken for this.)
        if (_enabled && World.Cell == World.HubCell && World.HubDoor() is not null)
        {
            if (run.Trial != null) Settle(run);
            else CompleteIfDone(run, RecentDungeons());
            // The cards of the last won trial, once the player stands in the tavern.
            if (run.Offer is { } offer && !_cards.IsOpen && World.Player is { } p)
                _cards.Open(offer, new CardLook(offer.Tier, Math.Max(1, (int)World.Num(p, "LVL", 1))));
            if (run.TrialsWon > 0 && !RunOver(run)) TendMerchants(run);
        }
        // The window shows the run's offer or nothing (a load or a test command may have changed it).
        if (_cards.IsOpen && (!_enabled || run.Offer is not { } shown || !_cards.Shows(shown))) _cards.Close();
        if (_ticketOwed) TryGiveTicket(quiet: true);
        TryElite();
        if (run.Boons.Count > 0 && World.Player is { } owner)
        {
            // The trials are over (or off): no cost outlasts them.
            if ((!_enabled || RunOver(run)) && run.Boons.Any(b => b.CostUntil > 0))
            {
                ExpireCosts(owner, run, all: true);
                SaveRun(run);
            }
            ReapplyBoons(owner, run);
            if (_enabled) Trees.GreyOut(Catalog.LockedTrees(run));
        }
        // Cleared first: a banner that fails to work out must not leave the old one up.
        _bannerText = null;
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
        if (CurrentRun() is not { } run) return;
        // Walked back in and straight out again before Update noticed: the
        // last trial is settled before the next one can overwrite it.
        if (run.Trial != null) Settle(run);
        // A finished run is over for good: the door is a plain door again.
        if (RunOver(run)) return;
        var all = World.Dungeons();
        var pick = Pick(all, consume: true);
        if (pick is null)
        {
            if (!CompleteIfDone(run, all))
                Log.Warning($"no trial to take ({all.Count} dungeon(s) read); the door leads to the street this once");
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
        if (!_enabled || !World.IsHubDoor(c.Self) || _next is not { } choice || CurrentRun() is not { } run || World.Player is not { } player) return;
        _next = null;
        var d = choice.Dungeon;
        try
        {
            // The room first: if it cannot be found, nothing has been changed yet.
            int room = World.Room(World.DungeonRoom);
            if (room < 0) throw new InvalidOperationException($"no room {World.DungeonRoom}");
            // Endless: a dungeon that does not fit is made to, before it is generated.
            if (choice.Rewrite) d = Endless.Rewrite(player, d, choice.Tier, _rng, Log);
            c.SetArg(0, room);
            World.SetCell((d.X, d.Y));
            // The door's alarm has just set the arrival tag to its own
            // ("r_OSbrooktavern"); a dungeon's entrance sets "NA", which is
            // the tag its stairs inside carry. Without this the player arrives
            // where they stood in the tavern, often inside a wall in the dark.
            Globals.Set("position_tag", World.DungeonArrivalTag);
        }
        catch
        {
            // Out into the street as the game meant; the door's alarm has
            // already read its floor step, so the street would count as floor 1.
            Globals.Set("floor_counter", 0);
            throw;
        }
        _plan = Challenge.For(choice.Target, d.Tier, DifficultyExtras[DifficultyIndex]);
        _eliteKey = -1;
        _arrival.Expect();
        run.Trial = d;
        run.Won = false;
        // Kept on the run, so a load mid-trial finds its master elite again.
        run.Elite = _plan.Elite;
        run.LastKind = d.Kind;
        run.LastCell = $"{d.X}_{d.Y}";
        _ticketOwed = false;
        SaveRun(run);
        Log.Info($"trial {run.Level}: {d.Name} ({d.Kind}, tier {d.Tier}) at {d.X},{d.Y}; adds {_plan}");
        World.Say($"~y~Trial {run.Level}~/~: the door opens onto {Describe(d)} ({Skulls(d.Tier)}).");
    }

    /// <summary>
    /// Ends a finite run once every master of the world is dead. Over only on
    /// positive evidence: the world has dungeons and none has a master left. An
    /// empty list is a failed read, not a win. (An endless run never ends.)
    /// </summary>
    private bool CompleteIfDone(Run run, IReadOnlyList<World.Dungeon> all)
    {
        if (EndlessRuns || run.Completed || run.Trial != null || all.Count == 0 || all.Any(d => d.BossAlive)) return false;
        run.Completed = true;
        SaveRun(run);
        Log.Info($"run {run.Id} complete: {run.TrialsWon} trials won, {run.GoldEarned} crowns");
        World.Say("~lg~Every trial is won.~/~ No dungeon's master is left alive: the road is yours again.");
        return true;
    }

    /// <summary>
    /// An untouched dungeon (its boss alive) as hard as the character calls
    /// for (see <see cref="Progression"/>). A run is finite: once every
    /// dungeon of the world has been won, the trials are over.
    /// </summary>
    private Choice? Pick(IReadOnlyList<World.Dungeon> all, bool consume)
    {
        if (World.Player is not { } player || CurrentRun() is not { } run) return null;
        var a = Progression.Assess(player, run.Level, DifficultyShift[DifficultyIndex]);
        var open = all.Where(d => d.BossAlive).ToList();
        if (consume && _forcedNext is { } f)
        {
            _forcedNext = null;
            if (all.FirstOrDefault(d => (d.X, d.Y) == f) is { Name: not null } forced)
            {
                bool rewrite = EndlessRuns && (!forced.BossAlive || forced.Tier != a.Tier);
                // The same rules as a real pick: a finite run takes only an untouched
                // dungeon, an endless one remakes only what can be remade.
                if (rewrite ? Endless.Rewritable(forced, a.Tier) : forced.BossAlive)
                    return new Choice(forced, rewrite, a.Tier, a.Target);
            }
            Log.Warning($"tr.next {f.X},{f.Y} cannot be the next trial (not a dungeon, won, or not one that can be remade); picking as usual");
        }
        Choice? choice;
        var rng = PickRng(run);
        if (EndlessRuns)
        {
            (int, int)? lastCell = run.LastCell?.Split('_') is [var lx, var ly] && int.TryParse(lx, out int x) && int.TryParse(ly, out int y) ? (x, y) : null;
            choice = Endless.Pick(all, a.Tier, run.LastKind, lastCell, rng) is { } e ? new Choice(e.Dungeon, e.Rewrite, e.Tier, a.Target) : null;
        }
        else choice = Progression.Pick(open, a.Tier, run.LastKind, rng) is { } p ? new Choice(p, false, a.Tier, a.Target) : null;
        if (consume && choice is { } ch)
            Log.Info($"trial {run.Level}: {a}; {open.Count} untouched dungeon(s) left, taking tier {ch.Dungeon.Tier}{(ch.Rewrite ? $" made tier {ch.Tier}" : "")}");
        return choice;
    }

    // Ties between equally good dungeons are broken by a generator seeded with
    // the run and its trial number, so the banner, tr.pick and the door itself
    // name the same dungeon for as long as nothing else changes.
    private static Random PickRng(Run run, string salt = "")
    {
        int seed = 17;
        unchecked
        {
            foreach (char ch in $"{run.Id}:{run.Level}{salt}") seed = seed * 31 + ch;
        }
        return new Random(seed);
    }

    // Once in the trial's dungeon: its extra enemies, and the elite master
    // when it is on this floor (else it waits for it; see Update).
    private void OnArrived()
    {
        // Only in the trial's own dungeon (the arrival check may have timed out elsewhere).
        if (!_plan.Any || World.Player is not { } player || !InTrial)
        {
            if (_plan.Any) Log.Warning($"arrived outside the trial ({World.Cell.X},{World.Cell.Y}, in dungeon {World.InDungeon}); the trial adds nothing");
            _plan = Challenge.Plan.None;
            return;
        }
        int made = Challenge.SpawnExtras(player, _plan, _rng, Log);
        _plan = Challenge.Plan.None;
        if (made > 0) World.Say($"This trial is harder: ~y~{made} more~/~ lie in wait.");
        TryElite();
    }

    // The elite master as made in this session (an instance id; -1 while it is
    // still to be found). The run says whether there is one (Run.Elite); the
    // instance is found again after a load or a change of floor.
    private long _eliteKey = -1;
    // Its health as a recalculation began (see OnEliteCalc).
    private double _eliteHp = double.NaN;

    private void TryElite()
    {
        // Once the trial is won its elite is dead: no other master takes its place.
        if (CurrentRun() is not { Elite: true, Won: false } || !InTrial) return;
        if (_eliteKey >= 0 && Builtins.instance_exists(_eliteKey).AsBool) return;
        if (Challenge.TryElite(Log) is not { } elite) return;
        _eliteKey = World.IdKey(elite.Master.Id);
        _eliteWritten = World.Num(elite.Master, "max_hp");
        // Said once per master: one loaded with its plain name is said again.
        if (elite.Renamed) World.Say($"~r~{elite.Name} is an elite~/~: tougher than any master of its kind.");
    }

    // scr_atr_calc, run as a unit every turn, rebuilds its max_hp and clamps
    // its HP to it, which would undo the elite's extra health. Around that
    // call, for the elite only: its HP is noted first, and afterwards its
    // maximum is scaled and the HP it had put back (it takes no damage there).
    private void OnEliteCalc(HookCall c, bool after)
    {
        if (_eliteKey < 0 || c.Self.IsNull || World.IdKey(c.Self.Get("id")) != _eliteKey) return;
        var self = new InstanceRef(c.Self.Get("id"));
        if (!after)
        {
            _eliteHp = World.Num(self, "HP");
            return;
        }
        if (double.IsNaN(_eliteHp)) return;
        // A call that left max_hp as we wrote it (an early way out) is not scaled again.
        double now = World.Num(self, "max_hp");
        double max = now == _eliteWritten ? now : Challenge.EliteMax(now);
        self.Set("max_hp", max);
        self.Set("HP", Math.Min(_eliteHp, max));
        _eliteWritten = max;
        _eliteHp = double.NaN;
    }

    // The elite's maximum as last written, so it is never scaled twice.
    private double _eliteWritten = double.NaN;

    // The world's dungeons for the hub banner, read again every few seconds:
    // a read is two script calls per dungeon.
    private List<World.Dungeon>? _dungeonsSeen;
    private int _dungeonsAge;

    private IReadOnlyList<World.Dungeon> RecentDungeons()
    {
        if (_dungeonsSeen == null || ++_dungeonsAge >= 5)
        {
            _dungeonsSeen = World.Dungeons();
            _dungeonsAge = 0;
        }
        return _dungeonsSeen;
    }

    // The game's own danger colours for its skull rating: plain, light green, yellow, red.
    private static string Skulls(int tier) => tier switch
    {
        1 => "danger 1",
        2 => "~lg~danger 2~/~",
        3 => "~y~danger 3~/~",
        _ => $"~r~danger {tier}~/~",
    };

    private static string Describe(World.Dungeon d) => d.Kind switch
    {
        "Crypt" => "an abandoned crypt",
        "Catacombs" => "the catacombs under a ruined church",
        _ => "a bandit-held bastion",
    };

    // Read through CurrentRun: between rooms there is a frame without a player,
    // which drops the cached run, and arrival comes before the next refresh.
    private bool InTrial => CurrentRun()?.Trial is { } t && World.InDungeon && World.Cell == (t.X, t.Y);

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
        if (!_enabled || CurrentRun() is { } done && RunOver(done)) return;
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

    // The bag as it was when the ticket last did not fit, and the seconds since.
    private int _bagWhenFull = -1, _owedSeconds;

    private void TryGiveTicket(bool quiet)
    {
        // Each failed try makes the game throw a map away (and say so in the
        // log), so a full bag is tried again only once what it holds changed,
        // and otherwise every half minute (room made some way not counted).
        if (quiet && Ticket.BagCount() == _bagWhenFull && ++_owedSeconds < 30) return;
        _owedSeconds = 0;
        if (Ticket.Give() is null)
        {
            _bagWhenFull = Ticket.BagCount();
            if (!quiet) World.Say("~r~Your bag is full~/~: make room, and the Trial Ticket will find you.");
            return;
        }
        _bagWhenFull = -1;
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
        // A finished run leaves maps alone (its tickets have nothing left to do).
        if (CurrentRun() is { } done && RunOver(done) && !Ticket.Is(r)) return;
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
        _returnQueued = true;
    }

    private void ReturnToHub()
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
        // On its way: the used ticket goes along with any other (Settle).
        Settle(run);
    }

    /// <summary>
    /// Ends the trial under way once the player is on the way back to the
    /// tavern or already in it, by a ticket or on foot (the dungeon's stairs,
    /// then the road): a won trial is paid and counted, one not won is given
    /// up. The run is saved first, so nothing after this can leave the trial
    /// half counted; then every ticket is spent (a spare one would be a free
    /// way out of the next trial) and the crowns paid.
    /// </summary>
    private void Settle(Run run)
    {
        if (World.Player is not { } player) return;
        int gold = 0, finished = run.Level;
        bool givenUp = run is { Won: false, Trial: not null };
        if (run is { Won: true, Trial: { } won })
        {
            gold = Reward(won, run.Level);
            run.GoldEarned += gold;
            run.TrialsWon++;
            run.Level++;
            run.LastWonTier = Math.Clamp(won.Tier, 1, 5);
            ExpireCosts(player, run);
            // The cards for this win, dealt now and kept on the run, shown in the tavern.
            run.Offer = Deal(player, run, run.LastWonTier, finished);
        }
        run.Trial = null;
        run.Won = false;
        run.Elite = false;
        _ticketOwed = false;
        _eliteKey = -1;
        _plan = Challenge.Plan.None;
        SaveRun(run);
        Log.Info($"back to the tavern{(givenUp ? ", trial given up" : "")}; next is trial {run.Level}");
        foreach (var t in Ticket.All().ToList())
            if (t.Resolve() is { } inst) Scripts.scr_item_destroy.CallAs(inst, inst);
        if (gold > 0)
        {
            Scripts.scr_gold_add.CallAs(player, gold);
            World.Say($"The innkeeper counts out ~y~{gold} crowns~/~ for trial {finished}.");
        }
        else if (givenUp) World.Say($"~y~Trial {finished}~/~ is given up: its master still lives.");
    }

    // ------------------------------------------------------------ the traders

    // From the first win on. Their stock is made after wins 1, 3, 5... and
    // held for two trials; it lives in the world save, so it rolls back with
    // the run.
    private void TendMerchants(Run run)
    {
        bool due = run.StockedAt == 0 || run.TrialsWon - run.StockedAt >= 2;
        int tier = due ? run.LastWonTier : run.StockTier;
        int serial = due ? run.StockSerial + 1 : run.StockSerial;
        // A refresh that did not reach every trader is tried again next time;
        // the ones it did reach know this stock by its serial and are left alone.
        if (!_merchants.Tend(tier, due, serial) || !due) return;
        run.StockedAt = run.TrialsWon;
        run.StockTier = tier;
        run.StockSerial = serial;
        SaveRun(run);
    }

    // ------------------------------------------------------------ the cards

    // Seeded by the run and the trial, like the dungeon pick, so the same win deals the same hand.
    private Offer Deal(InstanceRef player, Run run, int tier, int trial)
    {
        var offer = Deck.Deal(Catalog.All, new CardContext(player, run, tier, PickRng(run, $"cards{trial}"), Log), trial);
        Log.Info($"trial {trial}: cards {string.Join(", ", offer.Cards)} (tier {tier})");
        return offer;
    }

    private void TakeCard(int index)
    {
        // Whatever happens below, the window shows no card that cannot be taken.
        if (World.Player is not { } player || CurrentRun() is not { Offer: { } offer } run)
        {
            _cards.Close();
            return;
        }
        if (index < 0 || index >= offer.Cards.Count || Catalog.Find(offer.Cards[index]) is not { } card) return;
        string? chosen = index < offer.Details.Count ? offer.Details[index] : null;
        // Recorded and saved before it is done: a card that fails halfway must
        // not stay on the table to be taken (and its first half given) again.
        int costTrials = card.CostTrials(offer.Tier);
        var boon = new Boon
        {
            Id = card.Id, Tier = offer.Tier, Trial = offer.Trial, Detail = chosen,
            CostUntil = costTrials > 0 ? run.TrialsWon + costTrials : 0,
        };
        run.Boons.Add(boon);
        run.Offer = null;
        _cards.Close();
        SaveRun(run);
        try
        {
            boon.Detail = card.Apply(new CardContext(player, run, offer.Tier, PickRng(run, $"take{offer.Trial}"), Log), chosen) ?? chosen;
            if (card.CostBuff != null) Effects.LongBuff(player, card.CostBuff, card.Id);
            SaveRun(run);
        }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException)
        {
            Log.Warning($"card {card.Id} was taken but not fully given: {ex.Message}");
            World.Say($"~r~{card.Title}~/~ fizzles: not all of it took hold.");
            return;
        }
        Log.Info($"took {card.Id} (tier {offer.Tier}){(boon.Detail != null ? $": {boon.Detail}" : "")}");
        World.Say($"~lg~{card.Title}~/~ is yours.");
    }

    private void DiscardCards()
    {
        _cards.Close();
        if (CurrentRun() is not { Offer: not null } run) return;
        run.Offer = null;
        SaveRun(run);
        Log.Info("turned the cards down");
        World.Say("You turn the boons down.");
    }

    // Costs that last some trials end once that many are won; all of them
    // end when the trials do (a finished run, or Trials turned off).
    private void ExpireCosts(InstanceRef player, Run run, bool all = false)
    {
        foreach (var boon in run.Boons.Where(b => b.CostUntil > 0 && (all || run.TrialsWon >= b.CostUntil)))
        {
            if (Catalog.Find(boon.Id) is not { } card || (card.Expire == null && card.CostBuff == null))
            {
                boon.CostUntil = 0;
                continue;
            }
            try
            {
                if (card.CostBuff != null) Effects.EndBuff(player, card.CostBuff, card.Id);
                card.Expire?.Invoke(new CardContext(player, run, boon.Tier, PickRng(run, $"ex{boon.Trial}"), Log), boon);
                // Only once ended: a cost that failed to end is tried again.
                boon.CostUntil = 0;
                World.Say($"The cost of ~y~{card.Title}~/~ is paid in full.");
            }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException)
            {
                Log.Warning($"boon {boon.Id}: {ex.Message}");
            }
        }
    }

    // What the game does not keep of the boons taken (night vision, custom
    // buff numbers) is put back: after a load, and with every new player
    // instance (each room). Every boon's Reapply does nothing when it is there.
    private void ReapplyBoons(InstanceRef player, Run run)
    {
        foreach (var boon in run.Boons)
        {
            if (Catalog.Find(boon.Id) is not { } card) continue;
            try
            {
                // A timed cost stays on, and lasting, until it is paid.
                if (boon.CostUntil > 0 && card.CostBuff != null) Effects.KeepBuff(player, card.CostBuff, card.Id);
                card.Reapply?.Invoke(new CardContext(player, run, boon.Tier, PickRng(run, $"re{boon.Trial}"), Log), boon);
            }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException)
            {
                Log.Warning($"boon {boon.Id}: {ex.Message}");
            }
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
        // A finished run gives experience back to the whole world.
        if (!_enabled || c.ArgCount < 1 || CurrentRun() is { } done && RunOver(done)) return;
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
        if (RunOver(run)) return World.HubDoor() is not null ? $"~lg~Every trial is won~/~  -  {run.TrialsWon} trial{(run.TrialsWon == 1 ? "" : "s")}, {run.GoldEarned} crowns" : null;
        if (World.HubDoor() is not null)
        {
            // The tier the door will really take: the nearest untouched one.
            if (Pick(RecentDungeons(), consume: false) is not { } next) return null;
            int tier = next.Rewrite ? next.Tier : next.Dungeon.Tier;
            return $"~y~Trial {run.Level}~/~  -  {Skulls(tier)} awaits  -  leave the tavern to start the next trial level";
        }
        if (!InTrial) return null;
        if (carryingTicket) return $"~y~Trial {run.Level}~/~  -  use the ~y~{Ticket.Name}~/~ to return to the tavern";
        return run.Won ? $"~y~Trial {run.Level}~/~  -  make room in your bag for the ~y~{Ticket.Name}~/~"
                       : $"~y~Trial {run.Level}~/~  -  slay the master of this place to earn your way back";
    }

    // Drawing only: what to say is worked out in Update.
    private void DrawGui()
    {
        if (_cards.IsOpen)
        {
            _cards.Draw();
            return;
        }
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
            elite = _run?.Elite,
            eliteId = _eliteKey,
            ticketOwed = _ticketOwed,
            inHub = World.HubDoor() is not null,
            inDungeon = World.InDungeon,
            inTrial = InTrial,
            tickets = Ticket.All().Count(),
        }, "tr.state: the trial number, the current trial, tickets, where the player is");
        TestHost.Register("tr.offer", _ => new
        {
            open = _cards.IsOpen,
            offer = CurrentRun()?.Offer,
            boons = _run?.Boons,
        }, "tr.offer: the cards on the table (and whether their window is open) and the boons taken");
        TestHost.Register("tr.deal", args =>
        {
            if (World.Player is not { } p || CurrentRun() is not { } run) return "no player";
            int tier = Math.Clamp(args.Count > 0 ? args[0].GetInt32() : 1, 1, 5);
            run.Offer = Deal(p, run, tier, run.Level);
            if (args.Count > 1)
            {
                var ctx = new CardContext(p, run, tier, PickRng(run, "tr.deal"), Log);
                var cards = args.Skip(1).Select(a => Catalog.Find(a.GetString()!)).OfType<CardDef>().Take(Deck.Size).ToList();
                run.Offer.Cards = cards.Select(c => c.Id).ToList();
                run.Offer.Details = cards.Select(c => c.Prepare?.Invoke(ctx)).ToList();
            }
            SaveRun(run);
            return run.Offer;
        }, "tr.deal [tier] [card ids...]: puts cards on the table now (shown in the tavern); ids choose them");
        TestHost.Register("tr.take", args =>
        {
            if (args.Count < 1) throw new ArgumentException("usage: tr.take <0-2>");
            TakeCard(args[0].GetInt32());
            return _run?.Boons.LastOrDefault();
        }, "tr.take <n>: takes the n-th card on the table (0-based), as its TAKE button does");
        TestHost.Register("tr.discard", _ =>
        {
            DiscardCards();
            return "ok";
        }, "tr.discard: turns all the cards down, as DISCARD ALL does");
        TestHost.Register("tr.traders", args =>
        {
            if (args.Count > 0 && args[0].GetString() == "restock" && CurrentRun() is { } run)
            {
                run.StockedAt = 0;
                SaveRun(run);
            }
            return Merchants.Describe();
        }, "tr.traders [restock]: the tavern traders (where, tiers, stock size); restock makes their stock anew on the next tavern tick");
        TestHost.Register("tr.give-ticket", _ =>
        {
            var t = Ticket.Give();
            return t is { } r ? r.Id.ToString() : "bag full";
        }, "tr.give-ticket: puts a Trial Ticket in the bag");
        TestHost.Register("tr.assess", args =>
        {
            if (World.Player is not { } p) return "no player";
            int trial = args.Count > 0 ? args[0].GetInt32() : CurrentRun()?.Level ?? 1;
            var a = Progression.Assess(p, trial, DifficultyShift[DifficultyIndex]);
            return new { trial, a.Level, a.LevelTier, a.Gear, a.Power, a.Target, a.Tier };
        }, "tr.assess [trial]: the character's level, gear score and power, and the tier that trial would take");
        TestHost.Register("tr.next", args =>
        {
            if (args.Count < 2) throw new ArgumentException("usage: tr.next <x> <y>");
            var cell = (args[0].GetInt32(), args[1].GetInt32());
            if (World.Dungeons().FirstOrDefault(d => (d.X, d.Y) == cell) is not { Name: not null } d) return "not a dungeon";
            _forcedNext = cell;
            // The door makes the last check (the tier is known only then); these are the ones it will surely refuse.
            if (!EndlessRuns && !d.BossAlive) return "won: a finite run will refuse it";
            if (EndlessRuns && !d.BossAlive && d.Floors != 1) return "won with two floors: it cannot be remade, so it will be refused";
            return "ok";
        }, "tr.next <x> <y>: the next trial takes that dungeon (an untouched one; in an endless run, one that can be remade)");
        TestHost.Register("tr.dset", args =>
        {
            RValue v = args[3].ValueKind == System.Text.Json.JsonValueKind.Number ? args[3].GetDouble()
                     : args[3].GetString() is "true" ? true : args[3].GetString() is "false" ? false : args[3].GetString()!;
            if (World.Player is not { } p) return "no player";
            Scripts.scr_globaltile_dungeon_set.CallAs(p, args[2].GetString()!, v, args[0].GetInt32(), args[1].GetInt32());
            return Scripts.scr_globaltile_dungeon_get.CallAs(p, args[2].GetString()!, args[0].GetInt32(), args[1].GetInt32()).ToString();
        }, "tr.dset <x> <y> <key> <value>: writes one key of a dungeon's data");
        TestHost.Register("tr.pick", _ => Pick(World.Dungeons(), consume: false)?.ToString() ?? "none", "tr.pick: the dungeon the next trial would take (a forced tr.next is not used up)");
        TestHost.Register("tr.return", _ =>
        {
            _returnQueued = true;
            return "queued";
        }, "tr.return: goes back to the tavern as a used ticket does (no ticket spent)");
    }
}
