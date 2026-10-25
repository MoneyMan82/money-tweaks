using System.Globalization;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Engine;

/// <summary>One setting a tweak step writes, normalised so steps of different kinds that touch the same thing compare equal.</summary>
/// <see cref="Value"/> is compared; <see cref="Shown"/> is how it reads in evidence (a command's label instead of its full text).
public sealed record RadarEffect(string Key, string Location, string Value, string Step, string? Shown = null)
{
    public string ShownValue => Shown ?? Value;
}

public enum RadarKind
{
    /// <summary>Two tweaks set the same thing to the same value.</summary>
    Duplicate,
    /// <summary>Two tweaks set the same thing to different values.</summary>
    Contradiction,
    /// <summary>The result depends on the order (e.g. power-plan settings written before the plan is switched).</summary>
    OrderMatters,
    /// <summary>A documented interaction from the tweak catalog's notes (no shared setting, but one affects the other).</summary>
    KnownInteraction,
}

public enum RadarFixKind { ApplyAnyway, SkipSelected, MoveFirst, UndoAppliedFirst }

/// <summary>A way to resolve a conflict. Only <see cref="RadarFixKind.UndoAppliedFirst"/> changes the system (through Undo).</summary>
public sealed record RadarFix(RadarFixKind Kind, string Label, string Detail, string TweakId)
{
    public bool ChangesSystem => Kind == RadarFixKind.UndoAppliedFirst;
}

/// <summary>
/// A conflict between <see cref="Selected"/> (about to be applied) and <see cref="Other"/>: earlier in the same selection, or
/// already applied (<see cref="OtherApplied"/>). Evidence lists the shared settings with both values.
/// </summary>
public sealed record RadarConflict(RadarKind Kind, Tweak Selected, Tweak Other, bool OtherApplied, IReadOnlyList<string> Evidence,
    string Explanation, IReadOnlyList<RadarFix> Fixes)
{
    public string KindLabel => Kind switch
    {
        RadarKind.Duplicate => "Duplicate",
        RadarKind.Contradiction => "Contradiction",
        RadarKind.OrderMatters => "Order matters",
        _ => "Known interaction",
    };
}

/// <summary>
/// Tweak Conflict Radar: compares what tweaks actually write (registry values and string entries, service start types,
/// power-plan settings, scheduled tasks, Store apps, and command steps that read the same setting) between the tweaks
/// about to be applied and against the ones already applied. Only shared settings and the catalog's documented
/// interactions are reported; nothing is guessed, and nothing is changed here.
/// </summary>
public static class ConflictRadar
{
    private const string ServicesPrefix = @"hklm\system\currentcontrolset\services\";

    /// <summary>
    /// The settings a tweak writes. Steps that discover their targets on this PC (network adapters, GPUs…) are resolved
    /// only when <paramref name="resolveDynamic"/> is set, since that reads the registry.
    /// </summary>
    public static List<RadarEffect> Effects(Tweak t, bool resolveDynamic = true)
    {
        var list = new List<RadarEffect>();
        foreach (var op in t.Ops)
            switch (op)
            {
                case RegValueOp r: list.Add(Reg(r)); break;
                case DynamicRegOp d when resolveDynamic:
                    try { list.AddRange(d.Resolve().Select(r => Reg(r) with { Step = d.Describe() })); } catch { /* discovery failed: nothing to compare */ }
                    break;
                case RegStringFlagOp f:
                    list.Add(new($@"regflag:{f.Hive}\{f.Path}\{f.Name}#{f.Flag}".ToLowerInvariant(), $@"{f.Hive}\{f.Path}\{f.Name} entry {f.Flag}", f.Value, f.Describe()));
                    break;
                case ServiceStartOp s:
                    list.Add(new("reg:" + ServicesPrefix + s.Service.ToLowerInvariant() + @"\start", $@"Service {s.Service} start type (HKLM\SYSTEM\CurrentControlSet\Services\{s.Service}\Start)",
                        StartText(s.Start), s.Describe()));
                    break;
                case PowerCfgOp p:
                    list.Add(new("power:" + p.SettingKey.ToLowerInvariant(), $"Active power plan › {p.Label} ({p.Subgroup} {p.Setting})", p.Value.ToString(CultureInfo.InvariantCulture), p.Describe()));
                    break;
                case ScheduledTaskOp st:
                    list.Add(new("task:" + st.TaskPath.ToLowerInvariant(), "Scheduled task " + st.TaskPath, "disabled", st.Describe()));
                    break;
                case AppxRemoveOp a:
                    list.Add(new("appx:" + a.PackageName.ToLowerInvariant(), "Store app " + a.PackageName, "removed", a.Describe()));
                    break;
                case CommandOp c when c.CheckCommand is not null:
                    // Commands that check the same setting and undo it the same way change the same thing (e.g. DNS choices).
                    list.Add(new($"cmd:{c.CheckCommand}|{c.UndoCommand}", "Setting checked with: " + Short(c.CheckCommand, 90), c.ApplyCommand, c.Describe(),
                        c.Label is { } l ? $"\"{l}\"" : Short(c.ApplyCommand, 120)));
                    break;
            }
        return list;
    }

