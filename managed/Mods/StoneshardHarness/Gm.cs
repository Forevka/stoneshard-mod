using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// Small readers over live instances: the game stores the same kind of fact as
/// a number, a bool, a reference or undefined depending on who wrote it, and
/// every reader here turns that into one plain value.
/// </summary>
internal static class Gm
{
    /// <summary>The live player, or null (title screen, loading, a cutscene that removed it).</summary>
    public static InstanceRef? Player => Objects.o_player.First is { } p && p.Exists ? p : null;

    public static InstanceRef RequirePlayer() =>
        Player ?? throw new InvalidOperationException("no player: start or load a game and let it run");

    /// <summary>An instance id as a number: the game hands them out as numbers or as references.</summary>
    public static long IdKey(RValue v) =>
        v.IsNumber ? (long)v.AsReal : v.Kind == RValueKind.Reference ? v.Int64 & 0xFFFFFFFF : -1;

    public static long Id(InstanceRef r) => IdKey(r.Id);

    /// <summary>The instance with this id, if it still exists.</summary>
    public static InstanceRef? ById(long id)
    {
        if (id < 0) return null;
        var r = new InstanceRef(RValue.FromReal(id));
        return r.Exists ? r : null;
    }

    public static InstanceRef RequireById(long id, string what) =>
        ById(id) ?? throw new ArgumentException($"no live {what} with id {id}");

    // The game stores flags as 0/1, true/false or undefined depending on who wrote them.
    public static bool Truthy(RValue v) => v.IsNumber ? v.AsReal > 0 : v.Kind == RValueKind.String ? v.ToString().Length > 0 : false;

    public static double Num(InstanceRef r, string name, double fallback = 0)
    {
        var v = r.Get(name);
        return v.IsNumber ? v.AsReal : fallback;
    }

    public static double? NumOrNull(InstanceRef r, string name)
    {
        var v = r.Get(name);
        return v.IsNumber ? v.AsReal : null;
    }

    public static string Str(InstanceRef r, string name)
    {
        var v = r.Get(name);
        return v.Kind == RValueKind.String ? v.ToString() : "";
    }

    public static bool Flag(InstanceRef r, string name) => Truthy(r.Get(name));

    public static int ObjectIndex(InstanceRef r)
    {
        var v = r.Get("object_index");
        return v.IsNumber ? (int)v.AsReal : -1;
    }

    public static string ObjectName(InstanceRef r) => GmlObject.FromIndex(ObjectIndex(r))?.Name ?? "?";

    /// <summary>Whether the instance's object is <paramref name="name"/> or one of its children.</summary>
    public static bool IsA(InstanceRef r, string name) => GmlObject.FromIndex(ObjectIndex(r)) is { } o && o.IsA(name);

    /// <summary>The instance's object and its ancestors, nearest first.</summary>
    public static IEnumerable<string> Lineage(InstanceRef r)
    {
        if (GmlObject.FromIndex(ObjectIndex(r)) is not { } o) yield break;
        yield return o.Name;
        foreach (var a in o.Ancestors()) yield return a.Name;
    }

    /// <summary>Live instances of an object and its children; none when the object is unknown.</summary>
    public static IEnumerable<InstanceRef> All(string objectName) =>
        GmlObject.Find(objectName)?.Instances().Where(i => i.Exists) ?? [];

    public static int Count(string objectName) => GmlObject.Find(objectName)?.InstanceCount ?? 0;

    /// <summary>
    /// The first live instance of exactly this object (not a child), or null.
    /// (InstanceRef is a struct: FirstOrDefault would answer an empty one, never null.)
    /// </summary>
    public static InstanceRef? Exact(string objectName)
    {
        foreach (var i in All(objectName))
            if (ObjectName(i) == objectName) return i;
        return null;
    }

    /// <summary>
    /// The event an instance runs for <paramref name="eventSuffix"/> (e.g. "Mouse_5"):
    /// its own object's, or the nearest ancestor's that defines one, as GameMaker
    /// inherits events. Null when no object in the chain has it.
    /// </summary>
    public static string? EventOf(InstanceRef r, string eventSuffix)
    {
        foreach (var name in Lineage(r))
        {
            string symbol = $"gml_Object_{name}_{eventSuffix}";
            if (Game.FindSymbol(symbol) != 0) return symbol;
        }
        return null;
    }

    /// <summary>
    /// Runs the instance's (possibly inherited) event; false when it has none.
    /// <paramref name="other"/> is GML's other in it: when the game runs an
    /// object's event for the player (arriving at a door), other is the player,
    /// and the event checks it.
    /// </summary>
    public static bool RunEvent(InstanceRef r, string eventSuffix, InstanceRef? other = null)
    {
        if (EventOf(r, eventSuffix) is not { } symbol) return false;
        var self = r.Resolve() ?? throw new InvalidOperationException("the instance cannot be resolved to run its event");
        var with = other is { } o ? o.Resolve() ?? self : self;
        Game.CallEvent(symbol, self, with);
        return true;
    }

    /// <summary>A number from a GML array element, or the fallback.</summary>
    public static double ArrNum(RValue arr, int i, double fallback = 0)
    {
        if (Gml.TypeOf(arr) != "array" || i >= Gml.ArrayLength(arr)) return fallback;
        var v = Gml.ArrayGet(arr, i);
        return v.IsNumber ? v.AsReal : fallback;
    }

    /// <summary>
    /// Text without the game's colour tags ("~lg~...~/~") and without a leading
    /// option number ("3. "), as a reader sees it.
    /// </summary>
    public static string Plain(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text, "~(/|[a-zA-Z]{1,3})~", ""), @"^\d+\.\s+", "").Trim();

    /// <summary>Tile distance as the game counts it: diagonal steps cost one (Chebyshev).</summary>
    public static int Tiles((int X, int Y) a, (int X, int Y) b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static double Round(double v, int digits = 1) => Math.Round(v, digits);
}
