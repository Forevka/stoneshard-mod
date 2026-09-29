using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace StoneshardCheats;

/// <summary>
/// Importing a save folder from another machine. Pure file work: no GML, safe on
/// any thread.
///
/// Copying the files is NOT enough, and this is the whole reason the feature has
/// to exist rather than being a drag-and-drop. Every save file is
/// <c>zlib( &lt;json text&gt; &lt;32 hex md5&gt; &lt;NUL&gt; )</c>, and the md5 is salted with the
/// file's own FOLDER PATH: <c>md5(jsonText + "stOne!" + "!".join(path from characters_v1 down) + "!shArd")</c>.
/// So <c>character_3/autosave_1/data.sav</c> and <c>character_1/autosave_1/data.sav</c> have
/// different checksums for byte-identical content, and a save dropped into a
/// different slot number fails its own integrity check. Importing therefore means
/// decompress, re-hash against the NEW path, recompress, per file.
///
/// (Scheme origin: MikaBuchholz/stoneshard-editor. tools/checkcksum.py verifies it
/// against every file in a live save directory rather than trusting it.)
///
/// There is no registry to update: characters.map holds only {lastCharacter,
/// lastSave}, so the game finds characters by scanning for character_N folders.
/// An import just has to produce well-formed folders.
/// </summary>
internal static class SaveMigration
{
    /// <summary>One character folder found in the source.</summary>
    /// <param name="Dir">Absolute path of the source character_N folder.</param>
    /// <param name="Name">nameKey from character.map.</param>
    /// <param name="Saves">Save slots inside it.</param>
    /// <param name="Files">.map/.sav files that will need re-signing.</param>
    /// <param name="Ok">Importable.</param>
    /// <param name="Note">Why it was rejected, when not <paramref name="Ok"/>.</param>
    public sealed record Character(string Dir, string Name, int Saves, int Files, bool Ok, string Note);

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    // ------------------------------------------------------------------ format

    /// <summary>The md5 of <paramref name="data"/> as 32 lowercase hex digits.</summary>
    public static string Md5Hex(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

    /// <summary>
    /// "stOne!characters_v1" + "!" + each component + "!shArd". The components
    /// are the containing folders below characters_v1 (the slot first), never the
    /// file name. Folder names are ASCII in practice (character_N, autosave_1...),
    /// so the encoding only matters for hand-named folders; UTF-8 is what the
    /// game's own strings use.
    /// </summary>
    public static string SaltFor(IEnumerable<string> components)
    {
        var s = new StringBuilder("stOne!characters_v1");
        foreach (var c in components) s.Append('!').Append(c);
        s.Append("!shArd");
        return s.ToString();
    }

    /// <summary>
    /// The JSON body of a packed save, without its checksum. Null for anything
    /// that is not a save file, which is how junk in the source folder is rejected
    /// rather than copied blindly. The old checksum is not checked: it was made
    /// for a path this file no longer lives at.
    /// </summary>
    public static byte[]? UnpackJson(byte[] packed)
    {
        byte[] plain;
        try
        {
            using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var ms = new MemoryStream(packed.Length * 8);
            z.CopyTo(ms);
            plain = ms.ToArray();
        }
        catch (InvalidDataException) { return null; }

        int len = plain.Length;
        while (len > 0 && plain[len - 1] == 0) len--;
        if (len < 33) return null;
        return plain.AsSpan(0, len - 32).ToArray();
    }

    /// <summary><paramref name="json"/> signed for the folder it is being written TO, and packed.</summary>
    public static byte[] Pack(byte[] json, IEnumerable<string> components)
    {
        var salt = Encoding.UTF8.GetBytes(SaltFor(components));
        var salted = new byte[json.Length + salt.Length];
        json.CopyTo(salted, 0);
        salt.CopyTo(salted, json.Length);
        var sum = Encoding.ASCII.GetBytes(Md5Hex(salted));

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(json);
            z.Write(sum);
            z.WriteByte(0);
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------- files

    private static byte[]? ReadSaveJson(string path)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return raw.Length == 0 ? null : UnpackJson(raw);
    }

    // Written beside the target and moved over it only once complete: a crash or
    // a full disk halfway through leaves the old file intact rather than a torn one.
    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(data);
                f.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static bool IsSaveFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext == ".map" || ext == ".sav";
    }

