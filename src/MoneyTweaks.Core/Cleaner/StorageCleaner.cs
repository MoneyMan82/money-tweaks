using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Cleaner;

public enum StorageKind { Files, RecycleBin, DeliveryOptimization, ComponentStore }

/// <summary>An approved location. A file root (a single file) is allowed too. Ages and sizes filter what counts as junk.</summary>
public sealed record ScanRoot(string Path, string Pattern = "*", bool Recursive = true, int MinAgeDays = 0, long MinBytes = 0, long MaxBytes = long.MaxValue);

public sealed record StorageCategory(string Id, string Name, string Why, StorageKind Kind, bool DefaultSelected, bool NeedsAdmin, IReadOnlyList<ScanRoot> Roots, string Note = "", bool Optional = false);

public sealed record CandidateFile(string Path, long Bytes, DateTime LastWriteUtc, string Root);

public sealed class CategoryScan
{
    public required StorageCategory Category { get; init; }
    public List<CandidateFile> Files { get; } = [];
    public long Bytes { get; set; }
    public int Count { get; set; }
    /// <summary>Why files in these locations were left out of the result (too new, system file, protected path, link…).</summary>
    public Dictionary<string, int> Skipped { get; } = [];
    public List<string> Locations { get; } = [];
    public bool Partial { get; set; }
    public string? Note { get; set; }
    /// <summary>For Windows mechanisms the size is an estimate from Windows (or unknown).</summary>
    public bool Estimated { get; set; }
    public void Skip(string why, int n = 1) => Skipped[why] = Skipped.GetValueOrDefault(why) + n;
}

public sealed class CategoryResult
{
    public required StorageCategory Category { get; init; }
    public int Deleted { get; set; }
    public long DeletedBytes { get; set; }
    public int Quarantined { get; set; }
    public long QuarantinedBytes { get; set; }
    public int Failed { get; set; }
    public Dictionary<string, int> Skipped { get; } = [];
    public string? Note { get; set; }
    public void Skip(string why) => Skipped[why] = Skipped.GetValueOrDefault(why) + 1;
}

public sealed record StorageCleanReport(DateTime At, IReadOnlyList<CategoryResult> Results, IReadOnlyDictionary<string, (long before, long after)> FreeSpace, bool Cancelled, string? QuarantineId)
{
    public long FreedBytes => Results.Sum(r => r.DeletedBytes);
    public long QuarantinedBytes => Results.Sum(r => r.QuarantinedBytes);
}

public sealed record QuarantineItem(string Original, string Stored, long Bytes);
public sealed record QuarantineSession(string Id, DateTime At, List<QuarantineItem> Items)
{
    public long Bytes => Items.Sum(i => i.Bytes);
}

/// <summary>
/// Storage Cleaner. Scans only approved locations for verifiably disposable files and cleans with Windows' own supported
/// mechanisms where those exist. Never: hidden-means-junk, personal folders, Downloads, browser profiles, credentials,
/// Installer cache, WinSxS (only DISM touches the component store), restore points, Prefetch, files in use, links or
/// junctions (never followed, never deleted), or anything outside the approved root it was found under.
/// </summary>
public static class StorageCleaner
{
    private static string Env(string v) => Environment.ExpandEnvironmentVariables(v);
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private const long Big = 50L * 1024 * 1024;

