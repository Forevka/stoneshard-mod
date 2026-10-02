using CoreLoader;
using StoneShard;

namespace StoneshardTrials.Cards;

/// <summary>
/// The character's skill trees, as the game names them in its three places,
/// and the lock a card can put on one.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25; .omc/research/trials-rewards-catalogue.md):
///   * the Abilities window lists each tree as an o_skill_category_* instance;
///     its `disabled` 1 greys the tree as the unfinished Wands tree is, and it
///     is lost with a load (it is set again every second while locked);
///   * learning a skill is the skill icon's user event 0 (o_skill_ico Other_10),
///     whose `branch` names the tree;
///   * reading a treatise calls scr_skill_branch_study as the treatise, an
///     o_inv_treatise_&lt;prefix&gt;&lt;n&gt;.
/// </remarks>
internal static class Trees
{
    public sealed record Tree(string Name, string Category, string Branch, string Treatise, int Treatises);

    public static readonly IReadOnlyList<Tree> All = new Tree[]
    {
        new("Swords", "o_skill_category_sword", "swords", "sword", 3),
        new("Axes", "o_skill_category_axe", "axes", "axe", 3),
        new("Maces", "o_skill_category_mace", "maces", "mace", 3),
        new("Daggers", "o_skill_category_dagger", "daggers", "dagger", 3),
        new("Greatswords", "o_skill_category_greatsword", "greatswords", "gsword", 3),
        new("Greataxes", "o_skill_category_greataxe", "greataxes", "greataxes", 3),
        new("Greatmauls", "o_skill_category_greatmauls", "greatmauls", "greatmaces", 3),
        new("Polearms", "o_skill_category_polearms", "polearms", "polearms", 3),
        new("Archery", "o_skill_category_bows", "bows", "ranged", 4),
        new("Shields", "o_skill_category_shields", "shields", "shield", 3),
        new("Staves", "o_skill_category_staves", "staves", "staff", 3),
        new("Armored Combat", "o_skill_category_basic_armor", "armor", "armor", 3),
        new("Athletics", "o_skill_category_athletics", "athletics", "athletics", 4),
        new("Warfare", "o_skill_category_combat", "combat", "combat", 4),
        new("Survival", "o_skill_category_survival", "survival", "survival", 2),
        new("Dual Wielding", "o_skill_category_dual_wielding", "dual wielding", "dualwield", 3),
        new("Arcanistics", "o_skill_category_arcanistics", "arcanistics", "arcane", 4),
        new("Electromancy", "o_skill_category_electromancy", "electromancy", "electro", 4),
        new("Geomancy", "o_skill_category_geomancy", "geomancy", "geo", 4),
        new("Pyromancy", "o_skill_category_pyromancy", "pyromancy", "pyro", 4),
        new("Magic Mastery", "o_skill_category_mastery_of_magic", "magic_mastery", "magic", 4),
    };

    public static Tree? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>The treatise object of a tree at a tier, the highest it has when asked for more.</summary>
    public static string TreatiseObject(Tree t, int tier) => $"o_inv_treatise_{t.Treatise}{Math.Clamp(tier, 1, t.Treatises)}";

    /// <summary>The tree a treatise object teaches, or null.</summary>
    public static Tree? OfTreatise(string objectName)
    {
        const string prefix = "o_inv_treatise_";
        if (!objectName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string rest = objectName[prefix.Length..].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return All.FirstOrDefault(t => t.Treatise == rest);
    }

    /// <summary>Greys the locked trees in an open Abilities window (cheap: only their instances are touched).</summary>
    public static void GreyOut(IEnumerable<Tree> locked)
    {
        foreach (var t in locked)
            if (GmlObject.Find(t.Category) is { InstanceCount: > 0 } cat)
                foreach (var inst in cat.Instances())
                    if (World.Num(inst, "disabled") != 1) inst.Set("disabled", 1);
    }

    /// <summary>Whether a learn (o_skill_ico user event 0) is of a locked tree.</summary>
    public static bool IsLockedSkill(Instance icon, IEnumerable<Tree> locked) =>
        !icon.IsNull && icon.Get("branch") is { Kind: RValueKind.String } b && locked.Any(t => t.Branch == b.ToString());

    /// <summary>Whether a treatise being read (scr_skill_branch_study's self) is of a locked tree.</summary>
    public static bool IsLockedTreatise(Instance treatise, IEnumerable<Tree> locked) =>
        !treatise.IsNull && OfTreatise(Builtins.object_get_name(treatise.Get("object_index")).ToString()) is { } t && locked.Contains(t);
}