    private static string NameFromCharacterMap(string charDir)
    {
        var bytes = ReadSaveJson(Path.Combine(charDir, "character.map"));
        if (bytes == null) return "";
        var json = Encoding.UTF8.GetString(bytes);

        // Deliberately not a JSON parser: one string field is wanted and the file
        // is machine-generated, so the key is found literally.
        const string key = "\"nameKey\":";
        int k = json.IndexOf(key, StringComparison.Ordinal);
        if (k < 0) return "";
        int q1 = json.IndexOf('"', k + key.Length);
        if (q1 < 0) return "";
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return "";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static Character Inspect(string dir)
    {
        var name = NameFromCharacterMap(dir);
        if (name.Length == 0) return new Character(dir, name, 0, 0, false, "no readable character.map");

        int files = 0, saves = 0;
        try
        {
            files = Directory.EnumerateFiles(dir, "*", Recursive).Count(IsSaveFile);
            saves = Directory.EnumerateDirectories(dir).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (saves == 0) return new Character(dir, name, saves, files, false, "no save slots inside");
        return new Character(dir, name, saves, files, true, "");
    }

    // The lowest character_N not already present, so nothing is ever overwritten.
    private static int FreeSlot(string target, int from)
    {
        for (int i = from; i < 10000; ++i)
            if (!Path.Exists(Path.Combine(target, $"character_{i}"))) return i;
        return -1;
    }

    // -------------------------------------------------------------------- scan

    /// <summary>
    /// Accepts either a characters_v1-style folder of character_N subfolders, or a
    /// single character_N folder handed over on its own.
    /// </summary>
    public static (List<Character> Found, string Status) Scan(string source)
    {
        var found = new List<Character>();
        if (string.IsNullOrWhiteSpace(source)) return (found, "Pick a folder to import from.");
        if (!Directory.Exists(source)) return (found, $"Not a folder: {source}");

        try
        {
            if (Path.Exists(Path.Combine(source, "character.map")))
                found.Add(Inspect(source));
            else
                foreach (var d in Directory.EnumerateDirectories(source))
                    if (Path.GetFileName(d).StartsWith("character_", StringComparison.Ordinal))
                        found.Add(Inspect(d));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (found, $"Could not read {source}: {ex.Message}");
        }

        int ok = found.Count(c => c.Ok);
        string status =
            found.Count == 0 ? "No character folders here. Point at a characters_v1 folder or a character_N folder." :
            ok == 0 ? $"Found {found.Count} folder(s), but none are importable." :
            $"Found {ok} character{(ok == 1 ? "" : "s")} ready to import.";
        return (found, status);
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Copies every importable character into a FREE slot number under
    /// <paramref name="target"/> and re-signs it. Existing characters are never
    /// touched or overwritten. A character that fails is removed again, but the
    /// ones imported before it stay.
    /// </summary>
    public static (bool Ok, string Status) Import(IReadOnlyList<Character> found, string target, Action<string> log)
    {
        if (string.IsNullOrEmpty(target)) return (false, "Could not locate the game's save folder.");
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"Could not create {target}: {ex.Message}");
        }

        int imported = 0, slot = 1;
        foreach (var c in found)
        {
            if (!c.Ok) continue;

            slot = FreeSlot(target, slot);
            if (slot < 0) return (false, "No free character slot.");

            var slotName = $"character_{slot}";
            var dest = Path.Combine(target, slotName);

            try
            {
                CopyCharacter(c.Dir, dest, slotName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Never leave a half-written character behind.
                try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                log($"saves: import of {c.Name} failed: {ex.Message}");
                return (false, $"Failed while importing {c.Name} - nothing was left behind.");
            }

            log($"saves: imported {c.Name} -> {slotName} ({c.Files} file(s) re-signed)");
            ++imported;
            ++slot;
        }

        if (imported == 0) return (false, "Nothing to import.");
        return (true, $"Imported {imported} character{(imported == 1 ? "" : "s")}. Restart the game to see them.");
    }

    private static void CopyCharacter(string src, string dest, string slotName)
    {
        // Enumerated up front: the walk must not see files it is itself writing,
        // should someone point the source at the target.
        var files = Directory.EnumerateFiles(src, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
        }).ToList();

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(src, file);
            var output = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            if (!IsSaveFile(file))
            {
                // preview.png and anything else is not signed: copy verbatim.
                File.Copy(file, output, overwrite: true);
                continue;
            }

            var json = ReadSaveJson(file) ?? throw new InvalidDataException($"{rel} is not a readable save file");

            // The salt is the CONTAINING DIRECTORY relative to characters_v1,
            // using the NEW slot name, which is the entire point of re-signing.
            // The file name is NOT part of it:
            //     character_2/autosave_1/data.sav -> stOne!characters_v1!character_2!autosave_1!shArd
            var components = new List<string> { slotName };
            var relDir = Path.GetDirectoryName(rel);
            if (!string.IsNullOrEmpty(relDir))
                components.AddRange(relDir.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries));

            WriteAtomic(output, Pack(json, components));
        }
    }
}
