using CoreLoader;
using StoneShard;

namespace Reliquary.Relics;

/// <summary>
/// Auras: a region of the floor that means something, indiscriminate on purpose.
/// </summary>
/// <remarks>
/// "No ability can be used" is the game's own pair of statuses for taking
/// abilities away: o_db_lock_skills (skills) and o_db_mute (spells). Every
/// unit inside the smoke - the player too - is kept under both for a couple of
/// turns at a time, so stepping out of the aura frees it again shortly after.
/// </remarks>
internal sealed class Censer : Relic
{
    private const double Radius = 3;
    // Long enough to bridge from one turn's refresh to the next, short enough
    // that leaving the smoke frees the unit a turn or two later.
    private const int Hold = 2;

    private static readonly string LockSkills = Objects.o_db_lock_skills.Name;
    private static readonly string Mute = Objects.o_db_mute.Name;

    public override string Id => "censer";
    public override string Name => "Censer of the Drowned Choir";
    public override string Family => "Auras";
    public override string Flavor => "Smoke thick enough that nothing spoken in it carries.";
    public override string Boon =>
        $"A ~y~{Radius:0}~/~-tile smoke in which no ability can be used by anyone: every skill is locked and every spell muted. " +
        "Walk it onto a caster and the caster is a man with a stick.";
    public override string Toll =>
        "~r~Including yours.~/~ Every skill, every spell - you are inside the smoke too. A weapon for a pure melee " +
        "character, dead weight for everyone else.";

    public override void OnTurn(RelicItem item, InstanceRef player)
    {
        if (!item.Carried) return;
        int smothered = 0;
        foreach (var unit in World.UnitsNear(player, Radius).Append(player))
        {
            // One unit that will not take a status (a summon, an odd object)
            // costs only itself.
            try
            {
                World.RefreshStatus(unit, LockSkills, Hold);
                World.RefreshStatus(unit, Mute, Hold);
                smothered++;
            }
            catch (Exception ex) when (ex is GmlException or InvalidOperationException) { }
        }
        item.Set("smothered", smothered);
    }

    public override string Status(RelicItem item)
    {
        double n = item.Get("smothered");
        return n > 0 ? $"Smoke: {n:0} unit(s) silenced, you among them" : "Smoke: waiting for a turn";
    }
}
