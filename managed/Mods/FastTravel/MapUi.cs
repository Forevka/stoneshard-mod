using CoreLoader;
using StoneShard;

namespace FastTravel;

/// <summary>
/// The mod's face on the map screen, drawn with the game's own pieces so it
/// looks like part of it:
///   * an entry in the map's controls bar ("[F] - Fast Travel"), made the way
///     the bar makes its own - a colour-text map from scr_colorTextCreate,
///     appended to the bar's textMapsList;
///   * while the mode is on, a banner at the top of the screen on the board
///     the bar itself is drawn on (scr_globalmapDrawBoard), with the same
///     colour text (scr_colorTextDraw).
/// </summary>
/// <remarks>
/// Layout, read from the live bar: it is built in a 960x540 design space
/// (drawAreaWidth) at drawScale 0.5, and the GUI layer is twice that at 1080p.
/// Entries sit contentX in from the left, each one width + 2 * spaceWidth after
/// the previous, and the board is the entries plus contentX on either side: the
/// four vanilla entries are 142 + 144 + 89 + 128 = 503 wide, the three gaps
/// between them 120, so the board is 20 + 503 + 120 + 20 = 663.
///
/// A hot reload leaves the entry of the old copy in a bar that is already
/// open; it is only drawn, and the next time the map opens it is built afresh.
/// </remarks>
internal sealed class MapUi
{
    private const double White = 16777215;
    private const int Pad = 20, BoardScale = 2;

    private readonly string _key;
    private readonly Dictionary<string, double> _textMaps = new();
    private int _entryIndex = -1;

    public MapUi(string key) => _key = key;

    public string EntryText => $"[~lg~{_key}~/~] - Fast Travel";

    /// <summary>Display GUI pixels per unit of the map's 960-wide design space.</summary>
    public static double GuiScale
    {
        get
        {
            var design = Objects.o_globalmapControlsRender.First is { } bar ? bar.Get("drawAreaWidth") : RValue.Undefined;
            return GameDraw.GuiWidth / (design.IsNumber && design.AsReal > 0 ? design.AsReal : 960);
        }
    }

    // ------------------------------------------------------------ the bar entry

    /// <summary>Right after the bar built its entries (its user event 10): add ours.</summary>
    public void AppendEntry(Instance bar)
    {
        _entryIndex = -1;
        var list = bar.Get(Objects.o_globalmapControlsRender.Vars.textMapsList);
        if (!list.IsNumber) return;
        // Made and measured before the bar is touched, so a refusal leaves it as the game built it.
        var map = Builtins.ds_map_create();
        double w;
        try
        {
            Scripts.scr_colorTextCreate.CallAs(bar, bar, map, EntryText, White, 1000, 1);
            w = new DsMap(map).Get("width").AsReal;
        }
        catch (GmlException)
        {
            Builtins.ds_map_destroy(map);
            throw;
        }
        Builtins.ds_list_add(list, map);
        // Owned by the bar's list from here on: destroyed with it, like its own.
        Builtins.ds_list_mark_as_map(list, Builtins.ds_list_size(list).AsReal - 1);
        _entryIndex = (int)Builtins.ds_list_size(list).AsReal - 1;
        // The bar draws as many entries as it counted, not as its list holds.
        bar.Set("textMapsListSize", _entryIndex + 1);

        double grow = w + 2 * bar.Get("spaceWidth").AsReal;
        foreach (var name in new[] { "width", "contentWidth", "surfaceWidth" })
            bar.Set(name, bar.Get(name).AsReal + grow);
        bar.Set("guiWidth", bar.Get("width").AsReal * bar.Get("drawScale").AsReal);
        bar.Set("surfaceRecreate", 1);
        bar.Set("surfaceRedraw", 1);
    }

    /// <summary>Whether the GUI point (in display GUI pixels) is on our bar entry.</summary>
    public bool EntryContains(double mx, double my)
    {
        if (_entryIndex < 0 || Objects.o_globalmapControlsRender.First is not { } bar) return false;
        var list = new DsList(bar.Get(Objects.o_globalmapControlsRender.Vars.textMapsList));
        if (!list.Exists || list.Count <= _entryIndex || Rect(bar) is not { } board) return false;
        double space = bar.Get("spaceWidth").AsReal, left = bar.Get("contentX").AsReal;
        for (int i = 0; i < _entryIndex; i++) left += new DsMap(list.At(i)).Get("width").AsReal + 2 * space;
        double width = new DsMap(list.At(_entryIndex)).Get("width").AsReal;
        double unit = bar.Get("drawScale").AsReal * GuiScale;
        double x0 = board.X + (left - space) * unit, x1 = board.X + (left + width + space) * unit;
        return mx >= x0 && mx <= x1 && my >= board.Y && my <= board.Y + board.H;
    }

