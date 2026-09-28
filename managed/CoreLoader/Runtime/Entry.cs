using System.Runtime.CompilerServices;
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
            if (api->Version != CoreApi.ExpectedVersion || api->Size < sizeof(CoreApi))
            {
                // Can't use Log yet if the table is the wrong shape.
                return 0;
            }
            Loader.Api = api;

            Log.Info($"CoreLoader {typeof(Entry).Assembly.GetName().Version} on .NET {Environment.Version}, " +
                     $"game '{Game.Name}', {api->SymbolCount()} GML functions");

            ModManager.DiscoverAndLoad();

            exports->Frame = &Frame;
            exports->Gui = &Gui;
            exports->Shutdown = &Shutdown;
            return 1;
        }
        catch (Exception ex)
        {
            try { Log.Error("Init failed", ex); } catch { /* the log itself is gone */ }
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void Frame()
    {
        try
        {
            Loader.MarkGameThread();
            Game.DrainPending(Log);

            if (!_initialisedMods)
            {
                _initialisedMods = true;
                foreach (var m in ModManager.Mods)
                {
                    ModManager.Invoke(m, nameof(CoreMod.OnInitialize), mod => mod.OnInitialize());
                    if (m.State != ModState.Faulted) m.State = ModState.Running;
                }
            }

            ModManager.ForEach(nameof(CoreMod.OnUpdate), mod => mod.OnUpdate());
        }
        catch (Exception ex)
        {
            Log.Error("frame dispatch failed", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void Gui()
    {
        try
        {
            if (!UI.BeginTabBar("##coreloader_mods")) return;
            try
            {
                if (UI.BeginTabItem("Loader"))
                {
                    try { DrawLoaderTab(); }
                    finally { UI.EndTabItem(); }
                }

                foreach (var m in ModManager.Mods)
                {
                    UI.PushId(m.Path);
                    try
                    {
                        if (!UI.BeginTabItem(m.Instance.Info.Name)) continue;
                        try
                        {
                            if (m.State == ModState.Faulted)
                                UI.TextColored(1f, 0.45f, 0.45f, $"Disabled - {m.Fault}");
                            else
                                ModManager.Invoke(m, nameof(CoreMod.OnGUI), mod => mod.OnGUI());
                        }
                        finally { UI.EndTabItem(); }
                    }
                    finally { UI.PopId(); }
                }
            }
            finally { UI.EndTabBar(); }
        }
        catch (Exception ex)
        {
            Log.Error("GUI dispatch failed", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void Shutdown()
    {
        try
        {
            ModManager.ForEach(nameof(CoreMod.OnShutdown), mod => mod.OnShutdown());
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
        UI.TextDisabled($".NET {Environment.Version}   mods folder: {ModManager.ModsDirectory}");
        UI.Separator();

        if (ModManager.Mods.Count == 0)
        {
            UI.TextDisabled("No mods loaded.");
            return;
        }

        foreach (var m in ModManager.Mods)
        {
            var i = m.Instance.Info;
            var line = $"{i.Name} {i.Version} by {i.Author}";
            switch (m.State)
            {
                case ModState.Faulted: UI.TextColored(1f, 0.45f, 0.45f, $"{line} - disabled: {m.Fault}"); break;
                case ModState.Running: UI.Text($"{line} - running"); break;
                default: UI.TextDisabled($"{line} - waiting for first frame"); break;
            }
        }
    }
}
