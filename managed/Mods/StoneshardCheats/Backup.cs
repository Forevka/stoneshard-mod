using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// Copies the save folder once per session, before the first cheat touches the
/// game: the player's safety net if a cheat corrupts a character.
/// </summary>
internal static class Backup
{
    private static bool _done;

    public static string Result { get; private set; } = "not run yet (runs before the first cheat)";

    public static string SaveDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoneShard", "characters_v1");

    public static void EnsureOnce(Logger log)
    {
        if (_done) return;
        _done = true;   // one attempt per session either way

        if (!System.IO.Directory.Exists(SaveDir))
        {
            Result = "no save directory found - nothing backed up";
            log.Info($"backup: {Result}");
            return;
        }

        var dst = Path.Combine(Game.LoaderDirectory, "save-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            CopyTree(SaveDir, dst);
            Result = $"saves backed up to {dst}";
            log.Info($"backup: {Result}");
        }
        catch (Exception ex)
        {
            Result = $"backup FAILED: {ex.Message}";
            log.Error($"backup: {Result}");
            return;
        }
        Prune(Path.GetDirectoryName(dst)!, log);
    }

    private const int KeepBackups = 10;

    // One backup per session adds up. Only after a good backup, so the newest
    // copies are never the ones removed; the folder names are timestamps
    // (yyyyMMdd-HHmmss), so ordinal order is age order. Best-effort: a folder
    // that cannot be removed (open in Explorer, say) is simply left for next time.
    private static void Prune(string root, Logger log)
    {
        try
        {
            var old = System.IO.Directory.EnumerateDirectories(root)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(KeepBackups)
                .ToList();
            foreach (var d in old)
            {
                try { System.IO.Directory.Delete(d, recursive: true); }
                catch (Exception ex) { log.Warning($"backup: could not prune {d}: {ex.Message}"); }
            }
            if (old.Count > 0) log.Info($"backup: pruned {old.Count} old backup(s), keeping the newest {KeepBackups}");
        }
        catch (Exception ex)
        {
            log.Warning($"backup: pruning skipped: {ex.Message}");
        }
    }

    internal static void CopyTree(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in System.IO.Directory.EnumerateDirectories(src))
            CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
