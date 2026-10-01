using CoreLoader.Runtime;

namespace CoreLoader;

/// <summary>
/// Settings a mod offers the player, as a list of controls bound to keys of its
/// <see cref="ModConfig"/>. The loader keeps the list; how it is shown is up to
/// whoever draws it - a game-styled settings screen in a game-specific mod, or
/// any other front end - so a mod declares its settings once, for every game.
/// </summary>
/// <remarks>
/// Values live in the mod's own config file, so they persist and survive a hot
/// reload; a control only says how to edit one. Registrations belong to the
/// registering mod and go when it unloads. Game thread only.
/// <code>
/// ModSettings.Slider(this, "xpScale", "Kill XP", "Experience from enemies", 1, 0, 3, 0.25);
/// ModSettings.Toggle(this, "enabled", "Trials", "Off: the tavern door leads to Osbrook again", true);
/// </code>
/// </remarks>
public static class ModSettings
{
    public enum Kind { Toggle, Slider, Choice }

    /// <summary>One control. Read and write its value with GetBool/GetNumber and SetBool/SetNumber.</summary>
    public sealed class Setting
    {
        internal Setting(CoreMod mod, LoadedMod? owner, Kind kind, string key, string label, string description)
        {
            Mod = mod;
            Owner = owner;
            Kind = kind;
            Key = key;
            Label = label;
            Description = description;
        }

        internal LoadedMod? Owner { get; }
        internal CoreMod Mod { get; }

        /// <summary>The registering mod's display name.</summary>
        public string ModName => Mod.Info.Name;
        public Kind Kind { get; }
        public string Key { get; }
        public string Label { get; }
        public string Description { get; }

        public bool DefaultBool { get; internal init; }
        public double DefaultNumber { get; internal init; }
        public double Min { get; internal init; }
        public double Max { get; internal init; }
        public double Step { get; internal init; }
        /// <summary>A choice's options, in order; its value is the chosen index.</summary>
        public IReadOnlyList<string> Options { get; internal init; } = Array.Empty<string>();
        /// <summary>How a slider's value reads ("x1.5", "150%"); null: the number itself.</summary>
        public Func<double, string>? Format { get; internal init; }

        internal Action? Changed { get; init; }

        /// <summary>The registering mod's assembly name: unique, unlike display names.</summary>
        public string ModId => Mod.GetType().Assembly.GetName().Name ?? ModName;

        /// <summary>
        /// False once its mod unloaded or faulted: the config behind it is no
        /// longer saved, so writes are ignored. Front ends re-read <see cref="Registered"/>.
        /// </summary>
        public bool IsRegistered => All.Contains(this);

        public bool GetBool() => Mod.Config.Get(Key, DefaultBool);

        /// <summary>A slider's value, or a choice's index (read as a number, so a hand-written 1.0 counts).</summary>
        public double GetNumber() => Kind == Kind.Choice
            ? Math.Clamp((int)Math.Round(Mod.Config.Get(Key, DefaultNumber)), 0, Math.Max(0, Options.Count - 1))
            : Math.Clamp(Mod.Config.Get(Key, DefaultNumber), Min, Max);

        public string ValueText() => Kind switch
        {
            Kind.Toggle => GetBool() ? "On" : "Off",
            Kind.Choice => Options.Count > 0 ? Options[(int)GetNumber()] : "",
            _ => Format?.Invoke(GetNumber()) ?? GetNumber().ToString("0.##"),
        };

        public void SetBool(bool value)
        {
            Loader.EnsureGameThread();
            if (!IsRegistered || GetBool() == value) return;
            Mod.Config.Set(Key, value);
            Notify();
        }

        /// <summary>Clamped to the range and snapped to the step (a choice: to an option).</summary>
        public void SetNumber(double value)
        {
            Loader.EnsureGameThread();
            if (!IsRegistered) return;
            if (Kind == Kind.Choice)
            {
                int i = Math.Clamp((int)Math.Round(value), 0, Math.Max(0, Options.Count - 1));
                if ((int)GetNumber() == i) return;
                Mod.Config.Set(Key, (double)i);
            }
            else
            {
                double v = Math.Clamp(value, Min, Max);
                if (Step > 0) v = Math.Clamp(Min + Math.Round((v - Min) / Step) * Step, Min, Max);
                v = Math.Round(v, 6);
                if (GetNumber() == v) return;
                Mod.Config.Set(Key, v);
            }
            Notify();
        }

