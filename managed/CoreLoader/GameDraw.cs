using CoreLoader.Runtime;

namespace CoreLoader;

/// <summary>
/// Drawing into the game itself (not the overlay), with GameMaker's own draw_*
/// functions and a mod's <see cref="Sprite"/>s. Works in any game.
/// </summary>
/// <remarks>
/// Handlers run once per frame inside a Draw GUI event, so coordinates are GUI
/// pixels (see <see cref="GuiWidth"/>). The loader borrows the Draw GUI event
/// of some object that has a live instance, and moves to another when a room
/// change removes it; handlers do not see which.
/// </remarks>
public static class GameDraw
{
    private static readonly Logger Log = new("CoreLoader");
    private static readonly List<(Action Draw, LoadedMod? Owner)> Handlers = new();
    private static List<string>? _events;
    private static HookHandle? _carrier;
    private static string? _carrierSymbol;
    private static long _frame, _drawnFrame = -1, _lastFire = -1, _lastPick = -1000;

    /// <summary>
    /// Runs <paramref name="draw"/> once a frame during the game's GUI pass.
    /// Dispose the result to stop; a mod's handlers also stop when it unloads.
    /// </summary>
    public static IDisposable OnGui(Action draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        Loader.EnsureGameThread();
        var entry = (draw, ModManager.Current);
        Handlers.Add(entry);
        return new Registration(() => Handlers.Remove(entry));
    }

    /// <summary>Width of the GUI layer handlers draw on.</summary>
    public static double GuiWidth => Game.CallBuiltin("display_get_gui_width").AsReal;

    /// <summary>Height of the GUI layer handlers draw on.</summary>
    public static double GuiHeight => Game.CallBuiltin("display_get_gui_height").AsReal;

    /// <summary>The Draw GUI event currently carrying the handlers, if any (diagnostics).</summary>
    public static string? Carrier => _carrierSymbol;

    internal static void RemoveOwner(LoadedMod owner) => Handlers.RemoveAll(h => h.Owner == owner);

    /// <summary>Once per frame, between frames: keeps a live Draw GUI event hooked while anyone draws.</summary>
    internal static void Tick()
    {
        _frame++;
        if (Handlers.Count == 0)
        {
            DropCarrier();
            return;
        }
        // Fired recently: fine. Otherwise the carrier's instances are gone (a
        // room change) or never drew: pick another - at most twice a second
        // while nothing qualifies, since a search asks the game about every
        // candidate object.
        if (_carrier != null && _frame - _lastFire <= 30) return;
        if (_carrier == null && _frame - _lastPick < 30) return;
        _lastPick = _frame;
        PickCarrier();
    }

    private static void PickCarrier()
    {
        _events ??= Game.Symbols
            .Select(s => s.Name)
            .Where(n => n.StartsWith("gml_Object_", StringComparison.Ordinal) && n.EndsWith("_Draw_64", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var symbol in _events)
        {
            if (symbol == _carrierSymbol) continue;
            string obj = symbol["gml_Object_".Length..^"_Draw_64".Length];
            if (GmlObject.Find(obj) is not { } o || o.InstanceCount == 0) continue;
            // Invisible instances skip their draw events entirely.
            if (!o.Instance(0).Get("visible").AsBool) continue;

            DropCarrier();
            var previous = ModManager.Current;
            ModManager.Current = null;   // the loader's own hook, not the calling mod's
            try { _carrier = Hooks.After(symbol, _ => Fire()); }
            finally { ModManager.Current = previous; }
            _carrierSymbol = symbol;
            _lastFire = _frame;          // give it a few frames to prove itself
            Log.Info($"game drawing rides on {symbol}");
            return;
        }
        if (_carrier != null)
        {
            // The old carrier stopped firing and nothing else qualifies.
            DropCarrier();
            Log.Warning("no object with a Draw GUI event is alive; mod drawing is paused");
        }
    }

    private static void DropCarrier()
    {
        _carrier?.Dispose();
        _carrier = null;
        _carrierSymbol = null;
    }

    private static void Fire()
    {
        _lastFire = _frame;
        // The event runs once per instance; handlers run once per frame.
        if (_drawnFrame == _frame) return;
        _drawnFrame = _frame;

        foreach (var (draw, owner) in Handlers.ToArray())
        {
            if (owner == null)
            {
                try { draw(); }
                catch (Exception ex) { Log.Error("a GUI draw handler threw", ex); }
            }
            else if (owner.State != ModState.Faulted)
            {
                ModManager.Invoke(owner, "GameDraw.OnGui", _ => draw());
            }
        }
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose()
        {
            _remove?.Invoke();
            _remove = null;
        }
    }
}
