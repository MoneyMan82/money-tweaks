using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MoneyTweaks.AgentCenter.Core;

/// <summary>
/// The Money Tweaks workspace this app is allowed to look at: the repository root and its agent worktrees under
/// .claude\worktrees. Every path from an event is resolved against it; anything outside is ignored.
/// </summary>
public sealed class Workspace
{
    public string Root { get; }

    public Workspace(string root) => Root = Path.GetFullPath(root).TrimEnd('\\', '/');

    /// <summary>Finds the repository (folder with MoneyTweaks.slnx) from the given start folders.</summary>
    public static Workspace? Find(params string?[] starts)
    {
        foreach (var s in starts)
        {
            for (var d = s; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, "MoneyTweaks.slnx")) && Directory.Exists(Path.Combine(d, ".claude"))) return new Workspace(d);
        }
        return null;
    }

    public string AgentsFolder => Path.Combine(Root, ".claude", "agents");
    public string WorktreesFolder => Path.Combine(Root, ".claude", "worktrees");
    public string TeamFolder => Path.Combine(Root, "team");

    /// <summary>
    /// Maps a path from a hook payload to a real path inside the workspace, or null. Claude Code's hook JSON on this PC
    /// replaces non-ASCII characters of the project path ("Työpöytä") with U+FFFD, so each U+FFFD matches any one
    /// character of the real root.
    /// </summary>
    public string? Resolve(string? path, string? cwd = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('/', '\\');
        if (!Path.IsPathRooted(p))
        {
            var baseDir = Resolve(cwd) ?? Root;
            p = Path.Combine(baseDir, p);
        }
        if (p.Contains('�'))
        {
            // Only the root prefix may be garbled; the rest of the path must be readable.
            if (p.Length < Root.Length || !Matches(p[..Root.Length], Root) || p[Root.Length..].Contains('�')) return null;
            p = Root + p[Root.Length..];
        }
        string full;
        try { full = Path.GetFullPath(p); } catch { return null; }
        return IsInside(full) ? full : null;
    }

    private static bool Matches(string garbled, string real) =>
        garbled.Length == real.Length && garbled.Zip(real).All(x => x.First == '�' || char.ToUpperInvariant(x.First) == char.ToUpperInvariant(x.Second));

    public bool IsInside(string fullPath) =>
        fullPath.Equals(Root, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Path relative to the root (e.g. ".claude\worktrees\agent-x\src\a.cs" or "src\a.cs").</summary>
    public string Relative(string fullPath) => IsInside(fullPath) ? Path.GetRelativePath(Root, fullPath) : fullPath;

    /// <summary>The git working copy a file belongs to: an agent worktree folder or the main checkout.</summary>
    public string CheckoutOf(string fullPath)
    {
        var wt = WorktreesFolder + "\\";
        if (fullPath.StartsWith(wt, StringComparison.OrdinalIgnoreCase))
        {
            var name = fullPath[wt.Length..].Split('\\')[0];
            if (name.Length > 0) return Path.Combine(WorktreesFolder, name);
        }
        return Root;
    }

    /// <summary>Worktree name for a cwd/path ("agent-a69..."), or null for the main checkout.</summary>
    public string? WorktreeName(string? pathOrCwd)
    {
        if (pathOrCwd is null) return null;
        var i = pathOrCwd.IndexOf(@".claude\worktrees\", StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = pathOrCwd.IndexOf(".claude/worktrees/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = pathOrCwd[(i + ".claude/worktrees/".Length)..];
        var name = rest.Split('\\', '/')[0];
        return name.Length > 0 ? name : null;
    }

    // ---- git (read-only commands only, no shell, paths checked) ----------------------------------------------------------------

    public string? Branch(string checkout) => Git(checkout, "rev-parse", "--abbrev-ref", "HEAD")?.Trim() is { Length: > 0 } b ? b : null;

    /// <summary>Lines added/removed in the working copy vs HEAD for one file (null if git can't tell, e.g. untracked).</summary>
    public (int Added, int Removed)? NumStat(string fullPath)
    {
        if (!IsInside(fullPath)) return null;
        var checkout = CheckoutOf(fullPath);
        var rel = Path.GetRelativePath(checkout, fullPath);
        var o = Git(checkout, "diff", "--numstat", "HEAD", "--", rel);
        if (string.IsNullOrWhiteSpace(o)) return null;
        var parts = o.Split('\t');
        return parts.Length >= 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var r) ? (a, r) : null;
    }

    /// <summary>Files git reports as modified, added or untracked in a checkout (full paths), or null if git failed.</summary>
    public HashSet<string>? ChangedFiles(string checkout)
    {
        var o = Git(checkout, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (o is null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = o.Split('\0');
        for (int i = 0; i < parts.Length; i++)
        {
            var entry = parts[i];
            if (entry.Length < 4) continue;
            var code = entry[..2];
            set.Add(Path.GetFullPath(Path.Combine(checkout, entry[3..].Replace('/', '\\'))));
            if (code[0] is 'R' or 'C') i++;   // renames/copies are followed by the old name
        }
        return set;
    }

    /// <summary>Unified diff of one file: uncommitted changes vs HEAD; if none, its changes on the branch vs main.</summary>
    public string Diff(string fullPath)
    {
        if (!IsInside(fullPath)) return "This file is outside the Money Tweaks workspace.";
        var checkout = CheckoutOf(fullPath);
        var rel = Path.GetRelativePath(checkout, fullPath);
        var d = Git(checkout, "diff", "--no-color", "HEAD", "--", rel);
        if (string.IsNullOrWhiteSpace(d))
        {
            var branch = Branch(checkout);
            if (branch is not null && branch != "main") d = Git(checkout, "diff", "--no-color", "main...HEAD", "--", rel);
        }
        if (string.IsNullOrWhiteSpace(d))
        {
            var tracked = Git(checkout, "ls-files", "--", rel);
            if (string.IsNullOrWhiteSpace(tracked) && File.Exists(fullPath))
                return "New file (not committed yet):\n" + string.Join("\n", File.ReadLines(fullPath).Take(400).Select(l => "+" + l));
            return "No changes compared with HEAD or main.";
        }
        return Sanitizer.Text(d, 200_000)!;
    }

    public string? Git(string workDir, params string[] args)
    {
        if (!IsInside(Path.GetFullPath(workDir))) return null;
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=off");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch { return null; }
    }
}
