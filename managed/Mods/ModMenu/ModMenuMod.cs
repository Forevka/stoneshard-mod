using CoreLoader;
using StoneShard;

[assembly: CoreModInfo(typeof(ModMenu.ModMenuMod), "Mod Settings", "0.1.0", "Lodestone")]
[assembly: CoreModGame("StoneShard")]

namespace ModMenu;

/// <summary>
/// A "MODS" entry in Stoneshard's pause menu (Esc) that opens a window, drawn
/// with the game's own board, buttons and text, listing every setting mods
/// registered through <see cref="ModSettings"/>. Changes apply and save at once.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25):
///   * the pause menu is an o_close_panel; its Create event makes its buttons
///     with scr_guiCreateInteractive(panel, o_ingame_menu_button, depth, 480, y),
///     28 apart, and gives each a `text` and an `event`;
///   * a click runs the button's user event (10 + its event): 0 is CONTINUE,
///     which closes the menu. Ours is a CONTINUE that then opens the window.
/// </remarks>
public sealed class ModMenuMod : CoreMod
{
    private const int RowsPerPage = 6;
    private const double ButtonStep = 28;

    private static readonly Area Window = new(180, 62, 600, 416);
    private static readonly Area Content = new(354, 104, 410, 300);

    private readonly Canvas _canvas = new();
    private long _ourButton = -1;
    private bool _open;
    private bool _escPressed;
    private string? _mod;
    private int _page;
    private string _lastError = "";

    public override void OnInitialize()
    {
        Objects.o_close_panel.Create_0.After(c => Guard("menu", () => AddMenuButton(c.Self)));
        Objects.o_ingame_menu_button.Other_10.After(c => Guard("menu click", () =>
        {
            if (!c.Self.IsNull && IdKey(c.Self.Get("id")) == _ourButton) Open();
        }));
        // While the window is up the keyboard is ours: Esc closes it, and no
        // hotkey reaches the game (o_controller's Begin Step runs before any
        // key is read; TavernGames' table does the same).
        Objects.o_controller.Step_1.Before(_ =>
        {
            if (!_open) return;
            try
            {
                if (Builtins.keyboard_check_pressed(27).AsBool) _escPressed = true;
                Builtins.io_clear();
            }
            catch (GmlException) { }
        });
        GameDraw.OnGui(Draw);
        if (TestHost.Enabled) RegisterCommands();
        Log.Info("ready: Esc, then MODS");
    }

    public override void OnShutdown()
    {
        if (Input.IsPicking) Input.CancelPick();
        _canvas.Clear();
    }

