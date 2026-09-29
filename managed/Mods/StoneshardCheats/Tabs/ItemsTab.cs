using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// The item catalogue with give buttons: objects through the quest-reward flow,
/// gear through the game's own weapon spawner, plus the gear constructor.
/// </summary>
internal sealed class ItemsTab : Tab
{
    private string _filter = "";
    private int _count = 1;
    private int _category;            // 0 == "All categories"
    private Item? _selected;
    private int _rarity = Gear.Common;

    // The count box's range. A mistyped 1000 would otherwise run a thousand
    // give calls in one frame.
    private const int MaxCount = 99;

    // The filtered rows, as indices into Catalogue.Items, rebuilt only when the
    // filter or category changes rather than on every frame.
    private List<int> _shown = new();
    private string? _shownFilter;
    private int _shownCategory = -1;
    private List<string> _categoryNames = new();

    // Constructor state: the template as read, and the copy being edited.
    private List<Gear.Field> _template = new();
    private List<Gear.Field> _edit = new();
    private string _editFor = "";
    // The rarity the template was spawned at. Spawn configured uses it, not the
    // rarity combo as it is now: the fields being edited are that rarity's roll,
    // and spawning them onto another rarity's item would mix the two.
    private int _templateRarity = Gear.Common;
    private int _addChoice;

    public override string Name => "Items";

    public override void Initialize(CheatsMod mod) => Catalogue.Start();

    public override void Draw()
    {
        if (!Catalogue.Done)
        {
            UI.ProgressBar(Catalogue.Progress, 0f, Catalogue.Status);
            return;
        }
        if (!Catalogue.Loaded)
        {
            UI.TextColored(0.95f, 0.4f, 0.4f, $"Item list unavailable: {Catalogue.Status}");
            return;
        }

        var items = Catalogue.Items;
        var cats = Catalogue.Categories;
        if (_categoryNames.Count != cats.Count + 1) _categoryNames = ["All categories", .. cats];

        UI.TextDisabled($"{items.Count} items in {cats.Count} categories, read from the game's own data.");

        UI.SetNextItemWidth(200f);
        UI.Combo("##cat", ref _category, _categoryNames);
        UI.SameLine();
        UI.SetNextItemWidth(-1f);
        UI.InputTextWithHint("##itemfilter", "filter by name or id...", ref _filter, 96);

        if (_shownFilter != _filter || _shownCategory != _category) Refilter(items, cats);

        UI.Text($"{_shown.Count} shown");

        UI.BeginChild("##itemlist", 260f);
        UI.Clipped(_shown.Count, row =>
        {
            int k = _shown[row];
            var it = items[k];
            if (UI.Selectable($"{it.Display}###item{k}", ReferenceEquals(_selected, it))) _selected = it;
            UI.SameLine(260f);
            UI.TextDisabled($"{it.Category,-12} {it.Id}");
        });
        UI.EndChild();

        if (_selected == null) { UI.TextDisabled("Select an item above."); return; }
        var sel = _selected;

        UI.Text($"Selected: {sel.Display}  [{sel.Category}]");
        UI.SetNextItemWidth(130f);
        UI.InputInt("count", ref _count);
        _count = Math.Clamp(_count, 1, MaxCount);
        UI.Spacing();

        bool ready = Player.Available;

        if (sel.Source == ItemSource.Object)
        {
            // Objects go through the game's own quest-reward flow, which does its
            // own placement. One argument: the object index.
            UI.BeginDisabled(!ready);
            if (UI.Button("Give item", 220f))
            {
                int n = _count;
                Actions.Run($"scr_dialogue_reward_add_item {sel.Index}  x{n}  ({sel.Id})", () => GiveObject(sel.Index, n));
            }
            UI.EndDisabled();
            UI.SameLine();
            UI.TextDisabled("delivered as a quest reward");
            return;
        }

        // Rarity is argument index 4 of scr_weapon_loot. Anything above Common
        // makes the GAME roll the bonus stats itself - which is a truer enchanted
        // item than typing stat keys in by hand, and it fills Curse/Suffix/Colour
        // correctly.
        int rarityIndex = _rarity - Gear.Common;
        UI.SetNextItemWidth(180f);
        if (UI.Combo("rarity", ref rarityIndex, Gear.RarityNames)) _rarity = rarityIndex + Gear.Common;
        UI.SameLine();
        UI.TextDisabled("the game rolls the bonus stats above Common");

        UI.BeginDisabled(!ready);
        if (UI.Button("Give weapon", 220f))
        {
            int n = _count, rarity = _rarity;
            // scr_weapon_loot(name, x, y, chance, rarity): chance 100 is certain.
            Actions.Run($"scr_weapon_loot \"{sel.Display}\" at the player +48, 100, {Gear.RarityName(rarity)}  x{n}",
                () => GiveGear(sel.Display, rarity, n));
        }
        UI.SameLine();
        if (UI.Button("To inventory", 140f))
        {
            int n = _count, rarity = _rarity;
            Actions.Run($"scr_inventory_add_weapon \"{sel.Display}\" {Gear.RarityName(rarity)}  x{n}", () =>
            {
                for (int i = 0; i < n; i++) AddToInventory(sel.Display, rarity);
            });
        }
        UI.EndDisabled();
        if (!ready)
        {
            UI.SameLine();
            UI.TextColored(0.95f, 0.8f, 0.35f, "waiting for the player...");
        }

        DrawConstructor(sel, ready);
    }

