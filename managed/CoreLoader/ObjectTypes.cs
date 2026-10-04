using CoreLoader.Native;
using CoreLoader.Runtime;

namespace CoreLoader;

/// <summary>
/// A GameMaker object event: the event type and its subtype (GML's
/// <c>event_type</c> / <c>event_number</c>).
/// </summary>
public readonly record struct GameEvent(int Type, int Subtype)
{
    public static GameEvent Create => new(0, 0);
    public static GameEvent Destroy => new(1, 0);
    /// <summary>Alarm 0..11.</summary>
    public static GameEvent Alarm(int n) => new(2, CheckRange(n, 0, 11));
    public static GameEvent Step => new(3, 0);
    public static GameEvent BeginStep => new(3, 1);
    public static GameEvent EndStep => new(3, 2);
    /// <summary>Collision with instances of <paramref name="other"/> (and its children).</summary>
    public static GameEvent Collision(GmlObject other) => new(4, other.Index);
    /// <summary>Key held, by virtual key code.</summary>
    public static GameEvent Keyboard(int key) => new(5, key);
    /// <summary>A mouse event, by GameMaker's mouse event number (0 left button ... 60 wheel up).</summary>
    public static GameEvent Mouse(int n) => new(6, n);
    /// <summary>An "Other" event by number (0 outside room ... 7 animation end, 10+ user events, 60+ async).</summary>
    public static GameEvent Other(int n) => new(7, n);
    public static GameEvent GameStart => Other(2);
    public static GameEvent GameEnd => Other(3);
    public static GameEvent RoomStart => Other(4);
    public static GameEvent RoomEnd => Other(5);
    public static GameEvent AnimationEnd => Other(7);
    /// <summary>User event 0..15 (what <c>event_user(n)</c> runs).</summary>
    public static GameEvent User(int n) => Other(10 + CheckRange(n, 0, 15));
    public static GameEvent AsyncHttp => Other(62);
    public static GameEvent AsyncNetworking => Other(68);
    public static GameEvent AsyncSaveLoad => Other(72);
    public static GameEvent AsyncSystem => Other(75);
    public static GameEvent Draw => new(8, 0);
    public static GameEvent DrawGui => new(8, 64);
    public static GameEvent DrawBegin => new(8, 72);
    public static GameEvent DrawEnd => new(8, 73);
    public static GameEvent DrawGuiBegin => new(8, 74);
    public static GameEvent DrawGuiEnd => new(8, 75);
    public static GameEvent PreDraw => new(8, 76);
    public static GameEvent PostDraw => new(8, 77);
    /// <summary>Key pressed, by virtual key code.</summary>
    public static GameEvent KeyPress(int key) => new(9, key);
    /// <summary>Key released, by virtual key code.</summary>
    public static GameEvent KeyRelease(int key) => new(10, key);
    public static GameEvent CleanUp => new(12, 0);

    private static readonly string[] TypeNames =
    [
        "Create", "Destroy", "Alarm", "Step", "Collision", "Keyboard", "Mouse", "Other", "Draw",
        "KeyPress", "KeyRelease", "Trigger", "CleanUp", "Gesture", "PreCreate",
    ];

    /// <summary>The event the way compiled code names it, e.g. "Step_0".</summary>
    public override string ToString() =>
        (Type >= 0 && Type < TypeNames.Length ? TypeNames[Type] : $"Event{Type}") + "_" + Subtype;

    private static int CheckRange(int n, int min, int max) =>
        n >= min && n <= max ? n : throw new ArgumentOutOfRangeException(nameof(n), $"must be {min}..{max}");
}

/// <summary>One call of an event of a defined object, as its handler sees it. Only valid during the handler.</summary>
public readonly unsafe struct ObjectEventCall
{
    private readonly HookCall _call;

    internal ObjectEventCall(HookCall call, ObjectType type, GameEvent ev)
    {
        _call = call;
        Type = type;
        Event = ev;
    }

    /// <summary>The instance the event runs for (an instance of the type, or of a child of it).</summary>
    public Instance Self => _call.Self;

    /// <summary>GML's <c>other</c>: in a collision event, the instance collided with.</summary>
    public Instance Other => _call.Other;

    /// <summary>The type whose handler this is.</summary>
    public ObjectType Type { get; }

    public GameEvent Event { get; }

    /// <summary>
    /// Runs the parent object's code for this event, as <c>event_inherited()</c>
    /// does in GML. False when the parent has no such event. A defined event
    /// replaces what it would have inherited, so a child of a game object that
    /// still wants the parent's behaviour calls this (usually first).
    /// </summary>
    public bool CallInherited()
    {
        Loader.EnsureGameThread();
        return Loader.Api->ObjtypeCallInherited(_call.Raw) != 0;
    }
}

