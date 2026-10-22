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

    /// <summary>The junction whose id (its name up to the first dash) is <paramref name="id"/>: "J1" is not J10's.</summary>
    public SignalTestRegion.Junction Junction(string id) =>
        SignalTestRegion.Junctions.Single(j => j.Name.Split('-')[0] == id.TrimEnd('-'));

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
    public void J7s_east_crosswalk_crosses_its_wide_island_in_two_stages()
    {
        // #1030: the east arm's island (3.4 m at the zebra, from its exit hatch) holds walkers between the halves: each half a group
        // of its own, its heads at its kerb and on the island back to back, its walkers' lines kerb to island, the first walker of
        // each green on the island at most a third of the cycle, no zebra bar on the island
        var j = region.Junction("J7");
        var tile = region.Tile(j.E, j.N);
        var signal = tile.Signals.Single();
        var plan = signal.Plan;
        Assert.Empty(plan.Validate());
        Assert.True(plan.ConflictTimed);
        int east = plan.Arms.FindIndex(a => Math.Abs(Math.IEEERemainder(a.Heading, 2 * Math.PI)) < 0.2);
        var halves = Enumerable.Range(0, plan.Groups.Count).Where(g => plan.Groups[g].Kind == SignalGroupKind.Pedestrian && plan.Groups[g].Arm == east).ToList();
        Assert.Equal([CrossingHalf.Arriving, CrossingHalf.Departing], halves.Select(g => plan.Groups[g].Half));
        // only that arm: the others' crosswalks have no island
        Assert.All(plan.Groups.Where(g => g.Kind == SignalGroupKind.Pedestrian && g.Arm != east), g => Assert.Equal(CrossingHalf.Whole, g.Half));

        const SignalPoleFlags Both = SignalPoleFlags.ArrivingHalf | SignalPoleFlags.DepartingHalf;
        var poles = signal.Poles.Where(p => p.Arm == east && (p.Flags & SignalPoleFlags.Pedestrian) != 0).ToList();
        Assert.Equal(3, poles.Count);
        var island = Assert.Single(poles, p => (p.Flags & Both) == Both);
        var arriving = Assert.Single(poles, p => (p.Flags & Both) == SignalPoleFlags.ArrivingHalf);
        var departing = Assert.Single(poles, p => (p.Flags & Both) == SignalPoleFlags.DepartingHalf);
        Assert.True((arriving.Flags & SignalPoleFlags.Main) != 0, "the arriving half's head stands on the main pole");
        // the island between the kerbs, its arriving head facing the way the departing kerb's does (toward the arriving kerb)
        static float Across(SignalPole a, SignalPole b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
        Assert.Equal(Across(arriving, departing), Across(arriving, island) + Across(island, departing), 0.2f);
        Assert.Equal(0f, MathF.IEEERemainder(island.PedHeading - departing.PedHeading, 2 * MathF.PI), 0.05f);
        Assert.Equal(MathF.PI, MathF.Abs(MathF.IEEERemainder(island.PedHeading - arriving.PedHeading, 2 * MathF.PI)), 0.05f);

        // each half's walkers between its kerb and the island, both ways, shorter than the road
        static float Length(RoadConflictLine l) => MathF.Sqrt(MathF.Pow(l.Points[^3] - l.Points[0], 2) + MathF.Pow(l.Points[^1] - l.Points[2], 2));
        foreach (int g in halves)
        {
            var walks = signal.ConflictLines.Where(l => l.Group == g && l.Mover == ConflictMover.Walker).ToList();
            Assert.Equal(2, walks.Count);
            Assert.All(walks, l => Assert.InRange(Length(l), 2f, plan.Arms[east].CrossingM - 3f));
        }
        var waits = plan.IslandWaits(g => signal.ConflictLines.Where(l => l.Group == g && l.Mover == ConflictMover.Walker).Max(Length) / SignalPlan.WalkSpeed);
        Assert.NotEmpty(waits);
        Assert.All(waits, w => Assert.True(w.Wait <= plan.Cycle * SignalPlan.IslandWaitShare + 0.1f, $"waits {w.Wait:F1} s of {plan.Cycle:F1}"));

        // the zebra (east arm: outward +X, the island pole just behind it) leaves the island bare (the user)
        int bars = 0;
        foreach (var paint in tile.Paint.Where(p => p.Type == PaintType.YellowSolid && p.Shape == PaintShape.Triangles))
            for (int t = 0; t + 2 < paint.Indices.Length; t += 3)
            {
                float x = 0, z = 0;
                for (int k = 0; k < 3; k++) { x += paint.Vertices[paint.Indices[t + k] * 3] / 3; z += paint.Vertices[paint.Indices[t + k] * 3 + 2] / 3; }
                if (x < island.X - 4 || x > island.X || Math.Abs(z - island.Z) > 8) continue;
                bars++;
                Assert.True(Math.Abs(z - island.Z) > 1.5f, $"a zebra bar on the island, {z - island.Z:F2} m across from its pole");
            }
        Assert.True(bars > 0, "no zebra found");
    }

    [Fact]
    public void Canton_row_differs_only_by_its_canton()
    {
        // #1038: J2's crossroads once per zone; Geneva's walkers' heads have two lenses, Vaud's heads arrow plates, Basel-Landschaft's
        // and Bern's poles their colours (#1037), the rest alike
        var plans = new[] { "default", "GE", "VD", "BL", "BE" }
            .Select(c => SignalTestRegion.Junctions.Single(j => j.Name == $"J12-canton-{c}"))
            .Select(j => region.Tile(j.E, j.N).Signals.Single().Plan).ToArray();
        Assert.Equal((true, false), (plans[0].PedestrianAmber, plans[0].ArrowPlates));
        Assert.Equal((false, false), (plans[1].PedestrianAmber, plans[1].ArrowPlates));
        Assert.Equal((true, true), (plans[2].PedestrianAmber, plans[2].ArrowPlates));
        Assert.Equal([SignalPoleColour.SilverGrey, SignalPoleColour.SilverGrey, SignalPoleColour.SilverGrey, SignalPoleColour.Anthracite,
            SignalPoleColour.TrafficGrey], plans.Select(p => p.PoleColour));
        Assert.All(plans, p => Assert.Equal((plans[0].Cycle, plans[0].Groups.Count), (p.Cycle, p.Groups.Count)));
    }

    [Fact]
    public void Canton_borders_are_marked_where_roads_cross()
    {
        // #1038: the main road crosses four borders (#1037 added Basel-Landschaft and Bern): a stripe across it at each, and a sign
        // each way standing in the canton it names, facing the traffic coming in across the border
        var tiles = region.Tiles().Select(t => t.Tile).ToList();
        Assert.Equal(4, tiles.Sum(t => t.Paint.Count(p => p.Rgba == SignalTestRegion.BorderRgba)));
        var signs = tiles.SelectMany(t => t.PointProps.Where(p => p.Type == PointPropType.CantonSign).Select(p => (t.Id, Sign: p))).ToList();
        Assert.Equal(8, signs.Count);
        foreach (var (id, s) in signs)
        {
            double e = id.MinE + s.X, n = id.MaxN - s.Z, border = Math.Round(e / 1000) * 1000;
            Assert.Equal(SignalTestRegion.CantonAt(e, n), RoadSigns.CantonCode(s.Variant));
            double frontEast = -Math.Sin(s.Heading);   // heading 0 faces -Z (north)
            Assert.True(Math.Sign(frontEast) == Math.Sign(border - e), $"the sign at E {e:F1} faces away from the border at {border}");
        }
    }

    [Fact]
    public void Wide_approaches_carry_their_heads_overhead()
    {
        // #1037: J6's 2+2 artery on mast arms; the 1+1 roads with pockets keep their kerb poles; every head 4.50-5.50 m over the
        // road (5.50 m and up on a wire). J13's 4+4 artery took portals and J14 (the same with trams, between houses) span wires
        // while a head hung over every lane; their one cluster over the middle of the lanes (#1110) is within an arm's reach
        (OverheadKind Kind, int Count) At(string id)
        {
            var j = region.Junction(id);
            var all = region.Tile(j.E, j.N).Signals.SelectMany(s => s.Overheads).ToList();
            return (all.Select(o => o.Kind).DefaultIfEmpty().First(), all.Count);
        }
        Assert.Equal((OverheadKind.Arm, 2), At("J6"));
        Assert.Equal((OverheadKind.Arm, 2), At("J13"));
        Assert.Equal((OverheadKind.Arm, 2), At("J14"));
        Assert.Equal(0, At("J2").Count);
        foreach (var (_, tile) in region.Tiles())
            foreach (var s in tile.Signals)
                foreach (var o in s.Overheads)
                {
                    Assert.NotEmpty(o.Heads);
                    Assert.InRange(o.Heads.Count, 1, 3);
                    Assert.True(o.Heads.Max(h => h.Along) - o.Heads.Min(h => h.Along) <= 2 * SignalOverhead.ClusterPitch + 0.01f, "one cluster");
                    Assert.All(o.Heads, h => Assert.InRange(h.Along, 0.3f, o.Length));
                    float lift = o.Bottom - s.Y;
                    Assert.InRange(lift, o.Kind == OverheadKind.Span ? 5.4f : 4.4f, 6.2f);
                    if (o.Kind is OverheadKind.Arm or OverheadKind.RefugeArm) Assert.True(o.Length <= 10.01f, $"an arm {o.Length:F1} m long");
                }
    }

    [Fact]
    public void Riders_turning_left_go_with_the_left_arrow()
    {
        // #971, the user: a riders' left turn from the left-turn bike lane follows the left arrow, never the through green; where
        // their clearing is long, on a bike group of their own green with that arrow (#979)
        var j = region.Junction("J1");
        var tile = region.Tile(j.E, j.N);
        var plan = tile.Signals.Single().Plan;
        var lefts = tile.JunctionPaths.Where(p => p.Kind == JunctionPathKind.Bike && p.Move == SignalMoves.Left && p.Signal == 0).ToList();
        Assert.NotEmpty(lefts);
        Assert.All(lefts, p => Assert.True(plan.Groups[p.Group].Kind == SignalGroupKind.LeftArrow
            || plan.Groups[p.Group] is { Kind: SignalGroupKind.Bike, Moves: SignalMoves.Left }));
        foreach (var p in lefts.Where(p => plan.Groups[p.Group].Kind == SignalGroupKind.Bike))
        {
            int arrow = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.LeftArrow && g.Arm == plan.Groups[p.Group].Arm);
            Assert.Contains(Enumerable.Range(0, (int)plan.Cycle * 4).Select(k => k * 0.25),
                t => plan.State(p.Group, t) == SignalAspect.Green && plan.State(arrow, t) == SignalAspect.Green);
        }
    }

    [Fact]
    public void J2s_riders_go_from_path_to_path_straight_on_and_right_and_flash_turning_across_a_crossing()
    {
        // #971, the user: the separated paths' riders straight on over the next arm's bike crossing and right round the corner, on
        // their bike group; turning right across a crossing green with them, their head has the flasher
        var j = region.Junction("J2");
        var tile = region.Tile(j.E, j.N);
        var plan = tile.Signals.Single().Plan;
        var rides = tile.JunctionPaths.Where(p => p.Kind == JunctionPathKind.Path).ToList();
        foreach (var move in new[] { SignalMoves.Through, SignalMoves.Right })
            Assert.Equal(4, rides.Count(p => p.Move == move && p.Signal == 0 && plan.Groups[p.Group].Kind == SignalGroupKind.Bike));
        // the left turns from the left-turn bike lane carried on onto the exit's path, on the left arrow
        Assert.Equal(4, rides.Count(p => p.Move == SignalMoves.Left && p.Signal == 0
            && (plan.Groups[p.Group].Kind == SignalGroupKind.LeftArrow || plan.Groups[p.Group] is { Kind: SignalGroupKind.Bike, Moves: SignalMoves.Left })));
        Assert.Contains(plan.Groups, g => g.Kind == SignalGroupKind.Flasher && plan.Groups[g.Qualifies].Kind == SignalGroupKind.Bike);
        // and it flashes while their green is on: the bike head's own yellow, flashing beside the green
        int flasher = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Flasher && plan.Groups[g.Qualifies].Kind == SignalGroupKind.Bike);
        int riders = plan.Groups[flasher].Qualifies;
        Assert.Contains(Enumerable.Range(0, (int)(plan.Cycle * 4)).Select(k => k * 0.25),
            t => plan.State(riders, t) == SignalAspect.Green && plan.State(flasher, t) == SignalAspect.FlashingAmber);
        // and on while the crossing's walkers are still on it after their green (the user, #979: it went off as their green ended)
        var walkers = Enumerable.Range(0, plan.Groups.Count).Where(h => plan.Groups[h].Kind == SignalGroupKind.Pedestrian
            && plan.Conflicts()[riders, h] == SignalPlan.Conflict.Permissive).ToList();
        Assert.Contains(Enumerable.Range(0, (int)(plan.Cycle * 4)).Select(k => k * 0.25),
            t => plan.State(flasher, t) == SignalAspect.FlashingAmber && walkers.All(h => plan.State(h, t) != SignalAspect.Green));
    }

    [Fact]
    public void J2s_tile_carries_the_pairs_that_set_its_green_starts()
    {
        // #979, the user: the debug view's last and first movers, read back from the tile (CPAR): each pair a hard conflict of the
        // plan whose own lines give its time, and every starting green at least that long after the ending green before it
        var j = region.Junction("J2");
        var tile = region.Tile(j.E, j.N);
        var signal = tile.Signals.Single();
        var plan = signal.Plan;
        var conflicts = plan.Conflicts();
        Assert.NotEmpty(signal.ConflictPairs);
        // every movement once for the trails (the user: only the west path's riders turning right were shown): each lit path, the
        // riders' among them, and each crossing both ways
        Assert.Equal(tile.JunctionPaths.Count(p => p.Signal == 0), signal.ConflictLines.Count(l => l.Mover != ConflictMover.Walker));
        Assert.Equal(2 * plan.Groups.Count(g => g.Kind == SignalGroupKind.Pedestrian), signal.ConflictLines.Count(l => l.Mover == ConflictMover.Walker));
        Assert.All(signal.ConflictPairs, p => Assert.Contains(signal.ConflictLines, l => ReferenceEquals(l.Points, p.EndingLine)));
        foreach (var p in signal.ConflictPairs)
        {
            Assert.Equal(SignalPlan.Conflict.Hard, conflicts[p.Ending, p.Starting]);
            Assert.True(p.EndingLine.Length >= 6 && p.StartingLine.Length >= 6);
            Assert.Equal(p.Time, p.Amber + (p.Clear + p.Length) / p.ClearSpeed - p.Enter / p.EnterSpeed, 0.11f);
            var ends = plan.Groups[p.Ending].Intervals.Where(v => v.Aspect == SignalAspect.Green).Select(v => v.To).ToList();
            foreach (var green in plan.Groups[p.Starting].Intervals.Where(v => v.Aspect == SignalAspect.Green))
            {
                if (green.From < 1e-3f && plan.Groups[p.Starting].Intervals[^1].Aspect == SignalAspect.Green) continue;
                float since = ends.Select(e => ((green.From - e) % plan.Cycle + plan.Cycle) % plan.Cycle).Min();
                Assert.True(since >= Math.Max(0f, p.Time) - 0.11f, $"group {p.Starting} green at {green.From} only {since} s after {p.Ending}'s, needs {p.Time}");
            }
        }
    }

    [Theory]
    [InlineData("J1")]
    [InlineData("J2")]
    public void The_riders_timed_ride_their_written_paths(string id)
    {
        // #979, the user: the riders' left turns' ghosts went their lane's connector, not the way written onto the exit's path;
        // every riders' line of a pair, past its stop line, lies on one of the tile's riders' paths
        var j = region.Junction(id);
        var tile = region.Tile(j.E, j.N);
        var rides = tile.JunctionPaths.Where(p => p.Kind != JunctionPathKind.Car).ToList();
        int checkedLines = 0;
        foreach (var pair in tile.Signals.Single().ConflictPairs)
            foreach (var (who, line) in new[] { (pair.EndingMover, pair.EndingLine), (pair.StartingMover, pair.StartingLine) })
            {
                if (who != ConflictMover.Bike) continue;
                checkedLines++;
                var on = rides.FirstOrDefault(r => Enumerable.Range(1, line.Length / 3 - 1).All(i => Off(r, line[i * 3], line[i * 3 + 2]) < 0.3));
                Assert.True(on is not null, $"a riders' line of groups {pair.Ending} -> {pair.Starting} on none of the riders' paths");
            }
        Assert.True(checkedLines > 0);

        static double Off(RoadJunctionPath r, float x, float z)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < r.PointCount; i++)
            {
                double ax = r.Points[i * 3], az = r.Points[i * 3 + 2], bx = r.Points[i * 3 + 3], bz = r.Points[i * 3 + 5];
                double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
                double t = l2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0, 1);
                best = Math.Min(best, Math.Sqrt(Math.Pow(ax + dx * t - x, 2) + Math.Pow(az + dz * t - z, 2)));
            }
            return best;
        }
    }

    [Fact]
    public void J2s_poles_stand_by_the_crosswalk_past_every_stop_line()
    {
        // #1000, the user: the junction planner put the poles at the cars' line, behind the riders' line and box; beside a
        // crosswalk they stand on its near side, nearer the junction than every line (#711)
        var j = region.Junction("J2");
        var signal = region.Tile(j.E, j.N).Signals.Single();
        int checkedPoles = 0;
        foreach (var pole in signal.Poles)
        {
            int k = pole.Arm;
            if (float.IsNaN(signal.Stops[k * 3])) continue;
            var (sx, sz) = (signal.Stops[k * 3] - signal.X, signal.Stops[k * 3 + 2] - signal.Z);
            float len = MathF.Sqrt(sx * sx + sz * sz);
            float stopAlong = len, poleAlong = ((pole.X - signal.X) * sx + (pole.Z - signal.Z) * sz) / len;
            Assert.True(poleAlong < stopAlong - 0.3f, $"arm {k}'s pole {poleAlong:F1} m out, its cars' line {stopAlong:F1} m");
            checkedPoles++;
        }
        Assert.True(checkedPoles >= 4);
    }

    [Fact]
    public void No_car_path_crosses_a_repeater_island()
    {
        // #1000: the left repeater's island runs 3 m into the junction (the old layout's, #682); every car keeps clear of it, its
        // path's half width and a little more off its kerb
        int islands = 0;
        foreach (var (_, tile) in region.Tiles())
            foreach (var island in tile.AreaProps.Where(a => a.Type == AreaPropType.Island && a.Variant == 2))
            {
                islands++;
                var v = island.Vertices;
                int n = v.Length / 3;
                foreach (var path in tile.JunctionPaths.Where(p => p.Kind == JunctionPathKind.Car))
                    for (int i = 0; i < path.PointCount; i++)
                    {
                        float x = path.Points[i * 3], z = path.Points[i * 3 + 2];
                        // (its width going from its start's to its end's along it)
                        double half = (path.Width + (path.ToWidth - path.Width) * i / Math.Max(1, path.PointCount - 1)) * 0.5;
                        double gap = Inside(v, n, x, z) ? -1 : Enumerable.Range(0, n).Min(k => Segment(x, z, v[k * 3], v[k * 3 + 2], v[(k + 1) % n * 3], v[(k + 1) % n * 3 + 2]));
                        Assert.True(gap >= half - 0.3, $"a {path.Move} car path {gap:F2} m from an island at LV95 {tile.Id.MinE + x:F1},{tile.Id.MaxN - z:F1} (half width {half:F2})");
                    }
            }
        Assert.True(islands > 0);

        static bool Inside(float[] q, int n, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = q[i * 3], zi = q[i * 3 + 2], xj = q[j * 3], zj = q[j * 3 + 2];
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }
        static double Segment(float x, float z, float ax, float az, float bx, float bz)
        {
            double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0, 1);
            return Math.Sqrt(Math.Pow(ax + dx * t - x, 2) + Math.Pow(az + dz * t - z, 2));
        }
    }

    [Fact]
    public void Priority_signs_at_lights_stand_on_their_main_poles()
    {
        // #1000 (the old layout's rule, #350/#711): an approach's priority sign for when the lights are off is on its main pole,
        // not a post of its own; on a mast arm's or portal's post (#1037) out in front of its face, by its half width
        int onPoles = 0;
        foreach (var (_, tile) in region.Tiles())
            foreach (var sign in tile.PointProps.Where(x => (x.Flags & PropFlags.OnSignalPole) != 0))
            {
                Assert.Contains(tile.Signals.SelectMany(g => g.Poles), p => (p.Flags & SignalPoleFlags.Main) != 0
                    && Math.Abs(p.X - sign.X) < SignalOverhead.PostHalf && Math.Abs(p.Z - sign.Z) < SignalOverhead.PostHalf);
                onPoles++;
            }
        Assert.True(onPoles > 0);
    }

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
            // (a tram track's grooves cut the road's paint, #124: a stripe ending at the track's zone is cut there, #1057's J14 branch)
            var grooves = tile.Paint.Where(p => p.Type == PaintType.RailGroove).Select(p => Points(p.Vertices)).ToList();
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
                double OffGroove((double X, double Z) p) => grooves.Count == 0 ? double.MaxValue : grooves.Min(q => q.Count < 2 ? double.MaxValue
                    : q.Zip(q.Skip(1)).Min(s => Math.Sqrt(Math.Pow(p.X - Math.Clamp(p.X, Math.Min(s.First.X, s.Second.X), Math.Max(s.First.X, s.Second.X)), 2)
                        + Math.Pow(p.Z - Math.Clamp(p.Z, Math.Min(s.First.Z, s.Second.Z), Math.Max(s.First.Z, s.Second.Z)), 2))));
                var loose = pts.FirstOrDefault(p => Off(p) > 0.35 && OffGroove(p) > 1.2);
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

    [Fact]
    public void Flashers_say_what_they_warn_of_and_permissive_lefts_flash()
    {
        // #1109, the user: a plate under each flasher lamp (a pedestrian, a bike, the arrow of a turn crossing both); a left from a
        // shared lane giving way to the oncoming cars flashes its ball's yellow, or, shown by an arrow, has a flasher on its left
        var plates = new Dictionary<SignalPlate, int>();
        int balls = 0, arrows = 0;
        foreach (var (_, tile) in region.Tiles())
            foreach (var s in tile.Signals)
            {
                var p = s.Plan;
                for (int g = 0; g < p.Groups.Count; g++)
                {
                    if (p.Groups[g].Kind != SignalGroupKind.Flasher) continue;
                    var (plate, arrow) = p.PlateOf(g);
                    plates[plate] = plates.GetValueOrDefault(plate) + 1;
                    // a car group's lamp always says something; a bike group's flashes its own yellow, no lamp
                    Assert.Equal(p.Groups[p.Groups[g].Qualifies].Kind == SignalGroupKind.Car, plate != SignalPlate.None);
                    if (plate == SignalPlate.Arrow) Assert.Equal(SignalMoves.None, arrow & SignalMoves.Through);
                }
                for (int g = 0; g < p.Groups.Count; g++)
                {
                    if (p.LeftGivesWayTo(g).Count == 0) continue;
                    if (UnitSport.Terrain.SignalBuilder.ArrowMoves(p, g) == SignalMoves.None) balls++; else arrows++;
                }
            }
        Assert.True(plates.GetValueOrDefault(SignalPlate.Pedestrian) > 0 && plates.GetValueOrDefault(SignalPlate.Bike) > 0
            && plates.GetValueOrDefault(SignalPlate.Arrow) > 0, string.Join(", ", plates.Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.True(balls > 0 && arrows > 0, $"balls {balls}, arrows {arrows}");

        // J7's flashers: one turn crosses both, its arrow alone
        var j7 = region.Junction("J7");
        var p7 = region.Tile(j7.E, j7.N).Signals.Single().Plan;
        Assert.Contains(Enumerable.Range(0, p7.Groups.Count), g => p7.PlateOf(g) is (SignalPlate.Arrow, SignalMoves.Left or SignalMoves.Right));

        // J3b's south approach: straight on and left beside its right pocket's own group, an arrow head, its left giving way: the
        // flasher on its left, its yellow steady; J3's ball heads flash theirs
        var j3b = region.Junction("J3b");
        var tile3b = region.Tile(j3b.E, j3b.N);
        var lamps3b = UnitSport.Terrain.SignalBuilder.BuildLamps(tile3b)!;
        var leftFlasher = Assert.Single(lamps3b.Lenses, l => l.Role == UnitSport.Terrain.SignalBuilder.Role.Flash && l.Blink is not null
            && l.Blink.Qualifies == l.Group && lamps3b.Plans[0].LeftGivesWayTo(l.Group).Count > 0 && l is { Plate: SignalPlate.Arrow, PlateArrow: SignalMoves.Left });
        // and its left turn's lamp for the bike way it crosses, on the approach's left pole: one more lamp of its group, a bike on its plate
        Assert.Contains(lamps3b.Lenses, l => l.Group == leftFlasher.Group && l.Role == UnitSport.Terrain.SignalBuilder.Role.Flash && l.Plate == SignalPlate.Bike);
        Assert.Equal(SignalMoves.Left | SignalMoves.Through, UnitSport.Terrain.SignalBuilder.ArrowMoves(lamps3b.Plans[0], leftFlasher.Group));
        Assert.DoesNotContain(lamps3b.Lenses, l => l.Group == leftFlasher.Group && l.Role == UnitSport.Terrain.SignalBuilder.Role.Amber && l.Blink is not null);
        var j3 = region.Junction("J3");
        var lamps3 = UnitSport.Terrain.SignalBuilder.BuildLamps(region.Tile(j3.E, j3.N))!;
        Assert.Contains(lamps3.Lenses, l => l.Role == UnitSport.Terrain.SignalBuilder.Role.Amber && l.Blink is not null);
    }

    [Fact]
    public void Turn_lanes_have_their_own_heads_and_are_protected_where_the_phases_allow()
    {
        // #1119, the user: "whenever there is a left turn only lane at a light, it should get its own light ... same for right
        // turns"; "usually permissive lefts are pretty rare in Switzerland"; "even right turns with their own lanes are usually
        // protected". Every pocket has a group and head of its own; lefts are protected; a right pocket no phase carries is
        // protected with its own approach where the walkers and riders it crosses keep a green elsewhere
        int lefts = 0, rights = 0, permissive = 0;
        foreach (var (_, tile) in region.Tiles())
            foreach (var s in tile.Signals)
            {
                var p = s.Plan;
                var c = p.Conflicts();
                var lamps = UnitSport.Terrain.SignalBuilder.BuildLamps(tile)!;
                for (int a = 0; a < p.Arms.Count; a++)
                {
                    if (!p.Arms[a].In) continue;
                    var turns = SignalMoves.None;
                    for (int to = 0; to < p.Arms.Count; to++) if (to != a && p.Arms[to].Out) turns |= SignalPlan.Turn(p.Arms, a, to);
                    if (p.Arms[a].LeftPocket && (turns & SignalMoves.Left) != 0 && turns != SignalMoves.Left)
                    {
                        int l = p.Groups.FindIndex(g => g.Kind == SignalGroupKind.LeftArrow && g.Arm == a);
                        Assert.True(l >= 0, $"LV95 {tile.Id.MinE + s.X:F0},{tile.Id.MaxN - s.Z:F0} arm {a}: no left arrow group; turns {turns}; {string.Join(", ", p.Groups.Where(g => g.Arm == a).Select(g => $"{g.Kind} {g.Moves}"))}");
                        Assert.Empty(p.LeftGivesWayTo(l));
                        Assert.Contains(lamps.Lenses, x => x.Group == l && x.Shape == UnitSport.Terrain.SignalBuilder.Shape.LeftArrow);
                        lefts++;
                    }
                    if (p.Arms[a].RightPocket && (turns & SignalMoves.Right) != 0 && turns != SignalMoves.Right)
                    {
                        int r = p.Groups.FindIndex(g => g.Arm == a && (g.Kind == SignalGroupKind.RightArrow || g is { Kind: SignalGroupKind.Car, Moves: SignalMoves.Right }));
                        Assert.True(r >= 0, $"arm {a}: no right-turn group");
                        Assert.Contains(lamps.Lenses, x => x.Group == r && x.Role == UnitSport.Terrain.SignalBuilder.Role.Green);
                        rights++;
                        if (p.Groups[r].Kind == SignalGroupKind.Car) { permissive++; continue; }
                        // protected: nothing it crosses green with it
                        for (int h = 0; h < p.Groups.Count; h++)
                            if (c[r, h] == SignalPlan.Conflict.Hard)
                                Assert.DoesNotContain(Enumerable.Range(0, (int)(p.Cycle * 10)).Select(k => k * 0.1),
                                    t => p.State(r, t) == SignalAspect.Green && p.State(h, t) == SignalAspect.Green);
                    }
                }
            }
        Assert.True(lefts > 0 && rights > 0, $"lefts {lefts}, rights {rights}");
        // J3's east right pocket ran across the walkers before: protected now; one is left giving way (its walkers have no other green)
        var j3 = region.Junction("J3");
        var p3 = region.Tile(j3.E, j3.N).Signals.Single().Plan;
        Assert.DoesNotContain(p3.Groups, g => g is { Kind: SignalGroupKind.Car, Moves: SignalMoves.Right });
        Assert.Equal(1, permissive);
    }
}
