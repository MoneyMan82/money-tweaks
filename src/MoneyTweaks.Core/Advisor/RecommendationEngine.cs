using System.Globalization;

namespace MoneyTweaks.Core.Advisor;

public sealed record AdvisorResult(IReadOnlyList<Recommendation> Top, IReadOnlyList<Recommendation> All, string? WhyFewer, IReadOnlyList<Recommendation> OverBudget, double? FpsNow, double Gap);

/// <summary>
/// The three smartest ways toward the FPS goal. Candidates come only from measured evidence (or hardware facts such as
/// XMP being off); each is scored by expected benefit (a projection when the measurements support one, otherwise a
/// stated qualitative level) × confidence, divided by cost. Free fixes cost nothing; hardware is only costed with a price
/// the user entered (with source and date) and is ranked conservatively until then. Hardware is left out when a free fix
/// is projected to reach the goal, and anything over budget is listed separately. Fewer than three only with a reason.
/// </summary>
public static class RecommendationEngine
{
    private enum Level { High, Medium, Low }
    private static double LevelValue(Level l) => l switch { Level.High => 0.6, Level.Medium => 0.3, _ => 0.1 };
    private static double ConfValue(string c) => c switch { "High" => 1.0, "Medium" => 0.7, _ => 0.4 };

    public static AdvisorResult Recommend(Diagnosis d, HardwareFacts hw, AdvisorGoal g, IReadOnlyDictionary<string, PriceEntry> prices)
    {
        double? now = d.MeasuredFps ?? g.CurrentFps;
        double gap = now is { } n ? g.TargetFps - n : g.TargetFps;
        var c = new List<(Recommendation r, double benefit)>();
        LimiterFactor? F(Limiter k) => d.Factors.FirstOrDefault(f => f.Kind == k);
        bool cpuBound = F(Limiter.Cpu) is { Score: >= 0.5 }, gpuBound = F(Limiter.Gpu) is { Score: >= 0.5 };
        string game = g.Game.Length > 0 ? g.Game : "your game";
        bool gameHasPotato = g.Game.Contains("Fortnite", StringComparison.OrdinalIgnoreCase) || g.Game.Contains("Minecraft", StringComparison.OrdinalIgnoreCase) || g.Game.Contains("Rocket League", StringComparison.OrdinalIgnoreCase);

        double Benefit(Projection? p, Level fallback) =>
            p is not null && now is { } nn && gap > 0 ? Math.Clamp((p.Mid - nn) / gap, 0, 1.2) : LevelValue(fallback);
        Projection? Upto(Projection? ceiling, double? from) =>
            ceiling is null || from is not { } f ? null : new(Math.Min(ceiling.Low, Math.Max(f, ceiling.Low)), Math.Max(f, ceiling.High), ceiling.Basis);

        void Add(string id, string title, RecTier tier, string difficulty, IEnumerable<string> evidence, string benefit, Projection? proj, Level level, string confidence,
                 IEnumerable<string> req, IEnumerable<string> trade, string? link, string? linkLabel, string? priceQuery = null, double factorScore = 0.8)
        {
            double? cost = tier == RecTier.Free ? 0 : prices.TryGetValue(id, out var p) ? p.Eur : null;
            string costText = tier == RecTier.Free ? "Free" : cost is { } ce ? $"€{ce:0} ({prices[id].Source}, {prices[id].Date:d})" : "Price not entered: use Find prices and enter what you find";
            double b = Benefit(proj, level) * ConfValue(confidence) * Math.Max(0.4, factorScore);
            double value = cost is { } cc ? b / (1 + cc / 100.0) : b * 0.35; // unknown price: ranked as if expensive, and said so
            c.Add((new Recommendation(id, title, tier, difficulty, cost, cost is not null, costText, evidence.Where(e => e.Length > 0).ToList(), benefit, proj, confidence,
                req.ToList(), trade.ToList(), link, linkLabel, priceQuery, value), b));
        }

        // ---- free fixes ---------------------------------------------------------------------------------------------------
        if (F(Limiter.FrameCap) is { } capF && d.CapFps is { } cap && g.TargetFps > cap)
        {
            var ceil = new[] { d.CpuCeiling, d.GpuCeiling }.Where(x => x is not null).Select(x => x!).ToList();
            Projection? proj = ceil.Count == 0 ? null : new(Math.Max(cap, ceil.Min(x => x.Low)), Math.Max(cap, ceil.Min(x => x.High)), "Up to whichever side (CPU or GPU) runs out of headroom first, from measured load.");
            Add("free.uncap", $"Remove the {cap:0} FPS cap", RecTier.Free, "Easy · 2 minutes", capF.Evidence,
                $"The hardware isn't the limit right now: the frame rate is held at {cap:0}. Turn off V-Sync (or set the in-game/driver frame limit above {g.TargetFps:0}) and the PC can run faster.",
                proj, Level.High, "High",
                [d.RefreshHz <= 0 ? "Your display's refresh rate couldn't be read." : d.RefreshHz >= g.TargetFps ? $"Your display refreshes at {d.RefreshHz:0} Hz, so the extra frames are visible." : $"Your display refreshes at {d.RefreshHz:0} Hz: above that you won't see more frames, though input latency still drops."],
                ["Without V-Sync you may see tearing; VRR (G-SYNC/FreeSync) with a limit 3 FPS under the refresh rate avoids it."], null, null, factorScore: capF.Score);
        }
        if (F(Limiter.GpuPower) is { } pw && hw.GpuMaxPowerLimitW is { } mx && hw.GpuPowerLimitW is { } pl && mx > pl + 2)
        {
            double headroom = mx / pl - 1;
            Projection? proj = now is { } nw ? new(nw, Math.Min(nw * (1 + headroom * 0.4), d.CpuCeiling?.High ?? double.MaxValue), "Clocks rise much less than power: at most about 40% of the extra power shows up as FPS, often less.") : null;
            Add("free.powerlimit", $"Raise the GPU power limit to {mx:0} W", RecTier.Free, "Easy · in Money Tweaks", pw.Evidence,
                "The card is slowed by its power limit while the game wants more. Raising the limit (within NVIDIA's range) lets it hold higher clocks.", proj, Level.Low, "Medium",
                ["NVIDIA card with an adjustable limit (yours is)", "Administrator rights"], ["More power use, heat and fan noise", "Resets when Windows restarts"], "tuning", "Open CPU & GPU Tuning", factorScore: pw.Score);
        }
        if (gpuBound && F(Limiter.Gpu) is { } gf)
        {
            var proj = Upto(d.CpuCeiling, now);
            Add("free.settings", "Lower GPU-heavy settings or use upscaling", RecTier.Free, "Easy · in the game",
                gf.Evidence.Append(d.CpuCeiling is { } cc ? $"Your CPU looks able to deliver roughly {cc.Low:0}–{cc.High:0} FPS here, so there's room above the current frame rate." : ""),
                $"The GPU is the limit, so less work per frame means more frames: lower shadows/effects/view distance, render resolution, or DLSS/FSR/XeSS Quality in {game}." +
                (gameHasPotato ? " Money Tweaks' Potato Graphics can set lighter presets for this game." : ""),
                proj, Level.High, "Medium", ["Supported upscaler in the game for DLSS/FSR"], ["Lower image quality; upscalers can blur fine detail"], gameHasPotato ? "potato" : null, gameHasPotato ? "Open Potato Graphics" : null, factorScore: gf.Score);
        }
        if (hw.XmpOff)
            Add("free.xmp", "Enable XMP/EXPO so RAM runs at its rated speed", RecTier.Free, "Medium · BIOS setting",
                [$"RAM runs at {hw.RamConfiguredMts} MT/s but the modules are rated {hw.RamRatedMts} MT/s."],
                "Faster memory helps most when the CPU is the limit" + (cpuBound ? " (it is here)." : " (it isn't the main limit here, so expect a smaller effect).") + " No reliable FPS estimate for your game can be given.",
                null, cpuBound ? Level.Medium : Level.Low, cpuBound ? "Medium" : "Low", ["BIOS access", "A board that supports the modules' profile"],
                ["Rarely unstable; the BIOS falls back if it doesn't boot", "Slightly higher memory power"], "cat:Bios", "Open BIOS guides");
        if (cpuBound && F(Limiter.Cpu) is { } cf)
        {
            var proj = Upto(d.GpuCeiling, now);
            Add("free.cpusettings", "Lower CPU-heavy settings", RecTier.Free, "Easy · in the game",
                cf.Evidence, $"The CPU is the limit: view distance, crowd/physics and draw-distance settings in {game} reduce CPU work per frame; lowering resolution won't help.",
                proj is null ? null : new(now ?? proj.Low, now is { } n2 ? n2 + (proj.Mid - n2) * 0.5 : proj.High, "At most part of the GPU's headroom; how much depends on the game's CPU-heavy settings."),
                Level.Medium, "Low", [], ["Less detail in the distance"], null, null, factorScore: cf.Score);
        }
        if (F(Limiter.Background) is { } bg)
            Add("free.background", "Close background apps while playing", RecTier.Free, "Easy · one click", bg.Evidence,
                "Other apps used CPU time the game could use. Close the ones you don't need with PC Cleaner (protected apps are never touched).",
                null, cpuBound ? Level.Medium : Level.Low, "Medium", [], ["Apps you close stop syncing/updating until reopened"], "cleaner", "Open PC Cleaner", factorScore: bg.Score);
        if (F(Limiter.CpuClocks) is { } ck)
            Add("free.cpuclocks", "Let the CPU reach its full clocks", RecTier.Free, "Easy · in Money Tweaks", ck.Evidence,
                "The CPU ran below base clock under load. Check the power plan and Windows boost setting (CPU & GPU Tuning measures the options) and that the PC isn't on battery saver.",
                null, cpuBound ? Level.Medium : Level.Low, "Medium", [], ["More power use and heat"], "tuning", "Open CPU & GPU Tuning", factorScore: ck.Score);
        if (F(Limiter.CpuThermal) is { } ct || F(Limiter.GpuThermal) is { } gt0)
        {
            var th = F(Limiter.CpuThermal) ?? F(Limiter.GpuThermal)!;
            Add("lowcost.cooling", "Fix cooling (dust, airflow, fan curve)", RecTier.LowCost, "Easy to medium", th.Evidence,
                "Parts at their temperature limit slow themselves down. Cleaning dust filters and fans is free; a GPU fan curve is in CPU & GPU Tuning; new case fans or thermal paste cost a little.",
                null, Level.Medium, "Medium", ["Free for cleaning; fans/paste need a price"], ["Faster fans are louder"], "tuning", "Open CPU & GPU Tuning", "case fan 120mm", th.Score);
        }
        if (F(Limiter.Memory) is { } mem)
            Add("free.memory", "Free up RAM while playing", RecTier.Free, "Easy · one click", mem.Evidence,
                "Windows ran short of RAM and read memory back from disk. Close memory-heavy apps (browsers are the usual cause) with PC Cleaner.",
                null, Level.Medium, "Medium", [], ["Closed apps lose their state"], "cleaner", "Open PC Cleaner", factorScore: mem.Score);
        if (F(Limiter.Vram) is { } vr)
            Add("free.textures", "Lower texture quality", RecTier.Free, "Easy · in the game", vr.Evidence,
                "The graphics memory is full, so the game streams textures from system RAM. One texture-quality step down usually fixes the stutter.",
                null, Level.High, "Medium", [], ["Blurrier surfaces up close"], null, null, factorScore: vr.Score);
        if (F(Limiter.Storage) is { } sto)
        {
            var ssd = hw.Storage.FirstOrDefault(x => x.Media == "SSD" && x.DriveLetters.Count > 0 && x != hw.GameDisk);
            if (hw.GameDisk is { Media: "HDD" } && ssd is not null)
                Add("free.movessd", $"Move {game} to your SSD ({string.Join(", ", ssd.DriveLetters.Select(l => l + ":"))})", RecTier.Free, "Medium · launcher's move option", sto.Evidence,
                    "Loading and texture streaming from a hard disk causes the spikes measured; your SSD is far faster for this.", null, Level.High, "Medium",
                    ["Enough free space on the SSD"], ["Takes a while to move"], null, null, factorScore: sto.Score);
            else if (hw.GameDisk is { Media: "HDD" })
                Add("hw.ssd", "Add an SSD for your games", RecTier.Hardware, "Medium · install + move games", sto.Evidence,
                    "Loading stutter from a hard disk largely disappears on an SSD. Doesn't raise average FPS.", null, Level.High, "Medium",
                    ["A free M.2 slot (NVMe) or a SATA port and power cable", "Check your motherboard manual"], ["Costs money", "Average FPS stays the same"], null, null, "NVMe SSD 1TB", sto.Score);
        }
        if (hw.DualChannel == false && hw.RamType is { } rt && (cpuBound || d.Mixed))
            Add("lowcost.dualchannel", $"Add a second matching {rt} stick (dual channel)", RecTier.LowCost, "Easy · plug in",
                [$"Only one RAM module is installed: memory runs in single-channel mode (half the bandwidth).", cpuBound ? "The CPU is the measured limit, where memory bandwidth matters most." : ""],
                "Dual-channel memory doubles bandwidth; CPU-limited games gain the most. No reliable FPS estimate for your game.", null, Level.Medium, "Medium",
                [$"Same type ({rt}), ideally the same speed and size{(hw.RamRatedMts is { } rr ? $" ({rr} MT/s)" : "")}", hw.RamSlots is { } sl ? $"{sl - hw.RamModules} free slot(s) reported" : "A free slot (check the board)"],
                ["Mixed sticks may run at the slower one's speed"], null, null, $"{rt} {hw.RamGb:0}GB {(hw.RamRatedMts is { } r3 ? r3.ToString(CultureInfo.InvariantCulture) : "")}".Trim());

        // ---- hardware upgrades -----------------------------------------------------------------------------------------
        if (gpuBound && F(Limiter.Gpu) is { } gf2)
        {
            var proj = Upto(d.CpuCeiling, now);
            bool cpuShort = d.CpuCeiling is { } cc2 && cc2.High < g.TargetFps;
            Add("hw.gpu", "Upgrade the graphics card", RecTier.Hardware, "Medium · install + driver",
                gf2.Evidence.Append(cpuShort ? $"But your CPU tops out around {d.CpuCeiling!.Low:0}–{d.CpuCeiling.High:0} FPS here, below your {g.TargetFps:0} target: a new GPU alone can't get there." : ""),
                d.CpuCeiling is { } c3 ? $"A faster GPU raises FPS until the CPU becomes the limit, roughly {c3.Low:0}–{c3.High:0} FPS here (projection). How close a given card gets depends on the card; Money Tweaks has no reliable per-card benchmark for {game}." : "A faster GPU raises FPS while the GPU is the limit.",
                proj, cpuShort ? Level.Low : Level.High, cpuShort ? "Low" : "Medium",
                ["Power supply wattage and PCIe power connectors (not readable: check the label)", "Case length/thickness clearance (measure)", "PCIe x16 slot"],
                ["Most expensive option", "Higher power and heat with bigger cards"], null, null, "graphics card", gf2.Score);
        }
        if (cpuBound && F(Limiter.Cpu) is { } cf2)
        {
            var proj = Upto(d.GpuCeiling, now);
            bool laptop = hw.Socket?.StartsWith("laptop", StringComparison.Ordinal) == true;
            if (!laptop)
            {
                var platform = hw.Platform ?? "Socket couldn't be identified: check your motherboard's CPU support list before buying.";
                Add("hw.cpu", "Upgrade the CPU", RecTier.Hardware, "Hard · BIOS update may be needed",
                    cf2.Evidence.Append(d.GpuCeiling is { } gc ? $"Your GPU could deliver roughly {gc.Low:0}–{gc.High:0} FPS here, so there's real room above today's frame rate." : ""),
                    $"A faster CPU (for gaming, high single-thread speed and large cache matter most) raises FPS until the GPU becomes the limit. {platform}.",
                    proj, Level.High, "Medium",
                    [hw.Socket is { } so ? $"Same socket ({so})" : "Matching socket (unknown here)", "BIOS version that supports the new CPU (update before swapping)", "A cooler that fits and handles it"],
                    ["Socket end-of-line platforms (AM4, LGA1200 and older) cap how far you can go without a new board and RAM"], null, null,
                    hw.Socket is "AM4" ? "Ryzen 7 5700X3D" : hw.Socket is "AM5" ? "Ryzen 7 7800X3D" : hw.Socket is "LGA1700" ? "Core i5-14600K" : "gaming CPU", cf2.Score);
            }
        }
        if (F(Limiter.Memory) is { } mem2 && hw.RamGb < 32 && hw.RamType is { } rt2)
            Add("hw.ram", $"More RAM ({(hw.RamGb < 16 ? 16 : 32)} GB {rt2})", RecTier.Hardware, "Easy · plug in", mem2.Evidence,
                "More RAM stops Windows paging to disk during play, which removes those hitches. It doesn't raise FPS when RAM isn't short.", null, Level.High, "Medium",
                [$"{rt2}, matching speed", hw.RamSlots is { } sl2 && sl2 > hw.RamModules ? $"{sl2 - hw.RamModules} free slot(s)" : "All slots used: replace with a larger kit"], ["Costs money"], null, null, $"{rt2} {(hw.RamGb < 16 ? 16 : 32)}GB kit", mem2.Score);

        // ---- ranking --------------------------------------------------------------------------------------------------
        var all = c.Select(x => x.r).ToList();
        var overBudget = all.Where(r => g.BudgetEur is { } b && r.CostEur is { } ce && ce > b).ToList();
        var pool = all.Except(overBudget).ToList();
        bool freeReaches = c.Any(x => x.r.Tier == RecTier.Free && x.r.Projected is { } p && p.Low >= g.TargetFps);
        if (freeReaches) pool = pool.Where(r => r.Tier != RecTier.Hardware).ToList();
        bool atTarget = now is { } nt && nt >= g.TargetFps * 0.98;
        // Already there: nothing to recommend for this goal (the planner still lists the options).
        var ranked = atTarget ? [] : pool.Where(r => r.Value > 0.005).OrderByDescending(r => r.Value).ThenBy(r => r.Tier).Take(3).ToList();
        for (int i = 0; i < ranked.Count; i++) ranked[i] = ranked[i] with { WhyRanked = WhyRanked(ranked, i, freeReaches) };

        string? why = null;
        if (atTarget) why = $"You already reach your target ({now:0} ≥ {g.TargetFps:0} FPS): nothing to buy or change for this goal. Raise the target to see what would get you further; the planner below still lists the options.";
        else if (ranked.Count < 3)
            why = (d.Factors.Count == 0 ? "The measurements don't point at a clear limit. " : "") +
                  $"Only {ranked.Count} option(s) are justified by the evidence; Money Tweaks doesn't pad the list with guesses." +
                  (freeReaches ? " Hardware is left out because a free fix is projected to reach your goal." : "") +
                  (overBudget.Count > 0 ? $" {overBudget.Count} option(s) are over your budget." : "") +
                  (!d.FpsMeasured ? " Measuring while playing (as administrator, for frame timing) usually reveals more." : "");
        return new(ranked, all, why, overBudget, now, gap);
    }