/// <summary>Handles one event of a defined object.</summary>
public delegate void ObjectEventHandler(ObjectEventCall call);

/// <summary>
/// A GameMaker object defined by a mod (<see cref="ObjectTypes.Define"/>): a
/// real object, with an index the game's builtins accept, whose events run C#.
/// </summary>
public sealed class ObjectType
{
    private readonly Dictionary<GameEvent, HookHandle> _handlers = new();

    internal ObjectType(string name, int index, LoadedMod? owner)
    {
        Name = name;
        Index = index;
        Owner = owner;
    }

    public string Name { get; }

    /// <summary>The object index: what <c>instance_create_depth</c>, <c>object_get_name</c> and the rest take.</summary>
    public int Index { get; }

    public GmlObject Object => new(Index, Name);

    internal LoadedMod? Owner { get; }

    /// <summary>The events this type implements itself.</summary>
    public IReadOnlyCollection<GameEvent> Events => _handlers.Keys;

    /// <summary>
    /// Implements <paramref name="ev"/> with <paramref name="handler"/>,
    /// replacing an earlier handler for it. Returns the type, for chaining.
    /// </summary>
    /// <remarks>
    /// The event replaces the parent's: call <see cref="ObjectEventCall.CallInherited"/>
    /// to run the parent's code too. An exception from the handler faults the
    /// mod, exactly as a throwing hook does; the event then falls back to the
    /// parent's code until the mod is reloaded.
    /// </remarks>
    public unsafe ObjectType On(GameEvent ev, ObjectEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Loader.EnsureGameThread();
        int id = Loader.Api->ObjtypeEvent(Index, ev.Type, ev.Subtype);
        if (id < 0) throw new GmlException($"could not give {Name} a {ev} event (see the loader log)");

        if (_handlers.Remove(ev, out var old)) old.Dispose();
        var owner = ModManager.OwnerOf(handler);
        _handlers[ev] = Hooks.AddDefined(id, $"gml_Object_{Name}_{ev}", call => handler(new ObjectEventCall(call, this, ev)), owner);
        return this;
    }

    /// <summary>Creates an instance at (x, y) on <paramref name="depth"/>; its Create event runs before this returns.</summary>
    public InstanceRef Create(double x, double y, double depth = 0) =>
        new(Game.CallBuiltin("instance_create_depth", x, y, depth, Index));

    /// <summary>Live instances of this type, including those of types that inherit from it.</summary>
    public int InstanceCount => Object.InstanceCount;

    public IEnumerable<InstanceRef> Instances() => Object.Instances();

    /// <summary>
    /// Destroys every live instance of the type (and of types inheriting from
    /// it). Destroy events do not run; Clean Up events do.
    /// </summary>
    public void DestroyAll()
    {
        Loader.EnsureGameThread();
        // Collected first: destroying while walking instance_find shifts the indices.
        var ids = Instances().ToList();
        bool withFlag = Game.BuiltinArity("instance_destroy") is int a && a != 1;
        foreach (var inst in ids)
        {
            if (withFlag) Game.CallBuiltin("instance_destroy", inst.Id, false);
            else Game.CallBuiltin("instance_destroy", inst.Id);
        }
    }

    internal void Detach()
    {
        foreach (var h in _handlers.Values) h.Dispose();
        _handlers.Clear();
    }

    public override string ToString() => $"{Name} ({Index})";
}

/// <summary>
/// New GameMaker objects, defined at runtime from C#: real objects the game's
/// builtins, <c>with</c>, collisions and instance counts all see, whose events
/// are implemented by mod code.
/// </summary>
/// <remarks>
/// An object type belongs to the mod that defined it. When the mod unloads,
/// faults or hot-reloads, its handlers go and every instance of its types is
/// destroyed; the object itself stays (GameMaker cannot delete objects), and a
/// reloaded mod defining the same name gets the same object back.
///
/// Only available where the loader located and proved the runtime's object
/// machinery (<see cref="Available"/>); everywhere else <see cref="Define"/>
/// throws and <see cref="Status"/> says why.
/// </remarks>
public static unsafe class ObjectTypes
{
    private static readonly Logger Log = new("Lodestone");
    private static readonly Dictionary<string, ObjectType> ByName = new(StringComparer.Ordinal);

