using System.Text;
using System.Text.Json;
using CoreLoader.Native;

namespace CoreLoader.Runtime;

/// <summary>
/// Writes the running game's code map as a C# project mods can compile against:
/// Lodestone/Interop/&lt;Game&gt;.Interop/ with typed refs for every script, every
/// object and its events, every builtin (with this runtime's real arity) and
/// every asset name, plus codemap.json with the same data for tools.
///
/// Everything comes from the live game - the symbol table, the builtin registry
/// and the runtime's own asset enumeration - so it works for any YYC game.
/// It is regenerated only when the exe changes (or this loader version does).
/// </summary>
internal static unsafe class InteropGenerator
{
    private static readonly Logger Log = new("Lodestone");
    private static bool _done;
    private static bool _degraded;
    private static int _waitFrames;

    private static readonly string[] EventTypes =
    {
        "PreCreate", "Create", "Destroy", "CleanUp", "Step", "Alarm", "Draw", "Mouse",
        "KeyPress", "KeyRelease", "Keyboard", "Collision", "Other", "Gesture", "Async",
    };

    public static string SafeGameName
    {
        get
        {
            var s = new string(Game.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
            if (s.Length == 0) return "YycGame";
            if (char.IsDigit(s[0]) || Keywords.Contains(s)) s = "_" + s;
            return s;
        }
    }

    public static string OutputDirectory =>
        Path.Combine(Game.LoaderDirectory, "Interop", SafeGameName + ".Interop");

    public static string Status { get; private set; } = "pending";

    /// <summary>Forces the next launch to regenerate (new knowledge, e.g. harvested variables).</summary>
    public static void MarkStale()
    {
        try { File.Delete(Path.Combine(OutputDirectory, ".stamp")); } catch (IOException) { }
    }

    /// <summary>Regenerates on the next frame, now, regardless of the stamp.</summary>
    public static void RequestRegenerate()
    {
        if (_job != null || _writer != null) return;   // one is already under way
        VarHarvest.Flush();
        MarkStale();
        _done = false;
        _waitFrames = 1800;   // assets are certainly loaded by now
    }

    // Generation never freezes the game: what needs the game (objects, assets,
    // builtins - tens of thousands of builtin calls in a big game) is collected
    // a few milliseconds per frame, and what does not (scanning code for
    // argument counts, writing several MB of source) runs on a worker thread.
    private const double SliceMs = 4;
    private static IEnumerator<bool>? _job;
    private static Task? _writer;
    private static System.Diagnostics.Stopwatch? _clock;
    private static string _stamp = "";

    /// <summary>Called every frame; does its work once, when builtins are available.</summary>
    public static void Tick()
    {
        if (_writer != null)
        {
            if (!_writer.IsCompleted) return;
            if (_writer.Exception is { } ex)
            {
                Status = $"failed: {ex.GetBaseException().Message}";
                Log.Error("interop generation failed", ex.GetBaseException());
            }
            _writer = null;
            return;
        }
        if (_job != null)
        {
            RunSlice();
            return;
        }
        if (_done) return;
        // No GML bridge (a VM-compiled game, or one whose helpers were not
        // found): there is nothing to scan, and an attempt only logs failures.
        // The bridge is settled before the managed runtime starts.
        if (!Game.IsGmlReady)
        {
            _done = true;
            Status = "not available: the GML bridge is off (see the Status tab)";
            Log.Info("interop: skipped, the GML bridge is off");
            return;
        }
        ++_waitFrames;
        // Builtins resolve a moment after startup, and some games (Stoneshard)
        // only finish loading their sprites, rooms and sounds seconds after
        // that - scanning too early records an empty asset list. Wait until an
        // asset answers, or give up waiting after ~30 s and generate anyway.
        if (_waitFrames < 1800)
        {
            if (Game.BuiltinCount == 0 || _waitFrames < 120) return;
            // As mods' start asks it: a check that faults means "not yet".
            if (_waitFrames % 30 != 0 || !Game.AssetsLoaded(whenUnsure: false)) return;
        }
        _done = true;

        try
        {
            _stamp = Stamp();
            var stampFile = Path.Combine(OutputDirectory, ".stamp");
            if (File.Exists(stampFile) && File.ReadAllText(stampFile) == _stamp)
            {
                Status = $"up to date: {OutputDirectory}";
                return;
            }
            _clock = System.Diagnostics.Stopwatch.StartNew();
            _degraded = _waitFrames >= 1800 && !Game.AssetsLoaded(whenUnsure: false);
            Status = "collecting from the game...";
            _job = Collect().GetEnumerator();
            RunSlice();
        }
        catch (Exception ex)
        {
            Status = $"failed: {ex.Message}";
            Log.Error("interop generation failed", ex);
        }
    }

    private static void RunSlice()
    {
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                        (long)(SliceMs * System.Diagnostics.Stopwatch.Frequency / 1000);
        try
        {
            while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
            {
                if (!_job!.MoveNext()) { _job = null; return; }
                // false: the job is waiting on something else's frames.
                if (!_job.Current) return;
            }
        }
        catch (Exception ex)
        {
            _job = null;
            Status = $"failed: {ex.Message}";
            Log.Error("interop generation failed", ex);
        }
    }

    // Bumped whenever the generated code changes shape, so installed interops
    // regenerate without waiting for a new loader version.
    private const int Format = 2;

    private static string Stamp()
    {
        var exe = Environment.ProcessPath ?? "";
        var info = new FileInfo(exe);
        return $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Game.Symbols.Count}|{Game.BuiltinCount}|" +
               $"{typeof(InteropGenerator).Assembly.GetName().Version}|f{Format}";
    }

