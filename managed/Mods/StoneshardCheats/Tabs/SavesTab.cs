using System.Runtime.InteropServices;
using CoreLoader;

namespace StoneshardCheats;

/// <summary>
/// Imports save folders copied from another machine (see <see cref="SaveMigration"/>
/// for why a plain copy does not work). Scanning, importing and the folder picker
/// all run off the game thread; the tab only polls their state, which is guarded
/// by <see cref="_gate"/>.
/// </summary>
internal sealed class SavesTab : Tab
{
    private readonly object _gate = new();
    private string _source = "";
    private int _sourceSeq;
    private string _status = "Pick a folder to import from.";
    private IReadOnlyList<SaveMigration.Character> _found = [];
    private bool _working;
    private int _browsing;

    // Game-thread only: the editable copy of the source path, re-synced when the
    // picker delivers a new one.
    private string _typed = "";
    private int _typedSeq;

    public override string Name => "Saves";

    private static string Target => Backup.SaveDir;

    public override void Draw()
    {
        string source, status;
        IReadOnlyList<SaveMigration.Character> found;
        bool working;
        lock (_gate)
        {
            source = _source;
            status = _status;
            found = _found;
            working = _working;
            if (_typedSeq != _sourceSeq) { _typed = _source; _typedSeq = _sourceSeq; }
        }
        bool browsing = Volatile.Read(ref _browsing) != 0;

        UI.TextWrapped(
            "Import save folders copied from another machine. Every save file is signed with " +
            "an MD5 salted by its own FOLDER PATH, so a save dropped into a different slot " +
            "number fails its own checksum - each file is decompressed, re-signed for its new " +
            "path, and recompressed.");
        UI.Spacing();

        UI.BeginDisabled(browsing || working);
        if (UI.Button("Select save folder...", 220f)) Browse();
        UI.EndDisabled();
        UI.SameLine();
        UI.TextDisabled(browsing ? "(dialog open)" : "a Windows folder picker");

        UI.SetNextItemWidth(-90f);
        UI.InputTextWithHint("##source", "or paste a folder path here and press Scan", ref _typed, 1024);
        UI.SameLine();
        UI.BeginDisabled(browsing || working);
        if (UI.Button("Scan###scan", 80f)) SetSourceAndScan(_typed.Trim().Trim('"'));
        UI.EndDisabled();

        UI.Text($"Source: {(source.Length == 0 ? "(none selected)" : source)}");
        UI.Text($"Target: {Target}");

        UI.Spacing();
        UI.SeparatorText("Scan");
        UI.TextWrapped(working ? $"{status}  (working...)" : status);

        if (found.Count > 0)
        {
            UI.BeginChild("##found", 160f, true);
            foreach (var c in found)
            {
                if (c.Ok)
                    UI.TextColored(0.55f, 0.90f, 0.55f,
                        $"{c.Name,-20} {c.Saves} save slot(s), {c.Files} file(s) to re-sign");
                else
                    UI.TextColored(0.95f, 0.65f, 0.55f,
                        $"{Path.GetFileName(c.Dir),-20} skipped - {c.Note}");
            }
            UI.EndChild();
        }

        UI.Spacing();
        int n = found.Count(c => c.Ok);

        UI.BeginDisabled(n == 0 || working);
        if (UI.Button("Import", 220f))
            Actions.Run($"import {n} character(s) from {source} into {Target}", () => StartImport(found));
        UI.EndDisabled();
        UI.SameLine();
        UI.TextDisabled(n == 0
            ? "select a folder with characters first"
            : "copies into free slots - existing characters are untouched");

        UI.Spacing();
        UI.TextDisabled(
            "Imported characters go into the lowest FREE character_N slots, so nothing you " +
            "already have is overwritten. Restart the game afterwards to pick them up.");
    }

    // ------------------------------------------------------------------ work

    private void SetStatus(string status)
    {
        lock (_gate) _status = status;
        Actions.Log.Info($"saves: {status}");
    }

    private void SetSourceAndScan(string source)
    {
        lock (_gate)
        {
            if (_working) return;
            _source = source;
            _sourceSeq++;
            _found = [];
            _working = true;
        }
        Task.Run(() =>
        {
            List<SaveMigration.Character> found = [];
            string status;
            try { (found, status) = SaveMigration.Scan(source); }
            catch (Exception ex) { status = $"Scan failed: {ex.Message}"; }
            lock (_gate) { _found = found; _status = status; _working = false; }
            Actions.Log.Info($"saves: {status}");
        });
    }