    private static string WhyRanked(List<Recommendation> r, int i, bool freeReaches)
    {
        var me = r[i];
        var parts = new List<string>();
        if (i == 0) parts.Add("Best expected benefit per euro of everything the evidence supports.");
        if (me.Tier == RecTier.Free) parts.Add("It's free.");
        if (i + 1 < r.Count)
        {
            var next = r[i + 1];
            if (me.Tier == RecTier.Free && next.Tier != RecTier.Free) parts.Add($"Ranks above \"{next.Title}\" because it costs nothing.");
            else if (me.Projected is not null && next.Projected is null) parts.Add($"Ranks above \"{next.Title}\" because its benefit can be projected from your measurements.");
            else parts.Add($"Ranks above \"{next.Title}\" on expected benefit × confidence per euro.");
        }
        if (me.Tier == RecTier.Hardware && !me.CostKnown) parts.Add("Its price isn't entered yet, so it's ranked as if expensive.");
        if (freeReaches && me.Tier == RecTier.Free) parts.Add("Hardware is left out: this is projected to reach your goal for free.");
        return string.Join(" ", parts);
    }

    /// <summary>Goal simulator: measured now vs. projections. Projections are ranges and labelled as such.</summary>
    public static List<Scenario> Simulate(Diagnosis d, AdvisorGoal g, AdvisorResult res)
    {
        var list = new List<Scenario>();
        double? now = d.MeasuredFps ?? g.CurrentFps;
        list.Add(new(d.FpsMeasured ? "Now (measured)" : "Now (your number)", now, now, d.FpsMeasured, d.FpsMeasured ? $"{d.Frames!.Frames:N0} frames measured; 1% low {d.Frames.Low1Fps:0}" : "Not measured: frame timing was unavailable."));
        var free = res.All.Where(r => r.Tier == RecTier.Free && r.Projected is not null).OrderByDescending(r => r.Projected!.Mid).FirstOrDefault();
        list.Add(free is not null ? new("Best free fix (projection)", free.Projected!.Low, free.Projected.High, false, free.Title)
                                  : new("Free fixes (projection)", null, null, false, "No free fix has a projectable FPS effect from these measurements; their benefit is described in words."));
        list.Add(d.CpuCeiling is { } cc && d.Factors.Any(f => f.Kind is Limiter.Gpu or Limiter.GpuPower)
            ? new("GPU upgrade: the most it can give (projection)", cc.Low, cc.High, false, "Until the CPU becomes the limit.")
            : new("GPU upgrade (projection)", null, null, false, "Not projected: the GPU isn't the measured limit, so a faster one wouldn't move the frame rate much."));
        list.Add(d.GpuCeiling is { } gc && d.Factors.Any(f => f.Kind == Limiter.Cpu)
            ? new("CPU upgrade: the most it can give (projection)", gc.Low, gc.High, false, "Until the GPU becomes the limit.")
            : new("CPU upgrade (projection)", null, null, false, "Not projected: the CPU isn't the measured limit."));
        return list;
    }

