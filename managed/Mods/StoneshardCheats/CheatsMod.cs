using CoreLoader;

[assembly: CoreModInfo(typeof(StoneshardCheats.CheatsMod), "Stoneshard Cheats", "1.0.0", "Lodestone")]
[assembly: CoreModGame("StoneShard")]

namespace StoneshardCheats;

/// <summary>One sub-tab of the mod.</summary>
internal abstract class Tab
{
    public abstract string Name { get; }

    /// <summary>Once, from OnInitialize: install hooks and the like.</summary>
    public virtual void Initialize(CheatsMod mod) { }

    public abstract void Draw();
}

/// <summary>
/// Stoneshard cheats: stats, items, potions, character, body, enemies and save
/// import. Every action runs as the player and backs up the saves first.
/// </summary>
public sealed class CheatsMod : CoreMod
{
    private readonly List<Tab> _tabs = new()
    {
        new StatsTab(),
        new ItemsTab(),
        new PotionsTab(),
        new CharacterTab(),
        new BodyTab(),
        new EnemiesTab(),
        new SavesTab(),
    };

    public override void OnInitialize()
    {
        Actions.Log = Log;
        Hooks.Before("gml_Object_o_player_Step_0", Player.OnStep);
        foreach (var t in _tabs) t.Initialize(this);
        TestCommands.Register(_tabs.OfType<PotionsTab>().Single());
    }

    public override void OnGUI()
    {
        if (!Game.IsGmlReady || !Game.IsAbiProven)
        {
            UI.TextColored(0.95f, 0.4f, 0.4f, "Disabled: the GML bridge's string round-trip has not passed in this session.");
            return;
        }

        if (Player.Available) UI.TextDisabled($"player found  |  {Backup.Result}");
        else UI.TextColored(0.95f, 0.8f, 0.35f, "Waiting for the player: load a save and let the game run.");
        Actions.DrawLast();
        UI.Separator();

        if (!UI.BeginTabBar("##cheats")) return;
        foreach (var t in _tabs)
        {
            if (!UI.BeginTabItem(t.Name)) continue;
            UI.Guarded(t.Draw, ex => UI.TextColored(0.95f, 0.4f, 0.4f, $"{t.Name}: {ex.Message}"));
            UI.EndTabItem();
        }
        UI.EndTabBar();
    }
}
