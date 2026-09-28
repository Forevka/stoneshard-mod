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
    private static readonly Logger Log = new("CoreLoader");
    private static bool _initialisedMods;

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

            // Freeing or copying an undefined value is a no-op for the runtime
            // but still reports whether its helper was found.
            RValue probe = RValue.Undefined, probe2 = RValue.Undefined;
            Values.CanFree = api->ValueFree(&probe) != 0;
            Values.CanCopy = api->ValueCopy(&probe2, &probe) != 0;

            Log.Info($"CoreLoader {typeof(Entry).Assembly.GetName().Version} on .NET {Environment.Version}, " +
                     $"game '{Game.Name}', {api->SymbolCount()} GML functions, " +
                     $"value free {(Values.CanFree ? "yes" : "no")} / copy {(Values.CanCopy ? "yes" : "no")}");

            ModManager.DiscoverAndLoad();

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

    // Both Frame and Gui run on the game thread, and either can be the first
    // call the runtime receives, so both go through here.
    private static void EnsureModsInitialised()
    {
        Loader.MarkGameThread();
        if (_initialisedMods) return;
        _initialisedMods = true;
        // Attribute hooks first, so OnInitialize can rely on them being live.
        foreach (var m in ModManager.Mods.ToList()) ModManager.Initialize(m);
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
        try
        {
            EnsureModsInitialised();
            // Rebuilt mod dlls are swapped in here, between frames, where no
            // mod code is on the stack.
            ModManager.PollChanges();
            Game.DrainPending(Log);
            InteropGenerator.Tick();
            ModManager.ForEach(nameof(CoreMod.OnUpdate), mod => mod.OnUpdate());
            ModConfig.FlushSettled();
            Values.Drain();
        }
        catch (Exception ex)
        {
            Log.Error("frame dispatch failed", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void Gui()
    {
        int baseMark = UI.Mark;
        UI.InGui = true;
        try
        {
            EnsureModsInitialised();
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
            UI.UnwindTo(baseMark);
            UI.InGui = false;
            Values.Drain();
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
        UI.TextDisabled($".NET {Environment.Version}   mods folder: {ModManager.ModsDirectory}");
        UI.Separator();

        bool hot = ModManager.HotReload;
        if (UI.Checkbox("Hot reload (rebuilt mods reload automatically)", ref hot)) ModManager.HotReload = hot;
        UI.SameLine();
        // Deferred to the next frame: reloading from inside the GUI pass would
        // unload the code that is drawing right now.
        if (UI.Button("Reload all")) Game.RunOnGameThread(ModManager.ReloadAll);

        if (ModManager.Mods.Count == 0)
        {
            UI.TextDisabled("No mods loaded.");
            return;
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
                case ModState.Running: UI.Text($"{line} - running, {Hooks.SubscriptionCount(m)} hook(s)"); break;
                default: UI.TextDisabled($"{line} - waiting for first frame"); break;
            }
            UI.PopId();
        }
    }
}
