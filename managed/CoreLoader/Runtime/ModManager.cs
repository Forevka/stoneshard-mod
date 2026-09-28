using System.Reflection;
using System.Runtime.Loader;

namespace CoreLoader.Runtime;

internal enum ModState
{
    Loaded,
    Running,
    Faulted,
}

internal sealed class LoadedMod
{
    public required CoreMod Instance { get; init; }
    public required string Path { get; init; }
    public ModState State { get; set; } = ModState.Loaded;
    public string? Fault { get; set; }
}

/// <summary>
/// Each mod gets its own load context so two mods can ship different versions
/// of the same dependency. CoreLoader itself always resolves to the one copy
/// already running, or a mod's CoreMod would be a different type from ours.
/// </summary>
internal sealed class ModLoadContext : AssemblyLoadContext
{
    private static readonly Assembly Self = typeof(CoreMod).Assembly;
    private readonly AssemblyDependencyResolver _resolver;

    public ModLoadContext(string mainAssemblyPath)
        : base(System.IO.Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (string.Equals(name.Name, Self.GetName().Name, StringComparison.OrdinalIgnoreCase))
            return Self;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : 0;
    }
}

internal static class ModManager
{
    private static readonly Logger Log = new("CoreLoader");
    private static readonly List<LoadedMod> ModList = new();

    public static IReadOnlyList<LoadedMod> Mods => ModList;

    public static string ModsDirectory => System.IO.Path.Combine(Game.Directory, "Mods");

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
                return;
            }

            candidates = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).ToList();
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var main = System.IO.Path.Combine(sub, System.IO.Path.GetFileName(sub) + ".dll");
                if (File.Exists(main)) candidates.Add(main);
            }
        }
        catch (Exception ex)
        {
            // A read-only or locked install must not take the loader down with it.
            Log.Error($"could not read the mods folder {dir}", ex);
            return;
        }

        foreach (var path in candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            TryLoad(path);

        Log.Info($"{ModList.Count} mod(s) loaded from {dir}");
    }

    private static void TryLoad(string path)
    {
        string file = System.IO.Path.GetFileName(path);
        try
        {
            if (!HasModInfo(path)) return;

            var ctx = new ModLoadContext(path);
            var asm = ctx.LoadFromAssemblyPath(path);
            var info = asm.GetCustomAttribute<CoreModInfoAttribute>()!;
            if (info.ModType == null || string.IsNullOrWhiteSpace(info.Name) ||
                string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.Author))
            {
                Log.Error($"{file}: [CoreModInfo] needs a mod type, name, version and author");
                return;
            }

            var games = asm.GetCustomAttribute<CoreModGameAttribute>();
            if (games != null && !games.Games.Any(g => string.Equals(g, Game.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Info($"skipping {info.Name}: it targets {string.Join(", ", games.Games)}, not {Game.Name}");
                return;
            }

            if (!typeof(CoreMod).IsAssignableFrom(info.ModType) || info.ModType.IsAbstract)
            {
                Log.Error($"{file}: {info.ModType.FullName} must be a non-abstract subclass of CoreMod");
                return;
            }

            var mod = (CoreMod)Activator.CreateInstance(info.ModType)!;
            mod.Info = info;
            mod.Log = new Logger(info.Name);
            mod.Directory = System.IO.Path.GetDirectoryName(path)!;

            ModList.Add(new LoadedMod { Instance = mod, Path = path });
            Log.Info($"loaded {info.Name} {info.Version} by {info.Author} ({file})");
        }
        catch (Exception ex)
        {
            Log.Error($"failed to load {file}", ex);
        }
    }

    // Checks for the attribute through metadata only, so a dependency dll in the
    // Mods folder is never loaded (and never locked into a context) by accident.
    private static bool HasModInfo(string path)
    {
        using var stream = File.OpenRead(path);
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
        foreach (var m in ModList)
        {
            if (m.State == ModState.Faulted) continue;
            Invoke(m, callback, action);
        }
    }

    public static void Invoke(LoadedMod m, string callback, Action<CoreMod> action)
    {
        try
        {
            action(m.Instance);
        }
        catch (Exception ex)
        {
            m.State = ModState.Faulted;
            m.Fault = $"{callback}: {ex.GetType().Name}: {ex.Message}";
            m.Instance.Log.Error($"{callback} threw - the mod is disabled for this session", ex);
        }
    }

    public static void Fault(LoadedMod m, string reason)
    {
        m.State = ModState.Faulted;
        m.Fault = reason;
        m.Instance.Log.Error($"{reason} - the mod is disabled for this session");
    }
}
