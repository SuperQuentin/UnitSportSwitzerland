namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Layout;

/// <summary>
/// <c>RoadGen --gantry-benchmark TEMP</c> (#1110, <c>docs/notes/tools/direction-signs.md</c>): the inference rule for gantries
/// of lane signs measured on Geneva against SITG's real ones. Reads the rewrite's <c>gantry_facts.tsv</c> (every lit approach,
/// its facts) and <c>sitg_signs.tsv</c>; an approach of canton Geneva "has" a gantry where SITG has lane signs over it
/// (<see cref="SitgGantries.At"/>). Prints the rule's hits, misses and false placements, the lanes and colours against SITG's
/// signs, and a sweep of the rule's thresholds (best F1 first).
/// </summary>
public static class GantryBenchmark
{
    /// <summary>The scored truth: SITG lane signs this far before the approach's line, this far to its side (m).</summary>
    public const double LooseUpstream = 200, LooseSide = 25;

    private sealed record Row(GantryFacts Facts, string Canton, string Built, int Runs, int SitgSigns, string Colours);

    public static int Run(string temp, Action<string> log)
    {
        string factsPath = Path.Combine(temp, "gantry_facts.tsv"), sitgPath = Path.Combine(temp, "sitg_signs.tsv");
        if (!File.Exists(factsPath)) { log($"no {factsPath}: run RoadGen --rewrite first"); return 1; }
        var sitg = SitgGantries.Load(sitgPath);
        if (sitg is null) { log($"no {sitgPath}: run TerrainPreprocessor --sitg-signs first"); return 1; }
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(factsPath).Distinct())
        {
            if (line.StartsWith('#') || line.StartsWith("e\t", StringComparison.Ordinal)) continue;
            var f = line.Split('\t');
            if (f.Length < 15 || GantryFacts.Parse(line) is not { } facts) continue;
            int.TryParse(f[12], out int runs);
            int.TryParse(f[13], out int signs);
            rows.Add(new Row(facts, f[10], f[11], runs, signs, f[14]));
        }
        // the canton where the rewrite had none (switzerland.bin not found from where it ran)
        var cantons = UnitSport.Tools.RoadGen.Rewrite.TileRewriter.Cantons.Find();
        var ge = rows.Where(r => (r.Canton != "" ? r.Canton : cantons?.CodeAt(r.Facts.E, r.Facts.N)) == "GE").ToList();
        var strict = ge.Select(r => sitg.At(r.Facts.E, r.Facts.N, r.Facts.Bearing) is not null).ToList();
        // the looser truth: lane signs over the approach within LooseUpstream m before its line (Geneva signs it lane by lane)
        var loose = ge.Select(r => sitg.At(r.Facts.E, r.Facts.N, r.Facts.Bearing, LooseUpstream, LooseSide) is not null).ToList();
        var truth = loose;
        var c = CultureInfo.InvariantCulture;
        log(string.Create(c, $"lit approaches: {rows.Count:N0} ({ge.Count:N0} in canton Geneva); with SITG lane signs over the line (60 m before it): {strict.Count(t => t):N0}; within {LooseUpstream:F0} m before it (the truth scored): {loose.Count(t => t):N0}"));
        // SITG's gantries near the region's lights, matched or not
        var matched = new HashSet<SitgGantry>();
        foreach (var r in ge) if (sitg.At(r.Facts.E, r.Facts.N, r.Facts.Bearing) is { } g) matched.Add(g);
        double minE = rows.Count == 0 ? 0 : rows.Min(r => r.Facts.E) - 200, maxE = rows.Count == 0 ? 0 : rows.Max(r => r.Facts.E) + 200;
        double minN = rows.Count == 0 ? 0 : rows.Min(r => r.Facts.N) - 200, maxN = rows.Count == 0 ? 0 : rows.Max(r => r.Facts.N) + 200;
        int inArea = sitg.Gantries.Count(g => g.E >= minE && g.E <= maxE && g.N >= minN && g.N <= maxN);
        log($"SITG gantries of lane signs in the area: {inArea:N0}; over a lit approach: {matched.Count:N0}; the rest stand away from lights (advance lane signs) or at lights the region has not inferred");