    /// <summary>
    /// Gives an object <paramref name="count"/> times through the quest-reward
    /// flow. Returns how many instances of it existed before and after.
    /// </summary>
    public static (int Before, int After) GiveObject(int index, int count)
    {
        int before = (int)Game.CallBuiltin("instance_number", index).AsReal;
        for (int i = 0; i < count; i++) Player.Call("scr_dialogue_reward_add_item", index);
        return (before, (int)Game.CallBuiltin("instance_number", index).AsReal);
    }

    /// <summary>
    /// Puts a weapon or armor straight into the player's inventory. The script
    /// runs as the INVENTORY, not the player: it takes self as the owning
    /// container and walks self.itemsContainer, which the player does not have
    /// (run as the player it throws "invalid with reference"). The game's own
    /// caller does the same from inside `with (o_inventory)`. Returns the new
    /// item's id, or noone (-4) when the name is unknown, a unique was already
    /// found, or the inventory is full (the item then drops to the floor).
    /// </summary>
    public static RValue AddToInventory(string displayName, int rarity)
    {
        if (!Game.CanResolveInstances)
            throw new InvalidOperationException("this runtime cannot run scripts as an instance by id");
        var obj = GmlObject.Find("o_inventory") ?? throw new InvalidOperationException("the game has no o_inventory object");
        // instance_find includes children of o_inventory (other containers);
        // `with (o_inventory)` in the game's caller means the object itself.
        InstanceRef? found = null;
        foreach (var candidate in obj.Instances())
        {
            if (Game.CallBuiltin("object_get_name", candidate.Get("object_index")).ToString() != "o_inventory") continue;
            found = candidate;
            break;
        }
        var inventory = (found ?? throw new InvalidOperationException("no o_inventory instance: load a save first")).Resolve()
                        ?? throw new InvalidOperationException("the o_inventory instance is gone");
        // Self is the inventory, other the player: what `with (o_inventory)`
        // inside the player's code gives the script.
        return Game.CallScriptAs(inventory, Player.Require(), "scr_inventory_add_weapon", displayName, rarity);
    }

