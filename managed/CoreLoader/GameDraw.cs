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
/// change removes it; handlers do not see which. Colour, alpha, font,
/// alignment and blend mode are restored after the handlers, so a handler may
/// set them freely - but should not rely on what they are when it starts.
/// </remarks>
public static class GameDraw
{
    private static readonly Logger Log = new("CoreLoader");
    private static readonly List<(Action Draw, LoadedMod? Owner)> Handlers = new();
    private static List<string>? _events;
    private static HookHandle? _carrier;
    private static string? _carrierSymbol;
    private static long _frame, _drawnFrame = -1, _lastFire = -1, _lastPick = -1000;
    private static bool _carrierFired, _pausedWarned;

    // Draw GUI events that failed or went quiet, and the frame they may be tried again.
    private static readonly Dictionary<string, long> Skipped = new(StringComparer.Ordinal);

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
            DropCarrier(stale: false);
            return;
        }
        // Fired recently: fine. Otherwise the carrier's instances are gone (a
        // room change) or never drew: pick another - at most twice a second
        // while nothing qualifies, since a search asks the game about every
        // candidate object.
        if (_carrier != null && _frame - _lastFire <= 30) return;
        if (_carrier == null && _frame - _lastPick < 30) return;
        _lastPick = _frame;
        if (_carrier != null) DropCarrier(stale: true);
        PickCarrier();
    }

    private static void PickCarrier()
    {
        _events ??= Game.Symbols
            .Select(s => s.Name)
            .Where(n => n.StartsWith("gml_Object_", StringComparison.Ordinal) && n.EndsWith("_Draw_64", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Fewest instances first: the event runs once per instance, and each
        // run is a trip into managed code. One instance is ideal.
        string? best = null;
        int bestCount = int.MaxValue;
        foreach (var symbol in _events)
        {
            if (Skipped.TryGetValue(symbol, out long until) && _frame < until) continue;
            try
            {
                string obj = symbol["gml_Object_".Length..^"_Draw_64".Length];
                if (GmlObject.Find(obj) is not { } o) continue;
                int n = o.InstanceCount;
                if (n == 0 || n >= bestCount) continue;
                // Invisible instances skip their draw events entirely.
                if (!o.Instance(0).Get("visible").AsBool) continue;
                best = symbol;
                bestCount = n;
                if (n == 1) break;
            }
            catch (Exception ex)
            {
                // One odd object must not stall the search for the rest.
                Skipped[symbol] = _frame + 3600;
                Log.Warning($"game drawing: skipping {symbol}: {ex.Message}");
            }
        }

        if (best == null)
        {
            if (!_pausedWarned)
            {
                _pausedWarned = true;
                Log.Warning("no object with a Draw GUI event is alive; mod drawing is paused");
            }
            return;
        }

        var previous = ModManager.Current;
        ModManager.Current = null;   // the loader's own hook, not the calling mod's
        try
        {
            _carrier = Hooks.After(best, _ => Fire());
        }
        catch (GmlException ex)
        {
            Skipped[best] = _frame + 3600;
            Log.Warning($"game drawing: cannot hook {best}: {ex.Message}");
            return;
        }
        finally
        {
            ModManager.Current = previous;
        }
        _carrierSymbol = best;
        _carrierFired = false;
        _lastFire = _frame;          // give it a few frames to prove itself
        _pausedWarned = false;
        Log.Info($"game drawing rides on {best}");
    }

    private static void DropCarrier(bool stale)
    {
        if (_carrier == null) return;
        // A carrier that went quiet sits out a while, so the search does not
        // pick it straight back: much longer if it never drew at all (a child
        // object overriding the event, say).
        if (stale && _carrierSymbol != null) Skipped[_carrierSymbol] = _frame + (_carrierFired ? 300 : 3600);
        _carrier.Dispose();
        _carrier = null;
        _carrierSymbol = null;
    }

    private static void Fire()
    {
        _lastFire = _frame;
        _carrierFired = true;
        // The event runs once per instance; handlers run once per frame.
        if (_drawnFrame == _frame) return;
        _drawnFrame = _frame;

        // Handlers leave behind the carrier's own draw state: an alpha or
        // colour a mod sets must not bleed into the rest of the game's GUI.
        var saved = SaveDrawState();
        try
        {
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
        finally
        {
            RestoreDrawState(saved);
        }
    }

    private static readonly (string Get, string Set)[] StateCalls =
    {
        ("draw_get_colour", "draw_set_colour"), ("draw_get_alpha", "draw_set_alpha"),
        ("draw_get_font", "draw_set_font"), ("draw_get_halign", "draw_set_halign"),
        ("draw_get_valign", "draw_set_valign"), ("gpu_get_blendmode", "gpu_set_blendmode"),
    };
    private static (string Get, string Set)[]? _stateCalls;

    private static RValue[] SaveDrawState()
    {
        // Only the pairs this runtime registers, with the argument counts expected.
        _stateCalls ??= StateCalls
            .Where(c => Game.BuiltinArity(c.Get) is 0 or -1 && Game.BuiltinArity(c.Set) is 1 or -1)
            .ToArray();
        var values = new RValue[_stateCalls.Length];
        for (int i = 0; i < values.Length; i++)
        {
            try { values[i] = Game.CallBuiltin(_stateCalls[i].Get); }
            catch (GmlException) { values[i] = RValue.Undefined; }
        }
        return values;
    }

    private static void RestoreDrawState(RValue[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].IsUndefined) continue;
            try { Game.CallBuiltin(_stateCalls![i].Set, values[i]); }
            catch (GmlException) { }
        }
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _remove, null);
            if (action == null) return;
            // The handler list belongs to the game thread.
            if (Loader.OnGameThread) action();
            else Game.RunOnGameThread(action);
        }
    }
}
