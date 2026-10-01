namespace CoreLoader;

/// <summary>
/// Base class for a mod. The loader instantiates the type named by the assembly's
/// <see cref="CoreModInfoAttribute"/> and drives these callbacks, all of which run
/// on the game thread. An exception from any of them is logged and disables the
/// mod; it never reaches the game.
/// </summary>
public abstract class CoreMod
{
    /// <summary>Metadata from the assembly's <see cref="CoreModInfoAttribute"/>.</summary>
    public CoreModInfoAttribute Info { get; internal set; } = null!;

    /// <summary>A logger tagged with this mod's name.</summary>
    public Logger Log { get; internal set; } = null!;

    /// <summary>Folder the mod's dll was loaded from.</summary>
    public string Directory { get; internal set; } = "";

    /// <summary>Persistent settings, saved as Mods/&lt;AssemblyName&gt;.json.</summary>
    public ModConfig Config { get; internal set; } = null!;

    /// <summary>Once, on the first frame after loading.</summary>
    public virtual void OnInitialize() { }

    /// <summary>Every rendered frame.</summary>
    public virtual void OnUpdate() { }

    /// <summary>
    /// Draws this mod's tab in the loader overlay. Only called while the tab is
    /// open; use <see cref="UI"/>.
    /// </summary>
    public virtual void OnGUI() { }

    /// <summary>When the game window is closing.</summary>
    public virtual void OnShutdown() { }
}

/// <summary>Marks an assembly as a CoreLoader mod and names its entry type.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CoreModInfoAttribute : Attribute
{
    public Type ModType { get; }
    public string Name { get; }
    public string Version { get; }
    public string Author { get; }

    public CoreModInfoAttribute(Type modType, string name, string version, string author)
    {
        ModType = modType;
        Name = name;
        Version = version;
        Author = author;
    }
}

/// <summary>
/// Restricts a mod to games whose exe name (without extension) matches one of
/// these, case-insensitively: <c>[assembly: CoreModGame("StoneShard")]</c>. The
/// game's interop namespace is accepted as well ("Dwarf_Eats_Mountain").
/// Every mod says which games it is for, with this or with
/// <see cref="CoreModAnyGameAttribute"/>; the loader refuses a mod that says
/// neither, rather than run code written for one game inside another.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CoreModGameAttribute : Attribute
{
    public string[] Games { get; }

    public CoreModGameAttribute(params string[] games) => Games = games;
}

/// <summary>
/// Declares a mod that works in any game: it relies on nothing a particular
/// game defines (no object, script or variable names of its own), like the
/// Console or SpeedControl. The alternative to <see cref="CoreModGameAttribute"/>;
/// a mod carries exactly one of the two.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CoreModAnyGameAttribute : Attribute
{
}
