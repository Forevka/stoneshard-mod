using CoreLoader;

[assembly: CoreModInfo(typeof(WidgetProbe.Main), "Widget probe", "1.0.0", "CoreLoader tests")]

namespace WidgetProbe;

/// <summary>
/// Exercises every UI round 3 widget, and each new scope under a fault: the
/// "throw inside ..." buttons throw with the scope open, inside UI.Guarded, and
/// the rest of the tab must keep drawing normally afterwards.
/// </summary>
public sealed class Main : CoreMod
{
    private static readonly string[] Rarities = { "Common", "Uncommon", "Rare", "Epic", "Legendary" };
    private int _rarity, _slider = 30, _selected = -1;
    private double _dbl = 1.5;
    private float _flt = 2.25f;
    private string _filter = "";
    private bool _disabled = true;
    private string _lastError = "";

    private enum Fault { None, Combo, Disabled, Color, Clipper }
    private Fault _fault;

    public override void OnGUI()
    {
        DrawFaults();
        UI.SeparatorText("Inputs");
        UI.SetNextItemWidth(160f);
        UI.Combo("rarity", ref _rarity, Rarities);
        UI.SameLine(0f, 20f);
        UI.Text($"picked {Rarities[_rarity]}");
        UI.SetNextItemWidth(160f);
        UI.InputDouble("double", ref _dbl, 0.5, 5, "%.2f");
        UI.SetNextItemWidth(160f);
        UI.InputFloat("float", ref _flt, 0.25f);
        UI.SetNextItemWidth(160f);
        UI.SliderInt("##slider", ref _slider, 5, 120, "every %d frames");
        UI.SetNextItemWidth(-1f);
        UI.InputTextWithHint("##filter", "filter rows...", ref _filter);

        UI.SeparatorText("Layout");
        UI.TextWrapped("This line is long enough that it has to wrap at the edge of the window, " +
                       "which is exactly what TextWrapped is for when a panel explains itself.");
        UI.Spacing();
        UI.ProgressBar(_slider / 120f, 220f, $"{_slider}/120");
        UI.ProgressBar(0.3f);
        UI.Button("Sized button", 220f);
        UI.Tooltip("a tooltip");
        UI.SameLine();
        UI.SmallButton("small");
        UI.Checkbox("disable the next row", ref _disabled);
        UI.BeginDisabled(_disabled);
        UI.Button("I grey out");
        UI.EndDisabled();
        UI.PushTextColor(0.55f, 0.9f, 0.55f);
        UI.Text("green text");
        UI.PopTextColor();

        UI.SeparatorText("Clipped list (5000 rows)");
        var needle = _filter.Trim();
        if (UI.BeginChild("##rows", 160f))
        {
            UI.Clipped(5000, i =>
            {
                string name = $"row {i}";
                if (needle.Length > 0 && !name.Contains(needle)) { UI.TextDisabled("-"); return; }
                if (UI.Selectable($"{name}###r{i}", _selected == i, allowOverlap: true)) _selected = i;
                UI.SameLine(200f);
                UI.SmallButton($"x##{i}");
            });
        }
        UI.EndChild();
        UI.Text($"selected: {_selected}");
    }

    private void DrawFaults()
    {
        UI.SeparatorText("Fault recovery");
        if (UI.Button("throw inside combo")) _fault = Fault.Combo;
        UI.SameLine();
        if (UI.Button("... disabled")) _fault = Fault.Disabled;
        UI.SameLine();
        if (UI.Button("... text colour")) _fault = Fault.Color;
        UI.SameLine();
        if (UI.Button("... clipper")) _fault = Fault.Clipper;

        var fault = _fault;
        _fault = Fault.None;
        UI.Guarded(() =>
        {
            switch (fault)
            {
                case Fault.Combo:
                    // A combo only opens while its popup is; force the throw either way.
                    if (UI.BeginCombo("##faultcombo", "x")) throw new InvalidOperationException("inside combo");
                    throw new InvalidOperationException("combo closed");
                case Fault.Disabled:
                    UI.BeginDisabled();
                    throw new InvalidOperationException("inside disabled");
                case Fault.Color:
                    UI.PushTextColor(1, 0, 0);
                    throw new InvalidOperationException("inside text colour");
                case Fault.Clipper:
                    UI.Clipped(100, i => { if (i == 3) throw new InvalidOperationException("inside clipper"); UI.Text($"{i}"); });
                    break;
            }
        }, ex => _lastError = ex.Message);
        UI.TextDisabled(_lastError.Length == 0 ? "no fault yet" : $"recovered from: {_lastError}");
        UI.Text("If this line is normal white, not greyed or green, every scope was unwound.");
    }
}
