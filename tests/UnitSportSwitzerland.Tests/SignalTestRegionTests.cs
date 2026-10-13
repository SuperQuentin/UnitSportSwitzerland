using UnitSport.Terrain.Format;
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
/// 1.5 m x 20 m, no car stop line across a bike lane with an advanced line, rounded corners at the
/// widened arms of the rural crossroads, red bike crossings only where a car crosses in the same phase.
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
            var closers = tile.Paint.Where(p => p.Type == PaintType.WhiteSolid && p.Shape == PaintShape.Polyline && p.Vertices.Length == 6).ToList();
            foreach (var hatch in tile.Paint.Where(p => p.Type == PaintType.Hatch))
            {
                hatches++;
                var pts = Points(hatch.Vertices);
                // its axis: the stripes lie along the road
                double mx = pts.Average(p => p.X), mz = pts.Average(p => p.Z);
                double sxx = pts.Sum(p => (p.X - mx) * (p.X - mx)), szz = pts.Sum(p => (p.Z - mz) * (p.Z - mz)), sxz = pts.Sum(p => (p.X - mx) * (p.Z - mz));
                double angle = 0.5 * Math.Atan2(2 * sxz, sxx - szz);
                (double X, double Z) u = (Math.Cos(angle), Math.Sin(angle)), n = (-u.Z, u.X);
                var along = pts.Select(p => (p.X - mx) * u.X + (p.Z - mz) * u.Z).ToList();
                var across = pts.Select(p => (p.X - mx) * n.X + (p.Z - mz) * n.Z).ToList();
                double reach = along.Max() - along.Min(), wide = across.Max() - across.Min();
                // a solid line across its wide end: short, square to it or angled back along the road (#700: a lead-in's,
                // the gentle lead into the pocket), at one end of its stripes; the
                // border runs on from that line's outer end to the narrow end
                var closer = closers.FirstOrDefault(c =>
                {
                    var q = Points(c.Vertices);
                    double dx = q[1].X - q[0].X, dz = q[1].Z - q[0].Z, len = Math.Sqrt(dx * dx + dz * dz);
                    double alongIt = Math.Abs(dx * u.X + dz * u.Z);
                    if (len < 0.5 || len > 15 || (alongIt / len > 0.3 && Math.Abs(dx * n.X + dz * n.Z) < 1)) return false;
                    double at = ((q[0].X + q[1].X) * 0.5 - mx) * u.X + ((q[0].Z + q[1].Z) * 0.5 - mz) * u.Z;
                    double off = ((q[0].X + q[1].X) * 0.5 - mx) * n.X + ((q[0].Z + q[1].Z) * 0.5 - mz) * n.Z;
                    return Math.Min(Math.Abs(at - along.Min()), Math.Abs(at - along.Max())) < 1.5 + alongIt * 0.5 && Math.Abs(off) < wide;
                });
                Assert.True(closer is not null, $"{path}: a hatch around ({mx:F0},{mz:F0}) has no line across its wide end");
                var ends = Points(closer!.Vertices);
                // its width: across the road; an angled line adds its run along the road to the border's length
                double width = Math.Abs((ends[1].X - ends[0].X) * n.X + (ends[1].Z - ends[0].Z) * n.Z);
                double slanted = Math.Abs((ends[1].X - ends[0].X) * u.X + (ends[1].Z - ends[0].Z) * u.Z);
                var border = tile.Paint.Where(p => p.Type == PaintType.WhiteSolid && p.Shape == PaintShape.Polyline && p.Vertices.Length >= 9)
                    .Select(p => Points(p.Vertices))
                    .FirstOrDefault(q => ends.Any(e => Math.Abs(q[0].X - e.X) + Math.Abs(q[0].Z - e.Z) < 0.02 || Math.Abs(q[^1].X - e.X) + Math.Abs(q[^1].Z - e.Z) < 0.02));
                Assert.True(border is not null, $"{path}: a hatch around ({mx:F0},{mz:F0}) has no border from its closing line");
                double length = slanted > 1 ? slanted : 0;
                for (int k = 1; k < border!.Count; k++) length += Math.Sqrt(Math.Pow(border[k].X - border[k - 1].X, 2) + Math.Pow(border[k].Z - border[k - 1].Z, 2));
                Assert.True(length >= 20 - 0.05 && width >= 1.5 - 0.05 && reach <= length + 0.5,
                    $"{path}: a hatch {length:F1} m long, {width:F1} m wide at its wide end (stripes over {reach:F1} m)");
            }
        }
        Assert.True(hatches >= 30, $"{hatches} hatches");   // islands (#682) take the wide end of an exit hatch and its stripes with it
    }

[Fact]    public void Left_repeater_signals_stand_on_islands_past_the_crosswalk()    {        int islands = 0, poles = 0;        foreach (var (_, tile) in region.Tiles())        {            islands += tile.AreaProps.Count(a => a.Type == AreaPropType.Island && a.Variant == 2 && (a.Flags & PropFlags.Solid) != 0 && a.Height > 0);            poles += tile.Signals.Sum(s => s.Poles.Count(p => (p.Flags & SignalPoleFlags.Second) != 0));        }        Assert.True(islands >= 10, $"{islands} islands");        // two islands an arm (past the crosswalk, with the pole, and behind it), cut by the crosswalk
        Assert.True(poles >= islands / 2, $"{poles} second poles for {islands} islands");    }
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

    private static double Extent(IReadOnlyList<(double X, double Z)> p) =>
        Math.Max(p.Max(q => q.X) - p.Min(q => q.X), p.Max(q => q.Z) - p.Min(q => q.Z));

    [Fact]
    public void Widened_arms_get_rounded_corners()
    {
        foreach (string name in (ReadOnlySpan<string>)["J1", "J3b", "J5a", "J5b"])
        {
            var j = region.Junction(name);
            var id = TileId.FromLv95(j.E, j.N);
            double cx = j.E - id.MinE, cz = id.MaxN - j.N;
            // a corner's kerb patch (#682: the pavement between the junction's own corner and the kerb arc, 9 points on it): a small polygon off every arm's axis
            var corners = region.Tile(j.E, j.N).AreaProps.Where(a => a.Type == AreaPropType.Pavement && a.Vertices.Length / 3 >= 13 && Extent(Points(a.Vertices)) < 25)
                .Select(a => Points(a.Vertices)).Select(p => (X: p.Average(q => q.X) - cx, Z: p.Average(q => q.Z) - cz))
                .Where(c => Math.Abs(c.X) > 1.5 && Math.Abs(c.Z) > 1.5 && Math.Abs(c.X) < 30 && Math.Abs(c.Z) < 30).ToList();
            foreach (var (sx, sz) in (ReadOnlySpan<(int, int)>)[(1, 1), (1, -1), (-1, 1), (-1, -1)])
                Assert.True(corners.Any(c => Math.Sign(c.X) == sx && Math.Sign(c.Z) == sz), $"{name}: no rounded corner at ({sx},{sz})");
        }
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
