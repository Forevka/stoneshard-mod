using CoreLoader;

[assembly: CoreModInfo(typeof(DwarfBoost.DwarfBoostMod), "Dwarf Boost", "1.0.0", "CoreLoader")]
[assembly: CoreModGame("Dwarf Eats Mountain")]

namespace DwarfBoost;

/// <summary>
/// Dwarf Eats Mountain tweaks.
///
/// The game keeps its economy on the oSys controller (gold, mithril, soul) and
/// every unit - miners, flamers, harpoons, cannons, all children of parDwarf -
/// carries its own `damage`, recomputed by the game from baseDamage and its
/// modifiers whenever an upgrade changes them. Found with the Instance Inspector
/// and ReflectionProbe; nothing here is hardcoded to an address.
///
///  * Gold income multiplier: each frame's rise in oSys.gold is scaled. Drops
///    (spending) are left alone, so purchases cost what they say.
///  * Unit damage multiplier: applied on top of whatever the game last computed.
///    Each instance remembers the value this mod wrote; when the game writes a
///    new one (an upgrade), that becomes the new base, so it never compounds.
///  * Resource editor for gold, mithril and soul.
/// </summary>
public sealed class DwarfBoostMod : CoreMod
{
    private float _goldMultiplier = 1f;
    private float _damageMultiplier = 1f;
    private double _lastGold = double.NaN;
    private double _bonusGold;
    private int _tick;
    private string _status = "";

    // instance id -> (the game's own damage, what we wrote, the unit's baseDamage then)
    private readonly Dictionary<long, (double Base, double Written, double BaseDamage)> _damage = new();

    public override void OnInitialize()
    {
        _goldMultiplier = Config.Get("goldMultiplier", 1f);
        _damageMultiplier = Config.Get("damageMultiplier", 1f);
        Log.Info($"gold x{_goldMultiplier}, damage x{_damageMultiplier}");
    }

    private InstanceRef? Sys()
    {
        var o = GmlObject.Find("oSys");
        return o is { } obj && obj.InstanceCount > 0 ? obj.Instance(0) : null;
    }

    public override void OnUpdate()
    {
        if (++_tick % 6 != 0) return;   // ten times a second is plenty
        try
        {
            ApplyGold();
            if (_tick % 30 == 0) ApplyDamage();
            if (_tick % 600 == 0 && _damage.Count > 0)
            {
                var sample = _damage.First().Value;
                Log.Info($"gold {_lastGold:N0} (bonus so far {_bonusGold:N0}); {_damage.Count} units, " +
                         $"e.g. damage {sample.Base:0.##} -> {sample.Written:0.##}");
            }
            _status = "";
        }
        catch (GmlException ex)
        {
            _status = ex.Message;   // e.g. at the title screen, before oSys exists
        }
    }

    private void ApplyGold()
    {
        if (Sys() is not { } sys) { _lastGold = double.NaN; return; }
        double gold = sys.Get("gold").AsReal;
        if (double.IsNaN(gold)) return;

        if (!double.IsNaN(_lastGold) && gold > _lastGold && _goldMultiplier != 1f)
        {
            double extra = (gold - _lastGold) * (_goldMultiplier - 1f);
            if (extra > 0)
            {
                gold += extra;
                sys.Set("gold", gold);
                _bonusGold += extra;
            }
        }
        _lastGold = gold;
    }

    private void ApplyDamage()
    {
        if (GmlObject.Find("parDwarf") is not { } units) return;
        var seen = new HashSet<long>();
        foreach (var unit in units.Instances())
        {
            if (!unit.Has("damage")) continue;
            long key = unit.Id.Int64;
            seen.Add(key);
            double current = unit.Get("damage").AsReal;
            if (double.IsNaN(current)) continue;

            // baseDamage moves when an upgrade lands, which catches a recompute
            // that happens to produce exactly the value we last wrote.
            double baseDamage = unit.Has("baseDamage") ? unit.Get("baseDamage").AsReal : double.NaN;
            bool ours = _damage.TryGetValue(key, out var d)
                        && Math.Abs(current - d.Written) < 1e-6
                        && (double.IsNaN(baseDamage) || baseDamage.Equals(d.BaseDamage));

            double gameValue = ours
                ? d.Base          // still our own value: the game has not recomputed
                : current;        // new instance or the game recomputed: that is the base now

            double want = gameValue * _damageMultiplier;
            if (Math.Abs(want - current) > 1e-6) unit.Set("damage", want);
            _damage[key] = (gameValue, want, baseDamage);
        }
        foreach (var gone in _damage.Keys.Where(k => !seen.Contains(k)).ToList()) _damage.Remove(gone);
    }

    public override void OnGUI()
    {
        if (_status.Length > 0) UI.TextColored(1f, 0.6f, 0.3f, _status);

        UI.Text("Gold income");
        if (UI.SliderFloat("gold x##gold", ref _goldMultiplier, 1f, 20f)) Config.Set("goldMultiplier", _goldMultiplier);
        UI.TextDisabled($"bonus gold granted this session: {_bonusGold:N0}");
        UI.Separator();

        UI.Text("Unit damage");
        if (UI.SliderFloat("damage x##dmg", ref _damageMultiplier, 1f, 20f)) Config.Set("damageMultiplier", _damageMultiplier);
        UI.TextDisabled($"{_damage.Count} unit(s) tracked");
        UI.Separator();

        if (Sys() is not { } sys) { UI.TextDisabled("oSys not present yet (title screen?)"); return; }
        UI.Text("Resources");
        Resource(sys, "gold", 1_000_000);
        Resource(sys, "mithril", 100);
        Resource(sys, "soul", 10);
    }

    private void Resource(InstanceRef sys, string name, double step)
    {
        double v = sys.Get(name).AsReal;
        UI.PushId(name);
        UI.Text($"{name}: {v:N0}");
        UI.SameLine();
        if (UI.Button($"+{step:N0}"))
        {
            sys.Set(name, v + step);
            if (name == "gold") _lastGold = v + step;   // not income: do not multiply it
            Log.Info($"{name} {v:N0} -> {v + step:N0}");
        }
        UI.SameLine();
        if (UI.Button($"x10"))
        {
            sys.Set(name, v * 10);
            if (name == "gold") _lastGold = v * 10;
        }
        UI.PopId();
    }
}
