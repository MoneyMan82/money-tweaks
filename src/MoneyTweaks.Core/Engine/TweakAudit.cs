using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Engine;

public enum AuditKind { Duplicate, Conflict, Obsolete, Ineffective, Risky, OneWay, Security }

/// <summary>A known limitation of a tweak. <see cref="Other"/> names the conflicting tweak, when there is one.</summary>
public sealed record AuditIssue(AuditKind Kind, string Text, string? Other = null)
{
    public string Label => Kind switch
    {
        AuditKind.OneWay => "One-way",
        AuditKind.Security => "Security",
        _ => Kind.ToString()
    };
}

/// <summary>
/// Catalog-wide review: hand-written notes for tweaks whose real-world effect is smaller than their name suggests,
/// plus computed duplicates/conflicts (two tweaks writing the same registry value or the same setting).
/// </summary>
public static class TweakAudit
{
    /// <summary>Tweaks Money Tweaks no longer applies. Undo stays available so anyone who applied them can revert.</summary>
    private static readonly Dictionary<string, string> Disabled = new()
    {
        ["adv.vbs-hvci"] = "Disabled in Money Tweaks: it turns off a Windows security protection (Memory Integrity). Money Tweaks does not trade security for FPS. If it's on, use Undo to turn the protection back on.",
    };

    private static readonly Dictionary<string, AuditIssue[]> Notes = new()
    {
        ["net.nagle"] = [new(AuditKind.Ineffective, "Only changes TCP. Fortnite and most shooters send gameplay over UDP, so in-match ping is not affected.")],
        ["net.qos-reserve"] = [new(AuditKind.Ineffective, "Windows only reserves bandwidth for apps that request QoS; without such apps nothing is held back, so there is usually nothing to free.")],
        ["net.port-range"] = [new(AuditKind.Obsolete, "MaxUserPort is ignored on Windows Vista and later. Only TcpTimedWaitDelay has an effect.")],
        ["net.default-ttl"] = [new(AuditKind.Ineffective, "TTL only limits hop count. It does not change speed or ping.")],
        ["net.throttling-index"] = [new(AuditKind.Ineffective, "Only matters while the multimedia scheduler throttles network processing (media playing). Expect no ping change otherwise.")],
        ["adv.prefetch"] = [new(AuditKind.Obsolete, "Windows 10/11 control Superfetch through the SysMain service; the EnableSuperfetch value is generally ignored. EnablePrefetcher still applies.")],
        ["adv.large-system-cache"] = [new(AuditKind.Risky, "Meant for file servers. On gaming PCs it can leave less RAM for games.")],
        ["adv.timer-hpet"] = [new(AuditKind.OneWay, "Removes an override; Undo cannot put back a value that was removed. Most PCs never had the override, so this reads as already applied.")],
        ["adv.svchost-split"] = [new(AuditKind.Ineffective, "Reduces the number of svchost.exe processes; no measurable FPS effect is expected.")],
        ["gen.hibernate-off"] = [new(AuditKind.Conflict, "Also turns off Fast Startup, which overlaps with 'Disable Fast Startup'.", "gen.fast-startup-off")],
        ["gen.ultimate-plan"] = [new(AuditKind.Conflict, "Power-plan tweaks (core parking, USB suspend, minimum processor state) change the plan that is active when you apply them. Apply this plan first, then those tweaks.")],
        ["dbl.telemetry"] = [new(AuditKind.Ineffective, "AllowTelemetry=0 (Security level) is only honoured on Enterprise, Education and Server. Other editions treat it as 1 (Required).")],
    };

    private static readonly Lazy<Dictionary<string, List<AuditIssue>>> Computed = new(Compute);

    public static string? DisabledReason(Tweak t) => Disabled.GetValueOrDefault(t.Id);

    public static IReadOnlyList<AuditIssue> IssuesFor(Tweak t)
    {
        var list = new List<AuditIssue>();
        if (Disabled.TryGetValue(t.Id, out var why)) list.Add(new(AuditKind.Security, why));
        if (Notes.TryGetValue(t.Id, out var n)) list.AddRange(n);
        if (t.Ops.OfType<PowerCfgOp>().Any()) list.Add(new(AuditKind.Conflict, "Changes the active power plan only. Switching plans later (e.g. Ultimate Performance) does not carry this setting over.", "gen.ultimate-plan"));
        if (Computed.Value.TryGetValue(t.Id, out var c)) list.AddRange(c);
        return list;
    }

    private static Dictionary<string, List<AuditIssue>> Compute()
    {
        var map = new Dictionary<string, List<AuditIssue>>();
        void Add(string id, AuditIssue i) { if (!map.TryGetValue(id, out var l)) map[id] = l = []; l.Add(i); }

        // Same registry value written by more than one tweak.
        var regs = TweakCatalog.All
            .SelectMany(t => t.Ops.OfType<RegValueOp>().Select(o => (t, o, key: $@"{o.Hive}\{o.Path}\{o.Name}".ToLowerInvariant())))
            .GroupBy(x => x.key).Where(g => g.Select(x => x.t.Id).Distinct().Count() > 1);
        foreach (var g in regs)
            foreach (var (t, o, _) in g)
                foreach (var (other, oo, _) in g.Where(x => x.t.Id != t.Id))
                {
                    var same = RegValueOp.ValuesEqual(RegValueOp.ConvertValue(o.Value, o.ValueKind), oo.Value);
                    Add(t.Id, same
                        ? new(AuditKind.Duplicate, $"Also set by '{other.Name}' ({o.Name}). Undoing one leaves the other's change active.", other.Id)
                        : new(AuditKind.Conflict, $"'{other.Name}' writes a different value to {o.Name}. Only one can be active.", other.Id));
                }

        // Command tweaks that read the same setting but set it differently (e.g. the DNS choices).
        var cmds = TweakCatalog.All
            .SelectMany(t => t.Ops.OfType<CommandOp>().Where(o => o.CheckCommand is not null).Select(o => (t, o)))
            .GroupBy(x => (x.o.CheckCommand, x.o.UndoCommand)).Where(g => g.Count() > 1);
        foreach (var g in cmds)
            foreach (var (t, o) in g)
                foreach (var (other, _) in g.Where(x => x.t.Id != t.Id && x.o.ApplyCommand != o.ApplyCommand))
                    Add(t.Id, new(AuditKind.Conflict, $"Changes the same setting as '{other.Name}'. Only one can be active; applying one replaces the other.", other.Id));
        return map;
    }
}