    /// <summary>Whether objects can be defined in this game.</summary>
    public static bool Available => Status == "available";

    /// <summary>"available", or why not.</summary>
    public static string Status
    {
        get
        {
            Loader.EnsureGameThread();
            return Utf8.Read(Loader.Api->ObjtypeStatus()) ?? "unavailable";
        }
    }

    /// <summary>
    /// Defines a new object, or takes back the one this mod (or its previous
    /// build, before a hot reload) defined under that name.
    /// </summary>
    /// <param name="name">A name no asset of the game uses, e.g. "o_mymod_ghost".</param>
    /// <param name="parent">An object to inherit from: its events run for this one wherever it defines none.</param>
    /// <param name="sprite">The sprite instances start with (also the collision mask unless <paramref name="mask"/> is given).</param>
    /// <param name="visible">Whether instances start visible.</param>
    /// <param name="persistent">Whether instances survive room changes.</param>
    /// <param name="solid">GameMaker's solid flag.</param>
    /// <param name="mask">A sprite to use as the collision mask instead of <paramref name="sprite"/>.</param>
    public static ObjectType Define(string name, string? parent = null, string? sprite = null, bool visible = true,
                                    bool persistent = false, bool solid = false, string? mask = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Loader.EnsureGameThread();
        if (!Available) throw new GmlException($"object types are not available in {Game.Name}: {Status}");

        var owner = ModManager.Current;
        if (ByName.TryGetValue(name, out var existing) && existing.Owner is { } prev && prev != owner &&
            ModManager.Mods.Contains(prev) && prev.State != ModState.Faulted)
            throw new InvalidOperationException($"{name} is already defined by {prev.Instance.Info.Name}");

        int parentIndex = -1;
        if (parent != null)
            parentIndex = (GmlObject.Find(parent) ?? throw new ArgumentException($"no object named {parent}", nameof(parent))).Index;
        int spriteIndex = sprite == null ? -1 : AssetOrThrow(sprite, nameof(sprite));
        int maskIndex = mask == null ? -1 : AssetOrThrow(mask, nameof(mask));

        int index;
        fixed (byte* n = Utf8.Get(name))
            index = Loader.Api->ObjtypeDefine(n, parentIndex);
        if (index < 0) throw new GmlException($"could not define {name} (see the loader log)");

        // A redefinition drops what the previous build attached.
        existing?.Detach();
        var type = new ObjectType(name, index, owner);
        ByName[name] = type;
        ObjectTable.Added(index, name, parentIndex);

        Game.CallBuiltin("object_set_sprite", index, spriteIndex);
        Game.CallBuiltin("object_set_mask", index, maskIndex);
        Game.CallBuiltin("object_set_visible", index, visible);
        Game.CallBuiltin("object_set_persistent", index, persistent);
        Game.CallBuiltin("object_set_solid", index, solid);
        return type;
    }

    /// <summary>A type defined this session, by name, or null.</summary>
    public static ObjectType? Find(string name) => ByName.GetValueOrDefault(name);

    private static int AssetOrThrow(string name, string param)
    {
        int i = ObjectTable.AssetIndex(Game.CallBuiltin("asset_get_index", name));
        return i >= 0 ? i : throw new ArgumentException($"no asset named {name}", param);
    }

    /// <summary>A mod is going away: its types' instances are destroyed and their handlers dropped.</summary>
    internal static void RemoveOwner(LoadedMod owner)
    {
        foreach (var type in ByName.Values.Where(t => t.Owner == owner).ToList())
        {
            type.Detach();
            try
            {
                int before = type.InstanceCount;
                type.DestroyAll();
                if (before > 0) Log.Info($"{type.Name}: destroyed {before} instance(s) of {owner.Instance.Info.Name}'s type");
            }
            catch (Exception ex)
            {
                Log.Warning($"{type.Name}: destroying its instances failed: {ex.Message}");
            }
        }
    }
}
