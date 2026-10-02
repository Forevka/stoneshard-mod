using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// One line of the game's colour text on the board the world map's controls bar
/// uses, centred at the top of the screen (FastTravel's banner), below the
/// character's status effects.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25): the status effect icons are the
/// children of one o_modificatorsMenu, centred at the top of the screen. Like
/// all of the game's GUI it lives in room space under global.cameraGUI (a
/// 960x540 view at -5000,-5000). Its guiHeight grows with each row of icons
/// (26 for one row, 56 for two), and the game moves its own quest tracker down
/// to match.
/// </remarks>
internal sealed class Banner
{
    private const double White = 16777215;
    private const int Pad = 20, BoardScale = 2, Top = 24, Gap = 8;

    // The game's colour-text maps are made once per string and kept; ds_exists
    // only guards against a game that was reloaded meanwhile.
    private readonly Dictionary<string, double> _textMaps = new();

    public void Draw(string text)
    {
        double map = TextMap(text);
        var data = new DsMap(map);
        double w = data.Get("width").AsReal + 2 * Pad, h = data.Get("height").AsReal + 2 * Pad;
        double x = Math.Round((GameDraw.GuiWidth - w) / 2);
        double top = Math.Max(Top, Math.Round(EffectsBottom() + Gap));
        Scripts.scr_globalmapDrawBoard.Call(x, top, w, h, BoardScale);
        Scripts.scr_colorTextDraw.Call(map, x + Pad, top + Pad, 0, 0, 0, 1);
    }

    /// <summary>
    /// Where the status effect icons end, in Draw GUI pixels; 0 when there are
    /// none or they cannot be read (the banner then keeps to the very top).
    /// </summary>
    private static double EffectsBottom()
    {
        if (Objects.o_modificatorsMenu.First is not { } bar) return 0;
        // The bar itself is never drawn (visible 0); its icons are, one child each.
        if (World.Num(bar, "guiChildrenCount") <= 0) return 0;
        var camera = Globals.Get("cameraGUI");
        if (!camera.IsNumber) return 0;
        double viewY = Builtins.camera_get_view_y(camera).AsReal, viewH = Builtins.camera_get_view_height(camera).AsReal;
        if (viewH <= 0) return 0;
        double bottom = World.Num(bar, "y") + World.Num(bar, "guiHeight");
        return (bottom - viewY) * GameDraw.GuiHeight / viewH;
    }

    private double TextMap(string text)
    {
        if (_textMaps.TryGetValue(text, out var id) && Builtins.ds_exists(id, 1).AsBool) return id;
        if (_textMaps.Count > 32) Clear();
        var map = Builtins.ds_map_create();
        try { Scripts.scr_colorTextCreate.Call(map, text, White, 1600, 1); }
        catch (GmlException)
        {
            Builtins.ds_map_destroy(map);
            throw;
        }
        return _textMaps[text] = map.AsReal;
    }

    public void Clear()
    {
        foreach (var id in _textMaps.Values)
            if (Builtins.ds_exists(id, 1).AsBool) Builtins.ds_map_destroy(id);
        _textMaps.Clear();
    }
}