    private void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex.Message != _lastError) Log.Warning($"{what}: {ex.Message}");
            _lastError = ex.Message;
        }
    }

    // ------------------------------------------------------------ the pause menu entry

    private void AddMenuButton(Instance panel)
    {
        // Only the in-game pause menu, and only once per opening of it.
        if (panel.IsNull || Objects.o_player.First is null || Objects.o_ingame_menu_button.Object is not { } obj) return;
        if (_ourButton >= 0 && Builtins.instance_exists(_ourButton).AsBool) return;
        // Below the lowest button the menu made, with the same look.
        double lowest = double.MinValue, depth = -12401, left = 480;
        foreach (var b in obj.Instances())
        {
            double top = b.Get("guiLayoutOffsetTop").AsReal;
            if (top <= lowest) continue;
            lowest = top;
            depth = b.Get("depth").AsReal;
            left = b.Get("guiLayoutOffsetLeft").AsReal;
        }
        if (lowest == double.MinValue) return;
        var id = panel.Get("id");
        var made = Scripts.scr_guiCreateInteractive.CallAs(panel, panel, id, obj.Index, depth, left, lowest + ButtonStep);
        var button = new InstanceRef(made);
        if (!button.Exists) return;
        button.Set("text", "MODS");
        button.Set("event", 0);
        _ourButton = IdKey(made);
    }

    // ------------------------------------------------------------ the window

    private void Open()
    {
        if (_open) return;
        _open = true;
        _escPressed = false;
        _page = 0;
        var mods = Mods();
        if (_mod == null || mods.All(m => m.Id != _mod)) _mod = mods.FirstOrDefault().Id;
        Input.ArmPick();
        Sfx("snd_ui_open_window_st");
    }

    private void Close()
    {
        if (!_open) return;
        _open = false;
        if (Input.IsPicking) Input.CancelPick();
        Sfx("snd_ui_close_window_st");
    }

    // Mods are keyed by assembly name (display names need not be unique).
    private static List<(string Id, string Name)> Mods() =>
        ModSettings.Registered.Select(s => (s.ModId, s.ModName)).Distinct().ToList();

    private List<ModSettings.Setting> Current() =>
        ModSettings.Registered.Where(s => s.ModId == _mod).ToList();

    public override void OnUpdate()
    {
        if (!_open) return;
        // No player (back at the title): nothing to set up here any more.
        if (Objects.o_player.First is null) { Close(); return; }
        if (_escPressed)
        {
            _escPressed = false;
            Close();
            return;
        }
        // Modal: while the window is up every click is its own, even one another
        // mod armed a pick for (that pick is taken over and lost).
        if (!Input.IsPicking) Input.ArmPick();
        if (!Input.TryTakePick(out var click)) return;
        Input.ArmPick();
        var (x, y) = _canvas.ToDesign(Builtins.device_mouse_x_to_gui(0).AsReal, Builtins.device_mouse_y_to_gui(0).AsReal);
        if (_canvas.ButtonAt(x, y) is not { } id) return;
        Guard("click", () => Click(id, click.RightButton));
    }

    private void Click(string id, bool right)
    {
        Sfx("snd_button_click");
        var parts = id.Split(':', 2);
        string arg = parts.Length > 1 ? parts[1] : "";
        var settings = Current();
        switch (parts[0])
        {
            case "close": Close(); return;
            case "mod": _mod = arg; _page = 0; return;
            case "prev": _page = Math.Max(0, _page - 1); return;
            case "next": _page++; return;
            case "defaults": foreach (var s in settings) s.Reset(); return;
        }
        var setting = settings.FirstOrDefault(s => s.Key == arg);
        if (setting == null) return;
        // A right click on any control steps it back.
        switch (parts[0])
        {
            case "dec": setting.Nudge(-1); break;
            case "inc": setting.Nudge(right ? -1 : 1); break;
            case "toggle": setting.Nudge(1); break;
        }
    }

    private void Draw()
    {
        if (!_open) return;
        try { DrawWindow(); }
        catch (GmlException ex)
        {
            if (ex.Message != _lastError) Log.Warning($"drawing: {ex.Message}");
            _lastError = ex.Message;
        }
    }

    private void DrawWindow()
    {
        var c = _canvas;
        c.Begin();
        c.Dim(0.55);
        c.Board(Window);
        c.Text("~w~MOD SETTINGS~/~", Window.CenterX, Window.Y + 14, align: 0);
        c.Rule(Window.X + 16, Window.Y + 34, Window.W - 32);

        var mods = Mods();
        if (mods.Count == 0)
        {
            c.Text("No mod offers settings yet.", Window.CenterX, Window.CenterY - 8, align: 0);
        }
        else
        {
            double y = Content.Y;
            foreach (var (mid, name) in mods)
            {
                c.Button("mod:" + mid, mid == _mod ? $"~y~{name}~/~" : name, new Area(Window.X + 16, y, 150, 24), selected: mid == _mod);
                y += 30;
            }
            DrawSettings(Current());
        }

        c.Button("defaults", "DEFAULTS", new Area(Window.X + 16, Window.Y + Window.H - 40, 120, 26));
        c.Button("close", "CLOSE", new Area(Window.X + Window.W - 136, Window.Y + Window.H - 40, 120, 26));
    }

    private void DrawSettings(List<ModSettings.Setting> settings)
    {
        var c = _canvas;
        int pages = Math.Max(1, (settings.Count + RowsPerPage - 1) / RowsPerPage);
        _page = Math.Clamp(_page, 0, pages - 1);
        double y = Content.Y;
        foreach (var s in settings.Skip(_page * RowsPerPage).Take(RowsPerPage))
        {
            c.Text($"~w~{s.Label}~/~", Content.X, y);
            if (s.Description.Length > 0) c.Text($"~gr~{s.Description}~/~", Content.X, y + 14, wrap: 250);
            double cx = Content.X + Content.W - 118;
            if (s.Kind == ModSettings.Kind.Toggle)
            {
                c.Button("toggle:" + s.Key, s.GetBool() ? "~lg~ON~/~" : "~r~OFF~/~", new Area(cx + 14, y, 90, 24));
            }
            else
            {
                c.Button("dec:" + s.Key, "<", new Area(cx, y, 24, 24));
                c.Text($"~y~{s.ValueText()}~/~", cx + 59, y + 6, align: 0);
                c.Button("inc:" + s.Key, ">", new Area(cx + 94, y, 24, 24));
            }
            c.Rule(Content.X, y + 44, Content.W);
            y += 50;
        }
        if (pages > 1)
        {
            double py = Window.Y + Window.H - 40;
            if (_page > 0) c.Button("prev", "<", new Area(Content.CenterX - 60, py, 26, 26));
            c.Text($"{_page + 1} / {pages}", Content.CenterX, py + 7, align: 0);
            if (_page < pages - 1) c.Button("next", ">", new Area(Content.CenterX + 34, py, 26, 26));
        }
    }

    // ------------------------------------------------------------ bits

    private void Sfx(string name)
    {
        try
        {
            double id = _canvas.Asset(name);
            if (id >= 0) Builtins.audio_play_sound(id, 5, false);
        }
        catch (GmlException) { }
    }

    private static long IdKey(RValue v) =>
        v.IsNumber ? (long)v.AsReal : v.Kind == RValueKind.Reference ? v.Int64 & 0xFFFFFFFF : -1;

    private void RegisterCommands()
    {
        TestHost.Register("mm.state", _ => new
        {
            open = _open,
            mod = _mod,
            page = _page,
            button = _ourButton,
            settings = ModSettings.Registered.Select(s => new { mod = s.ModId, s.Key, s.Label, kind = s.Kind.ToString(), value = s.ValueText() }),
        }, "mm.state: whether the window is open, the mod shown, every registered setting with its value");
        TestHost.Register("mm.open", _ => { Open(); return _open; }, "mm.open: opens the window");
        TestHost.Register("mm.close", _ => { Close(); return _open; }, "mm.close: closes it");
        TestHost.Register("mm.click", args => { Click(args[0].GetString()!, args.Count > 1 && args[1].GetString() == "right"); return "ok"; },
            "mm.click <button id> [right]: as a click on that button (mod:<name>, dec:<key>, inc:<key>, toggle:<key>, defaults, close, prev, next)");
    }
}
