using CoreLoader;
using StoneShard;

[assembly: CoreModInfo(typeof(FastTravel.FastTravelMod), "Fast Travel", "1.0.0", "Lodestone")]
[assembly: CoreModGame("StoneShard")]

namespace FastTravel;

/// <summary>
/// Fast travel from the world map. Open the map (M), turn the mode on with its
/// entry in the map's controls bar or the key (F by default), and click a
/// place: any land you have been to, or right next to where you have been.
/// </summary>
public sealed class FastTravelMod : CoreMod
{
    private MapUi _ui = null!;
    private Traveller _traveller = null!;
    private string _key = "F";
    private bool _mode;
    private bool _wasOpen;
    private double _frameSprite = -1;
    private string _lastDrawError = "";

    // Where the player has been cannot change while the map is open: read once per visit to it.
    private HashSet<(int X, int Y)>? _visited;

    // What the banner says about the cell under the mouse, recomputed only when that cell changes.
    private (int X, int Y)? _judged;
    private WorldMap.Verdict _verdict;

    public override void OnInitialize()
    {
        _key = Config.Get("toggleKey", "F").Trim().ToUpperInvariant();
        if (_key.Length != 1) _key = "F";
        _ui = new MapUi(_key);
        _traveller = new Traveller(Log);
        _frameSprite = Builtins.asset_get_index("s_highlight_globalmap").AsReal;

        Objects.o_globalmapControlsRender.Other_20.After(c =>
        {
            try { _ui.AppendEntry(c.Self); }
            catch (GmlException ex) { Log.Warning($"could not add the Fast Travel entry to the map: {ex.Message}"); }
        });
        GameDraw.OnGui(DrawGui);
        if (TestHost.Enabled) RegisterCommands();
        Log.Info($"ready: open the map and press [{_key}] or click Fast Travel");
    }

    public override void OnShutdown() => _ui?.Clear();

    private HashSet<(int X, int Y)> Visited => _visited ??= WorldMap.Visited();

    public override void OnUpdate()
    {
        _traveller.Tick();
        bool open = WorldMap.IsOpen;
        // The mode belongs to one visit of the map: closing it turns it off.
        if (!open)
        {
            if (_wasOpen) SetMode(false, quiet: true);
            _wasOpen = false;
            _visited = null;
            return;
        }
        _wasOpen = true;

        if (Builtins.keyboard_check_pressed(Builtins.ord(_key)).AsBool) SetMode(!_mode);
        if (!Builtins.mouse_check_button_pressed(1).AsBool) return;
        double mx = Builtins.device_mouse_x_to_gui(0).AsReal, my = Builtins.device_mouse_y_to_gui(0).AsReal;
        if (_ui.EntryContains(mx, my))
        {
            SetMode(!_mode);
            return;
        }
        // A click on a panel is the panel's; only one on the map itself picks a place.
        if (_mode && !_ui.OnPanel(mx, my) && WorldMap.CellAt(mx, my) is { } cell) TryTravel(cell);
    }

    private void SetMode(bool on, bool quiet = false)
    {
        _mode = on;
        _judged = null;
        if (!on) _ui.HideBanner();
        if (quiet) return;
        // The game's own sounds for switching a mode on and off; cosmetic, so never worth a fault.
        try
        {
            var sound = Builtins.asset_get_index(on ? Assets.Sounds.snd_gui_enable_mode : Assets.Sounds.snd_gui_disable_mode);
            if (sound.AsReal >= 0) Builtins.audio_play_sound(sound, 5, false);
        }
        catch (GmlException) { }
    }

    private string TryTravel((int X, int Y) cell, (double X, double Y)? arrival = null)
    {
        if (Objects.o_player.First is not { } player) return "no player";
        try
        {
            string? why = WorldMap.Judge(cell, Visited) switch
            {
                WorldMap.Verdict.Here => "you are already here",
                WorldMap.Verdict.Unknown => "you know no way there yet",
                WorldMap.Verdict.Water => "there is only water there",
                _ => _traveller.Refusal(player),
            };
            if (why != null)
            {
                Say($"~r~Fast travel~/~: {why}.");
                return why;
            }
            SetMode(false, quiet: true);
            Say("~lg~Fast travel~/~: you set off.");
            _traveller.Go(player, cell, arrival);
            _visited = null;
            return "travelling";
        }
        // The game refused a call part-way: say so, and stay where we are (Go puts the map back).
        catch (GmlException ex)
        {
            Log.Warning($"travel to {cell.X},{cell.Y} failed: {ex.Message}");
            Say("~r~Fast travel~/~: the way is barred.");
            return "failed: " + ex.Message;
        }
    }

