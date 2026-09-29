using System.Diagnostics;
using System.Text;
using CoreLoader;

namespace StoneshardCheats;

internal enum ItemSource { Object, Gear }

/// <summary>One catalogue entry.</summary>
/// <param name="Id">What gets passed to the game: the object name, or the gear row's id.</param>
/// <param name="Display">Human-readable label; for gear, the name the game's spawners take.</param>
/// <param name="Category">e.g. "Sword", "Head", "Food".</param>
/// <param name="Index">GameMaker asset index (objects only; -1 for gear).</param>
/// <param name="Source">An o_inv_* object, or a gear CSV row.</param>
internal sealed record Item(string Id, string Display, string Category, int Index, ItemSource Source);

/// <summary>
/// One thing a potion can do.
/// </summary>
/// <remarks>
/// Potions are the one item family with no object of its own. There is no
/// o_inv_potion_healing to spawn: every potion in the game is an o_inv_bottle
/// whose `data` map carries a rolled set of these effect tags, and the name you
/// see ("Potion of Healing") is assembled from the tags plus a quality tier.
/// That is why the item catalogue cannot list potions by name and never will.
/// </remarks>
internal sealed record PotionEffect(string Tag, string Display, bool Positive);

/// <summary>
/// A status the game can put on a unit: the object table filtered to the two
/// families the game uses for statuses, o_db_* debuffs (stun, poison, bleeding,
/// coma, pain, drunk, ...) and o_b_* buffs (bless, adrenaline, carnage, stances, ...).
/// </summary>
internal sealed record Condition(string Name, string Display, int Index, bool Positive);

/// <summary>
/// The item catalogue, assembled from the game's own data once per session.
/// </summary>
/// <remarks>
/// Two sources, because Stoneshard stores items two different ways:
///
///   1. The object table: the o_inv_* objects. Categories come from the
///      GameMaker parent hierarchy (o_inv_acorn's parent is o_inv_food_parent),
///      which is the game's own grouping. Read live through object_exists,
///      object_get_name and object_get_parent - thousands of builtin calls, so
///      they are spread over frames within a small budget instead of stalling one.
///
///   2. The exe's .rdata: rows of the embedded weapons/armor CSVs. Gear is
///      data-driven, so there are no weapon or armor objects at all; the rows
///      look like
///          Militia Falchion;2;sword21;sword;cleaver;;Common;metal;450;...
///      i.e. field 0 name, field 2 id, field 3 category. Enemy rows share the
///      format but leave field 6 empty, which is what separates them - no name
///      lists involved. The same section carries the gear tables' header rows
///      and the localisation rows the potion effects come from. The exe is read
///      from disk on a worker task, never on the game thread.
///
/// Nothing is baked in: both sources are re-read every launch, so a game patch
/// is picked up automatically.
/// </remarks>
internal static class Catalogue
{
    // Per-frame share of the game thread for the object walk. A few ms keeps the
    // frame rate intact while still finishing in a second or two.
    private const double BudgetMs = 4;

    // Mirrors GmlObject.All: asset indices are walked until this many in a row
    // do not exist.
    private const int MissLimit = 64;
    private const int IndexLimit = 100_000;

    private enum Phase { Idle, Scan, Parents, WaitDisk, Done }

    private static Phase _phase = Phase.Idle;
    private static Task<DiskData>? _disk;
    private static readonly Stopwatch Clock = new();

    // Object walk state: names by asset index (null for gaps), then the o_inv_*
    // indices whose parent is still to be asked for.
    private static readonly List<string?> Names = new();
    private static int _next, _misses;
    private static readonly List<int> Inv = new();
    private static int _cursor;
    private static readonly List<Item> ObjectItems = new();

    private static List<Item> _items = new();
    private static List<string> _categories = new();
    private static List<string> _weaponStats = new();
    private static List<string> _armorStats = new();
    private static List<PotionEffect> _potionEffects = new();
    private static List<Condition> _conditions = new();

    public static bool Loaded { get; private set; }

    /// <summary>True once the build finished, whether or not it found anything.</summary>
    public static bool Done => _phase == Phase.Done;

    public static string Status { get; private set; } = "not loaded";

    /// <summary>0..1 while the object parents are being read; the scan before it has no known total.</summary>
    public static float Progress => _phase switch
    {
        Phase.Parents => Inv.Count == 0 ? 0f : (float)_cursor / Inv.Count,
        Phase.WaitDisk or Phase.Done => 1f,
        _ => 0f,
    };

    public static IReadOnlyList<Item> Items => _items;

    /// <summary>Sorted, no duplicates.</summary>
    public static IReadOnlyList<string> Categories => _categories;

