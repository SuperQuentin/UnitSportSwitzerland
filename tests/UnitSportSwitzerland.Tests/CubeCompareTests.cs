using UnitSport.Tools.Preprocessor;
using Xunit;
using CompareLine = UnitSport.Tools.Preprocessor.Line;

namespace UnitSportSwitzerland.Tests;

/// <summary>The pure half of <c>--cube-compare</c> (#1507, G2 B3 PR 0): statistics, matching, bars.</summary>
public class CubeCompareTests
{
    [Fact]
    public void Spread_takes_nearest_rank_percentiles_of_sizes_and_a_signed_mean()
    {
        var values = Enumerable.Range(1, 100).Select(i => i % 2 == 0 ? i / 100.0 : -i / 100.0).ToList();
        var s = Spread.Of(values);
        Assert.Equal(100, s.Count);
        Assert.Equal(0.50, s.P50, 12);
        Assert.Equal(0.95, s.P95, 12);
        Assert.Equal(0.99, s.P99, 12);
        Assert.Equal(1.00, s.Max, 12);
        Assert.Equal(0.005, s.Mean, 12);   // 50 positive evens against 50 negative odds: +0.5 / 100
        Assert.Equal(default, Spread.Of(new List<double>()));
    }

    [Fact]
    public void Tally_agrees_with_the_exact_spread_to_its_bins()
    {
        var rng = new Random(7);
        var values = Enumerable.Range(0, 20000).Select(_ => (rng.NextDouble() - 0.5) * 0.2 * Math.Exp(rng.NextDouble() * 4)).ToList();
        var t = new Tally();
        foreach (double v in values) t.Add(v);
        var exact = Spread.Of(values.ToList());
        var binned = t.Spread();
        Assert.Equal(exact.Count, binned.Count);
        Assert.Equal(exact.Mean, binned.Mean, 9);
        Assert.Equal(exact.Max, binned.Max, 12);
        // a percentile is its bin's upper edge: never below the exact one, at most a bin above
        foreach (var (e, b) in new[] { (exact.P50, binned.P50), (exact.P95, binned.P95), (exact.P99, binned.P99) })
        {
            Assert.True(b >= e - 1e-12, $"{b} < {e}");
            Assert.True(b - e <= (e < 0.1 ? 0.00005 : 0.001) + 1e-12, $"{b} too far above {e}");
        }
        var merged = new Tally();
        merged.Merge(t);
        merged.Merge(t);
        Assert.Equal(2 * exact.Count, merged.Spread().Count);
        Assert.Equal(binned.P95, merged.Spread().P95);
    }

    [Fact]
    public void Features_pair_by_id_first_then_by_mutual_nearest_neighbour()
    {
        var a = new List<Feature>
        {
            new(0, 0, 0, Id: 11),
            new(100, 0, 0, Id: 12),
            new(50, 0, 50),             // no id: by position
            new(50.3, 0, 50),           // the second of two near one: left alone
            new(200, 0, 0, Id: 13),     // twice in a: no pairing by id
            new(210, 0, 0, Id: 13),
        };
        var b = new List<Feature>
        {
            new(100.01, 0, 0, Id: 11),  // the ids win over positions: 11 is a's first, 100 m away
            new(0.01, 0, 0, Id: 12),
            new(50.01, 0, 50),
            new(500, 0, 0, Id: 14),     // nowhere near anything
        };
        var m = FeatureMatch.Match(a, b, radius: 1);
        Assert.Contains((0, 0, true), m.Pairs);
        Assert.Contains((1, 1, true), m.Pairs);
        Assert.Contains((2, 2, false), m.Pairs);
        Assert.Equal(3, m.Pairs.Count);
        Assert.Equal(new[] { 3, 4, 5 }, m.OnlyA);
        Assert.Equal(new[] { 3 }, m.OnlyB);
    }

    [Fact]
    public void Matching_by_class_keeps_classes_apart()
    {
        var a = new List<Feature> { new(0, 0, 0, Class: 1) };
        var b = new List<Feature> { new(0.1, 0, 0, Class: 2), new(0.5, 0, 0, Class: 1) };
        Assert.Equal((0, 1, false), FeatureMatch.Match(a, b, 1).Pairs.Single());
        Assert.Equal((0, 0, false), FeatureMatch.Match(a, b, 1, byClass: false).Pairs.Single());
    }

    [Fact]
    public void An_offset_is_taken_above_ground_when_both_are_draped()
    {
        var (h, v) = FeatureMatch.Offset(new Feature(0, 500, 0, Above: 0.10), new Feature(0.03, 499.2, 0.04, Above: 0.12));
        Assert.Equal(0.05, h, 12);
        Assert.Equal(0.02, v, 12);
        Assert.Equal(-0.8, FeatureMatch.Offset(new Feature(0, 500, 0), new Feature(0, 499.2, 0)).V, 9);
    }

