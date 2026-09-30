using CoreLoader;
using StoneShard;

namespace TavernGames;

/// <summary>
/// Who will play: NPCs (o_NPC and its children) standing next to the player,
/// friendly, in a tavern - within a few tiles of an innkeeper, hostess or inn
/// worker - or tavern folk by trade wherever they stand (innkeepers, drunkards,
/// the inn's sellswords). Kinds are read from the object name's words
/// (o_npc_drunkard_osbrook, o_npc_merc01_ob), never from substrings, so a
/// merchant is not a "merc" nor a pirate a "rat".
/// </summary>
internal static class Tavern
{
    public const double Tile = 26;

    // Tavern folk by trade: they will play wherever they stand.
    private static readonly string[] TavernFolk = ["innkeeper", "inn", "drunkard", "tavern", "merc", "bard", "jester", "hostess*"];

    // Never asked, even in a tavern: people on duty, children, animals.
    private static readonly string[] NeverAsk = ["guard", "child", "kid", "boy", "girl", "dog", "cat", "rat", "horse", "cow", "pig"];

    public static bool Anywhere { get; set; }

    /// <summary>
    /// Whether an object name has <paramref name="word"/> as one of its
    /// _-separated words, allowing a suffix that starts a new word ("merc01",
    /// "drunkardMS") but not one that makes a longer word ("merchant"). A word
    /// ending in * takes any suffix ("hostess*" matches "hostessgs").
    /// </summary>
    public static bool HasWord(string objectName, string word)
    {
        bool any = word.EndsWith('*');
        if (any) word = word[..^1];
        foreach (var token in objectName.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;
            if (any || token.Length == word.Length || !char.IsLower(token[word.Length])) return true;
        }
        return false;
    }

    public static string ObjectName(InstanceRef unit) =>
        Builtins.object_get_name(unit.Get(Objects.o_player.Vars.object_index)).ToString();

    // How near an innkeeper counts as inside the tavern. A town is one room
    // with its buildings' interiors laid out far off the streets (Osbrook's
    // inn sits around x 10000, its square around x 1200), so the innkeeper's
    // company says "indoors, at the inn" where the room itself cannot.
    private const double TavernTiles = 15;

    /// <summary>Whether the player is in a tavern: an innkeeper, hostess or inn worker is within a few tiles.</summary>
    public static bool InTavern(InstanceRef player)
    {
        if (Objects.o_NPC.Object is not { } npcs) return false;
        foreach (var npc in npcs.Instances())
        {
            if (!npc.Exists) continue;
            string obj = ObjectName(npc);
            if ((HasWord(obj, "innkeeper") || HasWord(obj, "hostess*") || HasWord(obj, "inn")) && Tiles(player, npc) <= TavernTiles)
                return true;
        }
        return false;
    }

    // The flags the game keeps, on o_modificatorsMenu, for each of its menus.
    private static readonly string[] MenuFlags =
    [
        "escMenuActive", "inventoryMenuActive", "tradeMenuActive", "characterMenuActive", "skillMenuActive",
        "exploreMenuActive", "cookingMenuActive", "stashLeftMenuActive", "stashRightMenuActive", "fullscreenMenuActive",
    ];

    // Screens with no flag of their own: a conversation, the world map, a right-click menu.
    private static readonly string[] BusyObjects = ["o_dialogue", "o_globalmap", "o_gui_context"];

    /// <summary>
    /// Whether one of the game's own screens is up (the pause menu, the
    /// inventory, trade, a conversation, the map...). The play prompt hides and
    /// the key does nothing then: the table would sit on top of it and swallow
    /// its clicks.
    /// </summary>
    public static bool GameBusy()
    {
        if (GmlObject.Find("o_modificatorsMenu") is { InstanceCount: > 0 } mods)
        {
            var menu = mods.Instance(0);
            foreach (var flag in MenuFlags)
                if (Num(menu, flag) > 0) return true;
        }
        foreach (var name in BusyObjects)
            if (GmlObject.Find(name) is { InstanceCount: > 0 }) return true;
        return false;
    }

    /// <summary>Instance ids compare by their low 32 bits: numbers on older runtimes, references on newer ones.</summary>
    public static long IdKey(RValue v) =>
        v.IsNumber ? (long)v.AsReal : v.Kind == RValueKind.Reference ? v.Int64 & 0xFFFFFFFF : -1;

