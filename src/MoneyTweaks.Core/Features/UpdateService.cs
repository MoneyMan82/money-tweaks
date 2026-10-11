using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string HtmlUrl,
    string? SetupUrl, string? PortableUrl, string? PortableName, string? SumsUrl);

/// <summary>
/// Checks GitHub Releases for a newer version, downloads it, verifies its SHA-256 against the release's
/// SHA256SUMS.txt and installs it. The only network traffic is to api.github.com / github.com.
/// </summary>
public static class UpdateService
{
    public const string Repo = "MoneyMan82/money-tweaks";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MoneyTweaks", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>True when running from an Inno Setup install (uninstaller next to the exe), false for portable copies.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static Version ParseVersion(string tag)
    {
        var t = tag.TrimStart('v', 'V');
        var dash = t.IndexOf('-');
        if (dash > 0) t = t[..dash];
        return Version.TryParse(t, out var v) ? v : new Version(0, 0);
    }

    /// <summary>Returns the latest release if it is newer than <paramref name="current"/>, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var latest = ParseVersion(tag);
        if (Normalize(latest) <= Normalize(current)) return null;

        string? setup = null, portable = null, portableName = null, sums = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString();
            if (name.Equals("MoneyTweaks-Setup.exe", StringComparison.OrdinalIgnoreCase)) setup = url;
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
            else if (name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase)) { portable = url; portableName = name; }
        }
        return new UpdateInfo(latest, tag, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? ReleasesPage, setup, portable, portableName, sums);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>Downloads the right file for this copy (installer or portable exe) and verifies its checksum.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (url, name) = IsInstalled ? (u.SetupUrl, "MoneyTweaks-Setup.exe") : (u.PortableUrl, u.PortableName);
        if (url is null || name is null) throw new InvalidOperationException("This release has no file for your copy of Money Tweaks. Download it manually from the releases page.");
        if (u.SumsUrl is null) throw new InvalidOperationException("This release has no SHA256SUMS.txt, so the download can't be verified.");

        var dir = Path.Combine(Path.GetTempPath(), "MoneyTweaks-update-" + u.Tag);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);

        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buf = new byte[81920];
            long read = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        var sums = await Http.GetStringAsync(u.SumsUrl, ct);
        var expected = sums.Split('\n').Select(l => l.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*').Equals(name, StringComparison.OrdinalIgnoreCase))?[0];
        await using (var fs = File.OpenRead(path))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(path);
                throw new InvalidOperationException($"Checksum mismatch for {name}. The download was deleted and nothing was installed.");
            }
        }
        Log.Info($"Update {u.Tag} downloaded and verified: {path}");
        return path;
    }

    /// <summary>Starts the update. The caller must exit the app right after this returns.</summary>
    public static void Install(string downloadedPath)
    {
        if (IsInstalled)
        {
            // Keep the same install scope as the existing copy (per-user vs all users).
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var scope = AppContext.BaseDirectory.StartsWith(pf, StringComparison.OrdinalIgnoreCase) ? "/ALLUSERS" : "/CURRENTUSER";
            Process.Start(new ProcessStartInfo(downloadedPath, $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS {scope}") { UseShellExecute = true });
            Log.Info("Update installer started");
            return;
        }
        // Portable: Windows allows renaming a running exe, so swap the files and start the new one.
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the running executable.");
        var old = current + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(current, old);
        try { File.Copy(downloadedPath, current); }
        catch { File.Move(old, current); throw; }
        Process.Start(new ProcessStartInfo(current) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(current)! });
        Log.Info("Portable update swapped in; restarting");
    }

    /// <summary>Removes the previous exe left behind by a portable update.</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            var old = (Environment.ProcessPath ?? "") + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* the old process may still be exiting; try again next start */ }
    }
}