    private void DrawGui()
    {
        if (!_mode || !WorldMap.IsOpen) return;
        // Drawing reads the live map every frame; a bad frame costs that frame, logged once.
        try { DrawMode(); }
        catch (GmlException ex)
        {
            if (ex.Message != _lastDrawError) Log.Warning($"map drawing: {ex.Message}");
            _lastDrawError = ex.Message;
        }
    }

    private void DrawMode()
    {
        double mx = Builtins.device_mouse_x_to_gui(0).AsReal, my = Builtins.device_mouse_y_to_gui(0).AsReal;
        var cell = _ui.OnPanel(mx, my) ? null : WorldMap.CellAt(mx, my);
        if (cell != _judged)
        {
            _judged = cell;
            _verdict = cell is { } c ? WorldMap.Judge(c, Visited) : WorldMap.Verdict.Unknown;
        }
        // The game frames the hovered cell only where the fog has lifted; a
        // reachable cell still under it gets the same frame from us.
        if (cell is { } at && _verdict == WorldMap.Verdict.Allowed && _frameSprite >= 0 && !WorldMap.Revealed(at)
            && WorldMap.CellRect(at) is { } r)
        {
            double k = r.Size / Builtins.sprite_get_width(_frameSprite).AsReal;
            Builtins.draw_sprite_ext(_frameSprite, 0, r.X, r.Y, k, k, 0, 16777215, 1);
        }
        string hint = cell is null
            ? "~w~choose a destination on the map~/~"
            : _verdict switch
            {
                WorldMap.Verdict.Allowed => "~lg~click to travel here~/~",
                WorldMap.Verdict.Here => "~w~you are here~/~",
                WorldMap.Verdict.Water => "~r~only water there~/~",
                _ => "~r~too far: only places you have been, and next to them~/~",
            };
        _ui.DrawBanner($"Fast travel mode: ~lg~on~/~  -  {hint}  -  [~lg~{_key}~/~] to cancel");
    }

    // A line in the game's own action log, as the player (some callers have no current instance).
    private static void Say(string text)
    {
        try
        {
            if (Objects.o_player.First is { } p) Scripts.scr_actionsLogAddMessage.CallAs(p, text);
        }
        catch (GmlException) { }
    }

    private void RegisterCommands()
    {
        TestHost.Register("ft.state", _ => new
        {
            open = WorldMap.IsOpen,
            mode = _mode,
            busy = _traveller.Busy,
            cell = new[] { WorldMap.PlayerCell.X, WorldMap.PlayerCell.Y },
            hovered = WorldMap.HoveredCell is { } h ? new[] { h.X, h.Y } : null,
            visited = WorldMap.Visited().Select(v => $"{v.X}_{v.Y}").OrderBy(s => s).ToArray(),
        }, "ft.state: map open, mode, travelling, the player's cell, the hovered cell and every visited cell");
        TestHost.Register("ft.mode", args =>
        {
            if (args.Count > 0) SetMode(args[0].GetString() is "on" or "true" or "1", quiet: true);
            return _mode;
        }, "ft.mode [on|off]: the fast travel mode");
        TestHost.Register("ft.judge", args =>
            WorldMap.Judge((args[0].GetInt32(), args[1].GetInt32()), WorldMap.Visited()).ToString(),
            "ft.judge <x> <y>: whether that cell can be travelled to");
        TestHost.Register("ft.travel", args =>
            {
                _visited = null;
                return TryTravel((args[0].GetInt32(), args[1].GetInt32()),
                    args.Count > 3 ? (args[2].GetDouble(), args[3].GetDouble()) : null);
            },
            "ft.travel <x> <y> [arriveX arriveY]: travels as a click on the map would (same checks); the arrival point is for testing the blocked-arrival fix");
    }
}
