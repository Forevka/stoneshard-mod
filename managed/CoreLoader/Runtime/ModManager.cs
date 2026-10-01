using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace CoreLoader.Runtime;

internal enum ModState
{
    Loaded,
    Running,
    Faulted,
}

/// <summary>A mod file that was not loaded into this game, and why.</summary>
internal sealed record NotLoadedMod(string Path, string Name, string Reason, bool Refused);

internal sealed class LoadedMod
{
    // Set once construction succeeds; everything that reads it runs after that.
    public CoreMod Instance { get; set; } = null!;
    public required string Path { get; init; }
    public required ModLoadContext Context { get; init; }
    public ModState State { get; set; } = ModState.Loaded;
    public string? Fault { get; set; }
    public int Generation { get; init; }
}

/// <summary>
/// Each mod gets its own load context so two mods can ship different versions
/// of the same dependency, and so a mod can be unloaded and loaded again (hot
/// reload). CoreLoader itself always resolves to the one copy already running,
/// or a mod's CoreMod would be a different type from ours.
///
/// Assemblies are loaded from memory, never by path: the files stay unlocked,
/// so a build can overwrite a mod while the game runs.
/// </summary>
internal sealed class ModLoadContext : AssemblyLoadContext
{
    private static readonly Assembly Self = typeof(CoreMod).Assembly;
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;

    /// <summary>The mod this context was created for (set before its type is constructed).</summary>
    public LoadedMod? Owner { get; set; }

    public ModLoadContext(string mainAssemblyPath)
        : base(System.IO.Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _directory = System.IO.Path.GetDirectoryName(mainAssemblyPath)!;
    }

    public Assembly LoadUnlocked(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pdb = System.IO.Path.ChangeExtension(path, ".pdb");
        using var asm = new MemoryStream(bytes);
        if (File.Exists(pdb))
        {
            using var sym = new MemoryStream(File.ReadAllBytes(pdb));
            return LoadFromStream(asm, sym);
        }
        return LoadFromStream(asm);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (string.Equals(name.Name, Self.GetName().Name, StringComparison.OrdinalIgnoreCase))
            return Self;
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path != null) return LoadUnlocked(path);

        // Mods are often deployed as bare dlls without their deps.json; a
        // dependency (such as a generated <Game>.Interop.dll) next to the mod
        // is still the right one.
        var local = System.IO.Path.Combine(_directory, name.Name + ".dll");
        return File.Exists(local) ? LoadUnlocked(local) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : 0;
    }
}

internal static class ModManager
{
    private static readonly Logger Log = new("Lodestone");
    private static readonly List<LoadedMod> ModList = new();
    private static FileSystemWatcher? _watcher;
    private static readonly ConcurrentDictionary<string, long> Changed = new(StringComparer.OrdinalIgnoreCase);
    private static int _generation;

    // Writes arrive in bursts (dll, then pdb, then the dll again); wait for quiet.
    private const long SettleMs = 700;

    public static IReadOnlyList<LoadedMod> Mods => ModList;

    // Mods found in the folder but not loaded here, kept for the Loader tab and
    // the test host's status: the log line alone is easy to miss.
    private static readonly List<NotLoadedMod> NotLoadedList = new();

    /// <summary>Mods skipped as being for another game, or refused for not declaring one.</summary>
    public static IReadOnlyList<NotLoadedMod> NotLoaded => NotLoadedList;

    public static string ModsDirectory => System.IO.Path.Combine(Game.Directory, "Mods");

    /// <summary>Set once mods have been started (after the game loaded its assets).</summary>
    public static bool Started { get; internal set; }

    /// <summary>Whether changed mod files are reloaded automatically.</summary>
    public static bool HotReload { get; set; } = true;

