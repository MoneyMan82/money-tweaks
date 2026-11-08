using System.Text.RegularExpressions;

namespace MoneyTweaks.AgentCenter.Core;

/// <summary>
/// Second line of defence (the publisher already sanitizes): removes control characters and terminal escapes, redacts
/// anything that looks like a credential, and caps lengths, so nothing secret or hostile is stored or displayed.
/// </summary>
public static partial class Sanitizer
{
    // Whole terminal escape sequences first, then any remaining control character.
    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]|[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")] private static partial Regex ControlChars();

    private static readonly Regex[] Secrets =
    [
        new(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.Compiled),
        new(@"\b(sk-ant-[A-Za-z0-9_-]{16,}|sk-[A-Za-z0-9_-]{16,})", RegexOptions.Compiled),
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}", RegexOptions.Compiled),
        new(@"(?i)\b(bearer|token|authorization)\s*[:=]?\s*[A-Za-z0-9._~+/=-]{16,}", RegexOptions.Compiled),
        new(@"(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?key|client[_-]?secret)\s*[:=]\s*\S+", RegexOptions.Compiled),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
    ];

    public static string? Text(string? s, int max = 2000)
    {
        if (s is null) return null;
        s = ControlChars().Replace(s, "");
        foreach (var r in Secrets) s = r.Replace(s, "[redacted]");
        s = s.Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private static List<string>? List(List<string>? l, int max, int maxItems) =>
        l?.Select(x => Text(x, max)).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Take(maxItems).ToList();

    private static string? Id(string? s) => s is null ? null : new string(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':').Take(80).ToArray());

    public static AgentEvent Clean(AgentEvent e)
    {
        e.Session = Id(e.Session) is { Length: > 0 } s ? s : "unknown";
        e.Agent = Id(e.Agent) is { Length: > 0 } a ? a : "main";
        e.AgentType = Id(e.AgentType);
        e.Type = Id(e.Type) ?? "";
        e.Source = Id(e.Source) ?? "hook";
        e.PromptId = Id(e.PromptId);
        e.Status = e.Status is { } st && AgentStatus.All.Contains(st.ToUpperInvariant()) ? st.ToUpperInvariant() : null;
        e.Cwd = Text(e.Cwd, 400);
        e.Task = Text(e.Task, 600);
        e.Phase = Text(e.Phase, 200);
        e.Summary = Text(e.Summary, 2000);
        e.Decision = Text(e.Decision, 600);
        e.Why = Text(e.Why, 600);
        e.Next = Text(e.Next, 400);
        e.NeedsHelp = Id(e.NeedsHelp);
        e.Error = Text(e.Error, 800);
        e.Blocker = Text(e.Blocker, 600);
        e.To = Id(e.To);
        e.Query = Text(e.Query, 400);
        e.Branch = Text(e.Branch, 200);
        e.Findings = List(e.Findings, 800, 20);
        e.Files = List(e.Files, 400, 40);
        e.Sources = List(e.Sources, 600, 40)?.Where(IsWebLink).ToList();
        if (e.Tool is { } t) { t.Name = Id(t.Name); t.Target = Text(t.Target, 300); t.UseId = Id(t.UseId); }
        if (e.Todos is { Count: > 60 }) e.Todos = e.Todos.Take(60).ToList();
        if (e.Todos is not null) foreach (var td in e.Todos) { td.Content = Text(td.Content, 300); td.Active = Text(td.Active, 300); td.Status = Id(td.Status); }
        if (e.Tests is { Count: > 20 }) e.Tests = e.Tests.Take(20).ToList();
        if (e.Tests is not null) foreach (var tr in e.Tests) tr.Suite = Id(tr.Suite);
        return e;
    }

    /// <summary>Only http(s) links are shown as clickable sources.</summary>
    public static bool IsWebLink(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}
