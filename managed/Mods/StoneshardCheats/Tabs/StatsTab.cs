using CoreLoader;

namespace StoneshardCheats;

/// <summary>HP, attributes and gold through the game's own scripts.</summary>
internal sealed class StatsTab : Tab
{
    private double _hp = 100;
    private int _gold = 1000;
    private string _attribute = "STR";
    private int _value = 20;

    public override string Name => "Stats";

    public override void Draw()
    {
        UI.SeparatorText("Vitals");
        UI.SetNextItemWidth(160f);
        UI.InputDouble("amount##hp", ref _hp, 10, 100, "%.0f");
        Actions.CallRow("Restore HP", "scr_restore_hp", $"scr_restore_hp {_hp:0}",
            () => Player.Call("scr_restore_hp", _hp));

        UI.SeparatorText("Attributes");
        UI.SetNextItemWidth(160f);
        UI.InputText("attribute", ref _attribute, 64);
        UI.SameLine();
        UI.SetNextItemWidth(120f);
        UI.InputInt("value", ref _value);
        Actions.CallRow("Set attribute", "scr_atr_set", $"scr_atr_set \"{_attribute}\" {_value}",
            () => Player.Call("scr_atr_set", _attribute, _value));
        Actions.CallRow("Set attribute (simple)", "scr_atr_set_simple", $"scr_atr_set_simple \"{_attribute}\" {_value}",
            () => Player.Call("scr_atr_set_simple", _attribute, _value));
        if (Game.FindSymbol("gml_Script_scr_atr") != 0 && Player.Available)
        {
            UI.SameLine();
            if (UI.SmallButton("read"))
                Actions.Run($"scr_atr \"{_attribute}\"",
                    () => Actions.Report($"{_attribute} = {Player.Call("scr_atr", _attribute)}"));
        }

        UI.SeparatorText("Money");
        UI.SetNextItemWidth(160f);
        UI.InputInt("gold", ref _gold);
        Actions.CallRow("Add gold", "scr_gold_add", $"scr_gold_add {_gold}",
            () => Player.Call("scr_gold_add", _gold));
    }
}
