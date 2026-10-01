using CoreLoader;
using StoneShard;

namespace ModMenu;

internal readonly record struct Area(double X, double Y, double W, double H)
{
    public bool Contains(double x, double y) => x >= X && x < X + W && y >= Y && y < Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
}

/// <summary>
/// Drawing in a 960 x 540 design space scaled by a whole number to the GUI
/// layer (x2 at 1080p, the game's own GUI scale), with the game's pieces: the
/// board its map panels sit on, its menu button sprite and its colour text.
/// TavernGames' table canvas, trimmed to what a settings window needs.
/// </summary>
/// <remarks>
/// Buttons are immediate: <see cref="Button"/> draws one and remembers where,
/// and a click is matched against what the player saw last frame.
/// </remarks>
internal sealed class Canvas
{
    public const double DesignW = 960, DesignH = 540;
    private const double White = 16777215;
    private const string ButtonSprite = "s_menu_button";
    private const double ButtonSpriteW = 127, ButtonSpriteH = 26;

    private readonly Dictionary<string, double> _textMaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _assets = new(StringComparer.Ordinal);
    private readonly List<(Area Area, string Id)> _buttons = new();

    public double K { get; private set; } = 2;
    public double OriginX { get; private set; }
    public double OriginY { get; private set; }
    public (double X, double Y) Mouse { get; private set; }

    public void Begin()
    {
        double gw = GameDraw.GuiWidth, gh = GameDraw.GuiHeight;
        K = Math.Max(1, Math.Floor(Math.Min(gw / DesignW, gh / DesignH)));
        OriginX = Math.Round((gw - DesignW * K) / 2);
        OriginY = Math.Round((gh - DesignH * K) / 2);
        Mouse = ToDesign(Builtins.device_mouse_x_to_gui(0).AsReal, Builtins.device_mouse_y_to_gui(0).AsReal);
        _buttons.Clear();
    }

    public (double X, double Y) ToDesign(double guiX, double guiY) => ((guiX - OriginX) / K, (guiY - OriginY) / K);

    private double Gx(double x) => OriginX + x * K;
    private double Gy(double y) => OriginY + y * K;

    /// <summary>A flat rectangle; colour is 0xBBGGRR.</summary>
    public void Fill(Area a, int colour, double alpha = 1)
    {
        Builtins.draw_set_alpha(alpha);
        Builtins.draw_set_colour(colour);
        Builtins.draw_rectangle(Gx(a.X), Gy(a.Y), Gx(a.X + a.W) - 1, Gy(a.Y + a.H) - 1, false);
        Builtins.draw_set_alpha(1);
    }

    /// <summary>The whole GUI layer, darkened, as the game's menus do behind themselves.</summary>
    public void Dim(double alpha)
    {
        Builtins.draw_set_alpha(alpha);
        Builtins.draw_set_colour(0);
        Builtins.draw_rectangle(0, 0, GameDraw.GuiWidth, GameDraw.GuiHeight, false);
        Builtins.draw_set_alpha(1);
    }

    /// <summary>A framed board, the one the world map's panels sit on.</summary>
    public void Board(Area a)
    {
        try { Scripts.scr_globalmapDrawBoard.Call(Gx(a.X), Gy(a.Y), a.W * K, a.H * K, K); }
        catch (GmlException) { Fill(a, 0x141A20, 0.95); }
    }

    /// <summary>A thin rule, the colour of the game's section underlines.</summary>
    public void Rule(double x, double y, double w) => Fill(new Area(x, y, w, 1), 0x3A4552, 0.9);

    public double Asset(string name)
    {
        if (_assets.TryGetValue(name, out var id)) return id;
        var v = Builtins.asset_get_index(name);
        return _assets[name] = v.IsNumber ? v.AsReal : -1;
    }

    /// <summary>Colour text; <paramref name="align"/> is -1 left, 0 centred, 1 right of x. Returns its size.</summary>
    public (double W, double H) Text(string text, double x, double y, int align = -1, double wrap = 1600)
    {
        if (text.Length == 0) return (0, 0);
        double map = TextMap(text, wrap * K);
        var data = new DsMap(map);
        double w = data.Get("width").AsReal, h = data.Get("height").AsReal;
        double gx = Gx(x) - (align == 0 ? w / 2 : align > 0 ? w : 0);
        Scripts.scr_colorTextDraw.Call(map, Math.Round(gx), Math.Round(Gy(y)), 0, 0, 0, 1);
        return (w / K, h / K);
    }

    // Colour-text maps are made once per string and kept: laying text out is
    // too slow to redo every frame. Nothing else knows their ids.
    private double TextMap(string text, double wrap)
    {
        string key = wrap + "|" + text;
        if (_textMaps.TryGetValue(key, out var id) && Builtins.ds_exists(id, 1).AsBool) return id;
        if (_textMaps.Count > 256) Clear();
        var map = Builtins.ds_map_create();
        try { Scripts.scr_colorTextCreate.Call(map, text, White, wrap, 1); }
        catch (GmlException)
        {
            Builtins.ds_map_destroy(map);
            throw;
        }
        return _textMaps[key] = map.AsReal;
    }

    public void Clear()
    {
        foreach (var id in _textMaps.Values)
            if (Builtins.ds_exists(id, 1).AsBool) Builtins.ds_map_destroy(id);
        _textMaps.Clear();
    }

    /// <summary>A button in the game's own style (s_menu_button: frame 0 idle, 1 under the mouse).</summary>
    public void Button(string id, string label, Area a, bool selected = false)
    {
        bool hover = a.Contains(Mouse.X, Mouse.Y);
        _buttons.Add((a, id));
        if (Asset(ButtonSprite) is var s and >= 0)
        {
            double ox = Builtins.sprite_get_xoffset(s).AsReal, oy = Builtins.sprite_get_yoffset(s).AsReal;
            double sx = a.W / ButtonSpriteW, sy = a.H / ButtonSpriteH;
            Builtins.draw_sprite_ext(s, hover || selected ? 1 : 0, Gx(a.X) + ox * K * sx, Gy(a.Y) + oy * K * sy, K * sx, K * sy, 0, 0xFFFFFF, 1);
        }
        else Fill(a, hover ? 0x3A4A5A : 0x28323C);
        Text(label, a.CenterX, a.CenterY - 6, align: 0);
    }

    /// <summary>The id of the button drawn last frame under the design point, or null.</summary>
    public string? ButtonAt(double x, double y)
    {
        for (int i = _buttons.Count - 1; i >= 0; i--)
            if (_buttons[i].Area.Contains(x, y)) return _buttons[i].Id;
        return null;
    }
}