    public static IReadOnlyList<StorageCategory> Categories() =>
    [
        new("user-temp", "Your temporary files", "Apps put files here while they run and are meant to remove them afterwards. Only files untouched for 2+ days are included, and files still in use are skipped.",
            StorageKind.Files, true, false, [new(Path.GetTempPath(), MinAgeDays: 2, MaxBytes: Big - 1)]),
        new("windows-temp", "Windows temporary files", "Windows' own temp folder. Only files untouched for 2+ days; files in use are skipped.",
            StorageKind.Files, true, true, [new(Path.Combine(Win, "Temp"), MinAgeDays: 2, MaxBytes: Big - 1)]),
        new("large-temp", "Large leftover temp files", "Files over 50 MB left in temp folders for a day or more: usually leftover installers and extracted archives.",
            StorageKind.Files, true, false, [new(Path.GetTempPath(), MinAgeDays: 1, MinBytes: Big), new(Path.Combine(Win, "Temp"), MinAgeDays: 1, MinBytes: Big)]),
        new("crash-dumps", "Old crash dumps and error reports", "Memory dumps and Windows Error Reporting files older than 14 days. They're only useful for diagnosing a crash; recent ones are kept in case you need them.",
            StorageKind.Files, true, false,
            [new(Path.Combine(Local, "CrashDumps"), "*.dmp", MinAgeDays: 14), new(Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive"), MinAgeDays: 14),
             new(Path.Combine(Local, @"Microsoft\Windows\WER\ReportQueue"), MinAgeDays: 14), new(Env(@"%ProgramData%\Microsoft\Windows\WER\ReportArchive"), MinAgeDays: 14),
             new(Env(@"%ProgramData%\Microsoft\Windows\WER\ReportQueue"), MinAgeDays: 14), new(Path.Combine(Win, "Minidump"), "*.dmp", MinAgeDays: 14),
             new(Path.Combine(Win, "MEMORY.DMP"), MinAgeDays: 14), new(Path.Combine(Win, "LiveKernelReports"), "*.dmp", MinAgeDays: 14)],
            "Some locations need administrator rights."),
        new("servicing-logs", "Old Windows servicing logs", "Rotated Component-Based Servicing log archives (CbsPersist_*) older than 30 days. The current log is never touched.",
            StorageKind.Files, true, true, [new(Path.Combine(Win, @"Logs\CBS"), "CbsPersist_*", false, 30)]),
        new("thumbnails", "Thumbnail cache", "File Explorer's picture/video thumbnails. Windows rebuilds them when folders are opened (briefly slower the first time). Explorer usually has them open, so in-use files are skipped.",
            StorageKind.Files, true, false, [new(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", false)]),
        new("delivery-optimization", "Delivery Optimization cache", "Update pieces Windows keeps to share with other PCs. Cleaned with Microsoft's own Delete-DeliveryOptimizationCache command.",
            StorageKind.DeliveryOptimization, true, true, [new(Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))]),
        new("component-store", "Windows Update cleanup (component store)", "Superseded update components, cleaned by Windows itself with DISM /StartComponentCleanup (the supported way; WinSxS is never touched directly). Installed updates stay uninstallable.",
            StorageKind.ComponentStore, false, true, [], "Can take 10–30 minutes and can't be stopped safely once started. Use Analyze to see whether Windows recommends it."),
        new("recycle-bin", "Recycle Bin", "Files you already deleted. Emptying is permanent, so it's never selected by default and asks separately.",
            StorageKind.RecycleBin, false, false, []),
        new("shader-caches", "GPU shader caches (optional)", "DirectX/NVIDIA/AMD compiled shaders. Safe to delete, but games rebuild them: expect stutter the first time you play each game afterwards. Only useful after a driver change or if a cache is corrupt.",
            StorageKind.Files, false, false,
            [new(Path.Combine(Local, "D3DSCache")), new(Path.Combine(Local, @"NVIDIA\DXCache")), new(Path.Combine(Local, @"NVIDIA\GLCache")),
             new(Env(@"%USERPROFILE%\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache")), new(Path.Combine(Local, @"AMD\DxCache")), new(Path.Combine(Local, @"AMD\DxcCache")),
             new(Path.Combine(Local, @"AMD\GLCache")), new(Path.Combine(Local, @"AMD\VkCache"))],
            "Rebuild cost: first-run stutter in every game.", Optional: true),
    ];

    /// <summary>What is deliberately not offered, and why (shown in the app).</summary>
    public static readonly IReadOnlyList<(string what, string why)> NotOffered =
    [
        ("Prefetch", "Windows manages it and uses it to start apps and games faster; clearing it makes the next launches slower."),
        ("Browser caches, cookies and profiles", "They hold your logins, passwords and site data. Clear them in the browser itself if you want to."),
        ("Downloads, Documents and other personal folders", "Personal files are never treated as junk."),
        ("WinSxS and the Windows Installer cache", "Deleting from them directly breaks updates and uninstalls; only DISM cleans the component store."),
        ("System restore points", "They're your way back after a bad change; manage them in System Protection."),
        ("Windows.old / previous Windows installation", "Needed to roll back a Windows upgrade; remove it in Settings › System › Storage › Temporary files when you're sure."),
    ];

    // ---- safety ---------------------------------------------------------------------------------------------------------

    private static List<string>? _deny;

    /// <summary>Locations nothing may be deleted from, whatever a category says.</summary>
    public static IReadOnlyList<string> DenyList => _deny ??= BuildDeny();

    private static List<string> BuildDeny()
    {
        string P(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var user = P(Environment.SpecialFolder.UserProfile);
        var appData = P(Environment.SpecialFolder.ApplicationData);
        var list = new List<string>
        {
            P(Environment.SpecialFolder.MyDocuments), P(Environment.SpecialFolder.Desktop), P(Environment.SpecialFolder.MyPictures), P(Environment.SpecialFolder.MyVideos),
            P(Environment.SpecialFolder.MyMusic), Path.Combine(user, "Downloads"), Path.Combine(user, "Saved Games"), Path.Combine(user, "OneDrive"), Path.Combine(user, "Favorites"),
            Path.Combine(Local, @"Google\Chrome\User Data"), Path.Combine(Local, @"Microsoft\Edge\User Data"), Path.Combine(Local, @"BraveSoftware"), Path.Combine(appData, @"Mozilla\Firefox\Profiles"),
            Path.Combine(appData, "Opera Software"), Path.Combine(Local, @"Vivaldi\User Data"),
            Path.Combine(appData, @"Microsoft\Credentials"), Path.Combine(Local, @"Microsoft\Credentials"), Path.Combine(appData, @"Microsoft\Protect"), Path.Combine(Local, @"Microsoft\Vault"),
            Path.Combine(Win, "Installer"), Path.Combine(Win, "WinSxS"), Path.Combine(Win, "System32"), Path.Combine(Win, "SysWOW64"), Path.Combine(Win, "Prefetch"),
            Path.Combine(Win, @"SoftwareDistribution\DataStore"), Path.Combine(Path.GetPathRoot(Win)!, "System Volume Information"), Path.Combine(Path.GetPathRoot(Win)!, "Windows.old"),
            AppPaths.Root,
        };
        if (KnownFolder("374DE290-123F-4565-9164-39C4925E467B") is { } downloads) list.Add(downloads); // FOLDERID_Downloads (can be moved)
        return list.Where(p => p.Length > 3).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);
    private static string? KnownFolder(string guid)
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid(guid), 0, 0, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }

    public static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    public static bool Under(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why this path must not be deleted, or null when it may be considered. Exclusions are the user's.</summary>
    public static string? Denied(string fullPath, IEnumerable<string>? exclusions = null)
    {
        if (DenyList.FirstOrDefault(d => Under(fullPath, d)) is { } hit) return "protected location";
        if (exclusions?.Select(e => { try { return Normalize(Env(e)); } catch { return null; } }).FirstOrDefault(e => e is not null && Under(fullPath, e)) is not null) return "in your exclusions";
        return null;
    }

    // ---- scan -------------------------------------------------------------------------------------------------------------

    public static async Task<List<CategoryScan>> ScanAsync(IEnumerable<StorageCategory> categories, IReadOnlyCollection<string> exclusions, bool elevated,
        IProgress<(string category, string location, int files)>? progress, CancellationToken ct)
    {
        var results = new List<CategoryScan>();
        foreach (var c in categories)
        {
            ct.ThrowIfCancellationRequested();
            var scan = new CategoryScan { Category = c };
            progress?.Report((c.Name, "", 0));
            await Task.Run(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // keep the PC responsive while scanning
                switch (c.Kind)
                {
                    case StorageKind.Files: ScanFiles(scan, exclusions, elevated, progress, ct); break;
                    case StorageKind.RecycleBin: ScanRecycleBin(scan); break;
                    case StorageKind.DeliveryOptimization:
                        ScanFiles(scan, exclusions, elevated, progress, ct);
                        scan.Estimated = true;
                        if (!elevated) scan.Note = "Its folder can only be measured as administrator; size unknown.";
                        scan.Files.Clear(); // cleaned by Windows' own cmdlet, never file by file
                        break;
                    case StorageKind.ComponentStore:
                        scan.Estimated = true;
                        scan.Note = ComponentStoreNote ?? "Size not measured yet: use Analyze (takes a few minutes, administrator).";
                        break;
                }
            }, ct);
            results.Add(scan);
        }
        return results;
    }

    private static void ScanFiles(CategoryScan scan, IReadOnlyCollection<string> exclusions, bool elevated, IProgress<(string, string, int)>? progress, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var root in scan.Category.Roots)
        {
            string rootPath;
            try { rootPath = Normalize(root.Path); } catch { continue; }
            if (File.Exists(rootPath)) // a single-file root (e.g. MEMORY.DMP)
            {
                scan.Locations.Add(rootPath);
                var fi = new FileInfo(rootPath);
                Consider(scan, fi, root, Path.GetDirectoryName(rootPath)!, exclusions, now);
                continue;
            }
            if (!Directory.Exists(rootPath)) continue;
            var rootInfo = new DirectoryInfo(rootPath);
            if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) { scan.Skip("location is a link or junction (not followed)"); continue; }
            scan.Locations.Add(rootPath);
            var stack = new Stack<DirectoryInfo>();
            stack.Push(rootInfo);
            int seen = 0;
            var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos("*", opts).ToList(); }
                catch (UnauthorizedAccessException) { scan.Skip(elevated ? "no permission to read" : "needs administrator to read"); if (!elevated) scan.Partial = true; continue; }
                catch (IOException) { scan.Skip("couldn't be read"); continue; }
                foreach (var e in entries)
                {
                    // Links and junctions are never followed and never deleted (they may point anywhere).
                    if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) { scan.Skip("link or junction (not followed)"); continue; }
                    if (e is DirectoryInfo d) { if (root.Recursive) stack.Push(d); continue; }
                    if (e is FileInfo f) Consider(scan, f, root, rootPath, exclusions, now);
                    if (++seen % 500 == 0) progress?.Report((scan.Category.Name, dir.FullName, scan.Count));
                    if (scan.Files.Count >= 400_000) { scan.Note = "Stopped listing at 400,000 files."; return; }
                }
            }
        }
    }

    private static void Consider(CategoryScan scan, FileInfo f, ScanRoot root, string rootPath, IReadOnlyCollection<string> exclusions, DateTime nowUtc)
    {
        try
        {
            if (!FileSystemName.MatchesSimpleExpression(root.Pattern, f.Name)) return;
            var full = Normalize(f.FullName);
            if (!Under(full, rootPath) && !string.Equals(full, Normalize(root.Path), StringComparison.OrdinalIgnoreCase)) { scan.Skip("outside the approved location"); return; }
            if (Denied(full, exclusions) is { } why) { scan.Skip(why); return; }
            // System-attributed files belong to Windows/drivers even in temp folders: safety can't be established.
            if (f.Attributes.HasFlag(FileAttributes.System)) { scan.Skip("system file"); return; }
            if (f.Length < root.MinBytes || f.Length > root.MaxBytes) return; // counted by another category
            if (root.MinAgeDays > 0 && (nowUtc - f.LastWriteTimeUtc).TotalDays < root.MinAgeDays) { scan.Skip($"newer than {root.MinAgeDays} days"); return; }
            scan.Files.Add(new(full, f.Length, f.LastWriteTimeUtc, rootPath));
            scan.Bytes += f.Length;
            scan.Count++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { scan.Skip("couldn't be read"); }
    }

    // ---- Recycle Bin (shell API) -------------------------------------------------------------------------------------------

    // Natural (8-byte) alignment on 64-bit Windows: 24 bytes. A 4-byte pack (20 bytes) is rejected with E_INVALIDARG.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(nint hwnd, string? root, uint flags);

    public static (long bytes, long items)? RecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : null;
    }

    private static void ScanRecycleBin(CategoryScan scan)
    {
        scan.Locations.Add("Recycle Bin on all drives");
        if (RecycleBin() is { } rb) { scan.Bytes = rb.bytes; scan.Count = (int)Math.Min(int.MaxValue, rb.items); }
        else scan.Note = "Windows didn't report the Recycle Bin size.";
    }

    // ---- component store (DISM) -----------------------------------------------------------------------------------------

    public static string? ComponentStoreNote { get; private set; }

    /// <summary>DISM /AnalyzeComponentStore: Windows' own assessment (minutes, administrator).</summary>
    public static (bool ok, string summary) AnalyzeComponentStore()
    {
        var r = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "dism.exe"), "/Online /Cleanup-Image /AnalyzeComponentStore", 20 * 60_000);
        if (!r.Ok) return (false, "DISM couldn't analyze the component store: " + r.Combined.Trim().Split('\n').LastOrDefault());
        string Get(string label) => Regex.Match(r.StdOut, $@"{Regex.Escape(label)}\s*:\s*(.+)").Groups[1].Value.Trim();
        var summary = $"Windows reports: backups and disabled features {Get("Backups and Disabled Features")}, reclaimable packages {Get("Number of Reclaimable Packages")}, cleanup recommended: {Get("Component Store Cleanup Recommended")}.";
        ComponentStoreNote = summary;
        return (true, summary);
    }

    // ---- clean ------------------------------------------------------------------------------------------------------------

    public static string QuarantineRoot => Path.Combine(AppPaths.Root, "Quarantine");

    public static async Task<StorageCleanReport> CleanAsync(IReadOnlyList<CategoryScan> selected, bool quarantine, IReadOnlyCollection<string> exclusions,
        IProgress<(string category, int done, int total)>? progress, CancellationToken ct)
    {
        var drives = selected.SelectMany(s => s.Category.Roots.Select(r => Path.GetPathRoot(Env(r.Path)))).Append(Path.GetPathRoot(Win)).Where(d => d is not null).Distinct(StringComparer.OrdinalIgnoreCase).Select(d => d!).ToList();
        var before = drives.ToDictionary(d => d, Free, StringComparer.OrdinalIgnoreCase);
        var results = new List<CategoryResult>();
        string? qid = null;
        var qItems = new List<QuarantineItem>();
        string? qDir = null;
        if (quarantine)
        {
            qid = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            qDir = Path.Combine(QuarantineRoot, qid);
        }
        bool cancelled = false;
        try
        {
            foreach (var scan in selected)
            {
                ct.ThrowIfCancellationRequested();
                var res = new CategoryResult { Category = scan.Category };
                results.Add(res);
                switch (scan.Category.Kind)
                {
                    case StorageKind.Files:
                        await Task.Run(() => CleanFiles(scan, res, qDir, qItems, exclusions, progress, ct), ct);
                        break;
                    case StorageKind.RecycleBin:
                    {
                        var rbBefore = RecycleBin();
                        int hr = SHEmptyRecycleBin(0, null, 0x1 | 0x2 | 0x4); // no confirmation UI (the app asked), no progress, no sound
                        var rbAfter = RecycleBin();
                        if (rbBefore is { } b && rbAfter is { } a && a.items < b.items) { res.Deleted = (int)(b.items - a.items); res.DeletedBytes = b.bytes - a.bytes; }
                        else if (hr != 0 && rbBefore is { items: > 0 }) { res.Failed = 1; res.Note = $"Windows didn't empty the Recycle Bin (0x{hr:X8})."; }
                        if (rbAfter is { items: > 0 } left) res.Note = $"{left.items} item(s) are still in the Recycle Bin (in use or on a drive Windows couldn't empty).";
                        break;
                    }
                    case StorageKind.DeliveryOptimization:
                    {
                        long sizeBefore = FolderBytes(scan.Category.Roots[0].Path);
                        var r = await Task.Run(() => ProcessRunner.Run(Path.Combine(ProcessRunner.System32, @"WindowsPowerShell\v1.0\powershell.exe"),
                            "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"", 10 * 60_000), ct);
                        long sizeAfter = FolderBytes(scan.Category.Roots[0].Path);
                        if (!r.Ok) { res.Failed = 1; res.Note = "Windows' Delete-DeliveryOptimizationCache failed: " + r.Combined.Trim(); }
                        else { res.DeletedBytes = Math.Max(0, sizeBefore - sizeAfter); res.Note = sizeBefore < 0 ? "Done by Windows (size couldn't be measured)." : "Done by Windows' Delete-DeliveryOptimizationCache."; }
                        break;
                    }
                    case StorageKind.ComponentStore:
                    {
                        // Not cancellable: stopping DISM mid-way is unsafe. The UI says so before starting.
                        var free0 = Free(Path.GetPathRoot(Win)!);
                        var r = await Task.Run(() => ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup", 60 * 60_000), CancellationToken.None);
                        var free1 = Free(Path.GetPathRoot(Win)!);
                        if (!r.Ok) { res.Failed = 1; res.Note = "DISM reported a problem: " + r.Combined.Trim().Split('\n').LastOrDefault(); }
                        else { res.DeletedBytes = Math.Max(0, free1 - free0); res.Note = "Done by DISM /StartComponentCleanup (measured as the change in free space on the Windows drive)."; }
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            if (qDir is not null && qItems.Count > 0)
                AtomicFile.WriteAllText(Path.Combine(qDir, "manifest.json"), JsonSerializer.Serialize(new QuarantineSession(qid!, DateTime.Now, qItems)));
        }
        var after = drives.ToDictionary(d => d, d => (before[d], Free(d)), StringComparer.OrdinalIgnoreCase);
        return new(DateTime.Now, results, after, cancelled, qItems.Count > 0 ? qid : null);
    }

    private static void CleanFiles(CategoryScan scan, CategoryResult res, string? qDir, List<QuarantineItem> qItems, IReadOnlyCollection<string> exclusions,
        IProgress<(string, int, int)>? progress, CancellationToken ct)
    {
        int i = 0, total = scan.Files.Count;
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? qRootDrive = qDir is null ? null : Path.GetPathRoot(qDir);
        foreach (var c in scan.Files)
        {
            if (++i % 200 == 0) { ct.ThrowIfCancellationRequested(); progress?.Report((scan.Category.Name, i, total)); }
            try
            {
                // Re-check everything at clean time: things change between scan and clean.
                var f = new FileInfo(c.Path);
                if (!f.Exists) { res.Skip("already gone"); continue; }
                if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) { res.Skip("link or junction"); continue; }
                if (f.Attributes.HasFlag(FileAttributes.System)) { res.Skip("system file"); continue; }
                if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) { res.Skip("read-only"); continue; }
                var full = Normalize(f.FullName);
                if (!Under(full, c.Root) && !string.Equals(full, c.Root, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(full) != c.Root) { res.Skip("outside the approved location"); continue; }
                if (Denied(full, exclusions) is { } why) { res.Skip(why); continue; }
                if (f.LastWriteTimeUtc != c.LastWriteUtc || f.Length != c.Bytes) { res.Skip("changed since the scan"); continue; }
                // In use? An exclusive open fails if any process has it open.
                try { using var probe = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { res.Skip("in use"); continue; }
                catch (UnauthorizedAccessException) { res.Skip("no permission"); continue; }

                bool sameDrive = qRootDrive is not null && string.Equals(Path.GetPathRoot(full), qRootDrive, StringComparison.OrdinalIgnoreCase);
                if (qDir is not null && sameDrive)
                {
                    var rel = full.Replace(':', '_').TrimStart('\\');
                    var target = Path.Combine(qDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(full, target);
                    if (File.Exists(full) || !File.Exists(target)) { res.Failed++; continue; }
                    qItems.Add(new(full, target, c.Bytes));
                    res.Quarantined++; res.QuarantinedBytes += c.Bytes;
                }
                else
                {
                    File.Delete(full);
                    if (File.Exists(full)) { res.Failed++; continue; }
                    res.Deleted++; res.DeletedBytes += c.Bytes;
                }
                touchedDirs.Add(Path.GetDirectoryName(full)!);
            }
            catch (IOException) { res.Skip("in use"); }
            catch (UnauthorizedAccessException) { res.Skip("no permission"); }
            catch (Exception ex) { res.Failed++; Log.Debug("clean " + c.Path + ": " + ex.Message); }
        }
        // Remove folders left empty, bottom-up, never a root, never a link.
        foreach (var d in touchedDirs.OrderByDescending(d => d.Length))
        {
            var dir = d;
            var root = scan.Files.FirstOrDefault(f => Under(f.Path, f.Root) && Under(dir, f.Root))?.Root;
            while (root is not null && Under(dir, root) && !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (!di.Exists || di.Attributes.HasFlag(FileAttributes.ReparsePoint) || di.EnumerateFileSystemInfos().Any()) break;
                    di.Delete(false);
                }
                catch { break; }
                dir = Path.GetDirectoryName(dir)!;
            }
        }
        progress?.Report((scan.Category.Name, total, total));
    }

    public static long Free(string drive)
    {
        try { return new DriveInfo(drive).AvailableFreeSpace; } catch { return -1; }
    }

    private static long FolderBytes(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);
        }
        catch { return -1; }
    }

    // ---- quarantine -------------------------------------------------------------------------------------------------------

    public const int QuarantineDays = 7;

    public static List<QuarantineSession> QuarantineSessions()
    {
        var list = new List<QuarantineSession>();
        if (!Directory.Exists(QuarantineRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(QuarantineRoot))
        {
            var m = Path.Combine(dir, "manifest.json");
            try { if (File.Exists(m) && JsonSerializer.Deserialize<QuarantineSession>(File.ReadAllText(m)) is { } s) list.Add(s with { Items = s.Items.Where(i => File.Exists(i.Stored)).ToList() }); }
            catch (Exception ex) { Log.Warn("quarantine manifest unreadable: " + ex.Message); }
        }
        return list.OrderByDescending(s => s.At).ToList();
    }

    /// <summary>Moves quarantined files back where they were (not over a file that exists there now). Returns (restored, problems).</summary>
    public static (int restored, List<string> problems) Restore(QuarantineSession s)
    {
        int ok = 0; var problems = new List<string>();
        foreach (var item in s.Items)
        {
            try
            {
                if (!Under(Normalize(item.Stored), Normalize(QuarantineRoot))) { problems.Add($"{item.Original}: not in quarantine"); continue; }
                if (File.Exists(item.Original)) { problems.Add($"{item.Original}: a file with that name exists again (kept both; the quarantined copy stays)"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(item.Original)!);
                File.Move(item.Stored, item.Original);
                ok++;
            }
            catch (Exception ex) { problems.Add($"{item.Original}: {ex.Message}"); }
        }
        if (problems.Count == 0) Purge(s);
        return (ok, problems);
    }

    /// <summary>Deletes a quarantine session permanently. Only ever inside the quarantine folder.</summary>
    public static (long freed, int failed) Purge(QuarantineSession s)
    {
        var dir = Normalize(Path.Combine(QuarantineRoot, s.Id));
        if (!Under(dir, Normalize(QuarantineRoot)) || dir.Equals(Normalize(QuarantineRoot), StringComparison.OrdinalIgnoreCase)) return (0, 1);
        long freed = 0; int failed = 0;
        foreach (var item in s.Items)
        {
            try { File.Delete(item.Stored); if (!File.Exists(item.Stored)) freed += item.Bytes; else failed++; }
            catch { failed++; }
        }
        try { if (failed == 0 && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { failed++; }
        return (freed, failed);
    }

    /// <summary>Empties quarantine sessions older than <see cref="QuarantineDays"/> (called when the cleaner opens).</summary>
    public static long PurgeExpired()
    {
        long freed = 0;
        foreach (var s in QuarantineSessions().Where(s => DateTime.Now - s.At > TimeSpan.FromDays(QuarantineDays))) freed += Purge(s).freed;
        return freed;
    }

    public static string Size(long bytes) => bytes < 0 ? "unknown" : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" : $"{bytes / 1024.0:0} KB";
}
