using CoreLoader;

[assembly: CoreModInfo(typeof(ContentDemo.ContentDemoMod), "Content Demo", "1.0.0", "CoreLoader")]

namespace ContentDemo;

/// <summary>
/// New content in any YYC game, loaded from files at runtime: a spinning coin
/// sprite from a PNG strip drawn into the game's own GUI layer, a chime from an
/// OGG file, and a reskin of any of the game's sprites that is undone on unload.
/// </summary>
public sealed class ContentDemoMod : CoreMod
{
    private Sprite? _coin;
    private Sound? _chime;
    private Sprite? _reskin;
    private IDisposable? _drawing;
    private string _target = "";
    private string _status = "";
    private bool _badge = true;
    private double _spin;
    private int _chimes;

    public override void OnInitialize()
    {
        _badge = Config.Get("badge", true);
        _target = Config.Get("reskin", "");

        _coin = Content.AddSprite("assets/coin.png", frames: 8, xOrigin: 24, yOrigin: 24);
        _chime = Content.AddSound("assets/chime.ogg");
        _drawing = GameDraw.OnGui(DrawBadge);
        Log.Info($"coin sprite {_coin.Index} ({_coin.Frames} frames, {_coin.Width}x{_coin.Height}), chime sound {_chime.Index}");

        if (_target.Length > 0) Reskin(_target);
    }

    private void DrawBadge()
    {
        if (!_badge || _coin == null) return;
        _spin += 0.25;
        double x = GameDraw.GuiWidth - 40, y = 40;
        _coin.Draw(x, y, frame: _spin);
        // A second, tinted and fading copy shows the rest of draw_sprite_ext.
        _coin.Draw(x - 56, y, frame: _spin + 4, xScale: 0.75, yScale: 0.75, colour: 0xFFC080,
                   alpha: 0.5 + 0.5 * Math.Sin(_spin / 4));
    }

    public override void OnGUI()
    {
        if (_coin == null || _chime == null) return;
        UI.Text($"Coin: sprite {_coin.Index}, {_coin.Frames} frames - drawn via {GameDraw.Carrier ?? "(waiting for a Draw GUI event)"}");
        if (UI.Checkbox("Show the coin badge (top right of the game)", ref _badge)) Config.Set("badge", _badge);

        UI.Separator();
        UI.Text($"Chime: sound {_chime.Index}{(_chime.IsPlaying ? " - playing" : "")}, played {_chimes} time(s)");
        if (UI.Button("Play chime")) { _chime.Play(); _chimes++; }

        UI.Separator();
        UI.Text("Reskin one of the game's sprites with the coin (undone on unload):");
        UI.InputText("sprite name##reskin", ref _target, 128);
        if (UI.Button("Reskin")) Reskin(_target.Trim());
        UI.SameLine();
        if (UI.Button("Restore") && _reskin != null)
        {
            _reskin.Dispose();
            _reskin = null;
            _status = "restored";
            Config.Set("reskin", "");
        }
        if (_status.Length > 0) UI.TextDisabled(_status);
    }

    private void Reskin(string name)
    {
        try
        {
            _reskin?.Dispose();
            _reskin = Content.ReplaceSprite(name, "assets/coin.png", frames: 8, xOrigin: 24, yOrigin: 24);
            _status = $"{name} now shows the coin";
            Config.Set("reskin", name);
        }
        catch (GmlException ex)
        {
            _reskin = null;
            _status = ex.Message;
        }
    }

    public override void OnShutdown()
    {
        // Drawing stops and the assets are released by the loader anyway; this
        // just shows the explicit form.
        _drawing?.Dispose();
    }
}