    /// <summary>
    /// Spawns gear on the ground beside the player through scr_weapon_loot.
    /// With <paramref name="identify"/>, each spawn goes through Gear.Spawn,
    /// which also finds the instance it made (and fails if it cannot); the
    /// button does without, since a player only needs the item on the floor.
    /// </summary>
    public static List<InstanceRef> GiveGear(string displayName, int rarity, int count, bool identify = false)
    {
        var made = new List<InstanceRef>(count);
        var (px, py) = Player.Position;
        for (int i = 0; i < count; i++)
        {
            if (identify) made.Add(Gear.Spawn(displayName, rarity));
            else Player.Call("scr_weapon_loot", displayName, px + 48, py, 100, rarity);
        }
        Actions.Report("spawned on the ground at your feet - walk over it");
        return made;
    }

    private void Refilter(IReadOnlyList<Item> items, IReadOnlyList<string> cats)
    {
        _shownFilter = _filter;
        _shownCategory = _category;
        string? cat = _category > 0 && _category <= cats.Count ? cats[_category - 1] : null;
        string needle = _filter.Trim();

        _shown = new List<int>(items.Count);
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            if (cat != null && it.Category != cat) continue;
            if (needle.Length > 0 &&
                !(it.Display + " " + it.Id).Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            _shown.Add(k);
        }
    }

    // An item is one ds_map keyed by the CSV column names, carrying only the
    // stats it actually has - so ADDING a key is what the game does to make a
    // Unique, and it is what makes an enchantment here. The template comes from
    // a real spawned item rather than from re-reading the CSV, so the field set
    // and the base values are the game's own and cannot drift out of step.
    private void DrawConstructor(Item sel, bool ready)
    {
        UI.Spacing();
        if (!UI.CollapsingHeader("Constructor - pick the stats before spawning")) return;

        UI.BeginDisabled(!ready);
        if (UI.Button("Load template", 160f))
        {
            int rarity = _rarity;
            bool ok = Actions.Run($"template of \"{sel.Display}\" ({Gear.RarityName(rarity)}): spawn, read data, destroy", () =>
            {
                _template = Gear.LoadTemplate(sel.Display, rarity);
                Actions.Report($"template: {sel.Display}, {_template.Count} editable field(s)");
            });
            if (ok)
            {
                _edit = Gear.Clone(_template);
                _editFor = sel.Display;
                _templateRarity = rarity;
            }
            else
            {
                _template = new();
                _edit = new();
                _editFor = "";
            }
        }
        UI.EndDisabled();
        UI.SameLine();
        UI.TextDisabled("spawns one, reads it, removes it again");

        if (_editFor != sel.Display || _edit.Count == 0)
        {
            UI.TextDisabled($"Load a template for \"{sel.Display}\" to edit its stats.");
            return;
        }

        // Weapons and armor have different stat vocabularies; the item says
        // which it is rather than us inferring it from the category text.
        bool isArmor = _edit.Any(f => f.Key == "Metatype" && f.IsString && f.Str == "Armor");
        var vocab = isArmor ? Catalogue.ArmorStats : Catalogue.WeaponStats;

        UI.Text($"{_editFor} ({Gear.RarityName(_templateRarity)}, as loaded) - {_edit.Count} field(s)");

        UI.BeginChild("##fields", 240f);
        for (int i = 0; i < _edit.Count; i++)
        {
            var f = _edit[i];
            UI.PushId($"f{i}");
            UI.SetNextItemWidth(180f);
            if (f.IsString) UI.InputText(f.Key, ref f.Str, 96);
            else UI.InputDouble(f.Key, ref f.Num, 1.0, 10.0, "%.2f");
            UI.PopId();
        }
        UI.EndChild();

        // Only stats the item does NOT already carry: adding one of these is the
        // enchantment.
        var addable = vocab.Where(stat => !_edit.Any(f => f.Key == stat)).ToList();
        if (addable.Count > 0)
        {
            if (_addChoice >= addable.Count) _addChoice = 0;
            UI.SetNextItemWidth(220f);
            UI.Combo("##addstat", ref _addChoice, addable);
            UI.SameLine();
            if (UI.Button("Add stat")) _edit.Add(new Gear.Field { Key = addable[_addChoice] });
            UI.SameLine();
            UI.TextDisabled($"{addable.Count} more available");
        }

        UI.Spacing();
        UI.BeginDisabled(!ready);
        if (UI.Button("Spawn configured", 220f))
        {
            var fields = Gear.Clone(_edit);
            string name = _editFor;
            int rarity = _templateRarity;
            Actions.Run($"build \"{name}\" ({Gear.RarityName(rarity)}) with {fields.Count} field(s)", () =>
            {
                var failed = Gear.SpawnConfigured(name, fields, rarity);
                // The item exists either way; a partial write is reported as
                // such rather than as a failure that would suggest nothing spawned.
                if (failed.Count == 0) Actions.Report("built - it is on the ground at your feet");
                else Actions.Report($"spawned with errors - it is on the ground, but these fields were not written: {string.Join(", ", failed)}", ok: false);
            });
        }
        UI.EndDisabled();
        UI.SameLine();
        if (UI.Button("Reset to base")) _edit = Gear.Clone(_template);
        UI.SameLine();
        UI.TextDisabled($"spawns at {Gear.RarityName(_templateRarity)}, the template's rarity");
    }
}

