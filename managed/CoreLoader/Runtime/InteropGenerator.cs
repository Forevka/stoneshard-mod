using System.Text;
using System.Text.Json;
using CoreLoader.Native;

namespace CoreLoader.Runtime;

/// <summary>
/// Writes the running game's code map as a C# project mods can compile against:
/// CoreLoader/Interop/&lt;Game&gt;.Interop/ with typed refs for every script, every
/// object and its events, every builtin (with this runtime's real arity) and
/// every asset name, plus codemap.json with the same data for tools.
///
/// Everything comes from the live game - the symbol table, the builtin registry
/// and the runtime's own asset enumeration - so it works for any YYC game.
/// It is regenerated only when the exe changes (or this loader version does).
/// </summary>
internal static unsafe class InteropGenerator
{
    private static readonly Logger Log = new("CoreLoader");
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
        VarHarvest.Flush();
        MarkStale();
        _done = false;
        _waitFrames = 1800;   // assets are certainly loaded by now
    }

    /// <summary>Called every frame; does its work once, when builtins are available.</summary>
    public static void Tick()
    {
        if (_done) return;
        ++_waitFrames;
        // Builtins resolve a moment after startup, and some games (Stoneshard)
        // only finish loading their sprites, rooms and sounds seconds after
        // that - scanning too early records an empty asset list. Wait until an
        // asset answers, or give up waiting after ~30 s and generate anyway.
        if (_waitFrames < 1800)
        {
            if (Game.BuiltinCount == 0 || _waitFrames < 120) return;
            if (_waitFrames % 30 != 0 || !AssetsLoaded()) return;
        }
        _done = true;

        try
        {
            var stamp = Stamp();
            var stampFile = Path.Combine(OutputDirectory, ".stamp");
            if (File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
            {
                Status = $"up to date: {OutputDirectory}";
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _degraded = _waitFrames >= 1800 && !AssetsLoaded();
            Generate();
            // A scan that gave up waiting or was cut short is not stamped, so
            // the next launch tries again instead of keeping a partial map.
            if (_degraded) File.Delete(stampFile);
            else File.WriteAllText(stampFile, stamp);
            Status = $"generated{(_degraded ? " (partial - will retry next launch)" : "")} " +
                     $"in {sw.ElapsedMilliseconds} ms: {OutputDirectory}";
            Log.Info($"interop {Status}");
        }
        catch (Exception ex)
        {
            Status = $"failed: {ex.Message}";
            Log.Error("interop generation failed", ex);
        }
    }

    private static bool AssetsLoaded()
    {
        try
        {
            return Game.CallBuiltin("sprite_exists", 0).AsBool || Game.CallBuiltin("room_exists", 0).AsBool;
        }
        catch (GmlException)
        {
            return true;   // cannot tell: do not hold generation back on it
        }
    }

    private static string Stamp()
    {
        var exe = Environment.ProcessPath ?? "";
        var info = new FileInfo(exe);
        return $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Game.Symbols.Count}|{Game.BuiltinCount}|" +
               $"{typeof(InteropGenerator).Assembly.GetName().Version}";
    }

    // ------------------------------------------------------------------ model

    private sealed record ObjectInfo(string Name, int Index, List<(string Member, string Symbol)> Events);

    private static void Generate()
    {
        Directory.CreateDirectory(OutputDirectory);
        string ns = SafeGameName;

        var scripts = Game.Symbols
            .Where(s => s.IsScript && !s.Name.Contains('@'))
            .Select(s => s.Name)
            .ToList();

        var objects = new SortedDictionary<string, ObjectInfo>(StringComparer.Ordinal);
        foreach (var o in SafeEnumerate(GmlObject.All))
            objects[o.Name] = new ObjectInfo(o.Name, o.Index, new());
        foreach (var s in Game.Symbols.Where(s => s.IsObjectEvent))
        {
            if (!SplitEvent(s.Name, out var obj, out var member)) continue;
            if (!objects.TryGetValue(obj, out var info)) objects[obj] = info = new ObjectInfo(obj, -1, new());
            info.Events.Add((member, s.Name));
        }

        var builtins = new List<(string Name, int Arity)>();
        for (int i = 0; i < Game.BuiltinCount; i++)
        {
            var name = Utf8.Read(Loader.Api->BuiltinName(i));
            if (name == null) continue;
            builtins.Add((name, Game.BuiltinArity(name) ?? -1));
        }

        var sprites = EnumerateAssets("sprite_exists", "sprite_get_name");
        var rooms = EnumerateAssets("room_exists", "room_get_name");
        var sounds = EnumerateAssets("audio_exists", "audio_get_name");

        File.WriteAllText(Path.Combine(OutputDirectory, ns + ".Interop.csproj"), Csproj(ns));
        // Every function's start, in address order: each script is scanned only
        // up to the next function, never into its neighbour.
        var starts = Game.Symbols.Select(x => x.Address).Where(a => a != 0).Distinct().OrderBy(a => a).ToArray();
        var arity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in scripts)
        {
            var addr = Game.FindSymbol(s);
            if (addr == 0) continue;
            int at = Array.BinarySearch(starts, addr);
            nint next = at >= 0 && at + 1 < starts.Length ? starts[at + 1] : 0;
            arity[s] = CodeScan.ArgumentCount(addr, next);
        }
        int typed = arity.Values.Count(n => n is >= 1 and <= 8);
        Log.Info($"interop: argument counts read from code for {typed} of {scripts.Count} scripts");

        File.WriteAllText(Path.Combine(OutputDirectory, "Scripts.g.cs"), ScriptsSource(ns, scripts, arity));
        File.WriteAllText(Path.Combine(OutputDirectory, "Objects.g.cs"), ObjectsSource(ns, objects.Values));
        File.WriteAllText(Path.Combine(OutputDirectory, "Builtins.g.cs"), BuiltinsSource(ns, builtins));
        File.WriteAllText(Path.Combine(OutputDirectory, "Assets.g.cs"), AssetsSource(ns, sprites, rooms, sounds));
        WriteCodeMap(scripts, arity, objects.Values, builtins, sprites, rooms, sounds);

        Log.Info($"interop: {scripts.Count} scripts, {objects.Count} objects, " +
                 $"{objects.Values.Sum(o => o.Events.Count)} events, {builtins.Count} builtins, " +
                 $"{sprites.Count} sprites, {rooms.Count} rooms, {sounds.Count} sounds");
    }

    private static IEnumerable<T> SafeEnumerate<T>(Func<IReadOnlyList<T>> f)
    {
        try { return f(); }
        catch (GmlException ex)
        {
            Log.Warning($"interop: skipped part of the asset scan: {ex.Message}");
            _degraded = true;
            return Array.Empty<T>();
        }
    }

    // Asset indices are dense from 0; stop after a run of misses.
    private static List<string> EnumerateAssets(string exists, string getName)
    {
        var names = new List<string>();
        if (Game.BuiltinArity(exists) is null || Game.BuiltinArity(getName) is null)
        {
            Log.Warning($"interop: {exists}/{getName} not in this runtime's registry; skipping");
            return names;
        }
        try
        {
            int misses = 0;
            for (int i = 0; misses < 64 && i < 200_000; i++)
            {
                if (!Game.CallBuiltin(exists, i).AsBool) { misses++; continue; }
                misses = 0;
                names.Add(Game.CallBuiltin(getName, i).ToString());
            }
            if (names.Count == 0)
            {
                var probe = Game.CallBuiltin(exists, 0);
                Log.Warning($"interop: {exists} found nothing; {exists}(0) gave kind {probe.Kind} " +
                            $"(real {probe.Real}, int {probe.Int32})");
            }
        }
        catch (GmlException ex)
        {
            Log.Warning($"interop: {getName} scan stopped: {ex.Message}");
            _degraded = true;
        }
        return names;
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

    private static string ObjectsSource(string ns, IEnumerable<ObjectInfo> objects)
    {
        var sb = new StringBuilder(Header(ns));
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

            if (VarHarvest.Known.TryGetValue(o.Name, out var vars) && vars.Count > 0)
            {
                sb.Append($"        /// <summary>Variables seen on live {o.Name} instances (harvested while playing).</summary>\n");
                sb.Append("        public static class Vars\n        {\n");
                var seen = new HashSet<string>(StringComparer.Ordinal) { "Vars" };
                foreach (var v in vars)
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
                                     List<string> sprites, List<string> rooms, List<string> sounds)
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
                variables = VarHarvest.Known.TryGetValue(o.Name, out var v) ? v.ToArray() : Array.Empty<string>(),
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
