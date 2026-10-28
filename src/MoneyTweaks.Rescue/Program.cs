using System.Diagnostics;
using System.Text;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Rescue;

/// <summary>
/// Money Tweaks Emergency Rescue. Lists the changes Money Tweaks made (from its backups folder), shows the original
/// value of every setting next to what Windows reads now, and restores the ones you pick after you confirm, then reads
/// them back. Works when the main app can't start. Nothing is reset blindly and nothing is deleted.
///
///   MoneyTweaks-Rescue.exe              interactive
///   MoneyTweaks-Rescue.exe --list       print the changes and exit (read-only)
///   MoneyTweaks-Rescue.exe --log        print recent change-log entries and exit (read-only)
///   MoneyTweaks-Rescue.exe --verify     read back the last rescue and exit (read-only)
/// --select id,id (and --profile) preselect changes; a restore always asks for confirmation.
/// </summary>
public static class Program
{
    private static List<RescueEntry> _entries = [];
    private static ProfileSession? _profile;

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* old console host */ }
        if (Value(args, "--data") is { Length: > 0 } data)
        {
            if (!Directory.Exists(data)) { Bad($"Data folder not found: {data}"); return 2; }
            AppPaths.OverrideRoot(data);
        }
        if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?")) { Help(); return 0; }
        try
        {
            if (Has(args, "--list")) { Reload(); PrintList(); return 0; }
            if (Has(args, "--log")) { PrintLog(); return 0; }
            if (Has(args, "--verify")) return PrintVerify(EmergencyRescue.Sessions().FirstOrDefault()) ? 0 : 1;
            return Interactive(args);
        }
        catch (Exception ex)
        {
            Log.Error("Emergency Rescue crashed", ex);
            Bad("Emergency Rescue hit an unexpected error: " + ex.Message);
            Console.WriteLine($"Nothing more was changed. Details are in {AppPaths.Logs}.");
            Pause();
            return 3;
        }
    }

    // ---- interactive ---------------------------------------------------------------------------------------------------
    private static int Interactive(string[] args)
    {
        Header();
        Reload();
        if (Value(args, "--select") is { } pre)
        {
            var ids = pre.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            var picked = _entries.Where(e => ids.Contains(e.Id)).ToList();
            RestoreFlow(picked, Has(args, "--profile") ? _profile : null);
            Reload();
        }
        while (true)
        {
            PrintList();
            Console.WriteLine();
            Dim("  [number] details   R restore   V verify last rescue   L recent changes   H earlier rescues");
            Dim($"  F open data folder{(Admin.IsElevated ? "" : "   A restart as administrator")}   Q quit");
            var input = Ask("> ");
            if (input is null) return 0;
            switch (input.Trim().ToLowerInvariant())
            {
                case "q" or "quit" or "exit": return 0;
                case "": continue;
                case "r": RestoreFlow(null, null); Reload(); break;
                case "v": PrintVerify(EmergencyRescue.Sessions().FirstOrDefault()); Pause(); break;
                case "l": PrintLog(); Pause(); break;
                case "h": History(); Reload(); break;
                case "f": OpenFolder(AppPaths.Root); break;
                case "a" when !Admin.IsElevated:
                    if (Elevate("")) return 0;
                    break;
                default:
                    if (int.TryParse(input.Trim(), out int n) && n >= 1 && n <= _entries.Count) { Details(_entries[n - 1]); Pause(); }
                    else Warn("Type a number from the list or one of the letters.");
                    break;
            }
        }
    }

    private static void Reload()
    {
        Dim("  Reading Money Tweaks' backups and the current values from Windows…");
        _entries = EmergencyRescue.Scan();
        _profile = EmergencyRescue.ActiveProfile();
    }

    private static void Header()
    {
        Console.WriteLine();
        Color(ConsoleColor.Yellow, "  MONEY TWEAKS · EMERGENCY RESCUE");
        Console.WriteLine($"  Version {AppVersioning.Display(typeof(Program).Assembly.GetName().Version ?? new Version(0, 0))}  ·  data: {AppPaths.Root}");
        Console.WriteLine($"  Running as {(Admin.IsElevated ? "administrator" : "standard user (system-wide changes will ask for permission)")}");
        Dim("  Restores Money Tweaks changes from their backups. Your files and other settings are never touched.");
        if (MainAppRunning())
            Warn("  Money Tweaks is running. Close it before restoring, so it doesn't re-apply a gaming profile or change settings at the same time.");
        Console.WriteLine();
    }

    private static void PrintList()
    {
        Console.WriteLine();
        if (_entries.Count == 0 && _profile is null)
        {
            Good("  No Money Tweaks changes are applied: the backups folder is empty. There is nothing to restore.");
            return;
        }
        Console.WriteLine($"  {"#",3}  {"Applied",-16}  {"Admin",-5}  Change");
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            var color = !e.CanRestore ? ConsoleColor.DarkYellow : e.State == TweakState.NotApplied ? ConsoleColor.DarkGray : Console.ForegroundColor;
            Color(color, $"  {i + 1,3}  {(e.AppliedAt is { } at ? at.ToString("yyyy-MM-dd HH:mm") : "unknown"),-16}  {(e.NeedsAdmin ? "yes" : ""),-5}  {e.Name}  ({e.StatusText})");
        }
        if (_profile is not null)
            Color(ConsoleColor.Cyan, $"    P  {_profile.StartedAt:yyyy-MM-dd HH:mm}         Gaming profile on: {_profile.ProfileName} (turning it off restores what it changed)");
    }

    private static void Details(RescueEntry e)
    {
        Console.WriteLine();
        Color(ConsoleColor.Yellow, $"  {e.Name}  [{e.Id}]");
        Console.WriteLine($"  Applied {(e.AppliedAt is { } at ? at.ToString("g") : "at an unknown time")} · {e.StatusText}{(e.NeedsAdmin ? " · system-wide (needs administrator)" : "")}");
        if (e.Tweak?.RequiresReboot == true) Dim("  Restoring it needs a Windows restart to take full effect.");
        if (e.Status == RescueBackupStatus.Damaged) Warn($"  The backup file {BackupStore.PathOf(e.Id)} can't be read. Rescue won't guess the original values.");
        if (e.Status == RescueBackupStatus.UnknownTweak) Warn($"  This copy doesn't know what this change touched. Its backup ({BackupStore.PathOf(e.Id)}) is kept; a newer Money Tweaks can undo it.");
        foreach (var v in e.Values)
        {
            Console.WriteLine($"   • {v.Location}");
            Console.WriteLine($"       now:     {v.Now}");
            Console.WriteLine($"       restore: {v.Original}");
        }
    }

    // ---- restore ---------------------------------------------------------------------------------------------------
    private static void RestoreFlow(List<RescueEntry>? picked, ProfileSession? profile)
    {
        if (picked is null)
        {
            if (_entries.Count == 0 && _profile is null) { Good("  Nothing to restore."); return; }
            Console.WriteLine("  Which changes? Numbers from the list (e.g. 1,3-5), 'all', and P for the gaming profile. Empty cancels.");
            var text = Ask("  restore> ");
            if (string.IsNullOrWhiteSpace(text)) return;
            var tokens = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).ToList();
            bool wantProfile = tokens.RemoveAll(t => t.Equals("p", StringComparison.OrdinalIgnoreCase)) > 0 || (text.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) && _profile is not null);
            if (wantProfile && _profile is null) { Warn("  No gaming profile is on."); return; }
            var rest = string.Join(",", tokens);
            List<int>? idx = rest.Length == 0 ? [] : EmergencyRescue.ParseSelection(rest, _entries.Count);
            if (idx is null) { Warn("  That selection isn't valid. Nothing was changed."); return; }
            picked = [.. idx.Select(i => _entries[i])];
            profile = wantProfile ? _profile : null;
        }
        if (picked.Count == 0 && profile is null) { Warn("  Nothing selected."); return; }

        var blocked = picked.Where(e => !e.CanRestore).ToList();
        var runnable = picked.Where(e => e.CanRestore).ToList();
        var admin = runnable.Where(e => e.NeedsAdmin).ToList();
        Console.WriteLine();
        Color(ConsoleColor.Yellow, "  This will restore:");
        if (profile is not null) Console.WriteLine($"   • Gaming profile '{profile.ProfileName}': turned off, its saved settings put back");
        foreach (var e in runnable)
            Console.WriteLine($"   • {e.Name}{(e.NeedsAdmin ? " (system-wide)" : "")}{(e.Tweak?.RequiresReboot == true ? " (restart needed)" : "")}");
        foreach (var e in blocked) Warn($"   × {e.Name}: skipped, {e.StatusText}");
        Dim($"  First, each change's backup and the values Windows has now are saved to {EmergencyRescue.SessionsRoot}\\<time>,");
        Dim("  so this rescue can be undone (H). Every restored value is read back afterwards.");

        if (admin.Count > 0 && !Admin.IsElevated)
        {
            Warn($"  {admin.Count} of these are system-wide and need administrator rights.");
            Console.WriteLine("  A = ask Windows for permission and restore everything selected in an administrator window");
            Console.WriteLine($"  U = restore only the {runnable.Count - admin.Count} per-user change(s) now{(profile is null ? "" : " and the profile")}");
            Console.WriteLine("  anything else cancels");
            var choice = Ask("  > ")?.Trim().ToLowerInvariant();
            if (choice == "a")
            {
                var ids = string.Join(",", runnable.Select(e => e.Id));
                if (Elevate($"--select \"{ids}\"{(profile is null ? "" : " --profile")}", wait: true))
                    PrintSession(EmergencyRescue.Sessions().FirstOrDefault(), afterElevation: true);
                return;
            }
            if (choice != "u") { Dim("  Cancelled. Nothing was changed."); return; }
            runnable = [.. runnable.Where(e => !e.NeedsAdmin)];
            if (runnable.Count == 0 && profile is null) { Dim("  Nothing left to restore without administrator rights."); return; }
        }
        if (MainAppRunning()) Warn("  Money Tweaks is still running. Closing it first is safer.");
        Console.WriteLine();
        var confirm = Ask($"  Type YES to restore {runnable.Count + (profile is null ? 0 : 1)} item(s): ");
        if (!string.Equals(confirm?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)) { Dim("  Cancelled. Nothing was changed."); return; }

        var session = EmergencyRescue.Restore(runnable, profile, new SyncProgress(m => Dim("  … " + m)));
        PrintSession(session, afterElevation: false);
        Console.WriteLine();
        PrintVerify(session);
        Pause();
    }

    private static void PrintSession(RescueSession? s, bool afterElevation)
    {
        if (s is null) { Warn("  No rescue was recorded."); return; }
        Console.WriteLine();
        Color(ConsoleColor.Yellow, $"  Rescue {s.Id}{(afterElevation ? " (administrator window)" : "")}:");
        foreach (var o in s.Outcomes)
        {
            if (o.Ok) Good($"   ✓ {o.Name}: {o.Message}");
            else Bad($"   ✗ {o.Name}: {o.Message}");
        }
        int ok = s.Outcomes.Count(o => o.Ok), failed = s.Outcomes.Count - ok;
        Console.WriteLine($"  {ok} restored, {failed} not restored.{(s.FinishedAt is null ? " The rescue didn't finish (it was interrupted)." : "")}");
        if (failed > 0) Console.WriteLine("  Anything not restored keeps its backup, so you can retry here or from Money Tweaks.");
        if (s.RestartRecommended) Warn("  Restart Windows to finish restoring.");
        Dim($"  Saved in {s.Folder}");
    }

    private static bool PrintVerify(RescueSession? s)
    {
        if (s is null) { Dim("  No rescue has been run yet."); return true; }
        Color(ConsoleColor.Yellow, $"  Read-back of rescue {s.Id}:");
        var rows = EmergencyRescue.Verify(s);
        if (rows.Count == 0) { Dim("  Nothing in it was restored, so there is nothing to read back."); return true; }
        bool all = true;
        foreach (var (o, diffs) in rows)
        {
            if (diffs.Count == 0) Good($"   ✓ {o.Name}: every value matches the original backup");
            else { all = false; Bad($"   ✗ {o.Name}:"); foreach (var d in diffs) Bad($"       {d}"); }
        }
        return all;
    }

    private static void PrintLog()
    {
        Console.WriteLine();
        var lines = EmergencyRescue.RecentChanges();
        Color(ConsoleColor.Yellow, "  Recent Money Tweaks changes (from the logs, last 14 days):");
        if (lines.Count == 0) { Dim("  None found."); return; }
        foreach (var l in lines.Take(60).Reverse())
        {
            var text = $"   {l.Time:yyyy-MM-dd HH:mm:ss}  {l.Text}";
            if (l.Level is "ERROR") Bad(text); else if (l.Level is "WARN") Warn(text); else Console.WriteLine(text);
        }
        if (lines.Count > 60) Dim($"   ({lines.Count - 60} older entries not shown; full logs: {AppPaths.Logs})");
    }

    private static void History()
    {
        var sessions = EmergencyRescue.Sessions();
        Console.WriteLine();
        if (sessions.Count == 0) { Dim("  No earlier rescues."); Pause(); return; }
        for (int i = 0; i < sessions.Count; i++)
            Console.WriteLine($"  {i + 1,3}  {sessions[i].StartedAt:yyyy-MM-dd HH:mm}  {sessions[i].Outcomes.Count(o => o.Ok)} restored, {sessions[i].Outcomes.Count(o => !o.Ok)} not");
        var n = Ask("  Rescue number to open (empty goes back): ");
        if (!int.TryParse(n, out int k) || k < 1 || k > sessions.Count) return;
        var s = sessions[k - 1];
        PrintSession(s, false);
        var restored = s.Outcomes.Where(o => o.Ok && o.Id != "profile").ToList();
        if (restored.Count == 0) { Pause(); return; }
        Console.WriteLine();
        Console.WriteLine("  Undo this rescue? It applies those tweaks again with their original backups (so Undo later still");
        Console.WriteLine("  puts back your true original values). Numbers (e.g. 1,2), 'all', or empty to go back:");
        for (int i = 0; i < restored.Count; i++) Console.WriteLine($"  {i + 1,3}  {restored[i].Name}");
        var idx = EmergencyRescue.ParseSelection(Ask("  put back> "), restored.Count);
        if (idx is null) return;
        if (!string.Equals(Ask($"  Type YES to apply {idx.Count} tweak(s) again: ")?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)) { Dim("  Cancelled."); return; }
        foreach (var i in idx)
        {
            var r = EmergencyRescue.PutBack(s, restored[i].Id);
            if (r.Ok) Good($"   ✓ {r.Name}: {r.Message}"); else Bad($"   ✗ {r.Name}: {r.Message}");
        }
        Pause();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------
    /// <summary>Starts an elevated copy on the same data folder (UAC). False when the user declines.</summary>
    private static bool Elevate(string args, bool wait = false)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--data \"{AppPaths.Root}\" {args}".Trim()) { UseShellExecute = true, Verb = "runas" });
            if (wait) { Dim("  Continue in the administrator window. Waiting for it to close…"); p?.WaitForExit(); }
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Warn("  Administrator permission was declined. Nothing was changed.");
            return false;
        }
    }

    private static bool MainAppRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("MoneyTweaks");
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }
        catch { return false; }
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Warn("  Could not open the folder: " + ex.Message); }
    }

    private static void Help()
    {
        Console.WriteLine("Money Tweaks Emergency Rescue: undo Money Tweaks changes from their backups, without the main app.");
        Console.WriteLine();
        Console.WriteLine("  MoneyTweaks-Rescue.exe            interactive: inspect, restore (asks first), verify");
        Console.WriteLine("  MoneyTweaks-Rescue.exe --list     list applied changes and exit");
        Console.WriteLine("  MoneyTweaks-Rescue.exe --log      list recent change-log entries and exit");
        Console.WriteLine("  MoneyTweaks-Rescue.exe --verify   read back the last rescue and exit (exit code 1 if anything differs)");
    }

    private static string? Ask(string prompt) { Console.Write(prompt); return Console.ReadLine(); }
    private static void Pause() { if (!Console.IsInputRedirected) Ask("  Press Enter to continue…"); }
    private static bool Has(string[] a, string n) => a.Contains(n, StringComparer.OrdinalIgnoreCase);
    private static string? Value(string[] a, string n) { int i = Array.FindIndex(a, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static void Color(ConsoleColor c, string s) { var old = Console.ForegroundColor; Console.ForegroundColor = c; Console.WriteLine(s); Console.ForegroundColor = old; }
    private static void Good(string s) => Color(ConsoleColor.Green, s);
    private static void Bad(string s) => Color(ConsoleColor.Red, s);
    private static void Warn(string s) => Color(ConsoleColor.DarkYellow, s);
    private static void Dim(string s) => Color(ConsoleColor.DarkGray, s);

    /// <summary>Reports progress on the calling thread (Progress&lt;T&gt; would post to the thread pool and print out of order).</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
