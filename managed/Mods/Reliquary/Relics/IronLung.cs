using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Grafts: it breathes for you, on its own schedule.
/// </summary>
/// <remarks>
/// Fatigue: the gain rate (Fatigue_Gain) is capped to zero in the stat layer,
/// and whatever Fatigue is left is set back to zero through the game's own
/// attribute setter each turn. Airborne poison, gas and suffocation are the
/// game's cough, miasma and suffocating-cloud statuses; the lung removes them.
///
/// The design's "you cannot run" has no Stoneshard counterpart (there is no
/// running). In its place, chosen with the user: the lung is loud. Every turn,
/// every hostile within earshot hears where you are, through the same noise
/// fields (noise_x, noise_y, noise_source) the game's noise system fills.
///
/// Doubled torso damage: the torso's condition (Body_Parts_map "tors") is read
/// each turn, and whatever it lost since last turn is lost again.
/// </remarks>
internal sealed class IronLung : Relic
{
    private const string Torso = "tors";
    // How far the breathing carries, beyond the player's own sight.
    private const double Earshot = 4;

    private static readonly string[] Airborne =
    {
        Objects.o_db_cough.Name, Objects.o_db_miasma.Name, Objects.o_db_suffocating_cloud.Name,
    };

    public override string Id => "iron_lung";
    public override string Name => "The Iron Lung";
    public override string Family => "Grafts";
    public override string Flavor => "It breathes for you, on its own schedule.";
    public override string Boon =>
        "~lg~Immune to Fatigue~/~, and to cough, miasma and suffocating clouds. Plague villages, spore caves and long marches " +
        "stop being a resource problem.";
    public override string Toll =>
        "Your torso takes ~r~double~/~ condition damage, and the lung is ~r~loud~/~: every hostile within " +
        $"{Earshot:0} tiles beyond your sight hears exactly where you are, every turn. There is no sneaking with it.";

    public override void OnStats(RelicItem item, Stats stats)
    {
        if (!item.Carried) return;
        stats.Cap(Objects.o_player.Vars.Fatigue_Gain, 0);
    }

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        if (!item.Carried)
        {
            // Put down, the lung forgets the torso: picking it up again starts a
            // fresh comparison instead of charging for damage taken meanwhile.
            item.Set("torso", -1);
            return;
        }

        ClearFatigue(player);
        foreach (var status in Airborne)
        {
            try { World.RemoveStatus(player, status); }
            catch (GmlException) { }
        }
        DoubleTorsoDamage(item, player);
        Breathe(item, player);
    }

    // The need goes through the game's attribute setter, as the character
    // cheats do: writing the variable alone would not reach the game's own
    // bookkeeping of the need.
    private static void ClearFatigue(InstanceRef player)
    {
        var fatigue = player.Get(Objects.o_player.Vars.Fatigue);
        if (fatigue.IsNumber && fatigue.AsReal > 0) Scripts.scr_atr_set.CallAs(player, Objects.o_player.Vars.Fatigue, 0);
    }

    private static void DoubleTorsoDamage(RelicItem item, InstanceRef player)
    {
        var body = World.Body(player);
        var now = body.Exists ? body.Get(Torso) : RValue.Undefined;
        if (!now.IsNumber) return;
        double before = item.Get("torso", -1);
        double current = now.AsReal;
        if (before >= 0 && current < before)
        {
            current = Math.Max(0, current - (before - current));
            body.Set(Torso, current);
        }
        item.Set("torso", current);
    }

    private static void Breathe(RelicItem item, InstanceRef player)
    {
        int heard = 0;
        foreach (var e in World.Hostiles(player, World.Vision(player) + Earshot))
        {
            try
            {
                World.MakeHeard(e, player);
                heard++;
            }
            catch (GmlException) { }
        }
        item.Set("heard", heard);
    }

    public override string Status(RelicItem item) => $"Heard by {item.Get("heard"):0} hostile(s)";
}