/// <summary>
/// Gear construction: the runtime half of the gear CSV rows the catalogue reads.
/// </summary>
/// <remarks>
/// Weapons and armor are not objects in the asset sense - they are rows in two
/// CSV tables embedded in the exe, and the game builds an instance from a row on
/// demand. A dropped weapon IS an instance (o_weapon_loot on the ground), and its
/// rolled stats live in one ds_map called `data`, keyed by the CSV column names,
/// holding ONLY the stats that item actually has. So a "good enchantment" is not a
/// separate mechanism at all - it is an extra key. Adding "Lifesteal": 10 to a
/// sword that had none is exactly what the game does for a Unique. Values are
/// written with the game's own ds_map_replace, so anything the game can represent
/// can be set - including values no roll table would ever produce.
/// </remarks>
internal static class Gear
{
    // Rarity, as the game numbers it. Read off a live item's own instance
    // variables (Common=1 ... Treasure=7) and confirmed against ModShardLauncher,
    // whose loot module calls
    //     scr_weapon_loot(name, x, y, 100, rarity)
    // so the FIFTH argument is rarity. Sending 1 there is why every constructed
    // item once came out Common with an empty Curse array.
    public const int Common = 1;

    public static readonly string[] RarityNames = ["Common", "Uncommon", "Rare", "Epic", "Cursed", "Unique", "Treasure"];

    public static string RarityName(int rarity) =>
        rarity >= Common && rarity < Common + RarityNames.Length ? RarityNames[rarity - Common] : "?";

    private const string LootObject = "o_weapon_loot";

    public sealed class Field
    {
        public string Key = "";
        public bool IsString;
        public double Num;
        public string Str = "";
        // The kind the game stored a number as. Written back as the same kind:
        // a flag the game keeps as a bool must not come back as a real.
        public RValueKind Kind = RValueKind.Real;

        public RValue ToValue() => IsString ? Str : Kind switch
        {
            RValueKind.Bool => new RValue { Real = Num != 0 ? 1 : 0, Kind = RValueKind.Bool },
            RValueKind.Int32 => new RValue { Int32 = (int)Num, Kind = RValueKind.Int32 },
            RValueKind.Int64 => new RValue { Int64 = (long)Num, Kind = RValueKind.Int64 },
            _ => Num,
        };
    }

    public static List<Field> Clone(List<Field> fields) =>
        fields.Select(f => new Field { Key = f.Key, IsString = f.IsString, Num = f.Num, Str = f.Str, Kind = f.Kind }).ToList();

