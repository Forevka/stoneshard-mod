using CoreLoader.Runtime;

namespace CoreLoader;

/// <summary>
/// New content from files, loaded into the running game: sprites from PNG
/// (sprite_add / sprite_replace) and sounds from OGG (audio_create_stream).
/// Everything a mod adds belongs to it and is released when it unloads or hot
/// reloads, and a replaced sprite gets its original image back.
/// </summary>
/// <remarks>
/// Relative paths are looked up in the folder named after the mod's dll
/// (Mods/MyMod.dll + Mods/MyMod/sprites/x.png), or next to the dll when the
/// mod lives in that folder itself. Game thread only; OnInitialize is the
/// natural place to load. A released added sprite is emptied, not deleted, so
/// an instance still showing it draws nothing rather than crashing the game -
/// but pointing such instances back at a game sprite in OnShutdown is tidier.
/// Content files are not watched: after changing one, reload the mod.
/// </remarks>
public static class Content
{
    private static readonly Logger Log = new("CoreLoader");
    private static readonly List<IOwnedAsset> Owned = new();

    // Replacements of one game sprite, oldest first; see Sprite.Release.
    private static readonly Dictionary<int, List<Sprite>> Replacements = new();

    // Released added sprites are never deleted: an instance, a variable or the
    // game's own code may still name them, and drawing a deleted sprite is a
    // fatal GML error. They shrink to a transparent pixel and are reused by
    // the next AddSprite, so hot reloading does not accumulate them.
    private static readonly Stack<RValue> Retired = new();
    private static readonly HashSet<int> RetiredIndices = new();
    private static string? _placeholder;

    /// <summary>
    /// Adds a sprite from an image file. A horizontal strip is cut into
    /// <paramref name="frames"/> equal frames.
    /// </summary>
    /// <param name="file">A PNG, JPEG or GIF, relative to the mod's folder.</param>
    /// <param name="frames">Frames in the strip.</param>
    /// <param name="xOrigin">Origin, in pixels from the frame's left edge.</param>
    /// <param name="yOrigin">Origin, in pixels from the frame's top edge.</param>
    /// <param name="removeBackground">Makes the colour of the bottom-left pixel transparent.</param>
    /// <param name="smooth">Smooths the edges when <paramref name="removeBackground"/> is on.</param>
    public static Sprite AddSprite(string file, int frames = 1, int xOrigin = 0, int yOrigin = 0,
                                   bool removeBackground = false, bool smooth = false)
    {
        Loader.EnsureGameThread();
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        string path = ResolvePath(file, ".png", ".jpg", ".jpeg", ".gif");

        RValue id;
        if (Retired.TryPop(out var slot))
        {
            RetiredIndices.Remove(IndexOf(slot));
            id = slot;
            if (!TryLoadInto(id, path, frames, removeBackground, smooth, xOrigin, yOrigin))
            {
                Retire(id);
                throw new GmlException($"{Game.Name} could not load {path} as a sprite");
            }
        }
        else
        {
            id = Values.Keep(Game.CallBuiltin("sprite_add", path, frames, removeBackground, smooth, xOrigin, yOrigin));
            if (IndexOf(id) < 0 || !Loaded(id))
                throw new GmlException($"{Game.Name} could not load {path} as a sprite");
        }
        int index = IndexOf(id);

        var s = new Sprite(id, index, path, ModManager.Current, replaces: null);
        Own(s);
        Log.Info($"{OwnerName(s.Owner)} added sprite {index} from {Path.GetFileName(path)} ({frames} frame(s))");
        return s;
    }

    /// <summary>
    /// Replaces the image of one of the game's own sprites, e.g. a reskin. Every
    /// place that draws it shows the new image; unloading the mod restores the
    /// original.
    /// </summary>
    public static Sprite ReplaceSprite(string spriteName, string file, int frames = 1, int xOrigin = 0, int yOrigin = 0,
                                       bool removeBackground = false, bool smooth = false)
    {
        Loader.EnsureGameThread();
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        string path = ResolvePath(file, ".png", ".jpg", ".jpeg", ".gif");
        var target = FindSprite(spriteName);
        int index = IndexOf(target);

        // The original survives as a hidden duplicate, assigned back on release.
        var backup = Game.CallBuiltin("sprite_duplicate", target);
        if (IndexOf(backup) < 0) throw new GmlException($"could not back up {spriteName} before replacing it");
        backup = Values.Keep(backup);

        if (!TryLoadInto(target, path, frames, removeBackground, smooth, xOrigin, yOrigin))
        {
            // Whatever state the failed load left the sprite in, the original comes back.
            try { Game.CallBuiltin("sprite_assign", target, backup); }
            finally { Game.CallBuiltin("sprite_delete", backup); }
            throw new GmlException($"{Game.Name} could not load {path} over {spriteName}");
        }

        var s = new Sprite(Values.Keep(target), index, path, ModManager.Current, replaces: spriteName) { Backup = backup };
        if (!Replacements.TryGetValue(index, out var stack)) Replacements[index] = stack = new List<Sprite>();
        stack.Add(s);
        Own(s);
        Log.Info($"{OwnerName(s.Owner)} replaced {spriteName} with {Path.GetFileName(path)}");
        return s;
    }