    /// <summary>
    /// Whether the GUI point is on one of the map screen's panels (the
    /// controls bar, the legend, the button that hides them, our banner) rather
    /// than on the map under them.
    /// </summary>
    public bool OnPanel(double mx, double my)
    {
        if (BannerContains(mx, my)) return true;
        if (Objects.o_globalmap.First is not { } map) return false;
        foreach (var name in new[] { "controlsRender", "legendRender", "togglePanelsButton" })
        {
            var panel = map.Get(name);
            if (Traveller.IdKey(panel) < 0) continue;
            var inst = new InstanceRef(panel);
            if (!inst.Exists || inst.Get("guiVisible").AsReal <= 0) continue;
            if (Rect(inst) is { } r && mx >= r.X && mx <= r.X + r.W && my >= r.Y && my <= r.Y + r.H) return true;
        }
        return false;
    }

    // A map GUI element's box on the display GUI. The map's GUI lives at an
    // offset (-5000, -5000 live), which its background knows.
    private static (double X, double Y, double W, double H)? Rect(InstanceRef element)
    {
        var gw = element.Get("guiWidth");
        var gh = element.Get("guiHeight");
        if (!gw.IsNumber || !gh.IsNumber) return null;
        double w = gw.AsReal, h = gh.AsReal, x = element.Get("x").AsReal, y = element.Get("y").AsReal;
        // A button is sized by its sprite, not by the layout (live: guiWidth 0).
        if (w <= 0 || h <= 0)
        {
            var sprite = element.Get("sprite_index");
            if (!sprite.IsNumber || sprite.AsReal < 0) return null;
            double sx = element.Get("image_xscale").AsReal, sy = element.Get("image_yscale").AsReal;
            w = Builtins.sprite_get_width(sprite).AsReal * sx;
            h = Builtins.sprite_get_height(sprite).AsReal * sy;
            x -= Builtins.sprite_get_xoffset(sprite).AsReal * sx;
            y -= Builtins.sprite_get_yoffset(sprite).AsReal * sy;
        }
        double ox = -5000, oy = -5000;
        if (Objects.o_globalmapBackground.First is { } bg)
            (ox, oy) = (bg.Get("guiVisibleAreaBorderLeft").AsReal, bg.Get("guiVisibleAreaBorderTop").AsReal);
        double k = GuiScale;
        return ((x - ox) * k, (y - oy) * k, w * k, h * k);
    }

    // ------------------------------------------------------------ the banner

    // Where the banner was last drawn, so a click on it is not a click on the map.
    private (double X, double Y, double W, double H) _banner;

    public bool BannerContains(double mx, double my) =>
        _banner.W > 0 && mx >= _banner.X && mx <= _banner.X + _banner.W && my >= _banner.Y && my <= _banner.Y + _banner.H;

    public void HideBanner() => _banner = default;

    /// <summary>A board with one line of colour text, centred at the top of the screen.</summary>
    public void DrawBanner(string text)
    {
        double map = TextMap(text);
        var data = new DsMap(map);
        double w = data.Get("width").AsReal + 2 * Pad, h = data.Get("height").AsReal + 2 * Pad;
        double x = Math.Round((GameDraw.GuiWidth - w) / 2), y = 24;
        _banner = (x, y, w, h);
        Scripts.scr_globalmapDrawBoard.Call(x, y, w, h, BoardScale);
        Scripts.scr_colorTextDraw.Call(map, x + Pad, y + Pad, 0, 0, 0, 1);
    }

    // The game's colour-text maps are made once per string and kept. They are
    // this mod's own: nothing else knows their ids, so none is destroyed under
    // us and ds_exists only guards against a game that was reloaded meanwhile.
    private double TextMap(string text)
    {
        if (_textMaps.TryGetValue(text, out var id) && Builtins.ds_exists(id, 1).AsBool) return id;
        if (_textMaps.Count > 32) Clear();
        var map = Builtins.ds_map_create();
        Scripts.scr_colorTextCreate.Call(map, text, White, 1600, 1);
        return _textMaps[text] = map.AsReal;
    }

    public void Clear()
    {
        foreach (var id in _textMaps.Values)
            if (Builtins.ds_exists(id, 1).AsBool) Builtins.ds_map_destroy(id);
        _textMaps.Clear();
    }
}