    [Fact]
    public void A_vertex_finds_the_nearest_line_of_its_class_in_plan()
    {
        var lines = new List<CompareLine>
        {
            new([0, 0, 0, 10, 2, 0, 20, 2, 0], 1, [0, 0.1, 0.1]),
            new([0, 0, 1, 20, 0, 1], 2),
        };
        var index = new LineIndex(lines, cell: 4);
        Assert.True(index.Nearest(5, 3, 0.03, 0.12, 1, 5, out double h, out double dy, out double dAbove));
        Assert.Equal(0.03, h, 12);
        Assert.Equal(2, dy, 12);           // the line is at y 1 halfway along its first segment
        Assert.Equal(0.07, dAbove, 12);    // and 0.05 above its ground there
        Assert.True(index.Nearest(5, 0, 0.9, double.NaN, 2, 5, out h, out _, out dAbove));
        Assert.Equal(0.1, h, 12);
        Assert.True(double.IsNaN(dAbove));
        Assert.False(index.Nearest(5, 0, 7, double.NaN, 1, 5, out _, out _, out _));
        Assert.False(index.Nearest(5, 0, 0, double.NaN, 3, 5, out _, out _, out _));
        // past the end the nearest point is the end
        Assert.True(index.Nearest(23, 2, 4, double.NaN, 1, 10, out h, out _, out _));
        Assert.Equal(5, h, 12);
    }

    [Fact]
    public void A_length_counts_the_edges_its_filter_keeps_in_plan()
    {
        // x, z in plan; the first edge also climbs 4 m (y), which a plan length leaves out
        var line = new CompareLine([0, 0, 0, 3, 4, 0, 3, 4, 10], 0);
        Assert.Equal(13, LineIndex.Length(line), 12);
        Assert.Equal(9, LineIndex.Length(line, (x, _, z) => z > 1), 6);   // the climb from z 0 to 10 cut at z 1
    }

    [Fact]
    public void Agreement_is_counted_per_class_of_the_reference()
    {
        var a = new Agreement();
        for (int i = 0; i < 1000; i++) a.Add(1, i < 996 ? 1 : 2);
        for (int i = 0; i < 10; i++) a.Add(2, 2);
        var other = new Agreement();
        other.Add(3, 1);
        a.Merge(other);
        var per = a.PerClass().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, per.Select(p => p.Class));
        Assert.Equal(0.996, per[0].Share, 12);
        Assert.Equal(1.0, per[1].Share, 12);
        Assert.Equal(0.0, per[2].Share, 12);
        Assert.Equal(1011, a.Count);
        Assert.Equal(1006 / 1011.0, a.Overall, 12);
    }

    [Fact]
    public void The_shift_fit_recovers_a_moved_surface()
    {
        // dh = c − ∇h·s for a cube surface moved by s = (+3 cm east, −2 cm north), 1 cm lower, with noise
        var rng = new Random(3);
        var fit = new ShiftFit();
        for (int k = 0; k < 50000; k++)
        {
            double ge = rng.NextDouble() * 2 - 1, gn = rng.NextDouble() * 2 - 1;
            double noise = (rng.NextDouble() - 0.5) * 0.07;   // the LV95 quantum
            fit.Add(-0.01 - (ge * 0.03 + gn * -0.02) + noise, ge, gn);
        }
        var (e, n, bias) = fit.Solve();
        Assert.Equal(0.03, e, 3);
        Assert.Equal(-0.02, n, 3);
        Assert.Equal(-0.01, bias, 3);
        // flat ground cannot tell a shift: only the bias comes out
        var flat = new ShiftFit();
        for (int k = 0; k < 10; k++) flat.Add(0.02, 0, 0);
        var (fe, fn, fb) = flat.Solve();
        Assert.Equal((0.0, 0.0), (fe, fn));
        Assert.Equal(0.02, fb, 12);
    }

    [Fact]
    public void A_bar_breaks_only_above_its_limit()
    {
        var r = new KindReport("roads");
        var s = Spread.Of(Enumerable.Range(1, 100).Select(i => i / 1000.0).ToList());   // p99 9.9 cm, max 10 cm
        r.Check("vertex", s, true, new Bar(Stat.P99, 0.099), new Bar(Stat.Max, 0.0999));
        Assert.Single(r.Breaches);
        Assert.Contains("max 10.00 cm > 9.99", r.Breaches[0]);
        r.AtLeast("agreement", 0.9949, 0.995);
        r.AtMost("alone", 0, 0, "F0");
        Assert.Equal(2, r.Breaches.Count);
        Assert.Equal(3, r.Lines.Count);
        Assert.Equal(0.0, Density.Relative(0, 0));
        Assert.Equal(0.01, Density.Relative(1000, 990), 12);
    }
}