    /// <summary>
    /// Adds a sound from an OGG Vorbis file. It is streamed from disk as it
    /// plays, so even long music costs little memory.
    /// </summary>
    public static Sound AddSound(string file)
    {
        Loader.EnsureGameThread();
        string path = ResolvePath(file, ".ogg");

        var id = Game.CallBuiltin("audio_create_stream", path);
        int index = IndexOf(id);
        if (index < 0) throw new GmlException($"{Game.Name} could not open {path} as a sound");

        var s = new Sound(Values.Keep(id), index, path, ModManager.Current);
        Own(s);
        Log.Info($"{OwnerName(s.Owner)} added sound {index} from {Path.GetFileName(path)}");
        return s;
    }

    /// <summary>
    /// The full path of a mod file: absolute paths as given, otherwise next to
    /// the mod's dll or in a folder named after it.
    /// </summary>
    public static string ResolvePath(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (Path.IsPathRooted(file)) return Path.GetFullPath(file);

        var owner = ModManager.Current;
        string baseDir = owner?.Instance.Directory ?? Game.Directory;
        var candidates = new List<string>();
        if (owner != null)
        {
            // The mod's own folder first. Next to the dll only for a mod that has
            // a folder to itself - never the Mods root every mod shares, where
            // another mod's file of the same name could be picked up instead.
            string name = Path.GetFileNameWithoutExtension(owner.Path);
            candidates.Add(Path.Combine(baseDir, name, file));
            bool ownFolder = string.Equals(Path.GetFileName(baseDir.TrimEnd('\\', '/')), name, StringComparison.OrdinalIgnoreCase);
            if (ownFolder) candidates.Add(Path.Combine(baseDir, file));
        }
        else
        {
            candidates.Add(Path.Combine(baseDir, file));
        }

        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        throw new FileNotFoundException($"{file} not found (looked in {string.Join(" and ", candidates.Select(Path.GetDirectoryName).Distinct())})", file);
    }