    /// <summary>
    /// Spawns gear by its CSV display name ("Militia Falchion") through the
    /// game's own scr_weapon_loot, 48 px beside the player, and returns the
    /// instance it made.
    /// </summary>
    /// <remarks>
    /// scr_weapon_loot RETURNS the instance it made - ModShardLauncher relies on
    /// that, wrapping the call in `with (...)` to set Duration on the result - so
    /// the spawned item is taken from the return value. Diffing the o_weapon_loot
    /// instances stays as the fallback for a return value that is not a usable
    /// reference.
    /// </remarks>
    public static InstanceRef Spawn(string displayName, int rarity)
    {
        var before = LootRefs();
        var (px, py) = Player.Position;
        var r = Player.Call("scr_weapon_loot", displayName, px + 48, py, 100, rarity);
        if (r.Kind == RValueKind.Reference) return new InstanceRef(r);
        return NewLoot(before);
    }

    // Identity of an instance, for telling "the one we just made" from the pile
    // of earlier drops on the same tile. NOT the `id` variable read as a number:
    // this runtime answers instance queries with a REFERENCE (kind 15), and its
    // bits are the identity, compared but never decoded.
    private static (RValueKind, long) Identity(InstanceRef r) => (r.Id.Kind, r.Id.Int64);

    private static HashSet<(RValueKind, long)> LootRefs()
    {
        var set = new HashSet<(RValueKind, long)>();
        if (GmlObject.Find(LootObject) is { } obj)
            foreach (var r in obj.Instances()) set.Add(Identity(r));
        return set;
    }

    // Picks the instance whose reference was not present before the spawn. The
    // last one wins: creation order puts ours last. More than one new instance
    // is said out loud rather than papered over, because silently reading the
    // WRONG item is how a probe once reported a Royal Blade that was really a
    // Militia Falchion.
    private static InstanceRef NewLoot(HashSet<(RValueKind, long)> before)
    {
        var obj = GmlObject.Find(LootObject) ?? throw new InvalidOperationException($"{LootObject} does not exist");
        InstanceRef? found = null;
        int fresh = 0, total = 0;
        foreach (var r in obj.Instances())
        {
            total++;
            if (before.Contains(Identity(r))) continue;
            fresh++;
            found = r;
        }
        if (found is not { } f) throw new InvalidOperationException($"no new {LootObject} instance appeared ({total} exist)");
        if (fresh > 1) Actions.Report($"{fresh} new {LootObject} instances appeared, using the last", ok: false);
        return f;
    }

    // The item's `data` map, which is where everything lives.
    private static DsMap DataMap(InstanceRef item, string displayName)
    {
        var d = item.Get("data");
        if (!d.IsNumber) throw new InvalidOperationException($"the spawned {displayName} has no data map");
        return new DsMap(d);
    }

    /// <summary>
    /// Spawns one throwaway of <paramref name="displayName"/>, reads its `data`
    /// map, then destroys it. The result is a template with the game's own field
    /// set and real base values, which beats reconstructing it from the CSV: it
    /// cannot drift, and it works for weapons and armor without either being
    /// special-cased.
    /// </summary>
    public static List<Field> LoadTemplate(string displayName, int rarity)
    {
        var item = Spawn(displayName, rarity);
        try
        {
            var fields = ReadFields(DataMap(item, displayName));
            if (fields.Count == 0) throw new InvalidOperationException($"could not read the data map of {displayName}");
            return fields;
        }
        finally
        {
            // The template item is scaffolding, not a gift: take it back off the floor.
            Player.Builtin("instance_destroy", item.Id);
        }
    }

    /// <summary>
    /// Spawns the item, then writes every field over its `data` map. Fields
    /// absent from the template are added; that is how a stat becomes an
    /// enchantment. Returns the keys that could not be written (the item stays).
    /// </summary>
    public static List<string> SpawnConfigured(string displayName, List<Field> fields, int rarity)
    {
        var map = DataMap(Spawn(displayName, rarity), displayName);

        var failed = new List<string>();
        foreach (var f in fields)
        {
            try
            {
                // replace, not add: it sets an existing key and creates a missing
                // one, which is exactly the "a new stat IS an enchantment" case.
                map.Set(f.Key, f.ToValue());
            }
            catch (GmlException ex)
            {
                failed.Add(f.Key);
                Actions.Log.Warning($"  {f.Key}: {ex.Message}");
            }
        }
        Actions.Log.Info($"  configured {displayName} - {fields.Count - failed.Count} of {fields.Count} field(s) written");
        return failed;
    }

