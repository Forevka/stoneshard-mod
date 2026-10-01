using CoreLoader;
using StoneShard;

namespace StoneshardHarness;

/// <summary>
/// The skills on the hotbar.
/// </summary>
/// <remarks>
/// Read on the running game: o_skill_fast_panel.skills is a ds_list of the
/// panel's rows (num is the row shown), each row a ds_list of 10 slots, slot 0
/// on key 1 and slot 9 on key 0. A slot holds a skill's object index (noone
/// when empty). The skill itself is the o_skill_ico child of that object owned
/// by the player (the player owns every skill's icon, learned or not): KD is the
/// turns left on its cooldown, maxKD the full cooldown, MPcost its energy.
/// </remarks>
internal static class Hotbar
{
    public static List<object> Read()
    {
        var slots = new List<object>();
        if (Gm.Player is not { } me || Objects.o_skill_fast_panel.First is not { } panel) return slots;
        var rows = new DsList(panel.Get("skills"));
        int row = (int)Gm.Num(panel, "num");
        if (!rows.Exists || row < 0 || row >= rows.Count) return slots;
        var keys = new DsList(rows.At(row));
        if (!keys.Exists) return slots;

        long owner = Gm.Id(me);
        var mine = Gm.All("o_skill_ico").Where(s => Gm.IdKey(s.Get("owner")) == owner).ToList();
        double mp = Units.Atr(me, "MP");
        for (int i = 0; i < Math.Min(10, keys.Count); i++)
        {
            var v = keys.At(i);
            if (!v.IsNumber || v.AsReal < 0) continue;
            int obj = (int)v.AsReal;
            int found = mine.FindIndex(s => Gm.ObjectIndex(s) == obj);
            string key = ((i + 1) % 10).ToString();
            if (found < 0)
            {
                // Utility actions (trap search, crafting) are single instances, not skill icons.
                string objName = GmlObject.FromIndex(obj)?.Name ?? "?";
                string label = Gm.All(objName).Select(u => Gm.Str(u, "name")).FirstOrDefault(n => n.Length > 0) ?? objName;
                slots.Add(new { key, name = label, id = (long?)null, cooldown = 0.0, maxCooldown = 0.0, mp = 0.0, ready = (bool?)null });
                continue;
            }
            var s = mine[found];
            double cd = Gm.Num(s, "KD"), cost = Gm.Num(s, "MPcost");
            slots.Add(new
            {
                key,
                name = Gm.Str(s, "name"),
                id = (long?)Gm.Id(s),
                cooldown = cd,
                maxCooldown = Gm.Num(s, "maxKD"),
                mp = cost,
                ready = (bool?)(cd <= 0 && Gm.Num(s, "is_ready", 1) > 0 && (double.IsNaN(mp) || mp >= cost)),
            });
        }
        return slots;
    }
}