        /// <summary>One step up or down (a toggle flips; a choice cycles).</summary>
        public void Nudge(int direction)
        {
            switch (Kind)
            {
                case Kind.Toggle: SetBool(!GetBool()); break;
                case Kind.Choice:
                    int n = Math.Max(1, Options.Count);
                    SetNumber((((int)GetNumber() + direction) % n + n) % n);
                    break;
                default: SetNumber(GetNumber() + direction * (Step > 0 ? Step : (Max - Min) / 10)); break;
            }
        }

        public void Reset()
        {
            if (Kind == Kind.Toggle) SetBool(DefaultBool);
            else SetNumber(DefaultNumber);
        }

        // The owner's own callback, run as the owner: what it registers belongs
        // to it, not to the front end that happened to make the change. One that
        // throws is logged, not passed to that front end.
        private void Notify()
        {
            if (Changed is null || Owner is { State: not ModState.Running }) return;
            var previous = ModManager.Current;
            try
            {
                if (Owner != null) ModManager.Current = Owner;
                Changed();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Mod.Log.Warning($"setting {Key}: {ex.Message}");
            }
            finally { ModManager.Current = previous; }
        }
    }

    private static readonly List<Setting> All = new();

    /// <summary>Changes whenever the list does (a mod registered, or went), for front ends that cache it.</summary>
    public static int Version { get; private set; }

    /// <summary>Every registered setting, in registration order.</summary>
    public static IReadOnlyList<Setting> Registered
    {
        get
        {
            Loader.EnsureGameThread();
            return All.ToList();
        }
    }

    public static Setting Toggle(CoreMod mod, string key, string label, string description, bool defaultValue,
                                 Action? changed = null) =>
        Add(new Setting(mod, Owner(changed), Kind.Toggle, key, label, description)
        {
            DefaultBool = defaultValue,
            Changed = changed,
        });

    public static Setting Slider(CoreMod mod, string key, string label, string description, double defaultValue,
                                 double min, double max, double step, Func<double, string>? format = null,
                                 Action? changed = null)
    {
        if (!(max > min)) throw new ArgumentException("max must be above min");
        return Add(new Setting(mod, Owner(changed), Kind.Slider, key, label, description)
        {
            DefaultNumber = Math.Clamp(defaultValue, min, max),
            Min = min,
            Max = max,
            Step = step,
            Format = format,
            Changed = changed,
        });
    }

    public static Setting Choice(CoreMod mod, string key, string label, string description, int defaultIndex,
                                 IReadOnlyList<string> options, Action? changed = null)
    {
        if (options.Count == 0) throw new ArgumentException("a choice needs options");
        return Add(new Setting(mod, Owner(changed), Kind.Choice, key, label, description)
        {
            DefaultNumber = Math.Clamp(defaultIndex, 0, options.Count - 1),
            Options = options.ToArray(),
            Changed = changed,
        });
    }

    private static LoadedMod? Owner(Action? changed) =>
        changed != null ? ModManager.OwnerOf(changed) : ModManager.Current;

    private static Setting Add(Setting s)
    {
        Loader.EnsureGameThread();
        if (string.IsNullOrWhiteSpace(s.Key)) throw new ArgumentException("a setting needs a key");
        // Registering the same key again replaces it in place.
        int at = All.FindIndex(o => o.Mod == s.Mod && o.Key == s.Key);
        if (at >= 0) All[at] = s;
        else All.Add(s);
        Version++;
        return s;
    }

    internal static void RemoveOwner(LoadedMod owner)
    {
        if (All.RemoveAll(s => s.Owner == owner || s.Mod == owner.Instance) > 0) Version++;
    }
}