    // Walks the map with the game's own iterator rather than parsing json_encode
    // output: the keys and values arrive as values, so a string stays a string
    // and a number stays a number with nothing to re-parse.
    private static List<Field> ReadFields(DsMap map)
    {
        var nested = NestedKeys(map);
        var fields = new List<Field>();
        foreach (var (key, val) in map.Entries())
        {
            // Nested lists and maps never become editable fields, so they are
            // also never written back - which is what keeps them intact.
            if (nested.Contains(key)) continue;

            switch (val.Kind)
            {
                // AsReal reads a bool from whichever half the runtime put it in;
                // reading it as raw 64 bits once turned "identified": 1 into
                // 4607182418800017408.
                case RValueKind.Real or RValueKind.Bool or RValueKind.Int32 or RValueKind.Int64:
                    fields.Add(new Field { Key = key, Num = val.AsReal, Kind = val.Kind });
                    break;
                case RValueKind.String:
                    fields.Add(new Field { Key = key, IsString = true, Str = val.ToString() });
                    break;
            }
        }
        return fields;
    }

    // Which keys hold a NESTED ds_list/ds_map rather than a scalar.
    //
    // This matters more than it looks. `Curse` and `Main` are nested lists;
    // ds_map_find_value hands their raw ds id back as a plain number (9977, 9978).
    // Storing that and writing it back with ds_map_replace destroys the nesting,
    // leaving "Main": 9977.0 where [ "Slashing_Damage", 19.0 ] belonged - which
    // is what a constructed staff once came back with.
    //
    // The obvious test, ds_map_is_list, DOES NOT WORK here: on a pristine
    // game-spawned item it answers false for a key that json_encode renders as an
    // array and that ds_list_size reports as 2 entries. ds_exists(value,
    // ds_type_list) would work but collides: a legitimate "Duration": 94 could
    // match a live ds id and get silently dropped.
    //
    // So json_encode decides, since it demonstrably gets this right. Only the
    // shape is inspected - "key": [ or "key": { - which needs no JSON parser, but
    // the walk does track strings and depth: a string value may hold escaped
    // quotes or brackets, and a nested map's own keys are not the item's keys.
    // json_encode on a stale or non-map id returns a null string rather than
    // faulting, which makes this safe to try.
    private static HashSet<string> NestedKeys(DsMap map)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string json = map.ToJson();
        if (json.Length == 0 || json == "<null string>") return keys;

        int depth = 0;
        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (ch is '{' or '[') { depth++; continue; }
            if (ch is '}' or ']') { depth--; continue; }
            if (ch != '"') continue;

            // To the closing quote, stepping over every escaped character.
            int close = i + 1;
            while (close < json.Length && json[close] != '"') close += json[close] == '\\' ? 2 : 1;
            if (close >= json.Length) break;
            string raw = json.Substring(i + 1, close - i - 1);
            i = close;

            // Only the top-level map's keys: depth 1 is inside its braces.
            if (depth != 1) continue;

            // A key is followed by ':'; it counts when a bracket comes next.
            int j = close + 1;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j >= json.Length || json[j] != ':') continue;
            j++;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j < json.Length && (json[j] == '[' || json[j] == '{')) keys.Add(Unescape(raw));
        }
        return keys;
    }

    // Keys are compared against the map's own, unescaped, keys.
    private static string Unescape(string raw)
    {
        if (!raw.Contains('\\')) return raw;
        try { return System.Text.Json.JsonSerializer.Deserialize<string>("\"" + raw + "\"") ?? raw; }
        catch (System.Text.Json.JsonException) { return raw; }
    }
}