    // Runs inside Actions.Run, so the saves have been backed up before the first
    // file is written. The work itself is on a Task; its outcome reaches the
    // action log from the game thread once it is done.
    private void StartImport(IReadOnlyList<SaveMigration.Character> found)
    {
        lock (_gate)
        {
            if (_working) throw new InvalidOperationException("a scan or import is already running");
            _working = true;
            _status = "Importing...";
        }
        Task.Run(() =>
        {
            bool ok;
            string status;
            try { (ok, status) = SaveMigration.Import(found, Target, Actions.Log.Info); }
            catch (Exception ex) { (ok, status) = (false, $"Import failed: {ex.Message}"); }
            lock (_gate) { _status = status; _working = false; }
            Actions.Log.Info($"saves: {status}");
            Game.RunOnGameThread(() => Actions.Report(status, ok));
        });
    }

    // --------------------------------------------------------------- picker

    // On a thread of its own, deliberately. The overlay draws on the game's own
    // thread inside the Present hook, so a modal Windows dialog opened from there
    // would block that thread and freeze the game behind its own dialog. The UI
    // polls instead.
    private void Browse()
    {
        if (Interlocked.Exchange(ref _browsing, 1) != 0) return;   // one dialog at a time
        var t = new Thread(() =>
        {
            try
            {
                var picked = FolderPicker.Pick("Select the save folder to import");
                if (!string.IsNullOrEmpty(picked)) SetSourceAndScan(picked);
            }
            catch (Exception ex)
            {
                SetStatus($"The folder picker failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _browsing, 0);
            }
        })
        {
            IsBackground = true,
            Name = "StoneshardCheats folder picker",
        };
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);   // the dialog requires STA
        t.Start();
    }

    /// <summary>
    /// IFileOpenDialog with FOS_PICKFOLDERS, called through its vtable. Mods load
    /// into a collectible load context, where [ComImport] interfaces are not
    /// allowed, so the COM calls are made by slot number instead.
    /// </summary>
    private static unsafe class FolderPicker
    {
        private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        private static readonly Guid IidFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");

        private const uint CoinitApartmentThreaded = 0x2, CoinitDisableOle1Dde = 0x4;
        private const uint ClsctxInprocServer = 0x1;
        private const uint FosPickFolders = 0x20, FosForceFileSystem = 0x40;
        private const uint SigdnFileSysPath = 0x80058000;

        // Vtable slots: IUnknown is 0-2, IModalWindow::Show is 3, then IFileDialog.
        private const int SlotRelease = 2, SlotShow = 3, SlotSetOptions = 9, SlotGetOptions = 10,
                          SlotSetTitle = 17, SlotGetResult = 20;
        // IShellItem: IUnknown, BindToHandler, GetParent, GetDisplayName.
        private const int SlotGetDisplayName = 5;

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coinit);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);
        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr obj);

        private static void** Vtbl(IntPtr obj) => *(void***)obj;

        private static void Release(IntPtr obj) =>
            ((delegate* unmanaged<IntPtr, uint>)Vtbl(obj)[SlotRelease])(obj);

        /// <summary>The chosen folder, or null when cancelled or unavailable. Blocks: never call on the game thread.</summary>
        public static string? Pick(string title)
        {
            int init = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded | CoinitDisableOle1Dde);
            try
            {
                if (CoCreateInstance(ClsidFileOpenDialog, IntPtr.Zero, ClsctxInprocServer, IidFileOpenDialog,
                        out var dlg) < 0 || dlg == IntPtr.Zero)
                    return null;
                try
                {
                    var vt = Vtbl(dlg);
                    uint opts = 0;
                    ((delegate* unmanaged<IntPtr, uint*, int>)vt[SlotGetOptions])(dlg, &opts);
                    ((delegate* unmanaged<IntPtr, uint, int>)vt[SlotSetOptions])(dlg, opts | FosPickFolders | FosForceFileSystem);
                    fixed (char* t = title)
                        ((delegate* unmanaged<IntPtr, char*, int>)vt[SlotSetTitle])(dlg, t);

                    // Cancel comes back as a failure HRESULT, like any other error.
                    if (((delegate* unmanaged<IntPtr, IntPtr, int>)vt[SlotShow])(dlg, IntPtr.Zero) < 0) return null;

                    IntPtr item;
                    if (((delegate* unmanaged<IntPtr, IntPtr*, int>)vt[SlotGetResult])(dlg, &item) < 0 || item == IntPtr.Zero)
                        return null;
                    try
                    {
                        IntPtr path;
                        if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Vtbl(item)[SlotGetDisplayName])(
                                item, SigdnFileSysPath, &path) < 0 || path == IntPtr.Zero)
                            return null;
                        try { return Marshal.PtrToStringUni(path); }
                        finally { CoTaskMemFree(path); }
                    }
                    finally { Release(item); }
                }
                finally { Release(dlg); }
            }
            finally
            {
                if (init >= 0) CoUninitialize();
            }
        }
    }
}