        (int Tp, int Fp, int Fn) Score(GantryRule rule)
        {
            int tp = 0, fp = 0, fn = 0;
            for (int i = 0; i < ge.Count; i++)
            {
                bool want = rule.Wants(ge[i].Facts);
                if (want && truth[i]) tp++;
                else if (want) fp++;
                else if (truth[i]) fn++;
            }
            return (tp, fp, fn);
        }
        string Line(GantryRule rule)
        {
            var (tp, fp, fn) = Score(rule);
            double p = tp + fp == 0 ? 0 : tp / (double)(tp + fp), rc = tp + fn == 0 ? 0 : tp / (double)(tp + fn), f1 = p + rc == 0 ? 0 : 2 * p * rc / (p + rc);
            return string.Create(c, $"hits {tp,3}  misses {fn,3}  false {fp,4}  precision {p:P0}  recall {rc:P0}  F1 {f1:F2}   {rule}");
        }
        log("the user's plan (three lanes, or two going two ways; a main road or a motorway within 1 km), against the scored truth:");
        log("  " + Line(new GantryRule(3, 2, 2, 1, 1000, false)));
        log("the rule (default, tuned), against the scored truth:");
        log("  " + Line(GantryRule.Default));
        truth = strict;
        log("... and against lane signs over the line only:");
        log("  " + Line(GantryRule.Default));
        truth = loose;

        var sweep = new List<(double F1, GantryRule Rule)>();
        foreach (int minLanes in (int[])[1, 2, 3, 4])
            foreach (int turnLanes in (int[])[2, 3, 99])
                foreach (int turnSets in (int[])[2, 3])
                    foreach (int street in (int[])[1, 2])
                        foreach (double reach in (double[])[0, 200, 300, 400, 600, 1000, 1500])
                            foreach (bool both in (bool[])[false, true])
                            {
                                if (turnLanes == 99 && turnSets == 3) continue;
                                var rule = new GantryRule(minLanes, turnLanes, turnSets, street, reach, both);
                                var (tp, fp, fn) = Score(rule);
                                double p = tp + fp == 0 ? 0 : tp / (double)(tp + fp), rc = tp + fn == 0 ? 0 : tp / (double)(tp + fn);
                                sweep.Add((p + rc == 0 ? 0 : 2 * p * rc / (p + rc), rule));
                            }
        log("the sweep, best F1 first:");
        foreach (var (_, rule) in sweep.OrderByDescending(s => s.F1).Take(8)) log("  " + Line(rule));

        // where SITG builds them: its lanes and colours against what the rule's inference would give
        var built = ge.Where(r => r.Built == "sitg" && r.Runs > 0).ToList();
        if (built.Count > 0)
        {
            int same = built.Count(r => r.Runs == r.SitgSigns);
            log(string.Create(c, $"gantries from SITG: {built.Count:N0}; signs as many as the lane runs (one per way): {same:N0} ({same / (double)built.Count:P0}); fewer signs than runs {built.Count(r => r.SitgSigns < r.Runs):N0}, more {built.Count(r => r.SitgSigns > r.Runs):N0}"));
            var pairs = built.SelectMany(r => r.Colours.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
            int agree = pairs.Count(p => p.Length == 2 && p[0] == p[1]);
            string mix = string.Join(", ", pairs.GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => $"{Name(g.Key[0])} for {Name(g.Key[1])} {g.Count()}"));
            log(string.Create(c, $"sign colours, the inference against SITG ({pairs.Count:N0} signs): agree {agree:N0} ({(pairs.Count == 0 ? 0 : agree / (double)pairs.Count):P0}); {mix}"));
        }
        return 0;
    }

    private static string Name(char c) => c switch { 'G' => "green", 'B' => "blue", 'W' => "white", _ => "?" };
}
