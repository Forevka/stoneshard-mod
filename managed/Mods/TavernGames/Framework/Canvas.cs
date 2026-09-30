using CoreLoader;
using StoneShard;

namespace TavernGames;

/// <summary>
/// Drawing for the table, in a 960 x 540 design space scaled by a whole number
/// to the GUI layer, so pixel art stays crisp (x2 at 1080p, the scale the
/// game's own GUI uses). Text is the game's colour text (scr_colorTextCreate /
/// scr_colorTextDraw, with its ~y~ tags); frames and buttons are its sprites.
/// </summary>
/// <remarks>
/// Buttons are immediate: <see cref="Button"/> draws one and remembers where,
/// and the table matches the frame's click against what was drawn. Draw GUI
/// runs before the loader's per-frame update, so a click is always tested
/// against the buttons of the frame the player saw.
/// </remarks>
internal sealed class Canvas
{
    public const double DesignW = 960, DesignH = 540;
    private const double White = 16777215;

    private readonly Dictionary<string, double> _textMaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _assets = new(StringComparer.Ordinal);
    private readonly List<(Area Area, string Id, bool Enabled)> _buttons = new();

    /// <summary>GUI pixels per design unit.</summary>
    public double K { get; private set; } = 2;

    /// <summary>Where design (0, 0) sits on the GUI layer: the design space is centred.</summary>
    public double OriginX { get; private set; }

    public double OriginY { get; private set; }

    /// <summary>The GUI point under the mouse, in design units.</summary>
    public (double X, double Y) Mouse { get; private set; }

    public IReadOnlyList<(Area Area, string Id, bool Enabled)> ButtonsDrawn => _buttons;

    /// <summary>Starts a frame: measures the GUI layer and forgets last frame's buttons.</summary>
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

    // ------------------------------------------------------------ shapes

    /// <summary>A flat rectangle; colour is 0xBBGGRR.</summary>
    public void Fill(Area a, int colour, double alpha = 1)
    {
        Builtins.draw_set_alpha(alpha);
        Builtins.draw_set_colour(colour);
        Builtins.draw_rectangle(Gx(a.X), Gy(a.Y), Gx(a.X + a.W) - 1, Gy(a.Y + a.H) - 1, false);
        Builtins.draw_set_alpha(1);
    }

    public void Outline(Area a, int colour, double alpha = 1)
    {
        Builtins.draw_set_alpha(alpha);
        Builtins.draw_set_colour(colour);
        Builtins.draw_rectangle(Gx(a.X), Gy(a.Y), Gx(a.X + a.W) - 1, Gy(a.Y + a.H) - 1, true);
        Builtins.draw_set_alpha(1);
    }

    /// <summary>
    /// A framed board, the one the world map's panels sit on. Falls back to a
    /// plain dark box should the game refuse (it is the game's script, called
    /// outside the map).
    /// </summary>
    public void Board(Area a)
    {
        try
        {
            Scripts.scr_globalmapDrawBoard.Call(Gx(a.X), Gy(a.Y), a.W * K, a.H * K, K);
        }
        catch (GmlException)
        {
            Fill(a, 0x141A20, 0.95);
            Outline(a, 0x4A6A86);
        }
    }

    // ------------------------------------------------------------ sprites

    /// <summary>A mod sprite, its frame scaled by <see cref="K"/> times <paramref name="scale"/>.</summary>
    public void Sprite(Sprite s, int frame, double x, double y, double scale = 1, int colour = 0xFFFFFF, double alpha = 1) =>
        s.Draw(Gx(x), Gy(y), frame, K * scale, K * scale, 0, colour, alpha);

    /// <summary>
    /// A mod sprite squeezed horizontally about its own centre: <paramref name="xScale"/>
    /// from 1 down to 0 and back is a card turning over. The unsqueezed frame's
    /// top-left sits at (x, y); <paramref name="width"/> is the frame's width in pixels.
    /// </summary>
    public void SpriteTurned(Sprite s, int frame, double x, double y, double width, double xScale, double scale = 1) =>
        s.Draw(Gx(x + width * scale * (1 - xScale) / 2), Gy(y), frame, K * scale * xScale, K * scale);