    /// <summary>Tiles between two units, as the grid counts them (diagonals are one step).</summary>
    public static double Tiles(InstanceRef a, InstanceRef b) =>
        Math.Max(Math.Abs(Num(a, "x") - Num(b, "x")), Math.Abs(Num(a, "y") - Num(b, "y"))) / Tile;

    public static double Num(InstanceRef r, string name, double fallback = 0)
    {
        var v = r.Get(name);
        return v.IsNumber ? v.AsReal : fallback;
    }

    /// <summary>The nearest NPC within <paramref name="reach"/> tiles who would sit down to play, if any.</summary>
    public static InstanceRef? NearestPlayer(InstanceRef player, double reach, out string? why)
    {
        why = null;
        if (Objects.o_NPC.Object is not { } npcs) return null;
        bool tavern = Anywhere || InTavern(player);
        InstanceRef? best = null;
        double bestTiles = double.MaxValue, refusedTiles = double.MaxValue;
        foreach (var npc in npcs.Instances())
        {
            if (!npc.Exists) continue;
            double tiles = Tiles(player, npc);
            if (tiles > reach || tiles >= bestTiles) continue;
            string refusal = Refusal(npc, tavern);
            if (refusal.Length > 0)
            {
                // The reason given is the nearest refuser's.
                if (tiles < refusedTiles) (why, refusedTiles) = (refusal, tiles);
                continue;
            }
            best = npc;
            bestTiles = tiles;
        }
        if (best != null) why = null;
        return best;
    }

    /// <summary>Why this NPC will not play, or "" if it will.</summary>
    public static string Refusal(InstanceRef npc, bool inTavern)
    {
        string obj = ObjectName(npc);
        // Animals are o_NPC too (o_villagepig02, o_villagechicken); people are o_npc_*.
        if (!obj.StartsWith("o_npc_", StringComparison.Ordinal)) return "they cannot play";
        if (NeverAsk.Any(w => HasWord(obj, w))) return "they have no time for games";
        if (Num(npc, "is_hostile") > 0 || Num(npc, "is_player_enemy") > 0) return "they would sooner fight you";
        if (npc.Get("HP") is { IsNumber: true } hp && hp.AsReal <= 0) return "they are in no state to play";
        if (!inTavern && !TavernFolk.Any(w => HasWord(obj, w))) return "games are for the tavern";
        return "";
    }

    /// <summary>What kind of player an NPC is, and how much it carries to lose.</summary>
    public static (Temperament Temper, int Purse) Classify(InstanceRef npc)
    {
        string raw = ObjectName(npc), obj = raw.ToLowerInvariant();
        if (obj.Contains("drunk")) return (Temperament.Reckless, 120);
        if (HasWord(raw, "merc") || HasWord(raw, "leif") || HasWord(raw, "darrel")) return (Temperament.Reckless, 400);
        if (obj.Contains("innkeeper") || obj.Contains("hostess")) return (Temperament.Cautious, 600);
        if (obj.Contains("worker") || obj.Contains("servant") || obj.Contains("wife") || obj.Contains("daughter"))
            return (Temperament.Cautious, 150);
        return (Temperament.Steady, 200);
    }

    /// <summary>The NPC's name as the game shows it, else one made from its object name.</summary>
    public static string DisplayName(InstanceRef npc)
    {
        foreach (var v in new[] { "name", "unitParamName" })
        {
            var s = npc.Get(v);
            if (s.Kind == RValueKind.String && s.ToString() is { Length: > 0 } text && !text.StartsWith("o_", StringComparison.Ordinal)) return text;
        }
        // o_npc_drunkard_osbrook -> Drunkard
        string obj = ObjectName(npc);
        string word = obj.Split('_', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                         .FirstOrDefault(w => !w.Equals("npc", StringComparison.OrdinalIgnoreCase)) ?? "Stranger";
        word = new string(word.TakeWhile(char.IsLetter).ToArray());
        return word.Length == 0 ? "Stranger" : char.ToUpperInvariant(word[0]) + word[1..];
    }

    /// <summary>A key that finds the same NPC again after a room change: its id_name, or its object name.</summary>
    public static string Key(InstanceRef npc)
    {
        var id = npc.Get("id_name");
        return id.Kind == RValueKind.String && id.ToString().Length > 0 ? id.ToString() : ObjectName(npc);
    }
}
