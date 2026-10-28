using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

/// <summary>The release's files are not newer than the running app (e.g. a tag pointing at old binaries).</summary>
public sealed class UpdateMismatchException(string message) : Exception(message);

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

    /// <summary>
    /// The highest-versioned published release (not a draft or pre-release) that has a checksum file. GitHub's own
    /// "latest" is simply the most recently published one, so two releases built at the same time could leave an older
    /// version marked latest and stop updates; choosing by version number can't.
    /// </summary>
    public static JsonElement? PickNewest(JsonElement releases)
    {
        JsonElement? best = null;
        var bestV = new Version(0, 0);
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
            if (!r.TryGetProperty("assets", out var assets) || !assets.EnumerateArray().Any(a => a.GetProperty("name").GetString() == "SHA256SUMS.txt")) continue;
            var v = ParseVersion(r.GetProperty("tag_name").GetString() ?? "");
            if (best is null || IsNewer(v, bestV)) { best = r; bestV = v; }
        }
        return best;
    }

    /// <summary>Returns the newest release if it is newer than <paramref name="current"/>, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (PickNewest(doc.RootElement) is not { } root) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var latest = ParseVersion(tag);
        if (!IsNewer(latest, current)) return null;

        string? setup = null, portable = null, portableName = null, sums = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString();
            if (!IsTrustedDownloadUrl(url)) { Log.Warn($"Update asset {name} ignored: unexpected URL {url}"); continue; }
            if (name.Equals("MoneyTweaks-Setup.exe", StringComparison.OrdinalIgnoreCase)) setup = url;
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
            else if (IsPortableAsset(name)) { portable = url; portableName = name; }
        }
        var html = root.GetProperty("html_url").GetString();
        return new UpdateInfo(latest, tag, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            IsTrustedPageUrl(html) ? html! : ReleasesPage, setup, portable, portableName, sums);
    }

    /// <summary>
    /// The portable app itself (MoneyTweaks-1.74.0-win-x64.exe), never another program in the release such as
    /// MoneyTweaks-Rescue.exe: a portable update replaces the running exe with this file.
    /// </summary>
    public static bool IsPortableAsset(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^MoneyTweaks-\d+(\.\d+){1,3}-win-x64\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Release files must come from this repository's GitHub releases over HTTPS (GitHub may redirect to its CDN).</summary>
    public static bool IsTrustedDownloadUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The release page opened in the browser must be this repository on github.com (it's launched via the shell).</summary>
    public static bool IsTrustedPageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith($"/{Repo}/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tag reduced to characters that are safe in a folder name (no "..", slashes or drive letters).</summary>
    public static string SafeTag(string tag)
    {
        var s = new string(tag.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray()).Replace("..", ".");
        return s.Trim('.').Length == 0 ? "unknown" : s.Trim('.');
    }

    // SHA-256 of each verified download, re-checked right before it is started.
    private static readonly Dictionary<string, string> Verified = new(StringComparer.OrdinalIgnoreCase);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>True only when <paramref name="candidate"/> is strictly newer. 1.2 == 1.2.0 == 1.2.0.0.</summary>
    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>Version stamped into an exe's file-version resource (Inno Setup and .NET both write it), or null.</summary>
    public static Version? FileVersionOf(string path)
    {
        var fv = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (string.IsNullOrWhiteSpace(fv)) return null;
        var core = fv.Trim().Split(' ', '+', '-')[0];
        return Version.TryParse(core, out var v) ? v : null;
    }

    public static string InstallerLogPath => Path.Combine(Path.GetTempPath(), "MoneyTweaks-update-install.log");

    /// <summary>Downloads the right file for this copy (installer or portable exe) and verifies its checksum.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, Version current, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (url, name) = IsInstalled ? (u.SetupUrl, "MoneyTweaks-Setup.exe") : (u.PortableUrl, u.PortableName);
        if (url is null || name is null) throw new InvalidOperationException("This release has no file for your copy of Money Tweaks. Download it manually from the releases page.");
        if (u.SumsUrl is null) throw new InvalidOperationException("This release has no SHA256SUMS.txt, so the download can't be verified.");

        if (!IsTrustedDownloadUrl(url) || !IsTrustedDownloadUrl(u.SumsUrl)) throw new InvalidOperationException("The update's download address isn't this project's GitHub releases, so nothing was downloaded.");
        var dir = Path.Combine(Path.GetTempPath(), "MoneyTweaks-update-" + SafeTag(u.Tag));
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
            lock (Verified) Verified[Path.GetFullPath(path)] = actual;
        }
        // Root cause of the old endless-update loop: a release tag whose files were built with an older version.
        // Installing those changes nothing, so refuse them instead of looping.
        var fileVersion = FileVersionOf(path);
        if (fileVersion is not null && !IsNewer(fileVersion, current))
        {
            File.Delete(path);
            throw new UpdateMismatchException($"The {u.Tag} release on GitHub contains Money Tweaks {AppVersioning.Display(fileVersion)}, which is not newer than the version you have ({AppVersioning.Display(current)}). The release was published with the wrong files, so installing it would change nothing. Nothing was installed.");
        }
        Log.Info($"Update {u.Tag} downloaded and verified: {path} (file version {fileVersion?.ToString() ?? "unknown"})");
        return path;
    }

    /// <summary>Starts the update. The caller must exit the app right after this returns.</summary>
    public static void Install(string downloadedPath)
    {
        // The file sat in a user-writable temp folder since it was verified. Lock it against writes and deletion,
        // hash it again and start it while the lock is held, so it can't be swapped between the check and the start.
        using var guard = OpenVerified(downloadedPath);
        if (IsInstalled)
        {
            // Keep the same install scope as the existing copy (per-user vs all users).
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var scope = AppContext.BaseDirectory.StartsWith(pf, StringComparison.OrdinalIgnoreCase) ? "/ALLUSERS" : "/CURRENTUSER";
            Process.Start(new ProcessStartInfo(downloadedPath, $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS {scope} /LOG=\"{InstallerLogPath}\"") { UseShellExecute = true });
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
        // --restarted: the new copy waits for this one to close instead of handing off to it (single instance).
        Process.Start(new ProcessStartInfo(current, "--restarted") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(current)! });
        Log.Info("Portable update swapped in; restarting");
    }

    /// <summary>
    /// Opens a downloaded update so nobody can change or delete it (readers allowed), and checks it still has the hash
    /// verified at download time. Throws, and nothing is started, if it was never verified or has changed.
    /// </summary>
    public static FileStream OpenVerified(string path)
    {
        var full = Path.GetFullPath(path);
        string? expected;
        lock (Verified) Verified.TryGetValue(full, out expected);
        if (expected is null) throw new InvalidOperationException("This update file wasn't verified in this session, so it won't be started.");
        var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var now = Convert.ToHexString(SHA256.HashData(fs));
            fs.Position = 0;
            if (!now.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded update changed after it was verified, so it was not started. Download it again.");
            return fs;
        }
        catch { fs.Dispose(); throw; }
    }

    /// <summary>Test hook: marks a file as verified with its current hash.</summary>
    internal static void MarkVerifiedForTests(string path)
    {
        using var fs = File.OpenRead(path);
        lock (Verified) Verified[Path.GetFullPath(path)] = Convert.ToHexString(SHA256.HashData(fs));
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