    // GameMaker's built-in instance variables. Every instance has them, but
    // variable_instance_get_names does not list them, so the harvest never sees
    // them: without this list a mod reading x or object_index falls back to strings.
    private static readonly string[] BuiltinInstanceVars =
    {
        "id", "object_index", "x", "y", "xstart", "ystart", "xprevious", "yprevious",
        "direction", "speed", "hspeed", "vspeed", "friction", "gravity", "gravity_direction",
        "sprite_index", "image_index", "image_speed", "image_number", "image_xscale", "image_yscale",
        "image_angle", "image_alpha", "image_blend", "sprite_width", "sprite_height",
        "sprite_xoffset", "sprite_yoffset", "mask_index", "bbox_left", "bbox_right", "bbox_top", "bbox_bottom",
        "depth", "layer", "visible", "solid", "persistent", "alarm",
    };

    // ------------------------------------------------------------------ model

    private sealed record ObjectInfo(string Name, int Index, List<(string Member, string Symbol)> Events);

    // Everything generation needs from the game, collected on the game thread.
    private sealed class Snapshot
    {
        public List<string> Scripts = new();
        public Dictionary<string, nint> Addresses = new(StringComparer.Ordinal);
        public nint[] Starts = Array.Empty<nint>();
        public SortedDictionary<string, ObjectInfo> Objects = new(StringComparer.Ordinal);
        public List<(string Name, int Arity)> Builtins = new();
        public List<string> Sprites = new(), Rooms = new(), Sounds = new();
        // A copy: the harvester keeps adding to its own on the game thread.
        public Dictionary<string, string[]> Variables = new(StringComparer.Ordinal);
    }

