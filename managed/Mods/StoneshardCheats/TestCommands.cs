using System.Text.Json;
using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// The cheats as test-host commands (<c>cheats.*</c>), registered only when the
/// game runs with CORELOADER_TEST=1. Each one calls the method its button
/// calls, and answers with something a test can assert on - usually the value
/// read back after the change.
/// </summary>
internal static class TestCommands
{
    public static void Register(PotionsTab potions)
    {
        if (!TestHost.Enabled) return;

        Actions.Query("cheats.player", "cheats.player: {available, x, y, id}", _ =>
        {
            if (Player.Current is not { } p) return new { available = false };
            var (x, y) = Player.Position;
            return new { available = true, x, y, id = p.Get("id") };
        });

        // ----------------------------------------------------------- stats

        Actions.Query("cheats.atr-get", "cheats.atr-get <key>: scr_atr <key> as the player",
            a => StatsTab.ReadAttr(Actions.Str(a, 0, "key")));
        Actions.Command("cheats.atr-set", "cheats.atr-set <key> <value> [simple]: scr_atr_set, answers scr_atr read back", a =>
        {
            string key = Actions.Str(a, 0, "key");
            bool simple = a.Count > 2 && a[2].ValueKind == JsonValueKind.True;
            StatsTab.SetAttr(key, Actions.Num(a, 1, "value"), simple);
            return StatsTab.ReadAttr(key);
        });
        Actions.Command("cheats.hp", "cheats.hp <amount>: scr_restore_hp, answers {before, after} HP", a =>
        {
            double before = StatsTab.ReadAttr("HP");
            StatsTab.RestoreHp(Actions.Num(a, 0, "amount"));
            return new { before, after = StatsTab.ReadAttr("HP") };
        });
        Actions.Query("cheats.gold-get", "cheats.gold-get: scr_gold_count as the player (null if unreadable)",
            _ => StatsTab.GoldCount());
        Actions.Command("cheats.gold", "cheats.gold <amount>: scr_gold_add, answers {before, after} from scr_gold_count", a =>
        {
            double? before = StatsTab.GoldCount();
            StatsTab.AddGold(Actions.Num(a, 0, "amount"));
            return new { before, after = StatsTab.GoldCount() };
        });

        // ------------------------------------------------------- character

        Actions.Command("cheats.xp", "cheats.xp <amount>: scr_get_XP (the level-up path), answers {xp, lvl} before and after", a =>
        {
            var before = new { xp = StatsTab.ReadAttr("XP"), lvl = StatsTab.ReadAttr("LVL") };
            CharacterTab.GrantXp(Actions.Num(a, 0, "amount"));
            return new { before, after = new { xp = StatsTab.ReadAttr("XP"), lvl = StatsTab.ReadAttr("LVL") } };
        });
        Actions.Query("cheats.conditions", "cheats.conditions: the status catalogue [{name, display, positive}] (empty while loading)",
            _ => Catalogue.Conditions.Select(c => new { name = c.Name, display = c.Display, positive = c.Positive }).ToList());
        Actions.Query("cheats.buffs", "cheats.buffs: how many statuses the player carries (-1 unreadable)",
            _ => CharacterTab.ActiveConditionCount());
        Actions.Command("cheats.condition", "cheats.condition <object> [duration=200]: applies a status, answers {before, after} status counts", a =>
        {
            string name = Actions.Str(a, 0, "object");
            var c = Catalogue.Conditions.FirstOrDefault(x => x.Name == name)
                    ?? throw new ArgumentException(Catalogue.Conditions.Count == 0
                        ? $"the status catalogue is not loaded yet ({Catalogue.Status})"
                        : $"'{name}' is not in the status catalogue (cheats.conditions lists it)");
            var (before, after) = CharacterTab.ApplyCounted(c, (int)Actions.Num(a, 1, "duration", 200));
            return new { before, after };
        });
        Actions.Query("cheats.psy-get", "cheats.psy-get [key]: psyData[key], or the whole map",
            a => a.Count > 0
                ? CharacterTab.PsyMap().Get(Actions.Str(a, 0, "key"))
                : CharacterTab.PsyMap().Entries().ToDictionary(e => e.Key, e => (object)e.Value));
        Actions.Command("cheats.psy-set", "cheats.psy-set <key> <value>: writes psyData, answers the value read back", a =>
        {
            string key = Actions.Str(a, 0, "key");
            CharacterTab.SetPsy(key, Actions.Num(a, 1, "value"));
            return CharacterTab.PsyMap().Get(key);
        });

        // ------------------------------------------------------------ body

        Actions.Query("cheats.body", "cheats.body: Body_Parts_map as {part: condition}", _ => BodyMap());
        Actions.Command("cheats.body-set", "cheats.body-set <part|all> <value>: writes a body part (0-100), answers the map", a =>
        {
            string part = Actions.Str(a, 0, "part");
            double value = Actions.Num(a, 1, "value");
            var parts = BodyTab.ReadParts();
            if (part == "all") foreach (var p in parts) BodyTab.SetCondition(p.Key, value);
            else if (parts.Any(p => p.Key == part)) BodyTab.SetCondition(part, value);
            else throw new ArgumentException($"no body part '{part}' ({string.Join(", ", parts.Select(p => p.Key))})");
            return BodyMap();
        });

        // ----------------------------------------------------------- items

        Actions.Query("cheats.items", "cheats.items [filter]: catalogue items matching the filter, at most 200 [{id, display, category, source}]", a =>
        {
            if (!Catalogue.Done) throw new InvalidOperationException($"the item catalogue is still loading ({Catalogue.Status})");
            string filter = a.Count > 0 ? Actions.Str(a, 0, "filter") : "";
            return Catalogue.Items
                .Where(i => (i.Display + " " + i.Id).Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(200)
                .Select(i => new { id = i.Id, display = i.Display, category = i.Category, source = i.Source.ToString().ToLowerInvariant() })
                .ToList();
        });
        Actions.Command("cheats.item-give", "cheats.item-give <display name|id> [rarity=Common] [count=1]: gear spawns at the player (answers the instances), objects go through the quest reward", a =>
        {
            var item = FindItem(Actions.Str(a, 0, "item"));
            int count = Math.Clamp((int)Actions.Num(a, 2, "count", 1), 1, 99);
            if (item.Source == ItemSource.Object)
            {
                var (before, after) = ItemsTab.GiveObject(item.Index, count);
                return new { item = item.Display, source = "object", count, before, after };
            }
            int rarity = a.Count > 1 ? Rarity(a[1]) : Gear.Common;
            var made = ItemsTab.GiveGear(item.Display, rarity, count, identify: true);
            return new { item = item.Display, source = "gear", rarity = Gear.RarityName(rarity), count, instances = made };
        });
        Actions.Command("cheats.object-give", "cheats.object-give <object> [count=1]: an object through the quest reward, answers {before, after} instance counts", a =>
        {
            string name = Actions.Str(a, 0, "object");
            var obj = GmlObject.Find(name) ?? throw new ArgumentException($"no object named '{name}'");
            var (before, after) = ItemsTab.GiveObject(obj.Index, Math.Clamp((int)Actions.Num(a, 1, "count", 1), 1, 99));
            return new { @object = name, before, after };
        });

        // --------------------------------------------------------- potions

        Actions.Query("cheats.potion-effects", "cheats.potion-effects: the effect table [{tag, display, positive}] (empty while loading)",
            _ => Catalogue.PotionEffects.Select(e => new { tag = e.Tag, display = e.Display, positive = e.Positive }).ToList());
        Actions.Command("cheats.potion", "cheats.potion <tag...>: arms a potion build and gives the bottle; answers {seq}; poll cheats.potion-result until seq matches and pending is false", a =>
        {
            var tags = Enumerable.Range(0, a.Count).Select(i => Actions.Str(a, i, "tag")).ToList();
            return new { armed = true, seq = potions.Arm(tags) };
        });
        Actions.Query("cheats.potion-result", "cheats.potion-result: {pending, seq, ok, message} of the last potion build",
            _ => potions.Outcome());

        // --------------------------------------------------------- enemies

        Actions.Query("cheats.enemies", "cheats.enemies: the roster, nearest first [{key, id, name, race, type, hp, maxHp, level, x, y, dist}]", a =>
            EnemiesTab.ReadRoster(out _).Select(e => new
            {
                key = e.Key, id = e.Ref.Id, name = e.Name, race = e.Race, type = e.Type,
                hp = e.Hp, maxHp = e.MaxHp, level = e.Level, x = e.X, y = e.Y, dist = e.Dist,
            }).ToList());
        Actions.Command("cheats.enemy-remove", "cheats.enemy-remove <key|id>: destroys one enemy, answers {name, gone, left}", a =>
        {
            var which = Actions.Arg(a, 0, "key or id");
            var roster = EnemiesTab.ReadRoster(out _);
            var e = which.ValueKind == JsonValueKind.Number
                ? roster.FirstOrDefault(x => PotionsTab.IdKey(x.Ref.Id) == (long)which.GetDouble())
                : roster.FirstOrDefault(x => x.Key == which.GetString());
            if (e == null) throw new ArgumentException($"no live enemy {which.GetRawText()} (cheats.enemies lists them)");
            bool gone = EnemiesTab.Destroy(e);
            EnemiesTab.ReadRoster(out int left);
            return new { name = e.Name, gone, left };
        });
    }

    private static Dictionary<string, double> BodyMap() =>
        BodyTab.ReadParts().ToDictionary(p => p.Key, p => p.Condition);

    private static Item FindItem(string what)
    {
        if (!Catalogue.Done) throw new InvalidOperationException($"the item catalogue is still loading ({Catalogue.Status})");
        return Catalogue.Items.FirstOrDefault(i => string.Equals(i.Display, what, StringComparison.OrdinalIgnoreCase))
               ?? Catalogue.Items.FirstOrDefault(i => string.Equals(i.Id, what, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"no item '{what}' in the catalogue");
    }

    // A rarity by number (1 Common .. 7 Treasure) or by name.
    private static int Rarity(JsonElement arg)
    {
        if (arg.ValueKind == JsonValueKind.Number) return (int)arg.GetDouble();
        int i = Array.FindIndex(Gear.RarityNames, n => string.Equals(n, arg.GetString(), StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? Gear.Common + i : throw new ArgumentException($"unknown rarity {arg.GetRawText()} ({string.Join(", ", Gear.RarityNames)})");
    }
}