    /// <summary>
    /// One of the game's sprites by name, its top-left corner at (x, y) whatever
    /// its origin (s_menu_button's is its centre), or nothing if this build lacks it.
    /// </summary>
    public void GameSprite(string name, int frame, double x, double y, double xScale = 1, double yScale = 1, int colour = 0xFFFFFF, double alpha = 1)
    {
        double id = Asset(name);
        if (id < 0) return;
        double ox = Builtins.sprite_get_xoffset(id).AsReal, oy = Builtins.sprite_get_yoffset(id).AsReal;
        Builtins.draw_sprite_ext(id, frame, Gx(x) + ox * K * xScale, Gy(y) + oy * K * yScale, K * xScale, K * yScale, 0, colour, alpha);
    }

    /// <summary>An asset's index by name, cached; -1 when the game has none by that name.</summary>
    public double Asset(string name)
    {
        if (_assets.TryGetValue(name, out var id)) return id;
        var v = Builtins.asset_get_index(name);
        return _assets[name] = v.IsNumber ? v.AsReal : -1;
    }

    // ------------------------------------------------------------ text

    /// <summary>Colour text; <paramref name="align"/> is -1 left, 0 centred, 1 right of x.</summary>
    public (double W, double H) Text(string text, double x, double y, int align = -1, double wrap = 1600)
    {
        if (text.Length == 0) return (0, 0);
        double map = TextMap(text, wrap);
        var data = new DsMap(map);
        double w = data.Get("width").AsReal, h = data.Get("height").AsReal;
        double gx = Gx(x) - (align == 0 ? w / 2 : align > 0 ? w : 0);
        Scripts.scr_colorTextDraw.Call(map, Math.Round(gx), Math.Round(Gy(y)), 0, 0, 0, 1);
        return (w / K, h / K);
    }

    // The game's colour-text maps are made once per string and kept: building
    // one lays the text out, which is too slow to redo every frame. Nothing
    // else knows their ids; ds_exists only guards against a restarted game.
    private double TextMap(string text, double wrap)
    {
        string key = wrap + "|" + text;
        if (_textMaps.TryGetValue(key, out var id) && Builtins.ds_exists(id, 1).AsBool) return id;
        if (_textMaps.Count > 256) Clear();
        var map = Builtins.ds_map_create();
        Scripts.scr_colorTextCreate.Call(map, text, White, wrap, 1);
        return _textMaps[key] = map.AsReal;
    }

    public void Clear()
    {
        foreach (var id in _textMaps.Values)
            if (Builtins.ds_exists(id, 1).AsBool) Builtins.ds_map_destroy(id);
        _textMaps.Clear();
    }

    // ------------------------------------------------------------ buttons

    private const string ButtonSprite = "s_menu_button";
    private const double ButtonSpriteW = 127, ButtonSpriteH = 26;

    /// <summary>
    /// A button in the game's own style (s_menu_button: frame 0 idle, 1 under
    /// the mouse, 2 pressed). Returns whether the mouse is over it.
    /// </summary>
    public bool Button(string id, string label, Area a, bool enabled = true)
    {
        bool hover = enabled && a.Contains(Mouse.X, Mouse.Y);
        _buttons.Add((a, id, enabled));
        if (Asset(ButtonSprite) >= 0)
        {
            GameSprite(ButtonSprite, hover ? 1 : 0, a.X, a.Y, a.W / ButtonSpriteW, a.H / ButtonSpriteH,
                       enabled ? 0xFFFFFF : 0x808080, enabled ? 1 : 0.6);
        }
        else
        {
            Fill(a, hover ? 0x3A4A5A : 0x28323C);
            Outline(a, 0x6A7A8A);
        }
        // A greyed button loses its colours; the dimmed sprite does the rest.
        Text(enabled ? label : Plain(label), a.CenterX, a.CenterY - 6, align: 0);
        return hover;
    }

    /// <summary>The label without its colour tags, for a greyed-out button.</summary>
    private static string Plain(string label)
    {
        var sb = new System.Text.StringBuilder(label.Length);
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] == '~')
            {
                int end = label.IndexOf('~', i + 1);
                if (end > i) { i = end; continue; }
            }
            sb.Append(label[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The button drawn this frame under the design point: its id, "" for a
    /// disabled one (the click lands on it and does nothing), null for none.
    /// </summary>
    public string? ButtonAt(double x, double y)
    {
        for (int i = _buttons.Count - 1; i >= 0; i--)
            if (_buttons[i].Area.Contains(x, y)) return _buttons[i].Enabled ? _buttons[i].Id : "";
        return null;
    }
}
