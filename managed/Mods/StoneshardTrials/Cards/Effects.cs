using CoreLoader;
using StoneShard;

namespace StoneshardTrials.Cards;

/// <summary>
/// The things cards do, each through a path the game itself uses.
/// </summary>
/// <remarks>
/// Established on the running game (0.9.4.25):
///   * scr_gold_add(n) run as the player adds crowns (a negative n adds too;
///     scr_gold_write_off(n) takes them);
///   * scr_inventory_add_item(object) run as the exact o_inventory puts a new
///     item in the bag and returns it; a full bag throws it on the ground and
///     returns noone;
///   * every potion is an o_inv_bottle whose data.atrdlist (a ds_list of effect
///     tags) is what it does: its Alarm 0 rolls one (scr_roll_potion), and
///     rewriting the list in an After hook on that alarm, then calling
///     scr_potion_set_param as the bottle, makes any combination
///     (StoneshardCheats' Potions tab);
///   * scr_item_destroy run as an item removes it, worn or not.
/// </remarks>
internal static class Effects
{
    public static void Gold(InstanceRef player, int amount) => Scripts.scr_gold_add.CallAs(player, amount);

    /// <summary>
    /// Adds to one of the character's saved numbers (global.characterDataMap):
    /// base attributes, "AP" (shown as SP, attribute points), "SP" (shown as AP,
    /// ability points), or a bonus key such as bEVS or bMp, which scr_atr_calc
    /// adds to the derived stat. Answers the new value.
    /// </summary>
    public static double AddAtr(InstanceRef player, string key, double amount)
    {
        var now = Scripts.scr_atr.CallAs(player, key);
        double value = (now.IsNumber ? now.AsReal : 0) + amount;
        Scripts.scr_atr_set_simple.CallAs(player, key, value);
        return value;
    }

    /// <summary>
    /// A status that lasts (99999 turns), made the way the game makes one:
    /// the buff instance, owned by the player, in its buff list, then its own
    /// Alarm 2, which fills in its numbers. Numbers given here replace those.
    /// The game saves the buff and its turns, but refills its numbers from the
    /// buff's defaults whenever it makes it again (a load), so custom numbers
    /// are written again by <see cref="Retune"/>. It carries <paramref name="tag"/>
    /// (the card's id), so the mod finds its own buff and never one the game
    /// gave the player of the same kind.
    /// </summary>
    public static void LongBuff(InstanceRef player, string objectName, string tag, IReadOnlyDictionary<string, double>? numbers = null, double turns = Forever)
    {
        // Everything that can be missing is looked up before anything is made.
        var obj = GmlObject.Find(objectName) ?? throw new InvalidOperationException($"no {objectName}");
        var list = new DsList(player.Get("buffs"));
        if (!list.Exists) throw new InvalidOperationException("the player has no buff list");
        string alarm = $"gml_Object_{objectName}_Alarm_2";
        if (Game.FindSymbol(alarm) == 0) throw new InvalidOperationException($"{objectName} has no Alarm 2");
        var buff = new InstanceRef(Builtins.instance_create_depth(World.Num(player, "x"), World.Num(player, "y"), 0, obj.Index));
        if (!buff.Exists) throw new InvalidOperationException($"{objectName} was not made");
        buff.Set("owner", player.Id);
        buff.Set("target", Objects.o_player.Object?.Index ?? -1);
        buff.Set("duration", turns);
        buff.Set(TagVar, tag);
        list.Add(buff.Id);
        player.Set("buffs_is_change", true);
        if (buff.Resolve() is { } inst) Game.CallEvent(alarm, inst);
        // Some buffs set their own duration in that alarm (Curse of Decay: 36 turns); ours wins.
        buff.Set("duration", turns);
        if (numbers != null) SetNumbers(player, buff, numbers);
    }

    public const double Forever = 99999;
    private const string TagVar = "lodestone_boon";

    /// <summary>
    /// The player's buff of that object made for <paramref name="tag"/>. A load
    /// makes buffs anew without the tag: the longest-lasting of the kind is
    /// then ours (the mod's last 99999 turns, against the game's few), and it
    /// is tagged again.
    /// </summary>
    public static InstanceRef? FindBuff(InstanceRef player, string objectName, string tag)
    {
        var list = new DsList(player.Get("buffs"));
        if (!list.Exists) return null;
        InstanceRef? longest = null;
        double most = -1;
        for (int i = 0; i < list.Count; i++)
        {
            var b = new InstanceRef(list.At(i));
            if (World.IdKey(b.Id) < 0 || !b.Exists || Builtins.object_get_name(b.Get("object_index")).ToString() != objectName) continue;
            var own = b.Get(TagVar);
            if (own.Kind == RValueKind.String && own.ToString() == tag) return b;
            if (own.Kind == RValueKind.String) continue; // another boon's
            double d = World.Num(b, "duration");
            if (d > most) (longest, most) = (b, d);
        }
        if (longest is { } found && most >= Forever / 2) found.Set(TagVar, tag);
        else longest = null;
        return longest;
    }

    /// <summary>Writes custom numbers onto the player's buff again (after the game refilled them).</summary>
    public static void Retune(InstanceRef player, string objectName, string tag, IReadOnlyDictionary<string, double> numbers)
    {
        if (FindBuff(player, objectName, tag) is { } buff) SetNumbers(player, buff, numbers);
    }

