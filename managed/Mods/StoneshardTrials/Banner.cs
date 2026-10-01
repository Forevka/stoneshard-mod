using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// One line of the game's colour text on the board the world map's controls bar
/// uses, centred at the top of the screen (FastTravel's banner).
/// </summary>
internal sealed class Banner
{
    private const double White = 16777215;
    private const int Pad = 20, BoardScale = 2, Top = 24;

    // The game's colour-text maps are made once per string and kept; ds_exists
    // only guards against a game that was reloaded meanwhile.
    private readonly Dictionary<string, double> _textMaps = new();

    public void Draw(string text)
    {
        double map = TextMap(text);
        var data = new DsMap(map);
        double w = data.Get("width").AsReal + 2 * Pad, h = data.Get("height").AsReal + 2 * Pad;
        double x = Math.Round((GameDraw.GuiWidth - w) / 2);
        Scripts.scr_globalmapDrawBoard.Call(x, Top, w, h, BoardScale);
        Scripts.scr_colorTextDraw.Call(map, x + Pad, Top + Pad, 0, 0, 0, 1);
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