    // The game-thread part, as steps: every `yield` is a point where the frame
    // may end and the rest continue next frame; `yield return false` ends it.
    private static IEnumerable<bool> Collect()
    {
        var snap = new Snapshot();
        snap.Scripts = Game.Symbols.Where(s => s.IsScript && !s.Name.Contains('@')).Select(s => s.Name).ToList();
        foreach (var s in Game.Symbols) if (s.Address != 0) snap.Addresses.TryAdd(s.Name, s.Address);
        // Every function's start, in address order: each script is scanned only
        // up to the next function, never into its neighbour.
        snap.Starts = snap.Addresses.Values.Distinct().OrderBy(a => a).ToArray();
        yield return true;

        // Objects come from the shared object table, so the session scans them
        // once. It builds a slice per frame; wait for it (a frame at a time)
        // rather than scanning here again. Only frames in which the table
        // actually worked count toward the ~10 s allowance: it may still be
        // waiting for the game's assets or for mods to start, and that wait
        // must not use the allowance up and force the scan into one frame. A
        // minute overall bounds a table that never gets going. Past either,
        // GmlObject.All finishes the names on the spot, and an empty answer
        // means the runtime could not list them: objects are then known only
        // by their events, and the run is marked degraded.
        ObjectTable.Start();
        var working = new System.Diagnostics.Stopwatch();
        var overall = System.Diagnostics.Stopwatch.StartNew();
        while (!ObjectTable.Ready && working.Elapsed < TimeSpan.FromSeconds(10) &&
               overall.Elapsed < TimeSpan.FromMinutes(1))
        {
            // The table ticks before interop each frame, so this reflects the
            // frame that just ran; the stopwatch runs across frames it worked in.
            if (ObjectTable.Advancing) working.Start(); else working.Stop();
            yield return false;
        }
        var objects = GmlObject.All();
        if (objects.Count == 0)
        {
            Log.Warning($"interop: the object table is empty ({ObjectTable.Status}); objects are listed from their events only");
            _degraded = true;
        }
        // Objects mods defined this session are theirs, not the game's: an
        // interop listing them would hand other mods names that only exist
        // while that mod is installed.
        foreach (var o in objects)
            if (!ObjectTypes.IsDefinedIndex(o.Index)) snap.Objects[o.Name] = new ObjectInfo(o.Name, o.Index, new());
        foreach (var s in Game.Symbols.Where(s => s.IsObjectEvent))
        {
            if (!SplitEvent(s.Name, out var obj, out var member)) continue;
            if (!snap.Objects.TryGetValue(obj, out var info)) snap.Objects[obj] = info = new ObjectInfo(obj, -1, new());
            info.Events.Add((member, s.Name));
        }
        yield return true;

        for (int i = 0; i < Game.BuiltinCount; i++)
        {
            var name = BuiltinName(i);
            if (name != null) snap.Builtins.Add((name, Game.BuiltinArity(name) ?? -1));
            if ((i & 255) == 0) yield return true;
        }

        // Sprites and sounds mods added at runtime are not part of the game.
        foreach (var step in EnumerateAssets("sprite_exists", "sprite_get_name", snap.Sprites, Content.IsModSprite)) yield return step;
        foreach (var step in EnumerateAssets("room_exists", "room_get_name", snap.Rooms)) yield return step;
        foreach (var step in EnumerateAssets("audio_exists", "audio_get_name", snap.Sounds, Content.IsModSound)) yield return step;

        foreach (var (obj, names) in VarHarvest.Known) snap.Variables[obj] = names.ToArray();

        // The rest reads only code and writes files: off the game thread.
        Status = "writing...";
        bool degraded = _degraded;
        string stamp = _stamp;
        var clock = _clock;
        _writer = Task.Run(() => Write(snap, degraded, stamp, clock));
    }

    // Outside the iterator: iterators cannot hold unsafe code.
    private static string? BuiltinName(int i) => Utf8.Read(Loader.Api->BuiltinName(i));

    private static void Write(Snapshot snap, bool degraded, string stamp, System.Diagnostics.Stopwatch? clock)
    {
        Directory.CreateDirectory(OutputDirectory);
        string ns = SafeGameName;

        var arity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in snap.Scripts)
        {
            if (!snap.Addresses.TryGetValue(s, out var addr)) continue;
            int at = Array.BinarySearch(snap.Starts, addr);
            nint next = at >= 0 && at + 1 < snap.Starts.Length ? snap.Starts[at + 1] : 0;
            arity[s] = CodeScan.ArgumentCount(addr, next);
        }
        int typed = arity.Values.Count(n => n is >= 1 and <= 8);
        Log.Info($"interop: argument counts read from code for {typed} of {snap.Scripts.Count} scripts");

        File.WriteAllText(Path.Combine(OutputDirectory, ns + ".Interop.csproj"), Csproj(ns));
        File.WriteAllText(Path.Combine(OutputDirectory, "Scripts.g.cs"), ScriptsSource(ns, snap.Scripts, arity));
        File.WriteAllText(Path.Combine(OutputDirectory, "Objects.g.cs"), ObjectsSource(ns, snap.Objects.Values, snap.Variables));
        File.WriteAllText(Path.Combine(OutputDirectory, "Builtins.g.cs"), BuiltinsSource(ns, snap.Builtins));
        File.WriteAllText(Path.Combine(OutputDirectory, "Assets.g.cs"), AssetsSource(ns, snap.Sprites, snap.Rooms, snap.Sounds));
        WriteCodeMap(snap.Scripts, arity, snap.Objects.Values, snap.Builtins, snap.Sprites, snap.Rooms, snap.Sounds, snap.Variables);