    /// <summary>Puts the buff back if the player lost it, and its turns back to lasting if they ran down.</summary>
    public static void KeepBuff(InstanceRef player, string objectName, string tag)
    {
        if (FindBuff(player, objectName, tag) is not { } buff) LongBuff(player, objectName, tag);
        else if (World.Num(buff, "duration") < Forever / 2) buff.Set("duration", Forever);
    }

    /// <summary>Ends the boon's buff the way a buff ends by itself: its last turn.</summary>
    public static void EndBuff(InstanceRef player, string objectName, string tag)
    {
        if (FindBuff(player, objectName, tag) is { } buff) buff.Set("duration", 1);
    }

    // The player's stats take a buff's numbers when its list is marked changed.
    private static void SetNumbers(InstanceRef player, InstanceRef buff, IReadOnlyDictionary<string, double> numbers)
    {
        var data = new DsMap(buff.Get("data"));
        if (!data.Exists) throw new InvalidOperationException("the buff has no data map");
        bool changed = false;
        foreach (var (key, value) in numbers)
        {
            if (data.Get(key) is { IsNumber: true } v && v.AsReal == value) continue;
            data.Set(key, value);
            changed = true;
        }
        if (changed) player.Set("buffs_is_change", true);
    }
    /// <summary>
    /// Night vision is a variable of the player instance, not a stat: it is lost
    /// with every new player instance (each room, each load), so it is set again then.
    /// </summary>
    public static void NightVision(InstanceRef player)
    {
        if (World.Num(player, "night_vision") != 1) player.Set("night_vision", 1);
    }

    /// <summary>
    /// A new item of that object in the bag; null when the bag was full, and
    /// the game then put it on the ground at the player's feet (which is said).
    /// </summary>
    public static InstanceRef? Item(string objectName)
    {
        var inventory = World.Inventory() ?? throw new InvalidOperationException("no o_inventory");
        var obj = GmlObject.Find(objectName) ?? throw new InvalidOperationException($"no {objectName}");
        var made = new InstanceRef(Scripts.scr_inventory_add_item.CallAs(inventory, obj.Index));
        if (World.IdKey(made.Id) >= 0 && made.Exists) return made;
        World.Say("~y~Your bag is full~/~: the gift lies at your feet.");
        return null;
    }

    private const string BottleAlarm = "gml_Object_o_inv_bottle_Alarm_0";
    private static readonly TimeSpan BottleTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A potion with exactly these effect tags. The bottle is given now; its
    /// effects are written when the game rolls it, on a later step.
    /// </summary>
    public static void Potion(IReadOnlyList<string> tags, Logger log)
    {
        if (Objects.o_inv_bottle.Object is not { } bottles) throw new InvalidOperationException("no o_inv_bottle");
        var before = bottles.Instances().Select(r => World.IdKey(r.Id)).ToHashSet();
        long aimed = -1;
        // Armed before the give: the alarm may come on the next step or sooner.
        var request = Hooks.NextAfter(BottleAlarm,
            c => !c.Self.IsNull && !c.OriginalSkipped && (aimed < 0 || World.IdKey(c.Self.Get("id")) == aimed),
            c =>
            {
                try { log.Info($"potion: {Rewrite(c.Self, tags)}"); }
                catch (Exception ex) when (ex is GmlException or InvalidOperationException) { log.Warning($"potion: {ex.Message}"); }
            },
            BottleTimeout, () => log.Warning("potion: the bottle never rolled; it stays a plain potion"));
        try
        {
            if (Item(Objects.o_inv_bottle.Name) is { } made) aimed = World.IdKey(made.Id);
        }
        catch
        {
            request.Dispose();
            throw;
        }
        // Not in the bag (thrown on the ground): the first new bottle is ours.
        if (aimed < 0)
            aimed = bottles.Instances().Select(r => World.IdKey(r.Id)).FirstOrDefault(id => !before.Contains(id), -1);
        // Still unaimed, the request would rewrite whichever bottle rolls next (another gift's, loot).
        if (aimed < 0 && request.IsPending)
        {
            request.Dispose();
            log.Warning("potion: the new bottle could not be singled out; it stays a plain potion");
        }
    }

    // Inside the bottle's own event, the only place the potion scripts run.
    private static string Rewrite(Instance bottle, IReadOnlyList<string> tags)
    {
        var data = new DsMap(bottle.Get("data"));
        if (!data.Exists) throw new InvalidOperationException("the bottle has no data map");
        // A ds_list held in the map: edited in place, never replaced.
        var list = new DsList(data.Get("atrdlist"));
        if (!list.Exists) throw new InvalidOperationException("the bottle has no atrdlist");
        list.Clear();
        foreach (var tag in tags) list.Add(tag);
        Game.CallScriptAs(bottle, bottle, "scr_potion_set_param");
        return $"made {data.Get("Name")}";
    }

    /// <summary>The items the player wears, from the bag (a chest's or a trader's are not).</summary>
    public static List<InstanceRef> WornItems()
    {
        var list = new List<InstanceRef>();
        if (World.Inventory() is not { } inventory || Objects.o_inv_slot.Object is not { } slots) return list;
        long bag = World.IdKey(inventory.Id);
        foreach (var item in slots.Instances())
            if (World.Truthy(item.Get("equipped")) && World.IdKey(item.Get("owner")) == bag) list.Add(item);
        return list;
    }
}
