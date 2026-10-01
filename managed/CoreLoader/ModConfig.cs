using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoreLoader;

/// <summary>
/// A mod's persistent settings: a flat JSON object stored as
/// <c>Mods/&lt;AssemblyName&gt;.json</c>. Changes are written shortly after they
/// are made (a dragged slider saves once, not every frame) and on shutdown,
/// through a temporary file, so a crash never leaves a truncated file behind.
/// Hand-editing the file while the game is closed works too.
/// </summary>
public sealed class ModConfig
{
    private const long SaveDelayMs = 1000;

    private static readonly List<ModConfig> All = new();

    private readonly string _path;
    private readonly Logger _log;
    private readonly JsonObject _values;
    private long _dirtySince = -1;

    internal ModConfig(string path, Logger log)
    {
        _path = path;
        _log = log;
        _values = Load(path, log);
        lock (All) All.Add(this);
    }

    /// <summary>Where the settings live.</summary>
    public string Path => _path;

    // A value set in this session keeps its CLR type (an int set stays an int,
    // which will not read back as a double); one read from the file is JSON
    // and converts freely. So any number reads as a double here.
    public double Get(string key, double fallback) =>
        _values[key] is JsonValue v
            ? v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<float>(out var f) ? f
            : fallback
            : fallback;

    public float Get(string key, float fallback) => (float)Get(key, (double)fallback);

    // Through the double reader, so 2.0 (a choice ModSettings stores) reads as 2.
    public int Get(string key, int fallback) =>
        _values[key] is JsonValue ? (int)Math.Round(Get(key, (double)fallback)) : fallback;

    public bool Get(string key, bool fallback) =>
        _values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public string Get(string key, string fallback) =>
        _values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    public void Set(string key, double value) => Put(key, JsonValue.Create(value));
    public void Set(string key, float value) => Put(key, JsonValue.Create((double)value));
    public void Set(string key, int value) => Put(key, JsonValue.Create(value));
    public void Set(string key, bool value) => Put(key, JsonValue.Create(value));
    public void Set(string key, string value) => Put(key, JsonValue.Create(value));

    /// <summary>Writes pending changes now.</summary>
    public void Save()
    {
        if (_dirtySince < 0) return;
        _dirtySince = -1;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);   // atomic replace on NTFS
        }
        catch (Exception ex)
        {
            _log.Warning($"could not save settings to {_path}: {ex.Message}");
        }
    }

    private void Put(string key, JsonNode? node)
    {
        if (_values[key]?.ToJsonString() == node?.ToJsonString()) return;
        _values[key] = node;
        if (_dirtySince < 0) _dirtySince = Environment.TickCount64;
    }

    /// <summary>A reloaded mod gets a fresh config; the old one must stop being flushed.</summary>
    internal static void Unregister(ModConfig c)
    {
        lock (All) All.Remove(c);
    }

    /// <summary>Called by the loader every frame; saves configs that settled.</summary>
    internal static void FlushSettled()
    {
        long now = Environment.TickCount64;
        lock (All)
            foreach (var c in All)
                if (c._dirtySince >= 0 && now - c._dirtySince >= SaveDelayMs) c.Save();
    }

    /// <summary>Called on shutdown: everything pending is written.</summary>
    internal static void FlushAll()
    {
        lock (All)
            foreach (var c in All) c.Save();
    }

    // Hand-edited files get the benefit of the doubt: comments and trailing
    // commas are accepted.
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static JsonObject Load(string path, Logger log)
    {
        try
        {
            if (!File.Exists(path)) return new JsonObject();
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) is JsonObject o) return o;
            throw new JsonException("the file does not hold a JSON object");
        }
        catch (Exception ex)
        {
            // Starting from empty settings means the next save overwrites the
            // file: keep what the user wrote beside it first.
            string bad = path + ".bad";
            try { File.Copy(path, bad, overwrite: true); } catch (Exception) { bad = "(could not keep a copy)"; }
            log.Warning($"ignoring unreadable settings {path}: {ex.Message} - the file was kept as {bad}");
        }
        return new JsonObject();
    }
}
