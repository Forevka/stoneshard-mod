using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// Per-limb condition from the player's Body_Parts_map, with heal and injure,
/// and the statuses the player currently carries.
/// </summary>
internal sealed class BodyTab : Tab
{
    // The game's own spelling, in the order a person reads a body.
    private static readonly (string Key, string Label)[] Parts =
    {
        ("head", "Head"),
        ("tors", "Torso"),
        ("lhand", "Left arm"),
        ("rhand", "Right arm"),
        ("legs", "Left leg"),
        ("rlegs", "Right leg"),
    };

    // More than any real character carries; bounds a list read every frame.
    private const int MaxStatuses = 64;

    public override string Name => "Body";

    public override void Draw()
    {
        if (!Player.Available)
        {
            UI.TextColored(0.95f, 0.8f, 0.35f, "Waiting for the player instance - load a character, then move a step.");
            return;
        }

        UI.TextWrapped(
            "Each body part carries its own 0-100 condition. Read live from the player's " +
            "Body_Parts_map every frame, so this is the game's current state rather than a " +
            "cached copy.");
        UI.Spacing();

        List<(string Key, string Label, double Condition)> parts;
        try { parts = ReadParts(); }
        catch (Exception ex) when (ex is GmlException or InvalidOperationException)
        {
            UI.TextColored(0.95f, 0.45f, 0.45f, ex.Message);
            return;
        }
        if (parts.Count == 0)
        {
            UI.TextColored(0.95f, 0.45f, 0.45f, "Body_Parts_map holds no numeric parts");
            return;
        }

        foreach (var (key, label, condition) in parts)
        {
            UI.PushId(key);

            // Colour by severity, so a glance finds the broken limb without
            // reading six numbers. The bar itself takes no colour here, so the
            // label carries it.
            float f = (float)(condition / 100.0);
            UI.ProgressBar(f, 220f, $"{condition:0} / 100");
            UI.SameLine();
            if (f >= 0.75f) UI.TextColored(0.45f, 0.8f, 0.45f, label.PadRight(10));
            else if (f >= 0.40f) UI.TextColored(0.9f, 0.8f, 0.35f, label.PadRight(10));
            else UI.TextColored(0.92f, 0.45f, 0.4f, label.PadRight(10));

            UI.SameLine();
            UI.BeginDisabled(condition >= 100.0);
            if (UI.Button("Heal")) Actions.Run($"Body_Parts_map.{key} = 100", () => SetCondition(key, 100));
            UI.EndDisabled();

            // Injuring one part on purpose is genuinely useful for testing the
            // very system this panel exists to inspect.
            UI.SameLine();
            if (UI.SmallButton("-25"))
            {
                double v = Math.Clamp(condition - 25.0, 0.0, 100.0);
                Actions.Run($"Body_Parts_map.{key} = {v:0}", () => SetCondition(key, v));
            }

            UI.PopId();
        }

        UI.Spacing();
        if (UI.Button("Heal everything", 200f))
            Actions.Run("Body_Parts_map: every part = 100", () =>
            {
                foreach (var p in ReadParts()) SetCondition(p.Key, 100);
            });
        UI.SameLine();
        UI.TextDisabled("all six parts to 100");

        DrawStatuses();
    }

    private static DsMap PartsMap()
    {
        var map = new DsMap(Player.Require().Get("Body_Parts_map"));
        if (!map.Exists) throw new InvalidOperationException("Body_Parts_map is not readable");
        return map;
    }

    private static List<(string Key, string Label, double Condition)> ReadParts()
    {
        var parts = new List<(string Key, string Label, double Condition)>();
        foreach (var (key, value) in PartsMap().Entries())
        {
            if (!value.IsNumber) continue;
            int order = Array.FindIndex(Parts, p => p.Key == key);
            parts.Add((key, order >= 0 ? Parts[order].Label : key, value.AsReal));
        }

        // A ds_map iterates in hash order, which would shuffle the body around the
        // panel between reads. Sort into anatomical order so the layout is stable.
        parts.Sort((a, b) => Order(a.Key).CompareTo(Order(b.Key)));
        return parts;
    }

    private static int Order(string key)
    {
        int i = Array.FindIndex(Parts, p => p.Key == key);
        return i >= 0 ? i : Parts.Length;
    }

    // Written straight into the map with ds_map_replace. The game's own
    // scr_bodyPartsConditionChange is not used: its arguments are not
    // established, and guessing a script's arguments is how two earlier ones
    // were made to fault.
    private static void SetCondition(string key, double value) =>
        PartsMap().Set(key, Math.Clamp(value, 0.0, 100.0));

    private static void DrawStatuses()
    {
        UI.Spacing();
        UI.SeparatorText("Active statuses");
        UI.TextWrapped(
            "Wounds and bleeding are NOT part of the condition value - they are entries in " +
            "the player's buffs list, so a limb can read 100 and still be bleeding. Listed " +
            "read-only: removing one needs a call whose arguments are not established yet, " +
            "and guessing them is how two earlier scripts were made to fault.");

        DsList list;
        try { list = new DsList(Player.Require().Get("buffs")); }
        catch (GmlException) { list = new DsList(RValue.Undefined); }
        if (!list.Exists)
        {
            UI.TextDisabled("buffs list not readable");
            return;
        }

        int n = Math.Min(list.Count, MaxStatuses);
        if (n == 0)
        {
            UI.TextDisabled("none - no wounds, bleeds or effects active");
            return;
        }

        for (int i = 0; i < n; i++)
        {
            var entry = list.At(i);
            // The entry is an instance reference; its object's name says what the
            // status is, where the bare id says nothing.
            var buff = new InstanceRef(entry);
            string what = buff.Exists
                ? Game.CallBuiltin("object_get_name", buff.Get("object_index")).ToString()
                : "(gone)";
            // A reference prints as "<ref>"; its low word is the instance id.
            string id = entry.Kind == RValueKind.Reference ? (entry.Int64 & 0xFFFFFFFF).ToString() : entry.ToString();
            UI.Text($"  - {what}  {id}");
        }
    }
}