    /// <summary>
    /// The gear tables' column names, taken from the two header rows embedded
    /// alongside the data ("name;Tier;id;Slot;Subtype;..." for weapons, 83
    /// columns; "name;Tier;id;Slot;class;..." for armor, 77).
    /// </summary>
    /// <remarks>
    /// These are not just documentation: an item's live `data` ds_map is keyed by
    /// these exact names, so the header doubles as the set of stats a constructed
    /// item may legally carry.
    /// </remarks>
    public static IReadOnlyList<string> WeaponStats => _weaponStats;

    public static IReadOnlyList<string> ArmorStats => _armorStats;

    /// <summary>Beneficial first, then alphabetical: the order you pick one in.</summary>
    public static IReadOnlyList<PotionEffect> PotionEffects => _potionEffects;

    /// <summary>Sorted by display name.</summary>
    public static IReadOnlyList<Condition> Conditions => _conditions;

    /// <summary>An object item's asset index by object name, or -1.</summary>
    public static int ObjectIndex(string objectName)
    {
        foreach (var it in _items)
            if (it.Source == ItemSource.Object && it.Id == objectName) return it.Index;
        return -1;
    }

    /// <summary>Starts the build (game thread). Later calls do nothing.</summary>
    public static void Start()
    {
        if (_phase != Phase.Idle) return;
        _phase = Phase.Scan;
        Status = "reading the object table...";
        Clock.Restart();
        _disk = Task.Run(ReadDisk);
        Game.RunOnGameThread(Step);
    }

    // One frame's share of the work; queues itself again until the build is done.
    private static void Step()
    {
        try
        {
            var frame = Stopwatch.StartNew();
            while (frame.Elapsed.TotalMilliseconds < BudgetMs)
            {
                if (_phase == Phase.Scan)
                {
                    if (_misses >= MissLimit || _next >= IndexLimit) { EndScan(); continue; }
                    int i = _next++;
                    Names.Add(null);
                    if (!Game.CallBuiltin("object_exists", i).AsBool) { _misses++; continue; }
                    _misses = 0;
                    Names[i] = Game.CallBuiltin("object_get_name", i).ToString();
                }
                else if (_phase == Phase.Parents)
                {
                    if (_cursor >= Inv.Count) { _phase = Phase.WaitDisk; Status = "reading the game exe..."; break; }
                    int i = Inv[_cursor++];
                    string name = Names[i]!;
                    int p = AssetIndex(Game.CallBuiltin("object_get_parent", i));
                    string category = p >= 0 && p < Names.Count && !string.IsNullOrEmpty(Names[p])
                        ? CategoryFromParent(Names[p]!) : "Misc";
                    ObjectItems.Add(new Item(name, PrettyName(name), category, i, ItemSource.Object));
                }
                else break;
            }

            if (_phase == Phase.Scan) Status = $"reading the object table ({_next} scanned)...";
            else if (_phase == Phase.Parents) Status = $"reading item categories ({_cursor} / {Inv.Count})...";

            if (_phase == Phase.WaitDisk && _disk!.IsCompleted)
            {
                Finish(_disk.Result);
                return;
            }
        }
        catch (Exception ex)
        {
            // A background build must never fault the mod: it just leaves the
            // catalogue empty and says why.
            _phase = Phase.Done;
            Status = $"failed: {ex.Message}";
            Actions.Log.Warning($"catalogue: {Status}");
            return;
        }
        Game.RunOnGameThread(Step);
    }

    // Older runtimes answer with the plain index; newer ones a typed asset
    // reference whose low 32 bits are the index. No parent is negative either way.
    private static int AssetIndex(RValue v) =>
        v.IsNumber ? (int)v.AsReal : v.Kind == RValueKind.Reference ? (int)(v.Int64 & 0xFFFFFFFF) : -1;

    private static void EndScan()
    {
        // Statuses, from the same table. Kept separate from items: they are not
        // spawnable, they are applied by asset index.
        var conditions = new List<Condition>();
        for (int i = 0; i < Names.Count; i++)
        {
            string? n = Names[i];
            if (n == null) continue;
            bool debuff = n.StartsWith("o_db_", StringComparison.Ordinal);
            bool buff = n.StartsWith("o_b_", StringComparison.Ordinal);
            if (!debuff && !buff) continue;
            if (IsParent(n)) continue;
            conditions.Add(new Condition(n, Capitalise(n[(debuff ? 5 : 4)..].Replace('_', ' ')), i, buff));
        }
        conditions.Sort((a, b) => string.CompareOrdinal(a.Display, b.Display));
        _conditions = conditions;

        for (int i = 0; i < Names.Count; i++)
        {
            string? n = Names[i];
            // Parents themselves are abstract templates, not spawnable items.
            if (n != null && n.StartsWith("o_inv_", StringComparison.Ordinal) && !IsParent(n)) Inv.Add(i);
        }
        _phase = Phase.Parents;
    }