    /// <summary>Reputable retailers / price comparison sites for the user's country, as search links (prices are never fetched).</summary>
    public static IReadOnlyList<(string name, string url)> RetailerSearch(string query, string country)
    {
        string q = Uri.EscapeDataString(query);
        return country.ToUpperInvariant() switch
        {
            "FI" => [("Verkkokauppa.com", $"https://www.verkkokauppa.com/fi/search?query={q}"), ("Jimm's PC-Store", $"https://www.jimms.fi/fi/Product/Search?q={q}"), ("Proshop", $"https://www.proshop.fi/?s={q}"), ("Hintaopas (price comparison)", $"https://hintaopas.fi/search?search={q}")],
            "SE" => [("Inet", $"https://www.inet.se/hitta?q={q}"), ("Komplett", $"https://www.komplett.se/search?q={q}"), ("Prisjakt (price comparison)", $"https://www.prisjakt.nu/search?search={q}")],
            "NO" => [("Komplett", $"https://www.komplett.no/search?q={q}"), ("Prisjakt (price comparison)", $"https://www.prisjakt.no/search?search={q}")],
            "DK" => [("Proshop", $"https://www.proshop.dk/?s={q}"), ("Pricerunner (price comparison)", $"https://www.pricerunner.dk/results?q={q}")],
            "DE" or "AT" => [("Geizhals (price comparison)", $"https://geizhals.de/?fs={q}"), ("Mindfactory", $"https://www.mindfactory.de/search_result.php?search_query={q}"), ("Alternate", $"https://www.alternate.de/listing.xhtml?q={q}")],
            "GB" => [("Scan", $"https://www.scan.co.uk/search?q={q}"), ("Overclockers UK", $"https://www.overclockers.co.uk/search?sSearch={q}"), ("PCPartPicker UK", $"https://uk.pcpartpicker.com/search/?q={q}")],
            "US" => [("Newegg", $"https://www.newegg.com/p/pl?d={q}"), ("B&H", $"https://www.bhphotovideo.com/c/search?q={q}"), ("PCPartPicker", $"https://pcpartpicker.com/search/?q={q}")],
            _ => [("Geizhals EU (price comparison)", $"https://geizhals.eu/?fs={q}"), ("PCPartPicker", $"https://pcpartpicker.com/search/?q={q}")],
        };
    }
}
