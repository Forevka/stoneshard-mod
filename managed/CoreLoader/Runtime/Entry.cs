using System.Runtime.InteropServices;
using CoreLoader.Native;

namespace CoreLoader.Runtime;

/// <summary>
/// The functions the native host binds. Nothing may escape these methods: an
/// exception crossing an [UnmanagedCallersOnly] boundary terminates the process,
/// so every entry point catches everything.
/// </summary>
internal static unsafe class Entry
{
    private static readonly Logger Log = new("Lodestone");
    private static bool _initialisedMods, _announcedWait, _probed;

    // Re-entry: a GML call made during a frame can present again from inside
    // it. The nested pass must not drain the pool the outer one still uses,
    // nor reload a mod whose code is on the stack. (The host guards too.)
    private static bool _inFrame, _inGui;
    private static System.Diagnostics.Stopwatch? _startWait;

    [UnmanagedCallersOnly]
    public static int Init(CoreApi* api, ManagedExports* exports)
    {
        try
        {
            if (api == null || exports == null) return 0;
            // Refuse a host built against a different table shape: writing past
            // a smaller ManagedExports, or calling through a missing CoreApi
            // field, would corrupt the game instead of failing.
            if (api->Version != CoreApi.ExpectedVersion || api->Size < sizeof(CoreApi)) return 0;
            if (exports->Size < sizeof(ManagedExports)) return 0;
            Loader.Api = api;

            // Init runs on the host's start-up thread, not the game's: nothing
            // here may touch GML (the value helpers are probed on the game
            // thread, just before mods start).
            Log.Info($"Lodestone {typeof(Entry).Assembly.GetName().Version} on .NET {Environment.Version}, " +
                     $"game '{Game.Name}', {api->SymbolCount()} GML functions");

            ModManager.DiscoverAndLoad();
            TestHost.Start();

            exports->Frame = &Frame;
            exports->Gui = &Gui;
            exports->Shutdown = &Shutdown;
            exports->HookDispatch = &HookDispatch;
            return 1;
        }
        catch (Exception ex)
        {
            try { Log.Error("Init failed", ex); } catch { /* the log itself is gone */ }
            return 0;
        }
    }

    // Only from Frame: starting mods inside the overlay's GUI pass would run
    // their OnInitialize in the middle of an ImGui tab bar.
    private static void EnsureModsInitialised()
    {
        Loader.MarkGameThread();
        if (_initialisedMods) return;
        // The native side proves the value helpers on the first frame, before
        // this runs: pick up the verdict at once - the runtime's own work (interop
        // generation, variable harvest) calls the game while mods are still
        // waiting, and must release what it gets.
        if (!_probed)
        {
            _probed = true;
            Values.Probe();
            Log.Info($"value lifetime: free {(Values.CanFree ? "yes" : "no")} / copy {(Values.CanCopy ? "yes" : "no")}");
        }
        // Mods start once the game has its assets: some games (Stoneshard)
        // load sprites, sounds and rooms seconds after the first frame, and a
        // sprite added before that would take a slot the game is about to fill.
        // A game that never answers still gets its mods after ~30 s, and one
        // without a GML bridge (nothing could ever answer) gets them at once.
        _startWait ??= System.Diagnostics.Stopwatch.StartNew();
        // A check that cannot tell (it faulted) counts as "not yet" until the timeout.
        if (Game.IsGmlReady && _startWait.Elapsed < TimeSpan.FromSeconds(30) &&
            !(Game.BuiltinCount > 0 && Game.AssetsLoaded(whenUnsure: false)))
        {
            if (!_announcedWait) { _announcedWait = true; Log.Info("waiting for the game's assets before starting mods"); }
            return;
        }
        _initialisedMods = true;
        ModManager.Started = true;
        if (_announcedWait) Log.Info($"starting mods after {_startWait.Elapsed.TotalSeconds:0.0} s");
        // Attribute hooks first, so OnInitialize can rely on them being live.
        // Only mods still waiting: never a second start for one already running.
        foreach (var m in ModManager.Mods.ToList())
            if (m.State == ModState.Loaded) ModManager.Initialize(m);
    }

