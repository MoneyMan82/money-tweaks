using System.Text.Json;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Benchmarks;

/// <summary>
/// Temporarily switches a set of tweaks on or off for an A/B benchmark and puts every one back to its original state
/// afterwards. The original states are written to disk first, so an interrupted test (crash, app closed) can still be
/// restored on the next start.
/// </summary>
public static class TweakStateGuard
{
    public static string PendingPath => Path.Combine(AppPaths.Root, "synthetic-restore.json");

    /// <summary>Tweaks that can be switched for a quick A/B test (no restart needed, not disabled, not app removal).</summary>
    public static bool Eligible(Tweak t, out string? why)
    {
        why = t.Kind != TweakKind.Toggle ? "guide" : t.RequiresReboot ? "needs a restart to take effect"
            : TweakAudit.DisabledReason(t) is not null ? "disabled in Money Tweaks" : t.Category == TweakCategory.AppRemoval ? "app removal" : null;
        return why is null;
    }

    /// <summary>Original on/off state per tweak id. Tweaks that are partly applied or not present are left out.</summary>
    public static Dictionary<string, bool> Capture(IEnumerable<Tweak> tweaks, List<string>? skipped = null)
    {
        var d = new Dictionary<string, bool>();
        foreach (var t in tweaks)
        {
            switch (TweakEngine.GetState(t))
            {
                case TweakState.Applied: d[t.Id] = true; break;
                case TweakState.NotApplied: d[t.Id] = false; break;
                case var s: skipped?.Add($"{t.Name} ({s})"); break;
            }
        }
        return d;
    }

    public static void SavePending(Dictionary<string, bool> original)
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(original));
    }

    public static Dictionary<string, bool>? LoadPending()
    {
        try { return File.Exists(PendingPath) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(PendingPath)) : null; }
        catch (Exception ex) { Log.Error("synthetic-restore.json unreadable", ex); return null; }
    }

    public static void ClearPending() { try { File.Delete(PendingPath); } catch { } }

    /// <summary>Turns every tweak on or off. Returns problems; the engine verifies each change and rolls back failures.</summary>
    public static List<string> SetAll(IEnumerable<string> ids, bool on, Func<string, Tweak?>? find = null)
    {
        find ??= TweakCatalog.Find;
        var problems = new List<string>();
        foreach (var id in ids)
        {
            if (find(id) is not { } t) { problems.Add($"{id}: not found"); continue; }
            var st = TweakEngine.GetState(t);
            if (on == (st == TweakState.Applied)) continue;
            var r = on ? TweakEngine.Apply(t) : TweakEngine.Undo(t);
            if (!r.Success) problems.Add($"{t.Name}: {r.Message}");
        }
        return problems;
    }

    /// <summary>Puts every tweak back to its original state and reads each one back. Empty list = fully restored.</summary>
    public static List<string> Restore(Dictionary<string, bool> original, Func<string, Tweak?>? find = null)
    {
        find ??= TweakCatalog.Find;
        var problems = new List<string>();
        foreach (var (id, wasOn) in original)
        {
            if (find(id) is not { } t) { problems.Add($"{id}: not found"); continue; }
            try
            {
                var st = TweakEngine.GetState(t);
                if (wasOn != (st == TweakState.Applied))
                {
                    var r = wasOn ? TweakEngine.Apply(t) : TweakEngine.Undo(t);
                    if (!r.Success) problems.Add($"{t.Name}: {r.Message}");
                }
                var now = TweakEngine.GetState(t);
                if (wasOn != (now == TweakState.Applied)) problems.Add($"{t.Name}: reads {now} after restore, expected {(wasOn ? "Applied" : "NotApplied")}");
            }
            catch (Exception ex) { problems.Add($"{t.Name}: {ex.Message}"); }
        }
        Log.Info(problems.Count == 0 ? $"Synthetic benchmark: restored {original.Count} tweak(s) to their original state" : "Synthetic benchmark restore problems: " + string.Join("; ", problems));
        return problems;
    }
}
