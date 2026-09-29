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
        Actions.CallRow("Restore HP", "scr_restore_hp", $"scr_restore_hp {_hp:0}", () => RestoreHp(_hp));

        UI.SeparatorText("Attributes");
        UI.SetNextItemWidth(160f);
        UI.InputText("attribute", ref _attribute, 64);
        UI.SameLine();
        UI.SetNextItemWidth(120f);
        UI.InputInt("value", ref _value);
        Actions.CallRow("Set attribute", "scr_atr_set", $"scr_atr_set \"{_attribute}\" {_value}",
            () => SetAttr(_attribute, _value));
        Actions.CallRow("Set attribute (simple)", "scr_atr_set_simple", $"scr_atr_set_simple \"{_attribute}\" {_value}",
            () => SetAttr(_attribute, _value, simple: true));
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
        Actions.CallRow("Add gold", "scr_gold_add", $"scr_gold_add {_gold}", () => AddGold(_gold));
    }

    // The actions, shared by the buttons here and on the Character tab and by
    // the test host's cheats.* commands.

    public static void RestoreHp(double amount) => Player.Call("scr_restore_hp", amount);

    /// <summary>scr_atr_set (or scr_atr_set_simple) as the player.</summary>
    public static void SetAttr(string key, double value, bool simple = false) =>
        Player.Call(simple ? "scr_atr_set_simple" : "scr_atr_set", key, value);

    /// <summary>An attribute as scr_atr reads it; throws if it is not a number.</summary>
    public static double ReadAttr(string key)
    {
        var r = Player.Call("scr_atr", key);
        return r.IsNumber ? r.AsReal : throw new InvalidOperationException($"scr_atr \"{key}\" is not a number ({r})");
    }

    public static void AddGold(double amount) => Player.Call("scr_gold_add", amount);

    /// <summary>
    /// The player's gold as scr_gold_count answers it, or null when it cannot:
    /// the script takes no arguments and runs as the player, which is how its
    /// name reads, but that is not established.
    /// </summary>
    public static double? GoldCount()
    {
        if (Game.FindSymbol("gml_Script_scr_gold_count") == 0) return null;
        try
        {
            var r = Player.Call("scr_gold_count");
            return r.IsNumber ? r.AsReal : null;
        }
        catch (GmlException) { return null; }
    }
}