    [UnmanagedCallersOnly]
    private static void HookDispatch(CoreHookCall* call)
    {
        try
        {
            Hooks.Dispatch(call);
        }
        catch (Exception ex)
        {
            Log.Error("hook dispatch failed", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void Frame()
    {
        if (_inFrame || _inGui) return;
        _inFrame = true;
        // Each stage is isolated: one failing (a half-written dll, a full disk)
        // must not cost every mod its update, and the pool must drain regardless.
        try
        {
            Stage("initialise", EnsureModsInitialised);
            // Rebuilt mod dlls are swapped in here, between frames, where no
            // mod code is on the stack.
            if (_initialisedMods) Stage("hot reload", ModManager.PollChanges);
            Stage("queued actions", () => Game.DrainPending(Log));
            Stage("test host", TestHost.Tick);
            Stage("hook request timeouts", Hooks.TickRequests);
            Stage("object table", ObjectTable.Tick);
            Stage("interop", InteropGenerator.Tick);
            Stage("variable harvest", VarHarvest.Tick);
            Stage("updates", () => ModManager.ForEach(nameof(CoreMod.OnUpdate), mod => mod.OnUpdate()));
            Stage("game drawing", GameDraw.Tick);
            Stage("settings", ModConfig.FlushSettled);
        }
        finally
        {
            try { Values.Drain(); } catch (Exception ex) { Log.Error("releasing the frame's values failed", ex); }
            _inFrame = false;
        }
    }

    private static void Stage(string name, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error($"frame stage '{name}' failed", ex); }
    }

    [UnmanagedCallersOnly]
    private static void Gui()
    {
        if (_inFrame || _inGui) return;
        _inGui = true;
        int baseMark = UI.Mark;
        UI.InGui = true;
        try
        {
            Loader.MarkGameThread();
            if (!UI.BeginTabBar("##coreloader_mods")) return;

            if (UI.BeginTabItem("Loader"))
            {
                try { DrawLoaderTab(); }
                catch (Exception ex) { Log.Error("loader tab failed", ex); }
                UI.EndTabItem();
            }

            foreach (var m in ModManager.Mods)
                DrawModTab(m);

            UI.EndTabBar();
        }
        catch (Exception ex)
        {
            Log.Error("GUI dispatch failed", ex);
        }
        finally
        {
            // Whatever is still open - ours after an exception, or anything that
            // slipped past a mod's own unwind - is closed before ImGui sees End().
            try
            {
                UI.UnwindTo(baseMark);
                UI.InGui = false;
                Values.Drain();
            }
            catch (Exception ex)
            {
                // Nothing may leave an [UnmanagedCallersOnly] method.
                try { Log.Error("closing the GUI pass failed", ex); } catch { }
            }
            _inGui = false;
        }
    }

    private static void DrawModTab(LoadedMod m)
    {
        int outer = UI.Mark;
        try
        {
            UI.PushId(m.Path);
            if (!UI.BeginTabItem(m.Instance.Info.Name)) return;

            if (m.State == ModState.Faulted)
            {
                UI.TextColored(1f, 0.45f, 0.45f, $"Disabled - {m.Fault}");
                return;
            }
            if (m.State != ModState.Running)
            {
                UI.TextDisabled("Starts once the game has loaded its assets.");
                return;
            }

            // The mod may only close what it opens; anything it leaves behind is
            // closed here and counts as a fault, since it would otherwise corrupt
            // every tab drawn after it.
            int mark = UI.Mark;
            int oldFloor = UI.SetFloor(mark);
            try
            {
                ModManager.Invoke(m, nameof(CoreMod.OnGUI), mod => mod.OnGUI());
            }
            finally
            {
                UI.SetFloor(oldFloor);
                int leaked = UI.UnwindTo(mark);
                if (leaked > 0 && m.State != ModState.Faulted)
                    ModManager.Fault(m, $"OnGUI left {leaked} UI scope(s) open (missing End/Pop)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"drawing the tab of {m.Instance.Info.Name} failed", ex);
        }
        finally
        {
            UI.UnwindTo(outer);
        }
    }

    [UnmanagedCallersOnly]
    private static void Shutdown()
    {
        try
        {
            ModManager.ForEach(nameof(CoreMod.OnShutdown), mod => mod.OnShutdown());
            ModConfig.FlushAll();
            VarHarvest.Flush();
            TestHost.Stop();
            Log.Info("shut down");
        }
        catch (Exception ex)
        {
            Log.Error("shutdown dispatch failed", ex);
        }
    }

    private static void DrawLoaderTab()
    {
        UI.Text($"Game: {Game.Name}");
        UI.Text($"GML functions: {Game.Symbols.Count:N0}   builtins: {Game.BuiltinCount:N0}");
        UI.Text($"GML bridge: {(Game.IsGmlReady ? "ready" : "unavailable")}   " +
                $"ABI self-test: {(Game.IsAbiProven ? "passed" : "not passed")}");
        UI.Text($"Hooked functions: {Hooks.NativeHookCount}   value lifetime: " +
                $"{(Values.CanFree ? "free" : "no free")}/{(Values.CanCopy ? "copy" : "no copy")}");
        UI.TextDisabled($"Interop: {InteropGenerator.Status}");
        UI.TextDisabled($"Variables harvested: {VarHarvest.Known.Sum(kv => kv.Value.Count):N0} on " +
                        $"{VarHarvest.Known.Count:N0} objects ({VarHarvest.LearnedThisSession:N0} new this session)");
        if (UI.Button("Regenerate interop now")) InteropGenerator.RequestRegenerate();
        UI.TextDisabled($".NET {Environment.Version}   mods folder: {ModManager.ModsDirectory}");
        UI.Separator();

        bool hot = ModManager.HotReload;
        if (UI.Checkbox("Hot reload (rebuilt mods reload automatically)", ref hot)) ModManager.HotReload = hot;
        UI.SameLine();
        // Deferred to the next frame: reloading from inside the GUI pass would
        // unload the code that is drawing right now.
        if (UI.Button("Reload all")) Game.RunOnGameThread(ModManager.ReloadAll);

        if (ModManager.Mods.Count == 0) UI.TextDisabled("No mods loaded.");

        foreach (var n in ModManager.NotLoaded)
        {
            var line = $"{n.Name} ({System.IO.Path.GetFileName(n.Path)})";
            if (n.Refused) UI.TextColored(1f, 0.45f, 0.45f, $"{line} - not loaded: {n.Reason}");
            else UI.TextDisabled($"{line} - skipped: {n.Reason}");
        }

        foreach (var m in ModManager.Mods)
        {
            var i = m.Instance.Info;
            var line = $"{i.Name} {i.Version} by {i.Author}";
            UI.PushId(m.Path);
            if (UI.Button("Reload")) { var target = m; Game.RunOnGameThread(() => ModManager.Reload(target)); }
            UI.SameLine();
            switch (m.State)
            {
                case ModState.Faulted: UI.TextColored(1f, 0.45f, 0.45f, $"{line} - disabled: {m.Fault}"); break;
                case ModState.Running:
                    int assets = Content.CountOwned(m);
                    UI.Text($"{line} - running, {Hooks.SubscriptionCount(m)} hook(s)" +
                            (assets > 0 ? $", {assets} asset(s)" : ""));
                    break;
                default: UI.TextDisabled($"{line} - waiting for the game to load its assets"); break;
            }
            UI.PopId();
        }
    }
}