    private static string ResolvePath(string file, params string[] extensions)
    {
        string path = ResolvePath(file);
        string ext = Path.GetExtension(path);
        if (!extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{Path.GetFileName(path)}: expected {string.Join(", ", extensions)}", nameof(file));
        return path;
    }

    /// <summary>A sprite of the game by name, e.g. "spr_player".</summary>
    internal static RValue FindSprite(string name)
    {
        var id = Game.CallBuiltin("asset_get_index", name);
        if (IndexOf(id) < 0 || !Game.CallBuiltin("sprite_exists", id).AsBool ||
            Game.CallBuiltin("sprite_get_name", id).ToString() != name)
            throw new GmlException($"{Game.Name} has no sprite named '{name}'");
        // Another mod's sprite (or a backup) can vanish under the replacement.
        if (IsModSprite(IndexOf(id)))
            throw new GmlException($"'{name}' was added by a mod; only the game's own sprites can be replaced");
        return id;
    }

    // sprite_add/sprite_replace answer a failed load with -1, an empty sprite,
    // or nothing at all depending on the runtime: a sprite with no frames is a
    // failure too.
    private static bool Loaded(RValue id) =>
        Game.CallBuiltin("sprite_exists", id).AsBool && Game.CallBuiltin("sprite_get_number", id).AsReal >= 1;

    private static bool TryLoadInto(RValue id, string path, int frames, bool removeBackground, bool smooth, int xOrigin, int yOrigin)
    {
        try
        {
            Game.CallBuiltin("sprite_replace", id, path, frames, removeBackground, smooth, xOrigin, yOrigin);
        }
        catch (GmlException)
        {
            return false;
        }
        return Loaded(id);
    }

    /// <summary>Shrinks an added sprite to a transparent pixel and keeps its slot for reuse.</summary>
    internal static void Retire(RValue id)
    {
        _placeholder ??= WritePlaceholder();
        if (!TryLoadInto(id, _placeholder, 1, false, false, 0, 0))
        {
            // Could not empty it: keep the image rather than risk a deleted sprite.
            Log.Warning($"sprite {IndexOf(id)} could not be emptied; it keeps its image");
        }
        Retired.Push(id);
        RetiredIndices.Add(IndexOf(id));
    }

    private static string WritePlaceholder()
    {
        string path = Path.Combine(Game.LoaderDirectory, "empty-sprite.png");
        if (!File.Exists(path))
            File.WriteAllBytes(path, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNgYGBgAAAABQABeqhXUAAAAABJRU5ErkJggg=="));
        return path;
    }

    // Older runtimes answer with a plain index, 2024+ ones with a typed
    // reference whose low 32 bits are the index; -1 is failure in both.
    internal static int IndexOf(RValue id) => id.Kind switch
    {
        RValueKind.Reference => id.Int32,
        _ when id.IsNumber => (int)id.AsReal,
        _ => -1,
    };

    private static void Own(IOwnedAsset a) => Owned.Add(a);

    internal static void Released(IOwnedAsset a) => Owned.Remove(a);

    internal static void ReleaseReplacement(Sprite s)
    {
        if (!Replacements.TryGetValue(s.Index, out var stack)) return;
        int i = stack.IndexOf(s);
        if (i < 0) return;

        try
        {
            if (i == stack.Count - 1)
            {
                // The newest: the image beneath it (the original, or an earlier
                // mod's) comes back.
                try { Game.CallBuiltin("sprite_assign", s.Id, s.Backup); }
                finally { Game.CallBuiltin("sprite_delete", s.Backup); }
            }
            else
            {
                // A later mod's replacement stays on top; it inherits this backup,
                // so unloading it later restores what was there before both.
                var above = stack[i + 1];
                var superseded = above.Backup;
                above.Backup = s.Backup;
                Game.CallBuiltin("sprite_delete", superseded);
            }
        }
        finally
        {
            stack.RemoveAt(i);
            if (stack.Count == 0) Replacements.Remove(s.Index);
        }
    }

    /// <summary>A sprite index that a mod created: an added sprite, or the hidden backup of a replaced one.</summary>
    internal static bool IsModSprite(int index) =>
        RetiredIndices.Contains(index) ||
        Owned.OfType<Sprite>().Any(s => (s.Replaces == null && s.Index == index) ||
                                        (s.Replaces != null && IndexOf(s.Backup) == index));

    /// <summary>Stops a mod's sounds (a faulted mod keeps its assets, but not its music).</summary>
    internal static void StopSounds(LoadedMod owner)
    {
        foreach (var s in Owned.OfType<Sound>().Where(s => s.Owner == owner).ToList())
        {
            try { s.Stop(); }
            catch (Exception ex) { Log.Warning($"stopping {s} of {OwnerName(owner)}: {ex.Message}"); }
        }
    }

    internal static bool IsModSound(int index) => Owned.OfType<Sound>().Any(s => s.Index == index);

    /// <summary>Number of live assets a mod owns.</summary>
    internal static int CountOwned(LoadedMod owner) => Owned.Count(a => a.Owner == owner);

    /// <summary>Releases everything a mod added, newest first. Game thread.</summary>
    internal static void RemoveOwner(LoadedMod owner)
    {
        for (int i = Owned.Count - 1; i >= 0; i--)
        {
            if (i >= Owned.Count || Owned[i].Owner != owner) continue;
            var a = Owned[i];
            try { a.Dispose(); }
            catch (Exception ex) { Log.Error($"releasing {a} of {OwnerName(owner)} failed", ex); Owned.Remove(a); }
        }
    }

    private static string OwnerName(LoadedMod? m) => m?.Instance.Info.Name ?? "CoreLoader";
}

internal interface IOwnedAsset : IDisposable
{
    LoadedMod? Owner { get; }
}

/// <summary>A sprite a mod added or replaced. Use <see cref="Id"/> wherever GML takes a sprite.</summary>
public sealed class Sprite : IOwnedAsset
{
    internal Sprite(RValue id, int index, string file, LoadedMod? owner, string? replaces)
    {
        Id = id;
        Index = index;
        File = file;
        Owner = owner;
        Replaces = replaces;
    }

    /// <summary>The sprite as the runtime identifies it (a number, or a reference in newer runtimes).</summary>
    public RValue Id { get; }

    /// <summary>The asset index.</summary>
    public int Index { get; }

    /// <summary>The image file it was loaded from.</summary>
    public string File { get; }

    /// <summary>The game sprite this replaces, or null for a new sprite.</summary>
    public string? Replaces { get; }

    public bool IsReleased { get; private set; }

    internal LoadedMod? Owner { get; }
    LoadedMod? IOwnedAsset.Owner => Owner;

    internal RValue Backup { get; set; }

    public int Frames => (int)Game.CallBuiltin("sprite_get_number", Id).AsReal;
    public int Width => (int)Game.CallBuiltin("sprite_get_width", Id).AsReal;
    public int Height => (int)Game.CallBuiltin("sprite_get_height", Id).AsReal;

    /// <summary>Moves the origin (the point drawn at x, y).</summary>
    public void SetOrigin(int x, int y) => Game.CallBuiltin("sprite_set_offset", Id, x, y);

    /// <summary>
    /// Draws one frame. Only inside a draw event, such as a
    /// <see cref="GameDraw.OnGui"/> handler.
    /// </summary>
    /// <param name="x">Where the origin goes.</param>
    /// <param name="y">Where the origin goes.</param>
    /// <param name="frame">Frame; wraps, and fractions round down, so a growing counter animates.</param>
    /// <param name="xScale">Horizontal scale; negative mirrors.</param>
    /// <param name="yScale">Vertical scale; negative flips.</param>
    /// <param name="rotation">Degrees, anticlockwise.</param>
    /// <param name="colour">Blend colour as 0xBBGGRR (GameMaker order); white leaves it unchanged.</param>
    /// <param name="alpha">0 transparent to 1 opaque.</param>
    public void Draw(double x, double y, double frame = 0, double xScale = 1, double yScale = 1,
                     double rotation = 0, int colour = 0xFFFFFF, double alpha = 1)
    {
        if (IsReleased) throw new ObjectDisposedException(nameof(Sprite));
        Game.CallBuiltin("draw_sprite_ext", Id, frame, x, y, xScale, yScale, rotation, colour, alpha);
    }

    /// <summary>
    /// Releases the sprite: a replaced one gets its original image back; an
    /// added one becomes an empty (transparent) sprite whose slot the next
    /// AddSprite reuses, so anything still showing it draws nothing instead
    /// of failing.
    /// </summary>
    public void Dispose()
    {
        if (IsReleased) return;
        Loader.EnsureGameThread();
        IsReleased = true;
        try
        {
            if (Replaces != null) Content.ReleaseReplacement(this);
            else Content.Retire(Id);
        }
        finally
        {
            Content.Released(this);
        }
    }

    public override string ToString() => Replaces != null ? $"sprite {Replaces} (replaced)" : $"sprite {Index}";
}

/// <summary>A sound a mod added. Use <see cref="Id"/> wherever GML takes a sound.</summary>
public sealed class Sound : IOwnedAsset
{
    internal Sound(RValue id, int index, string file, LoadedMod? owner)
    {
        Id = id;
        Index = index;
        File = file;
        Owner = owner;
    }

    public RValue Id { get; }
    public int Index { get; }
    public string File { get; }
    public bool IsReleased { get; private set; }
    internal LoadedMod? Owner { get; }
    LoadedMod? IOwnedAsset.Owner => Owner;

    /// <summary>Starts playing; returns the playing instance (for audio_* builtins).</summary>
    /// <param name="loop">Repeat until stopped.</param>
    /// <param name="priority">Higher wins when the game runs out of voices.</param>
    public RValue Play(bool loop = false, int priority = 0)
    {
        if (IsReleased) throw new ObjectDisposedException(nameof(Sound));
        return Game.CallBuiltin("audio_play_sound", Id, priority, loop);
    }

    /// <summary>Stops every playing instance of this sound.</summary>
    public void Stop()
    {
        if (!IsReleased) Game.CallBuiltin("audio_stop_sound", Id);
    }

    public bool IsPlaying => !IsReleased && Game.CallBuiltin("audio_is_playing", Id).AsBool;

    /// <summary>Stops the sound and closes its stream.</summary>
    public void Dispose()
    {
        if (IsReleased) return;
        Loader.EnsureGameThread();
        IsReleased = true;
        Content.Released(this);
        Game.CallBuiltin("audio_stop_sound", Id);
        Game.CallBuiltin("audio_destroy_stream", Id);
    }

    public override string ToString() => $"sound {Index}";
}