    private static RadarEffect Reg(RegValueOp r)
    {
        string key = $@"reg:{(r.Hive == Hive.HKLM ? "hklm" : "hkcu")}\{r.Path}\{r.Name}".ToLowerInvariant();
        string value;
        try
        {
            var v = RegValueOp.ConvertValue(r.Value, r.ValueKind);
            // A service's Start value written as a plain registry value compares with service start-type steps.
            value = key.StartsWith("reg:" + ServicesPrefix, StringComparison.Ordinal) && key.EndsWith(@"\start", StringComparison.Ordinal) && v is int i
                ? StartText(i) : $"{RegValueOp.FormatValue(v, r.ValueKind)} ({r.ValueKind})";
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { value = $"{r.Value} ({r.ValueKind})"; }
        return new(key, $@"{r.Hive}\{r.Path}\{(r.Name.Length == 0 ? "(Default)" : r.Name)}", value, r.Describe());
    }

    /// <summary>What Undo does for this tweak, as Tweak X-Ray describes it (commands run their reverse; values come from the backup).</summary>
    private static string UndoText(Tweak t) => (t.Ops.Any(o => o is CommandOp)
        ? $"Undo runs '{t.Name}''s reverse command and checks the result"
        : $"Undo restores the values backed up when '{t.Name}' was applied and reads them back") + "; you can apply it again later.";

    private static string Short(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    private static string StartText(int start) => $"{start} ({ServiceStartOp.StartName(start)})";

    /// <summary>Shared settings between two effect lists: (location, value in a, value in b, both as shown in evidence).</summary>
    public static List<(string location, string a, string b, string shownA, string shownB)> Shared(IEnumerable<RadarEffect> a, IEnumerable<RadarEffect> b)
    {
        var bk = b.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
        return a.Where(e => bk.ContainsKey(e.Key)).DistinctBy(e => e.Key).Select(e => (e.Location, e.Value, bk[e.Key].Value, e.ShownValue, bk[e.Key].ShownValue)).ToList();
    }

    /// <summary>
    /// Conflicts for applying <paramref name="selection"/> in this order. <paramref name="applied"/> lists tweaks currently
    /// applied by Money Tweaks (callers pass ones with a backup that read back as applied).
    /// </summary>
    public static List<RadarConflict> Scan(IReadOnlyList<Tweak> selection, IEnumerable<Tweak> applied)
    {
        var sel = selection.Where(t => t.Kind == TweakKind.Toggle).DistinctBy(t => t.Id).ToList();
        var effects = sel.ToDictionary(t => t.Id, t => Effects(t));
        var appliedList = applied.Where(a => a.Kind == TweakKind.Toggle && sel.All(s => s.Id != a.Id)).ToList();
        var result = new List<RadarConflict>();

        for (int j = 0; j < sel.Count; j++)
        {
            var t = sel[j];
            // Earlier in the same selection: the later one is the one to skip or keep.
            for (int i = 0; i < j; i++)
                if (Pair(t, sel[i], effects[t.Id], effects[sel[i].Id], otherApplied: false) is { } c) result.Add(c);
            foreach (var a in appliedList)
                if (Pair(t, a, effects[t.Id], Effects(a), otherApplied: true) is { } c) result.Add(c);
        }
        result.AddRange(PlanOrder(sel));
        result.AddRange(Interactions(sel, appliedList, result));
        return result;
    }

    /// <summary>
    /// <see cref="Scan"/> against the tweaks Money Tweaks has applied: those with a backup that share a setting (or a catalog
    /// note) with the selection and still read back as applied. Runs state checks: call off the UI thread.
    /// </summary>
    public static List<RadarConflict> ScanForApply(IReadOnlyList<Tweak> selection, Func<Tweak, TweakState>? stateOf = null)
    {
        stateOf ??= TweakEngine.GetState;
        var keys = selection.SelectMany(t => Effects(t)).Select(e => e.Key).ToHashSet();
        var noted = selection.SelectMany(TweakAudit.IssuesFor).Where(i => i.Kind == AuditKind.Conflict && i.Other is not null).Select(i => i.Other!).ToHashSet();
        var applied = Compatibility.Universe()
            .Where(o => o.Kind == TweakKind.Toggle && selection.All(s => s.Id != o.Id) && BackupStore.Exists(o.Id)
                && (noted.Contains(o.Id) || Effects(o).Any(e => keys.Contains(e.Key))))
            .Where(o => stateOf(o) is TweakState.Applied or TweakState.Partial)
            .ToList();
        return Scan(selection, applied);
    }

    private static RadarConflict? Pair(Tweak t, Tweak other, List<RadarEffect> te, List<RadarEffect> oe, bool otherApplied)
    {
        var shared = Shared(te, oe);
        if (shared.Count == 0) return null;
        var differ = shared.Where(s => !string.Equals(s.a, s.b, StringComparison.OrdinalIgnoreCase)).ToList();
        string where = otherApplied ? "applied" : "also selected";
        if (differ.Count > 0)
        {
            var evidence = differ.Select(s => $"{s.location}: '{t.Name}' sets {s.shownA}, '{other.Name}' ({where}) sets {s.shownB}").ToList();
            if (otherApplied)
                return new(RadarKind.Contradiction, t, other, true, evidence,
                    $"'{other.Name}' is applied and sets {(differ.Count == 1 ? "this setting" : "these settings")} differently. Applying '{t.Name}' on top would overwrite its change and its Undo would then restore the wrong value, so Money Tweaks won't apply it while '{other.Name}' is active.",
                    [new(RadarFixKind.SkipSelected, $"Skip '{t.Name}'", "Nothing changes; '" + other.Name + "' stays applied.", t.Id),
                     new(RadarFixKind.UndoAppliedFirst, $"Undo '{other.Name}' first, then apply '{t.Name}'", UndoText(other), other.Id)]);
            return new(RadarKind.Contradiction, t, other, false, evidence,
                $"Both are selected but write different values to the same setting. Only one can be in effect; after the first is applied, Money Tweaks refuses the second.",
                [new(RadarFixKind.SkipSelected, $"Keep '{other.Name}', skip '{t.Name}'", "Applies the one earlier in the list.", t.Id),
                 new(RadarFixKind.SkipSelected, $"Keep '{t.Name}', skip '{other.Name}'", "Applies this one instead.", other.Id)]);
        }
        var same = shared.Select(s => $"{s.location}: both set {s.shownA}").ToList();
        bool covers = shared.Count == te.Count;
        return new(RadarKind.Duplicate, t, other, otherApplied, same,
            (otherApplied ? $"'{other.Name}' is applied and already sets " : $"'{other.Name}' (also selected) sets ")
            + (covers ? "everything this tweak sets, so applying it changes nothing." : "part of what this tweak sets.")
            + $" Undoing either one later reverts the shared {(shared.Count == 1 ? "setting" : "settings")} for both.",
            otherApplied || !covers
                ? [new(RadarFixKind.ApplyAnyway, "Apply anyway", covers ? "Nothing is written for the shared settings." : "Writes the settings that aren't shared.", t.Id),
                   new(RadarFixKind.SkipSelected, $"Skip '{t.Name}'", "Nothing changes for it.", t.Id)]
                : [new(RadarFixKind.SkipSelected, $"Skip '{t.Name}'", $"'{other.Name}' already makes the same change.", t.Id),
                   new(RadarFixKind.ApplyAnyway, "Apply both", "The second one finds everything already set and changes nothing.", t.Id)]);
    }

    /// <summary>A command that switches the active power plan, selected after tweaks that write to the active plan.</summary>
    private static IEnumerable<RadarConflict> PlanOrder(List<Tweak> sel)
    {
        for (int j = 0; j < sel.Count; j++)
        {
            var switcher = PlanSwitch(sel[j]);
            if (switcher is null) continue;
            foreach (var before in sel.Take(j).Where(t => t.Ops.OfType<PowerCfgOp>().Any()))
                yield return new(RadarKind.OrderMatters, before, sel[j], false,
                    [$"'{sel[j].Name}' runs: {switcher.ApplyCommand}", $"'{before.Name}' writes {string.Join(", ", before.Ops.OfType<PowerCfgOp>().Select(p => p.Label))} to the plan that is active at that moment (powercfg … scheme_current)"],
                    $"'{before.Name}' is listed before '{sel[j].Name}', so its setting would go into the current plan and then '{sel[j].Name}' switches to another plan without it.",
                    [new(RadarFixKind.MoveFirst, $"Apply '{sel[j].Name}' first", "Reorders this run so the plan is switched before its settings are written.", sel[j].Id),
                     new(RadarFixKind.ApplyAnyway, "Keep this order", "The setting stays in the previous plan.", before.Id)]);
        }
    }

    /// <summary>The catalog's documented interactions that name another tweak, when both are involved and not already reported.</summary>
    private static List<RadarConflict> Interactions(List<Tweak> sel, List<Tweak> applied, List<RadarConflict> already)
    {
        var found = already.ToList();
        var added = new List<RadarConflict>();
        var involved = sel.Concat(applied).ToDictionary(t => t.Id);
        foreach (var t in sel)
            foreach (var note in TweakAudit.IssuesFor(t).Where(i => i.Kind == AuditKind.Conflict && i.Other is not null))
            {
                if (!involved.TryGetValue(note.Other!, out var other) || other.Id == t.Id) continue;
                if (found.Any(c => (c.Selected.Id == t.Id && c.Other.Id == other.Id) || (c.Selected.Id == other.Id && c.Other.Id == t.Id))) continue;
                int ti = sel.IndexOf(t), oi = sel.IndexOf(other);
                // A power-plan switch selected before the plan settings is the right order: the settings go into the new plan.
                if (oi >= 0 && oi < ti && IsPlanSwitch(other)) continue;
                bool otherApplied = oi < 0;
                // Reported on the later of the two in this run (the one to skip), with the note's text as evidence.
                var (later, earlier) = !otherApplied && oi > ti ? (other, t) : (t, other);
                found.Add(new(RadarKind.KnownInteraction, later, earlier, otherApplied, [$"Catalog note on '{t.Name}': {note.Text}"],
                    $"These don't write the same setting, but one affects the other ('{earlier.Name}' is {(otherApplied ? "applied" : "also selected")}).",
                    [new(RadarFixKind.ApplyAnyway, "Apply both", "Both are applied as listed.", later.Id),
                     new(RadarFixKind.SkipSelected, $"Skip '{later.Name}'", "Nothing changes for it.", later.Id)]));
                added.Add(found[^1]);
            }
        return added;
    }

    private static bool IsPlanSwitch(Tweak t) => PlanSwitch(t) is not null;

    private static CommandOp? PlanSwitch(Tweak t) => t.Ops.OfType<CommandOp>().FirstOrDefault(c =>
        c.ApplyCommand.Contains("powercfg", StringComparison.OrdinalIgnoreCase) && c.ApplyCommand.Contains("/setactive", StringComparison.OrdinalIgnoreCase));
}