    private static bool IsParent(string name) => name.Length > 7 && name.EndsWith("_parent", StringComparison.Ordinal);

    private static void Finish(DiskData disk)
    {
        var items = new List<Item>(ObjectItems.Count + disk.Gear.Count);
        items.AddRange(ObjectItems);
        items.AddRange(disk.Gear);
        items.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Category, b.Category);
            return c != 0 ? c : string.CompareOrdinal(a.Display, b.Display);
        });

        _items = items;
        _categories = items.Select(i => i.Category).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        _weaponStats = disk.WeaponStats;
        _armorStats = disk.ArmorStats;
        _potionEffects = disk.PotionEffects;
        Loaded = items.Count > 0;
        _phase = Phase.Done;

        Status = $"{items.Count} items ({ObjectItems.Count} objects + {disk.Gear.Count} gear rows) in " +
                 $"{_categories.Count} categories, {_potionEffects.Count} potion effects";
        if (disk.Error != null) Status += $"  (exe: {disk.Error})";
        Actions.Log.Info($"catalogue: {Status}, {Clock.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------ naming

    private static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // "o_inv_food_parent" -> "Food",  "o_inv_consum_passive" -> "Consum passive"
    private static string CategoryFromParent(string name)
    {
        if (name.StartsWith("o_inv_", StringComparison.Ordinal)) name = name[6..];
        const string suffix = "_parent";
        if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            name = name[..^suffix.Length];
        name = name.Replace('_', ' ');
        return name.Length == 0 ? "Misc" : Capitalise(name);
    }

    private static string PrettyName(string s)
    {
        if (s.StartsWith("o_inv_", StringComparison.Ordinal)) s = s[6..];
        return Capitalise(s.Replace('_', ' '));
    }

    // ------------------------------------------------------------ exe side
    //
    // Everything below runs on a worker task and touches no GML.

    private sealed class DiskData
    {
        public List<Item> Gear = new();
        public List<string> WeaponStats = new();
        public List<string> ArmorStats = new();
        public List<PotionEffect> PotionEffects = new();
        public string? Error;
    }

    private static DiskData ReadDisk()
    {
        var d = new DiskData();
        try
        {
            string path = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            byte[] rdata = ReadRdata(path);
            LoadCsvRows(rdata, d);
            LoadPotionEffects(rdata, d);
        }
        catch (Exception ex)
        {
            d.Error = ex.Message;
        }
        return d;
    }

    // The .rdata section's bytes as they are on disk: the same range the native
    // walker covered in memory. Past SizeOfRawData the loader only zero-fills, so
    // nothing the walkers look for can live there.
    private static byte[] ReadRdata(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs);

        fs.Position = 0x3C;
        int peOffset = br.ReadInt32();
        fs.Position = peOffset;
        if (br.ReadUInt32() != 0x00004550) throw new InvalidDataException("not a PE image");
        fs.Position = peOffset + 6;
        int sections = br.ReadUInt16();
        fs.Position = peOffset + 20;
        int optionalSize = br.ReadUInt16();
        long table = peOffset + 24 + optionalSize;

        for (int i = 0; i < sections; i++)
        {
            fs.Position = table + i * 40L;
            string name = Encoding.ASCII.GetString(br.ReadBytes(8)).TrimEnd('\0');
            uint virtualSize = br.ReadUInt32();
            br.ReadUInt32();                       // VirtualAddress
            uint rawSize = br.ReadUInt32();
            uint rawPointer = br.ReadUInt32();
            if (name != ".rdata") continue;

            long size = Math.Min(virtualSize, rawSize);
            if (size <= 0 || rawPointer + size > fs.Length) throw new InvalidDataException(".rdata is out of range");
            fs.Position = rawPointer;
            var bytes = new byte[size];
            fs.ReadExactly(bytes);
            return bytes;
        }
        throw new InvalidDataException("no .rdata section");
    }

    private static bool SplitRow(byte[] b, int start, int len, List<string> into, Encoding enc)
    {
        into.Clear();
        int from = start;
        for (int i = start; i < start + len; i++)
        {
            if (b[i] != (byte)';') continue;
            into.Add(enc.GetString(b, from, i - from));
            from = i + 1;
        }
        into.Add(enc.GetString(b, from, start + len - from));
        return into.Count >= 8;
    }

    private static bool IsAlnum(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsIdent(string s) => s.All(c => IsAlnum(c) || c == '_');

    private static void LoadCsvRows(byte[] b, DiskData d)
    {
        var f = new List<string>();
        int cur = 0, end = b.Length;

        // Walk NUL-terminated printable runs.
        while (cur < end)
        {
            if (b[cur] == 0) { cur++; continue; }

            int start = cur, semis = 0;
            while (cur < end && b[cur] != 0)
            {
                byte c = b[cur];
                if (c < 0x20 || c > 0x7e) break;
                if (c == (byte)';') semis++;
                cur++;
                if (cur - start > 900) break;
            }
            int len = cur - start;
            while (cur < end && b[cur] != 0) cur++;   // skip any tail

            if (len < 30 || semis < 20) continue;
            if (b[start] == (byte)'/') continue;      // "// CLEAVERS;;;;" comment rows
            if (!SplitRow(b, start, len, f, Encoding.Latin1)) continue;

            // The header rows. Both gear tables start "name;Tier;id;Slot;", and
            // the column after Slot tells them apart: armor carries "class" there.
            // Blank columns are separators in the sheet and are dropped - only
            // the named ones are real stats.
            if (f[0] == "name" && f[1] == "Tier" && f[2] == "id" && f[3] == "Slot")
            {
                var into = f.Count > 4 && f[4] == "class" ? d.ArmorStats : d.WeaponStats;
                if (into.Count == 0)
                    foreach (var col in f)
                        if (col.Length > 0) into.Add(col);
                continue;
            }

            string name = f[0], id = f[2], cat = f[3];

            // Enemy rows use the same shape but leave the rarity/material column
            // empty; that is the whole discriminator.
            if (f[6].Length == 0) continue;
            if (name.Length == 0 || name[0] is < 'A' or > 'Z') continue;
            if (id.Length == 0 || cat.Length == 0) continue;
            if (!IsIdent(id) || !IsIdent(cat)) continue;
            // Drops the numeric balance-table rows.
            if (!cat.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) continue;

            d.Gear.Add(new Item(id, name, Capitalise(cat), -1, ItemSource.Gear));
        }
    }

    // What a potion IS lives in the localisation table embedded in .rdata beside
    // the gear CSVs, in rows shaped like
    //
    //     good_pt_healing;исцеления / Исцеляющее;of Healing / Healing;治伤;...
    //
    // field 0 the effect tag, field 2 English - per the table's own header row
    // (";Русский;English;中文;..."). Every tag appears three times, once per
    // section: the name fragment ("of Healing / Healing"), the effect name
    // ("Healing") and the description ("Restores Health."). Rather than track
    // which section the walk is in - which would pin this to the current link
    // order - the shortest non-sentence English field wins, and that is always
    // the plain effect name.
    //
    // Rows here are UTF-8, unlike the gear CSVs, so the walker accepts every byte
    // above the control range instead of only printable ASCII. The fields
    // actually read (tag and English) are ASCII regardless.
    private static void LoadPotionEffects(byte[] b, DiskData d)
    {
        var best = new Dictionary<string, string>(StringComparer.Ordinal);
        var f = new List<string>();
        int cur = 0, end = b.Length;

        while (cur < end)
        {
            if (b[cur] == 0) { cur++; continue; }

            int start = cur;
            while (cur < end && b[cur] != 0 && b[cur] >= 0x20)
            {
                cur++;
                if (cur - start > 2000) break;
            }
            int len = cur - start;
            while (cur < end && b[cur] != 0) cur++;   // skip any tail

            if (len < 16) continue;
            if (!StartsWith(b, start, len, "good_pt_") && !StartsWith(b, start, len, "bad_pt_")) continue;
            if (!SplitRow(b, start, len, f, Encoding.UTF8) || f.Count < 3) continue;

            string tag = f[0], eng = f[2];
            if (eng.Length == 0 || eng == "//") continue;
            if (!IsIdent(tag)) continue;

            if (!best.TryGetValue(tag, out var held)) { best[tag] = eng; continue; }

            bool wasSentence = held[^1] == '.';
            bool isSentence = eng[^1] == '.';
            if ((wasSentence && !isSentence) || (wasSentence == isSentence && eng.Length < held.Length))
                best[tag] = eng;
        }

        foreach (var (tag, display) in best)
            d.PotionEffects.Add(new PotionEffect(tag, display, tag.StartsWith("good_", StringComparison.Ordinal)));

        d.PotionEffects.Sort((a, b2) =>
        {
            if (a.Positive != b2.Positive) return a.Positive ? -1 : 1;
            return string.CompareOrdinal(a.Display, b2.Display);
        });
    }

    private static bool StartsWith(byte[] b, int start, int len, string prefix)
    {
        if (len < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (b[start + i] != (byte)prefix[i]) return false;
        return true;
    }
}
