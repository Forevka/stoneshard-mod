using CoreLoader;
using StoneShard;
using StoneshardTrials.Ui;

namespace StoneshardTrials.Cards;

/// <summary>
/// The window that lays out the offered cards after a won trial, drawn with the
/// game's own board, buttons and colour text (ModMenu's way). It is modal: a
/// card is taken or all are turned down before play goes on.
/// </summary>
internal sealed class CardWindow
{
    private static readonly Area Window = new(110, 56, 740, 428);
    private const double CardW = 220, CardH = 300, CardGap = 20, CardTop = 118;

    private readonly Canvas _canvas = new();
    private readonly Logger _log;
    private readonly Action<int> _take;
    private readonly Action _discard;
    private Offer? _offer;
    private CardLook _look;
    private string _lastError = "";

    public CardWindow(Logger log, Action<int> take, Action discard)
    {
        _log = log;
        _take = take;
        _discard = discard;
    }

    public bool IsOpen => _offer != null;

    /// <summary>Whether the window shows this offer (the same trial and cards).</summary>
    public bool Shows(Offer offer) => _offer != null && _offer.Trial == offer.Trial && _offer.Cards.SequenceEqual(offer.Cards);

    public void Open(Offer offer, CardLook look)
    {
        if (_offer != null) return;
        _offer = offer;
        _look = look;
        Input.ArmPick();
        Sfx("snd_ui_open_window_st");
    }

    public void Close()
    {
        if (_offer == null) return;
        _offer = null;
        if (Input.IsPicking) Input.CancelPick();
        Sfx("snd_ui_close_window_st");
    }

    public void Clear() => _canvas.Clear();

    /// <summary>While open, every click is the window's (another mod's armed pick is taken over).</summary>
    public void Update()
    {
        if (_offer == null) return;
        if (!Input.IsPicking) Input.ArmPick();
        if (!Input.TryTakePick(out _)) return;
        Input.ArmPick();
        var (x, y) = _canvas.ToDesign(Builtins.device_mouse_x_to_gui(0).AsReal, Builtins.device_mouse_y_to_gui(0).AsReal);
        if (_canvas.ButtonAt(x, y) is not { } id) return;
        Sfx("snd_button_click");
        if (id == "discard") _discard();
        else if (id.StartsWith("take:", StringComparison.Ordinal) && int.TryParse(id[5..], out int i)) _take(i);
    }

    public void Draw()
    {
        if (_offer is not { } offer) return;
        // Any failure is the window's own: it must never fault the mod every frame.
        try { DrawWindow(offer); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex.Message != _lastError) _log.Warning($"cards: {ex.Message}");
            _lastError = ex.Message;
        }
    }

    private void DrawWindow(Offer offer)
    {
        var c = _canvas;
        c.Begin();
        c.Dim(0.6);
        c.Board(Window);
        c.Text($"~y~Trial {offer.Trial} is won~/~", Window.CenterX, Window.Y + 16, align: 0);
        c.Text("~gr~Choose one boon, or turn them all down.~/~", Window.CenterX, Window.Y + 36, align: 0);
        int n = offer.Cards.Count;
        // Three fit at full width; a fourth (Second Look) narrows them.
        double w = Math.Min(CardW, (Window.W - 40 - (n - 1) * CardGap) / Math.Max(1, n));
        double total = n * w + (n - 1) * CardGap, x = Window.CenterX - total / 2;
        for (int i = 0; i < n; i++, x += w + CardGap)
            DrawCard(i, Catalog.Find(offer.Cards[i]), new Area(x, CardTop, w, CardH),
                _look with { Detail = At(offer.Details, i) }, Catalog.FindCost(At(offer.Costs, i)), _look with { Detail = At(offer.CostDetails, i) });
        c.Button("discard", "DISCARD ALL", new Area(Window.CenterX - 70, Window.Y + Window.H - 40, 140, 26));
    }

    private static string? At(List<string?> list, int i) => i < list.Count ? list[i] : null;

    private void DrawCard(int index, CardDef? card, Area a, CardLook look, CostDef? cost, CardLook costLook)
    {
        var c = _canvas;
        c.Board(a);
        if (card == null)
        {
            c.Text("~gr~(no longer in the game)~/~", a.CenterX, a.CenterY, align: 0);
            return;
        }
        c.Text($"~w~{card.Title}~/~", a.CenterX, a.Y + 14, align: 0, wrap: a.W - 24);
        c.Text($"~gr~tier {look.Tier}~/~", a.CenterX, a.Y + 32, align: 0);
        double y = a.Y + 52;
        if (card.Icon != null && c.Sprite(card.Icon, 0, new Area(a.CenterX - 24, y, 48, 48))) y += 58;
        else y += 6;
        c.Rule(a.X + 16, y, a.W - 32);
        y += 8;
        foreach (var line in card.Gains(look)) y += c.Text($"~lg~{line}~/~", a.X + 14, y, wrap: a.W - 28).H + 4;
        // The cost dealt with it, or its own.
        var costs = cost != null ? cost.Text(costLook) : card.Costs(look);
        if (costs.Count > 0)
        {
            y += 6;
            foreach (var line in costs) y += c.Text($"~r~{line}~/~", a.X + 14, y, wrap: a.W - 28).H + 4;
        }
        double bw = Math.Min(120, a.W - 30);
        c.Button($"take:{index}", "TAKE", new Area(a.CenterX - bw / 2, a.Y + a.H - 38, bw, 26));
    }

    private void Sfx(string name)
    {
        try
        {
            double id = _canvas.Asset(name);
            if (id >= 0) Builtins.audio_play_sound(id, 5, false);
        }
        catch (GmlException) { }
    }
}
