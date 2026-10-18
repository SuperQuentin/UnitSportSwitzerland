using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.TestRegion;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The test region built once into a temp dir for every test of the class (~2 s).</summary>
public sealed class SignalTestRegionFixture : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "unitsport-386-" + Guid.NewGuid().ToString("N"));
    public readonly int Code;
    public readonly List<string> Log = new();

    public SignalTestRegionFixture() => Code = SignalTestRegion.Run(Dir, null, Log.Add);

    public IEnumerable<(string Path, RoadTile Tile)> Tiles()
    {
        foreach (string path in Directory.EnumerateFiles(Dir, "roads_*.road"))
        {
            using var fs = File.OpenRead(path);
            yield return (path, RoadCodec.Decode(fs));
        }
    }

    public RoadTile Tile(double e, double n)
    {
        using var fs = File.OpenRead(Path.Combine(Dir, RoadFormat.FileName(TileId.FromLv95(e, n))));
        return RoadCodec.Decode(fs);
    }

    public SignalTestRegion.Junction Junction(string prefix) =>
        SignalTestRegion.Junctions.Single(j => j.Name.StartsWith(prefix, StringComparison.Ordinal));

    public void Dispose()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        if (Directory.Exists(Dir + "_temp")) Directory.Delete(Dir + "_temp", recursive: true);
    }
}

/// <summary>
/// The traffic-lights test region (#386, <c>RoadGen --test-region</c>): built from scratch into a
/// temp dir by the real network stage, every designed approach gets its lane set, every plan is
/// valid, and the stem of the T turns only left and right. #406: hatches closed and never under
/// 1.5 m x 20 m, no car stop line across a bike lane with an advanced line, red bike crossings only where
/// a car crosses in the same phase. (#818: the cores' corners are the planner's, <c>JunctionPlannerTests</c>.)
/// </summary>
public class SignalTestRegionTests(SignalTestRegionFixture region) : IClassFixture<SignalTestRegionFixture>
{
    [Fact]
    public void Region_builds_with_the_designed_lanes_and_valid_plans()
    {
        Assert.True(region.Code == 0, string.Join("\n", region.Log.Where(l => l.StartsWith("J", StringComparison.Ordinal) || l.Contains("RESULT"))));

        int signals = 0;
        foreach (var (_, tile) in region.Tiles())
        {
            foreach (var s in tile.Signals)
            {
                signals++;
                Assert.Empty(s.Plan.Validate());
            }
            foreach (var a in tile.Approaches)
                Assert.All(a.Lanes, l => Assert.NotEqual(SignalMoves.None, l.Moves));
        }
        Assert.Equal(SignalTestRegion.Junctions.Count, signals);

        // the T's stem (J4, from the south): no straight on, so no lane goes straight
        var tee = region.Junction("J4");
        var stem = region.Tile(tee.E, tee.N).Approaches.Single(a => a.Signal >= 0 && Math.Sin(a.Heading) < -0.95);
        Assert.All(stem.Lanes, l => Assert.Equal(SignalMoves.None, l.Moves & SignalMoves.Through));
    }

    /// <summary>Plan-view (x east, z south) points of a paint or prop.</summary>
    private static List<(double X, double Z)> Points(float[] v)
    {
        var list = new List<(double, double)>();
        for (int i = 0; i + 2 < v.Length; i += 3) list.Add((v[i], v[i + 2]));
        return list;
    }

