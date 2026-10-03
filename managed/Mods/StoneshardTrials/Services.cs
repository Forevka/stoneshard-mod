using CoreLoader;
using StoneShard;

namespace StoneshardTrials;

/// <summary>
/// The hub innkeeper's services between trials, as lines in his own
/// conversation: a full healing, and a cheaper dressing of wounds, for crowns.
/// </summary>
/// <remarks>
/// The conversation hook is TavernGames' (DialogueOption.cs): a conversation's
/// options are fragment keys that scr_dialogue_sort_options orders (as
/// o_dialogue); dialogue_get_string(key) gives each button its line; choosing
/// one calls scr_dialogue_advance(key). Our keys are added before "leave",
/// answered before the lookup, and turned into "@dialogue_end" when chosen.
///
/// The healing (.omc/research/trials-next.md 4, verified): scr_injuryChange(
/// player, part, +n) run as the player heals a body part (the injury statuses
/// then clear by themselves within a turn or two); HP is capped by
/// Health_Threshold, which follows the body parts, so HP is written (the
/// attribute and the variable) after them; Pain is an attribute; an unwanted
/// status ends when its duration is set to 1. scr_restore_hp did not heal.
/// </remarks>
internal sealed class Services
{
    private const string Heal = "lodestone_heal", Treat = "lodestone_treat";
    private const string EndKey = "@dialogue_end", LeaveKey = "leave";
    private const string Innkeeper = "o_npc_innkeeper_osbrook";
    private static readonly string[] Parts = { "head", "tors", "lhand", "rhand", "legs", "rlegs" };
    // What a dressing takes off: bleeding and open wounds.
    private static readonly string[] Wounds = { "o_db_bleed", "o_db_wound", "o_db_light_wound", "o_db_deep_wound", "o_db_gaping_wound" };
    // What a full healing takes off besides: poison, intoxication, daze, pain, fever and injuries.
    private static readonly string[] Ailments =
        Wounds.Concat(new[] { "o_db_poison", "o_db_tox", "o_db_intoxicated", "o_db_daze", "o_db_pain", "o_db_fever", "o_db_inj_" }).ToArray();

    private readonly Logger _log;
    private readonly Func<bool> _open;

    /// <param name="log">Where a refused change to a conversation is reported.</param>
    /// <param name="open">Whether the services are offered now (the trials on, a character in play).</param>
    public Services(Logger log, Func<bool> open)
    {
        _log = log;
        _open = open;
    }

    public void Install()
    {
        Scripts.scr_dialogue_sort_options.After(c => Guard(() => AddOptions(c)));
        Scripts.dialogue_get_string.Before(c => Guard(() => Answer(c)));
        Scripts.scr_dialogue_advance.Before(c => Guard(() => Choose(c)));
    }

    private void Guard(Action act)
    {
        try { act(); }
        // These hooks run for every line of every conversation: nothing may fault the mod from here.
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warning($"services: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static int Level(InstanceRef player) => Math.Max(1, (int)World.Num(player, "LVL", 1));
    // Priced by the character's level: a full healing 40 + 20 per level, a dressing half that.
    public static int HealPrice(InstanceRef player) => 40 + 20 * Level(player);
    public static int TreatPrice(InstanceRef player) => HealPrice(player) / 2;

    private static bool IsInnkeeper(Instance dialogue)
    {
        if (dialogue.IsNull) return false;
        foreach (var name in new[] { "speaker_instance", "interact_id", "owner" })
        {
            var v = dialogue.Get(name);
            if (World.IdKey(v) < 0) continue;
            var npc = new InstanceRef(v);
            if (npc.Exists && Builtins.object_get_name(npc.Get("object_index")).ToString() == Innkeeper) return true;
        }
        return false;
    }

    private void AddOptions(HookCall c)
    {
        if (!_open() || !IsInnkeeper(c.Self)) return;
        var options = c.Result;
        if (Gml.TypeOf(options) != "array") return;
        int n = Gml.ArrayLength(options);
        var keys = Enumerable.Range(0, n).Select(i => Gml.ArrayGet(options, i)).Select(v => v.Kind == RValueKind.String ? v.ToString() : "").ToList();
        // Only on the main menu, the one that offers a way out.
        if (!keys.Contains(LeaveKey) || keys.Contains(Heal)) return;
        Builtins.array_insert(options, keys.IndexOf(LeaveKey), Treat);
        Builtins.array_insert(options, keys.IndexOf(LeaveKey), Heal);
    }

    private static bool IsKey(HookCall c, string key) =>
        c.ArgCount >= 1 && c.GetArg(0) is { Kind: RValueKind.String } a && a.ToString() == key;

    private void Answer(HookCall c)
    {
        if (World.Player is not { } player) return;
        if (IsKey(c, Heal))
        {
            c.SkipOriginal();
            c.Result = $"Patch me up, head to toe. ({HealPrice(player)} crowns)";
        }
        else if (IsKey(c, Treat))
        {
            c.SkipOriginal();
            c.Result = $"Just dress my wounds. ({TreatPrice(player)} crowns)";
        }
    }

    private void Choose(HookCall c)
    {
        bool heal = IsKey(c, Heal), treat = IsKey(c, Treat);
        if (!heal && !treat) return;
        // Our keys have no fragment: the conversation ends as leaving does, first.
        c.SetArg(0, EndKey);
        if (World.Player is not { } player) return;
        int price = heal ? HealPrice(player) : TreatPrice(player);
        if (Cards.Effects.GoldCount(player) < price)
        {
            World.Say($"The innkeeper shakes his head: that is ~y~{price} crowns~/~.");
            return;
        }
        // Healed first, then charged: a healing cut short by an error costs nothing.
        Mend(player, heal ? 100 : 50, full: heal);
        Cards.Effects.TakeGold(player, price);
        _log.Info($"services: {(heal ? "healed" : "wounds dressed")} for {price} crowns");
        World.Say(heal ? $"For ~y~{price} crowns~/~ the innkeeper's people patch you up, head to toe."
                       : $"For ~y~{price} crowns~/~ your wounds are cleaned and bound.");
    }

    /// <summary>Heals the body parts by <paramref name="amount"/>, ends bleeding and wounds (full: every ailment), and (full) restores health and pain.</summary>
    public static void Mend(InstanceRef player, double amount, bool full)
    {
        foreach (var part in Parts) Scripts.scr_injuryChange.CallAs(player, player.Id, part, amount);
        var list = new DsList(player.Get("buffs"));
        if (list.Exists)
            for (int i = 0; i < list.Count; i++)
            {
                var b = new InstanceRef(list.At(i));
                if (World.IdKey(b.Id) < 0 || !b.Exists || b.Get("lodestone_boon").Kind == RValueKind.String) continue;
                string name = Builtins.object_get_name(b.Get("object_index")).ToString();
                if ((full ? Ailments : Wounds).Any(a => name.StartsWith(a, StringComparison.Ordinal))) b.Set("duration", 1);
            }
        if (!full) return;
        // After the parts: HP is capped by the threshold they set.
        double max = World.Num(player, "max_hp", 100);
        Scripts.scr_atr_set.CallAs(player, "HP", max);
        player.Set("HP", max);
        Scripts.scr_atr_set.CallAs(player, "Pain", 0);
    }
}
