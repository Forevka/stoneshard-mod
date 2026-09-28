using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoreLoader;

/// <summary>
/// A mod's persistent settings: a flat JSON object stored as
/// <c>Mods/&lt;AssemblyName&gt;.json</c>. Values are written back on every change,
/// so settings survive a crash as well as a normal exit. Hand-editing the file
/// while the game is closed works too.
/// </summary>
public sealed class ModConfig
{
    private readonly string _path;
    private readonly Logger _log;
    private readonly JsonObject _values;

    internal ModConfig(string path, Logger log)
    {
        _path = path;
        _log = log;
        _values = Load(path, log);
    }

    /// <summary>Where the settings live.</summary>
    public string Path => _path;

    public double Get(string key, double fallback) =>
        _values[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    public float Get(string key, float fallback) => (float)Get(key, (double)fallback);

    public int Get(string key, int fallback) =>
        _values[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    public bool Get(string key, bool fallback) =>
        _values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public string Get(string key, string fallback) =>
        _values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    public void Set(string key, double value) => Put(key, JsonValue.Create(value));
    public void Set(string key, float value) => Put(key, JsonValue.Create((double)value));
    public void Set(string key, int value) => Put(key, JsonValue.Create(value));
    public void Set(string key, bool value) => Put(key, JsonValue.Create(value));
    public void Set(string key, string value) => Put(key, JsonValue.Create(value));

    private void Put(string key, JsonNode? node)
    {
        if (_values[key]?.ToJsonString() == node?.ToJsonString()) return;
        _values[key] = node;
        Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.Warning($"could not save settings to {_path}: {ex.Message}");
        }
    }

    private static JsonObject Load(string path, Logger log)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) return o;
        }
        catch (Exception ex)
        {
            log.Warning($"ignoring unreadable settings {path}: {ex.Message}");
        }
        return new JsonObject();
    }
}