        Log.Info($"interop: {snap.Scripts.Count} scripts, {snap.Objects.Count} objects, " +
                 $"{snap.Objects.Values.Sum(o => o.Events.Count)} events, {snap.Builtins.Count} builtins, " +
                 $"{snap.Sprites.Count} sprites, {snap.Rooms.Count} rooms, {snap.Sounds.Count} sounds");

        // A scan that gave up waiting or was cut short is not stamped, so the
        // next launch tries again instead of keeping a partial map.
        var stampFile = Path.Combine(OutputDirectory, ".stamp");
        if (degraded) File.Delete(stampFile);
        else File.WriteAllText(stampFile, stamp);
        Status = $"generated{(degraded ? " (partial - will retry next launch)" : "")} " +
                 $"in {clock?.ElapsedMilliseconds ?? 0} ms: {OutputDirectory}";
        Log.Info($"interop {Status}");
    }

    // Asset indices are dense from 0; stop after a run of misses. A step per
    // few hundred indices, so a game with 17,000 sprites spreads over frames.
    private static IEnumerable<bool> EnumerateAssets(string exists, string getName, List<string> names, Func<int, bool>? skip = null)
    {
        if (Game.BuiltinArity(exists) is null || Game.BuiltinArity(getName) is null)
        {
            Log.Warning($"interop: {exists}/{getName} not in this runtime's registry; skipping");
            yield break;
        }
        int misses = 0;
        for (int i = 0; misses < 64 && i < 200_000; i++)
        {
            string? name = null;
            bool stop = false;
            try
            {
                if (!Game.CallBuiltin(exists, i).AsBool) misses++;
                else
                {
                    misses = 0;
                    if (skip?.Invoke(i) != true) name = Game.CallBuiltin(getName, i).ToString();
                }
            }
            catch (GmlException ex)
            {
                Log.Warning($"interop: {getName} scan stopped: {ex.Message}");
                _degraded = true;
                stop = true;
            }
            if (stop) yield break;
            if (name != null) names.Add(name);
            if ((i & 255) == 0) yield return true;
        }
        if (names.Count == 0) Log.Warning($"interop: {exists} found nothing");
    }

    // gml_Object_<object>_<EventType>_<suffix>. Object names may themselves
    // contain underscores and event-like words, so the LAST event keyword that
    // is followed by a suffix wins.
    internal static bool SplitEvent(string symbol, out string obj, out string member)
    {
        obj = member = "";
        const string prefix = "gml_Object_";
        if (!symbol.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = symbol[prefix.Length..];
        int best = -1;
        string bestType = "";
        foreach (var t in EventTypes)
        {
            var marker = "_" + t + "_";
            int at = rest.LastIndexOf(marker, StringComparison.Ordinal);
            if (at > best) { best = at; bestType = t; }
        }
        if (best <= 0) return false;
        obj = rest[..best];
        member = rest[(best + 1)..];
        return true;
    }

    // ---------------------------------------------------------------- sources

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while",
    };

    internal static string Ident(string name)
    {
        var sb = new StringBuilder(name.Length + 1);
        foreach (var ch in name) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        var s = sb.ToString();
        return Keywords.Contains(s) ? "@" + s : s;
    }

    private static string Header(string ns) =>
        $"// <auto-generated>\n// Generated by CoreLoader from {Game.Name} ({DateTime.Now:yyyy-MM-dd HH:mm}). " +
        "Regenerated when the game changes; do not edit.\n// </auto-generated>\n#nullable enable\nusing CoreLoader;\n\n" +
        $"namespace {ns};\n\n";

    private static string Csproj(string ns) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <!-- Generated by CoreLoader. Reference this project (or the dll it builds) from a mod. -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <AssemblyName>{ns}.Interop</AssemblyName>
            <RootNamespace>{ns}</RootNamespace>
            <Nullable>enable</Nullable>
            <GenerateDocumentationFile>true</GenerateDocumentationFile>
            <NoWarn>$(NoWarn);CS1591</NoWarn>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="CoreLoader">
              <HintPath>..\..\CoreLoader.dll</HintPath>
              <Private>false</Private>
            </Reference>
          </ItemGroup>
        </Project>
        """;

    private static string ScriptsSource(string ns, List<string> scripts, IReadOnlyDictionary<string, int> arity)
    {
        var sb = new StringBuilder(Header(ns));
        sb.Append("/// <summary>\n/// Every compiled script in the game. Call them (<c>Invoke</c> is typed where the argument\n");
        sb.Append("/// count could be read from the compiled code, <c>Call</c> always works), or hook them with Before/After.\n/// </summary>\n");
        sb.Append("public static class Scripts\n{\n");
        // A member may not share its enclosing type's name (CS0542).
        var used = new HashSet<string>(StringComparer.Ordinal) { "Scripts" };
        foreach (var s in scripts)
        {
            var id = Ident(s["gml_Script_".Length..]);
            if (!used.Add(id)) continue;
            int n = arity.TryGetValue(s, out var a) ? a : 0;
            // 0 means "reads no guarded arguments" - either none, or it indexes
            // argument[i] dynamically - so it keeps the untyped Call(params).
            string type = n is >= 1 and <= 8 ? $"ScriptRef{n}" : "ScriptRef";
            string note = n is >= 1 and <= 8 ? $" - reads {n} argument{(n == 1 ? "" : "s")}" : "";
            sb.Append($"    /// <summary><c>{s}</c>{note}</summary>\n");
            sb.Append($"    public static readonly global::CoreLoader.{type} {id} = new(\"{s}\");\n");
        }
        return sb.Append("}\n").ToString();
    }

    private static string ObjectsSource(string ns, IEnumerable<ObjectInfo> objects, IReadOnlyDictionary<string, string[]> known)
    {
        var sb = new StringBuilder(Header(ns));
        sb.Append("/// <summary>GameMaker's built-in instance variables, which every instance has (each harvested <c>Vars</c> class repeats them).</summary>\n");
        sb.Append("public static class InstanceVars\n{\n");
        foreach (var v in BuiltinInstanceVars) sb.Append($"    public const string {Ident(v)} = \"{v}\";\n");
        sb.Append("}\n\n");
        sb.Append("/// <summary>Every object in the game, with its events.</summary>\n");
        sb.Append("public static class Objects\n{\n");
        var used = new HashSet<string>(StringComparer.Ordinal) { "Objects" };
        foreach (var o in objects)
        {
            var id = Ident(o.Name);
            // Each object class holds members called Name, Object, First and
            // Vars, so an object with one of those names would clash with them.
            if (id is "Name" or "Object" or "First" or "Vars") id += "_";
            if (!used.Add(id)) continue;
            sb.Append($"    /// <summary>Object <c>{o.Name}</c>.</summary>\n");
            sb.Append($"    public static class {id}\n    {{\n");
            sb.Append($"        public const string Name = \"{o.Name}\";\n");
            sb.Append($"        /// <summary>The object by name, resolved in the running game (null if it no longer exists).</summary>\n");
            sb.Append($"        public static global::CoreLoader.GmlObject? Object => global::CoreLoader.GmlObject.Find(Name);\n");
            sb.Append($"        /// <summary>The first live instance, or null when there is none.</summary>\n");
            sb.Append($"        public static global::CoreLoader.InstanceRef? First => Object is {{ InstanceCount: > 0 }} o ? o.Instance(0) : null;\n");
            var members = new HashSet<string>(StringComparer.Ordinal) { "Name", "Object", "First", "Vars", id };

            // Only for objects seen live: repeating the built-ins on all of a big
            // game's objects would double the file. The rest have InstanceVars.
            if (known.TryGetValue(o.Name, out var vars) && vars.Length > 0)
            {
                sb.Append($"        /// <summary>Variables seen on live {o.Name} instances (harvested while playing), and the built-in ones.</summary>\n");
                sb.Append("        public static class Vars\n        {\n");
                var seen = new HashSet<string>(StringComparer.Ordinal) { "Vars" };
                foreach (var v in vars.Concat(BuiltinInstanceVars))
                {
                    var vid = Ident(v);
                    if (!seen.Add(vid)) continue;
                    sb.Append($"            public const string {vid} = \"{v}\";\n");
                }
                sb.Append("        }\n");
            }
            foreach (var (member, symbol) in o.Events.OrderBy(e => e.Member, StringComparer.Ordinal))
            {
                var m = Ident(member);
                if (!members.Add(m)) continue;
                sb.Append($"        /// <summary><c>{symbol}</c></summary>\n");
                sb.Append($"        public static readonly global::CoreLoader.EventRef {m} = new(\"{symbol}\");\n");
            }
            sb.Append("    }\n");
        }
        return sb.Append("}\n").ToString();
    }

    private static string BuiltinsSource(string ns, List<(string Name, int Arity)> builtins)
    {
        var sb = new StringBuilder(Header(ns));
        sb.Append("/// <summary>GameMaker's built-in functions, with the argument counts this game's runtime registers.</summary>\n");
        sb.Append("public static class Builtins\n{\n");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, arity) in builtins)
        {
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_')) continue;
            if (name.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_'))) continue;
            var id = Ident(name);
            if (!used.Add(id)) continue;
            sb.Append($"    /// <summary><c>{name}</c> ({(arity < 0 ? "variadic" : arity + " argument" + (arity == 1 ? "" : "s"))})</summary>\n");
            if (arity < 0)
            {
                sb.Append($"    public static global::CoreLoader.RValue {id}(params global::CoreLoader.RValue[] args) => global::CoreLoader.Game.CallBuiltin(\"{name}\", args);\n");
            }
            else
            {
                var ps = string.Join(", ", Enumerable.Range(0, arity).Select(i => $"global::CoreLoader.RValue a{i}"));
                var args = string.Join(", ", Enumerable.Range(0, arity).Select(i => $"a{i}"));
                sb.Append($"    public static global::CoreLoader.RValue {id}({ps}) => global::CoreLoader.Game.CallBuiltin(\"{name}\"{(arity > 0 ? ", " + args : "")});\n");
            }
        }
        return sb.Append("}\n").ToString();
    }

    private static string AssetsSource(string ns, List<string> sprites, List<string> rooms, List<string> sounds)
    {
        var sb = new StringBuilder(Header(ns));
        sb.Append("/// <summary>Asset names, for asset_get_index and friends.</summary>\npublic static class Assets\n{\n");
        void Section(string cls, List<string> names)
        {
            sb.Append($"    public static class {cls}\n    {{\n");
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in names)
            {
                var id = Ident(n);
                if (!used.Add(id) || id == cls) continue;
                sb.Append($"        public const string {id} = \"{n.Replace("\\", "\\\\").Replace("\"", "\\\"")}\";\n");
            }
            sb.Append("    }\n");
        }
        Section("Sprites", sprites);
        Section("Rooms", rooms);
        Section("Sounds", sounds);
        return sb.Append("}\n").ToString();
    }

    private static void WriteCodeMap(List<string> scripts, IReadOnlyDictionary<string, int> arity, IEnumerable<ObjectInfo> objects,
                                     List<(string Name, int Arity)> builtins,
                                     List<string> sprites, List<string> rooms, List<string> sounds,
                                     IReadOnlyDictionary<string, string[]> known)
    {
        var map = new
        {
            game = Game.Name,
            generated = DateTime.UtcNow,
            functions = Game.Symbols.Select(s => new { name = s.Name, address = $"0x{s.Address:X}" }),
            scripts = scripts.Select(s => new { name = s, arguments = arity.TryGetValue(s, out var n) ? n : 0 }),
            objects = objects.Select(o => new
            {
                name = o.Name, index = o.Index, events = o.Events.Select(e => e.Symbol),
                variables = known.TryGetValue(o.Name, out var v) ? v : Array.Empty<string>(),
            }),
            builtins = builtins.Select(b => new { name = b.Name, arity = b.Arity }),
            sprites,
            rooms,
            sounds,
        };
        using var fs = File.Create(Path.Combine(OutputDirectory, "codemap.json"));
        JsonSerializer.Serialize(fs, map, new JsonSerializerOptions { WriteIndented = true });
    }
}