    [Fact]
    public void Every_hatch_is_closed_and_at_least_1_5_by_20_m()
    {
        int hatches = 0;
        foreach (var (path, tile) in region.Tiles())
        {
            var solid = tile.Paint.Where(p => p.Type == PaintType.WhiteSolid && p.Shape == PaintShape.Polyline).Select(p => Points(p.Vertices)).ToList();
            var meshes = tile.Paint.Where(p => p.Type == PaintType.Hatch).Select(p => Points(p.Vertices)).ToList();
            foreach (var hatch in tile.Paint.Where(p => p.Type == PaintType.Hatch))
            {
                hatches++;
                var pts = Points(hatch.Vertices);
                // its axis: the stripes lie along the road
                double mx = pts.Average(p => p.X), mz = pts.Average(p => p.Z);
                double sxx = pts.Sum(p => (p.X - mx) * (p.X - mx)), szz = pts.Sum(p => (p.Z - mz) * (p.Z - mz)), sxz = pts.Sum(p => (p.X - mx) * (p.Z - mz));
                double angle = 0.5 * Math.Atan2(2 * sxz, sxx - szz);
                (double X, double Z) u = (Math.Cos(angle), Math.Sin(angle)), n = (-u.Z, u.X);
                (double A, double C) Frame((double X, double Z) p) => ((p.X - mx) * u.X + (p.Z - mz) * u.Z, (p.X - mx) * n.X + (p.Z - mz) * n.Z);
                var local = pts.Select(Frame).ToList();
                double lo = local.Min(p => p.A), hi = local.Max(p => p.A), reach = hi - lo;
                // its wide end: the end whose last 3 m of stripes spread widest across (stripes cut there, at a mouth or a stop line)
                double Spread(Func<(double A, double C), bool> near) => local.Where(near).Select(p => p.C).DefaultIfEmpty(0).Max() - local.Where(near).Select(p => p.C).DefaultIfEmpty(0).Min();
                bool atHi = Spread(p => p.A > hi - 3) > Spread(p => p.A < lo + 3);
                var end = local.Where(p => atHi ? p.A > hi - 3 : p.A < lo + 3).ToList();
                double c0 = end.Min(p => p.C), c1 = end.Max(p => p.C), own = c1 - c0;
                // its width with the hatch across the centre line from it, if any (a median split by the centre line: the lead-in
                // on one side, the exit on the other)
                var halves = meshes.Where(m => m.Any(q => pts.Any(p => Math.Abs(p.X - q.X) + Math.Abs(p.Z - q.Z) < 0.6))).SelectMany(m => m).Select(Frame).ToList();
                // (at its widest 3 m: a lead-in's against its pocket, between two narrow ends)
                double wide = 0;
                for (double w0 = lo; w0 < hi; w0 += 0.5)
                {
                    var here = halves.Where(p => p.A >= w0 && p.A < w0 + 3).ToList();
                    if (here.Count > 0) wide = Math.Max(wide, here.Max(p => p.C) - here.Min(p => p.C));
                }
                // closed: across its whole wide end a solid line runs past the stripes' end, square (a mouth's, a stop line's) or angled
                // back along the road (#700: a lead-in's, the gentle lead into the pocket), within 15 m
                bool Crossed(double c) => solid.Any(q =>
                {
                    for (int k = 1; k < q.Count; k++)
                    {
                        var (a0, d0) = Frame(q[k - 1]);
                        var (a1, d1) = Frame(q[k]);
                        if ((d0 - c) * (d1 - c) > 0 || Math.Abs(d1 - d0) < 1e-6) continue;
                        double at = a0 + (a1 - a0) * (c - d0) / (d1 - d0);
                        double beyond = atHi ? at - hi : lo - at;
                        if (beyond > -1 && beyond < 15) return true;
                    }
                    return false;
                });
                // (a lead-in that runs out to a point at both ends, its widest part against the pocket, has no cut end)
                double widest = 0;
                for (double w0 = lo; w0 < hi; w0 += 0.5) widest = Math.Max(widest, Spread(p => p.A >= w0 && p.A < w0 + 3));
                if (own >= 0.8 * widest)
                    foreach (double f in (ReadOnlySpan<double>)[0.15, 0.5, 0.85])
                        Assert.True(Crossed(c0 + (c1 - c0) * f), $"{path}: a hatch around ({mx:F0},{mz:F0}) has no line across its wide end");
                // and bordered all round: every stripe ends on a solid line (a border, or the angled lead into a pocket)
                double Off((double X, double Z) p) => solid.Min(q =>
                {
                    double best = double.MaxValue;
                    for (int k = 1; k < q.Count; k++)
                    {
                        double dx = q[k].X - q[k - 1].X, dz = q[k].Z - q[k - 1].Z, len2 = dx * dx + dz * dz;
                        double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - q[k - 1].X) * dx + (p.Z - q[k - 1].Z) * dz) / len2, 0, 1);
                        best = Math.Min(best, Math.Sqrt(Math.Pow(p.X - q[k - 1].X - t * dx, 2) + Math.Pow(p.Z - q[k - 1].Z - t * dz, 2)));
                    }
                    return best;
                });
                var loose = pts.FirstOrDefault(p => Off(p) > 0.35);
                Assert.True(loose == default, $"{path}: a hatch around ({mx:F0},{mz:F0}) has a stripe ending off its border at ({loose.X:F1},{loose.Z:F1})");
                // the stripes start 0.6 m in and step 2.5 m, none where the hatch is under 0.3 m wide (its narrow tip, 0.3 / its width of a
                // 20 m taper): a 20 m hatch's stripes reach over at least 20 - 2.5 - 2 x 0.6 m less that tip. Their ends lie up to 3 m in
                // from the wide end, where a 1.5 m hatch over 20 m has narrowed by 0.2 m, and at the lights its widest part lies under
                // the cars' line 3-4 m out from the mouth, left unstriped (a stripe reaching into it dropped): up to 0.5 m in all
                double least = 20 - TransitionPaint.HatchStep - 1.2 - 0.3 * 20 / Math.Max(own, 0.5);
                Assert.True(reach >= least - 0.05 && wide >= 1.5 - 0.5, $"{path}: a hatch around ({mx:F0},{mz:F0}) {wide:F1} m wide at its wide end, stripes over {reach:F1} m");
            }
        }
        Assert.True(hatches >= 15, $"{hatches} hatches");   // one mesh a hatch; islands (#682) take the wide end of an exit hatch and its stripes with it
    }

    [Fact]
    public void Left_repeater_signals_stand_on_islands_past_the_crosswalk()
    {
        int islands = 0, poles = 0;
        foreach (var (_, tile) in region.Tiles())
        {
            islands += tile.AreaProps.Count(a => a.Type == AreaPropType.Island && a.Variant == 2 && (a.Flags & PropFlags.Solid) != 0 && a.Height > 0);
            poles += tile.Signals.Sum(s => s.Poles.Count(p => (p.Flags & SignalPoleFlags.Second) != 0));
        }
        // (#854) the planner's: a crosswalk's refuge across an exit hatch, else at the lights an island over the exit hatch's wide
        // end from the mouth to past the cars' line, where it is wide enough all along (the old layout's left-repeater island, #682)
        Assert.True(islands >= 5, $"{islands} islands");
        Assert.True(poles >= islands / 2, $"{poles} second poles for {islands} islands");
    }
    [Theory]
    [InlineData(SignalTestRegion.RingE, 8.0, 4)]
    [InlineData(SignalTestRegion.MiniRingE, 5.0, 3)]
    public void A_tight_roundabout_is_one_core_its_paths_counter_clockwise_round_the_ring(double e, double radius, int arms)
    {
        // #854: the ring's nodes one junction laid by RoundaboutCore; every lane to every other arm, never over the central island
        // (the ring at least 6 m wide round it), counter-clockwise (right-hand traffic); the island laid again for the ring
        double n = SignalTestRegion.RingN;
        var id = TileId.FromLv95(e, n);
        double cx = e - id.MinE, cz = id.MaxN - n;
        var tile = region.Tile(e, n);
        var paths = tile.JunctionPaths.Where(p => Enumerable.Range(0, p.PointCount).Any(i => Math.Abs(p.Points[i * 3] - cx) < 3 && Math.Abs(p.Points[i * 3 + 2] - cz) < radius + 3)).ToList();
        Assert.True(paths.Count >= arms * (arms - 1), $"{paths.Count} paths round the ring with {arms} arms");
        double inner = radius - 3.0;
        foreach (var p in paths)
        {
            double sweep = 0, nearest = double.MaxValue;
            for (int i = 0; i < p.PointCount; i++)
            {
                // plan view: x east, z south; the angle measured north-up (counter-clockwise from east)
                double x = p.Points[i * 3] - cx, y = cz - p.Points[i * 3 + 2];
                nearest = Math.Min(nearest, Math.Sqrt(x * x + y * y));
                if (i == 0) continue;
                double x0 = p.Points[i * 3 - 3] - cx, y0 = cz - p.Points[i * 3 - 1];
                sweep += Math.IEEERemainder(Math.Atan2(y, x) - Math.Atan2(y0, x0), 2 * Math.PI);
            }
            Assert.True(sweep > 0, $"a path round the ring at {e:F0} turns clockwise ({sweep:F2} rad)");
            Assert.True(nearest >= inner + p.Width * 0.5 - 0.3, $"a path round the ring at {e:F0} comes {nearest:F1} m from the centre (island edge {inner:F1})");
        }
        var islands = tile.AreaProps.Where(a => a.Type == AreaPropType.Island && a.Variant is 0 or 1
            && Math.Abs(a.Vertices[0] - cx) < 1.5 && Math.Abs(a.Vertices[2] - cz) < 1.5).ToList();
        Assert.Single(islands);
    }

    [Fact]
    public void No_car_stop_line_crosses_a_bike_lane_with_an_advanced_line()
    {
        int advanced = 0;
        foreach (var (path, tile) in region.Tiles())
        {
            var lines = tile.Paint.Where(p => p.Type == PaintType.StopLine).ToList();
            foreach (var bike in lines.Where(p => Math.Abs(p.Width - 0.3f) < 0.01f))
            {
                var q = Points(bike.Vertices);
                var (a, b) = (q[0], q[^1]);
                double dx = b.X - a.X, dz = b.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
                (double X, double Z) t = (dx / len, dz / len), f = (-t.Z, t.X);   // across the lanes, and along them
                bool ahead = false;
                foreach (var car in lines.Where(p => Math.Abs(p.Width - 0.5f) < 0.01f))
                {
                    var r = Points(car.Vertices);
                    var (c, d) = (r[0], r[^1]);
                    double gap = ((c.X + d.X - a.X - b.X) * 0.5) * f.X + ((c.Z + d.Z - a.Z - b.Z) * 0.5) * f.Z;
                    // an advanced bike line stands 3 m ahead of the cars' line (a bike box's yellow line 4 m)
                    if (Math.Abs(Math.Abs(gap) - 3.1) > 0.3) continue;
                    double c0 = (c.X - a.X) * t.X + (c.Z - a.Z) * t.Z, c1 = (d.X - a.X) * t.X + (d.Z - a.Z) * t.Z;
                    double lateral = Math.Abs((c.X - a.X) * f.X + (c.Z - a.Z) * f.Z);
                    if (lateral > 6) continue;   // another approach's line
                    ahead = true;
                    double overlap = Math.Min(Math.Max(c0, c1), len) - Math.Max(Math.Min(c0, c1), 0);
                    Assert.True(overlap < 0.05, $"{path}: a car stop line runs {overlap:F2} m across a bike lane with an advanced line at ({a.X:F0},{a.Z:F0})");
                }
                if (ahead) advanced++;
            }
        }
        Assert.True(advanced >= 10, $"{advanced} advanced bike lines");
    }

    [Fact]
    public void Without_lights_the_path_runs_to_the_kerb_and_the_edge_guide_follows_the_widening()
    {
        // the T without lights (#711, the user's rules): the main road's paths run on into the corners up to the side road's
        // kerb, so the red crossing spans only the side road (6 m and its kerb arcs, not mouth to mouth across the corners);
        // the dashed edge guide across the mouth starts on the west arm's widened edge, further north than the east arm's
        double e = SignalTestRegion.TownTeeE, n = SignalTestRegion.RowN;
        var id = TileId.FromLv95(e, n);
        double cx = e - id.MinE, cz = id.MaxN - n;
        var tile = region.Tile(e, n);
        bool Near(IReadOnlyList<(double X, double Z)> p) => p.All(q => Math.Abs(q.X - cx) < 15 && Math.Abs(q.Z - cz) < 15);
        var red = tile.Paint.Where(p => p.Type == PaintType.BikeCrossing).Select(p => Points(p.Vertices)).Where(Near).ToList();
        Assert.Single(red);
        Assert.True(red[0].Max(q => Math.Abs(q.X - cx)) < 7.5, $"the red crossing reaches {red[0].Max(q => Math.Abs(q.X - cx)):F1} m from the side road's axis");
        var paths = tile.AreaProps.Where(a => a.Type == AreaPropType.BikePath).Select(a => Points(a.Vertices))
            .Select(p => (X: p.Average(q => q.X) - cx, Z: p.Average(q => q.Z) - cz)).ToList();
        foreach (int sx in (ReadOnlySpan<int>)[-1, 1])
            Assert.True(paths.Any(c => Math.Sign(c.X) == sx && Math.Abs(c.X) > 3 && Math.Abs(c.X) < 14 && c.Z < -5 && c.Z > -11),
                $"no path carried into the corner on side {sx}");
        var guide = tile.Paint.Where(p => p.Type == PaintType.WhiteDashed && p.Shape == PaintShape.Polyline).Select(p => Points(p.Vertices))
            .Where(p => Near(p) && p.All(q => q.Z < cz - 3) && p.Min(q => q.X) < cx - 4 && p.Max(q => q.X) > cx + 4).ToList();
        Assert.Single(guide);
        var west = guide[0].MinBy(q => q.X);
        var east = guide[0].MaxBy(q => q.X);
        Assert.True(east.Z - west.Z > 0.5, $"the edge guide's west end is only {east.Z - west.Z:F2} m further out than its east end");
    }

    [Fact]
    public void Bike_lanes_across_the_lights_are_red_only_where_a_car_crosses_in_the_same_phase()
    {
        int Red(string name, double radius)
        {
            var j = region.Junction(name);
            var id = TileId.FromLv95(j.E, j.N);
            double cx = j.E - id.MinE, cz = id.MaxN - j.N;
            return region.Tile(j.E, j.N).Paint.Count(p => p.Type == PaintType.BikeCrossing
                && Points(p.Vertices).All(q => Math.Abs(q.X - cx) < radius && Math.Abs(q.Z - cz) < radius));
        }
        // J1: every turn across a bike lane is a protected arrow, held red while the riders go
        Assert.Equal(0, Red("J1", 40));
        // J5: the merged strip's shared straight + right lane turns across its own bike lane
        Assert.True(Red("J5a", 25) >= 1, "J5a: no red where the TR lane turns right across the bike lane");
        Assert.True(Red("J5b", 25) >= 1, "J5b: no red where the TR lane turns right across the bike lane");
    }
}