    /// <summary>
    /// Finds Mods/*.dll and Mods/&lt;Name&gt;/&lt;Name&gt;.dll. Only assemblies that carry
    /// [CoreModInfo] are treated as mods; anything else in the folder is assumed
    /// to be a dependency and left for a mod's load context to pick up.
    /// </summary>
    public static void DiscoverAndLoad()
    {
        var dir = ModsDirectory;
        List<string> candidates;
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                Log.Info($"created {dir} - put mod dlls here");
            }
            candidates = Candidates(dir);
        }
        catch (Exception ex)
        {
            // A read-only or locked install must not take the loader down with it.
            Log.Error($"could not read the mods folder {dir}", ex);
            return;
        }

        foreach (var path in candidates)
            if (TryLoad(path) is { } m) ModList.Add(m);

        Log.Info($"{ModList.Count} mod(s) loaded from {dir}");
        StartWatching(dir);
    }

    private static List<string> Candidates(string dir)
    {
        var list = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).ToList();
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var main = System.IO.Path.Combine(sub, System.IO.Path.GetFileName(sub) + ".dll");
            if (File.Exists(main)) list.Add(main);
        }
        return list.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Why a mod is not for this game, or null when it is. A mod for other
    /// games is skipped quietly (one mods folder may serve several games); one
    /// that does not say which games it is for is refused, so its author hears
    /// about it the first time it is run anywhere.
    /// </summary>
    private static NotLoadedMod? CheckGame(Assembly asm, string name)
    {
        var games = asm.GetCustomAttribute<CoreModGameAttribute>();
        bool any = asm.GetCustomAttribute<CoreModAnyGameAttribute>() != null;
        string[] named = (games?.Games ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();

        string? refusal = (games, any) switch
        {
            (null, false) => $"{name} does not say which game it is for. Add [assembly: CoreModGame(\"{Game.Name}\")] " +
                             "(the game's exe name; list several if it supports more) for a mod written for particular " +
                             "games, or [assembly: CoreModAnyGame] for one that works in any game",
            ({ }, true) => $"{name} carries both [CoreModGame] and [CoreModAnyGame]; keep the one that is true",
            ({ }, false) when named.Length == 0 => $"{name}'s [CoreModGame] names no game; name at least one exe, " +
                                                   "or use [CoreModAnyGame] instead",
            _ => null,
        };
        if (refusal != null) return new NotLoadedMod("", name, refusal, Refused: true);
        // The exe name, or the game's interop namespace (Dwarf_Eats_Mountain for
        // "Dwarf Eats Mountain"), which is what an interop-based mod knows it by.
        if (any || named.Any(g => string.Equals(g.Trim(), Game.Name, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(g.Trim().TrimStart('_'), InteropGenerator.SafeGameName.TrimStart('_'),
                                                StringComparison.OrdinalIgnoreCase)))
            return null;
        return new NotLoadedMod("", name, $"it is for {string.Join(", ", named)}, not {Game.Name}", Refused: false);
    }

    private static bool IsNotLoaded(string path) =>
        NotLoadedList.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    private static void ForgetNotLoaded(string path) =>
        NotLoadedList.RemoveAll(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    private static LoadedMod? TryLoad(string path)
    {
        string file = System.IO.Path.GetFileName(path);
        // A rebuilt file is judged afresh.
        ForgetNotLoaded(path);
        try
        {
            if (!HasModInfo(path)) return null;

            var ctx = new ModLoadContext(path);
            var asm = ctx.LoadUnlocked(path);
            var info = asm.GetCustomAttribute<CoreModInfoAttribute>()!;
            if (info.ModType == null || string.IsNullOrWhiteSpace(info.Name) ||
                string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.Author))
            {
                Log.Error($"{file}: [CoreModInfo] needs a mod type, name, version and author");
                ctx.Unload();
                return null;
            }

            if (CheckGame(asm, info.Name) is { } notHere)
            {
                if (notHere.Refused) Log.Error($"{file}: {notHere.Reason}");
                else Log.Info($"skipping {info.Name}: {notHere.Reason}");
                NotLoadedList.Add(notHere with { Path = path, Name = info.Name });
                ctx.Unload();
                return null;
            }

            if (!typeof(CoreMod).IsAssignableFrom(info.ModType) || info.ModType.IsAbstract)
            {
                Log.Error($"{file}: {info.ModType.FullName} must be a non-abstract subclass of CoreMod");
                ctx.Unload();
                return null;
            }

            // The record exists before the mod's constructor runs, and is the
            // current mod while it does: whatever a constructor or field
            // initialiser registers belongs to this mod and goes with it.
            var loaded = new LoadedMod { Path = path, Context = ctx, Generation = ++_generation };
            ctx.Owner = loaded;
            var previous = Current;
            Current = loaded;
            try
            {
                var mod = (CoreMod)Activator.CreateInstance(info.ModType)!;
                mod.Info = info;
                mod.Log = new Logger(info.Name);
                mod.Directory = System.IO.Path.GetDirectoryName(path)!;
                mod.Config = new ModConfig(
                    System.IO.Path.Combine(mod.Directory, System.IO.Path.GetFileNameWithoutExtension(path) + ".json"),
                    mod.Log);
                loaded.Instance = mod;
            }
            catch
            {
                // Nothing of a half-built mod may outlive it, nor its context.
                RemoveRegistrations(loaded);
                ctx.Unload();
                throw;
            }
            finally
            {
                Current = previous;
            }

            Log.Info($"loaded {info.Name} {info.Version} by {info.Author} ({file})");
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Error($"failed to load {file}", ex);
            return null;
        }
    }

    /// <summary>Everything a mod registered with the loader, torn down. Game thread.</summary>
    private static void RemoveRegistrations(LoadedMod m)
    {
        Hooks.RemoveOwner(m);
        GameDraw.RemoveOwner(m);
        Input.RemoveOwner(m);
        // After OnShutdown, which is where a mod points instances away from
        // its sprites before they go.
        Content.RemoveOwner(m);
        Values.RemoveOwner(m);
        TestHost.RemoveOwner(m);
        ModSettings.RemoveOwner(m);
    }

    /// <summary>
    /// The mod a callback belongs to. On the game thread inside a mod's
    /// callback that is the current mod; anywhere else (a constructor, a
    /// Task, a timer) it is read from the load context of the code itself.
    /// </summary>
    internal static LoadedMod? OwnerOf(Delegate callback)
    {
        if (Current != null && Loader.OnGameThread) return Current;
        return AssemblyLoadContext.GetLoadContext(callback.Method.Module.Assembly) is ModLoadContext ctx
            ? ctx.Owner
            : Loader.OnGameThread ? Current : null;
    }

    /// <summary>Hooks from attributes, then OnInitialize. Game thread.</summary>
    public static void Initialize(LoadedMod m)
    {
        Invoke(m, "hook attributes", _ => Hooks.AttachAttributes(m));
        if (m.State != ModState.Faulted) Invoke(m, nameof(CoreMod.OnInitialize), mod => mod.OnInitialize());
        if (m.State != ModState.Faulted) m.State = ModState.Running;
    }

    /// <summary>OnShutdown, then everything the mod registered goes, then its context. Game thread.</summary>
    private static void Unload(LoadedMod m)
    {
        try
        {
            // Only a mod that actually started gets a shutdown.
            if (m.State == ModState.Running) Invoke(m, nameof(CoreMod.OnShutdown), mod => mod.OnShutdown());
            RemoveRegistrations(m);
            try { m.Instance.Config.Save(); } catch (Exception ex) { Log.Warning($"saving {m.Instance.Info.Name}'s settings: {ex.Message}"); }
            ModConfig.Unregister(m.Instance.Config);
        }
        finally
        {
            // Whatever failed above, the mod must leave the list and its context.
            ModList.Remove(m);
            m.Context.Owner = null;
            m.Context.Unload();
            Log.Info($"unloaded {m.Instance.Info.Name}");
        }
    }

    /// <summary>Reloads one mod from its file, keeping its place in the list. Game thread.</summary>
    public static void Reload(LoadedMod m)
    {
        // A queued Reload can arrive after the watcher already replaced this
        // entry; reloading the stale one would start a second copy of the mod.
        if (!ModList.Contains(m)) return;
        int index = ModList.IndexOf(m);
        var path = m.Path;
        if (!File.Exists(path))
        {
            Unload(m);
            return;
        }

        // The new copy is loaded first: if the build is still being written, or
        // an antivirus holds the file, the running copy stays - and the file is
        // looked at again shortly - instead of the mod simply disappearing.
        // The running copy's settings are written first, so the new one reads them.
        try { m.Instance.Config.Save(); } catch (Exception ex) { Log.Warning($"saving {m.Instance.Info.Name}'s settings: {ex.Message}"); }
        var fresh = TryLoad(path);
        if (fresh == null && IsNotLoaded(path))
        {
            // The new build is for another game, or refused: a final verdict,
            // not a file still being written. The running copy goes with it.
            RetryCounts.TryRemove(path, out _);
            Unload(m);
            Log.Info($"unloaded {System.IO.Path.GetFileName(path)}: its new build is not loaded here");
            return;
        }
        if (fresh == null)
        {
            if (RetryCounts.AddOrUpdate(path, 1, (_, n) => n + 1) <= 3)
            {
                Changed[path] = Environment.TickCount64;
                Log.Warning($"could not load the new {System.IO.Path.GetFileName(path)} yet; keeping the running copy and trying again");
            }
            else
            {
                RetryCounts.TryRemove(path, out _);
                Log.Warning($"giving up on the new {System.IO.Path.GetFileName(path)}; the running copy stays");
            }
            return;
        }
        RetryCounts.TryRemove(path, out _);

        Unload(m);
        ModList.Insert(Math.Clamp(index, 0, ModList.Count), fresh);
        // Before mods have started (the game is still loading its assets)
        // the fresh copy waits with the rest.
        if (Started) Initialize(fresh);
        Log.Info($"reloaded {fresh.Instance.Info.Name} {fresh.Instance.Info.Version}");
    }

    private static readonly ConcurrentDictionary<string, int> RetryCounts = new(StringComparer.OrdinalIgnoreCase);

    public static void ReloadAll()
    {
        // Files deleted since: nothing to report about them any more.
        NotLoadedList.RemoveAll(n => !File.Exists(n.Path));
        foreach (var m in ModList.ToList()) Reload(m);
        // Mods added to the folder since startup.
        foreach (var path in Candidates(ModsDirectory))
        {
            if (ModList.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (TryLoad(path) is { } m)
            {
                ModList.Add(m);
                if (Started) Initialize(m);
            }
        }
    }

    private static void StartWatching(string dir)
    {
        try
        {
            _watcher = new FileSystemWatcher(dir, "*.dll")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler note = (_, e) => Changed[e.FullPath] = Environment.TickCount64;
            _watcher.Changed += note;
            _watcher.Created += note;
            _watcher.Deleted += note;
            _watcher.Renamed += (_, e) =>
            {
                // Both ends: the old name unloads, the new one loads.
                Changed[e.OldFullPath] = Environment.TickCount64;
                Changed[e.FullPath] = Environment.TickCount64;
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warning($"hot reload unavailable (cannot watch {dir}): {ex.Message}");
        }
    }

    /// <summary>Called every frame on the game thread: applies file changes that have settled.</summary>
    public static void PollChanges()
    {
        if (!HotReload || Changed.IsEmpty) return;
        long now = Environment.TickCount64;
        var ready = Changed.Where(kv => now - kv.Value >= SettleMs).Select(kv => kv.Key).ToList();
        if (ready.Count == 0) return;
        foreach (var p in ready) Changed.TryRemove(p, out _);

        var reload = new HashSet<LoadedMod>();
        var added = new List<string>();
        foreach (var path in ready)
        {
            var mod = ModList.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            if (mod != null) { reload.Add(mod); continue; }
            bool isMod;
            try { isMod = File.Exists(path) && HasModInfo(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                Changed[path] = now;   // still being written (or not a PE yet): look again later
                continue;
            }
            if (isMod) { added.Add(path); continue; }
            // Deleted, or no longer a mod: nothing to report about it any more.
            ForgetNotLoaded(path);

            // A dependency changed (e.g. a rebuilt <Game>.Interop.dll): reload the
            // mods that actually loaded an assembly by that name.
            var depName = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (var m in ModList.Where(m => m.Context.Assemblies.Any(a =>
                         string.Equals(a.GetName().Name, depName, StringComparison.OrdinalIgnoreCase))))
                reload.Add(m);
        }

        foreach (var m in reload) Reload(m);
        foreach (var path in added)
            if (TryLoad(path) is { } m) { ModList.Add(m); Initialize(m); }
    }

    // Checks for the attribute through metadata only, so a dependency dll in the
    // Mods folder is never loaded (and never locked into a context) by accident.
    private static bool HasModInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!pe.HasMetadata) return false;
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(h);
            if (attr.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference) continue;
            var ctor = md.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attr.Constructor);
            if (ctor.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference) continue;
            var type = md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)ctor.Parent);
            if (md.GetString(type.Name) == nameof(CoreModInfoAttribute) &&
                md.GetString(type.Namespace) == typeof(CoreModInfoAttribute).Namespace)
                return true;
        }
        return false;
    }

    /// <summary>Runs one callback for every healthy mod; a throw faults only that mod.</summary>
    public static void ForEach(string callback, Action<CoreMod> action)
    {
        foreach (var m in ModList.ToList())
        {
            // Not started yet (waiting for the game's assets) or disabled.
            if (m.State != ModState.Running) continue;
            Invoke(m, callback, action);
        }
    }

    /// <summary>
    /// The mod whose callback is running, so whatever it registers (hooks)
    /// is attributed to it and torn down with it if it faults or reloads.
    /// </summary>
    public static LoadedMod? Current { get; internal set; }

    public static void Invoke(LoadedMod m, string callback, Action<CoreMod> action)
    {
        var previous = Current;
        Current = m;
        try
        {
            action(m.Instance);
        }
        catch (Exception ex)
        {
            Fault(m, $"{callback}: {ex.GetType().Name}: {ex.Message}", ex);
        }
        finally
        {
            Current = previous;
        }
    }

    public static void Fault(LoadedMod m, string reason, Exception? ex = null)
    {
        if (m.State == ModState.Faulted) return;
        m.State = ModState.Faulted;
        m.Fault = reason;
        Hooks.RemoveOwner(m);
        // Its drawing and sounds stop, and a pick it armed is cancelled; its
        // sprites stay until it unloads, as instances may still show them.
        GameDraw.RemoveOwner(m);
        Content.StopSounds(m);
        Input.RemoveOwner(m);
        // Its settings' callbacks are its code too.
        ModSettings.RemoveOwner(m);
        if (ex != null) m.Instance.Log.Error($"{reason} - the mod is disabled until it is reloaded", ex);
        else m.Instance.Log.Error($"{reason} - the mod is disabled until it is reloaded");
    }
}
