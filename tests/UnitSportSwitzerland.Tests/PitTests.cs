using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The excavation pit (#619): which sites dig one, where it and its ramp lie, how deep, that the
/// yard keeps out of it, and that the ground cut round it (<see cref="PitTerrain"/>) leaves no ground
/// in the pit and no gap round it, at every LOD stride and in the collision.
/// </summary>
public class PitTests
{
    private static ConstructionSite Site(string key, float w, float d, float h, byte floors, float turn = 0f,
        float cx = 500, float cz = 500, Func<Vector2, bool>? blocked = null)
    {
        var b = ConstructionSiteTests.Box(cx, cz, w, d, h, floors, turn);
        // the street to the south of the box, square to it
        var south = new Vector2(-Mathf.Sin(turn), Mathf.Cos(turn));
        return ConstructionSites.Plan(key, b, PlanBox.Of(b)!.Value, new Vector2(cx, cz) + south * (d / 2 + 14f), blocked ?? (_ => false))!;
    }

    private static Vector2 ToSite(ConstructionSite s, Vector2 p)
    {
        var d = p - s.Box.Center;
        return new Vector2(d.Dot(s.Box.AxisU), d.Dot(s.Box.AxisV));
    }

    [Theory]
    [InlineData(1.3f, (byte)6, true)]    // foundations
    [InlineData(6.3f, (byte)6, true)]    // a shell two storeys up: its trench still open
    [InlineData(9.2f, (byte)6, false)]   // three storeys up: backfilled
    [InlineData(18.2f, (byte)6, false)]  // topped out
    public void Foundations_and_low_shells_dig_a_pit(float h, byte floors, bool dug)
    {
        var s = Site("2500_1120_7", 24, 15, h, floors);
        Assert.Equal(dug, s.Pit != null);
    }

    [Fact]
    public void The_pit_is_the_box_and_its_margin_with_the_lane_on_the_street_side()
    {
        foreach (float turn in new[] { 0f, 0.55f, -1.2f, 2.9f })
        {
            var s = Site("2500_1120_11", 26, 16, 1.2f, 5, turn);
            var pit = s.Pit!;
            float hw = s.Box.Width / 2, hd = s.Box.Depth / 2, m = ConstructionSites.PitMargin;
            // the box and its margin are inside
            Assert.True(pit.Area.X0 <= -hw - m + 0.01f && pit.Area.X1 >= hw + m - 0.01f);
            Assert.True(pit.Area.Z0 <= -hd - m + 0.01f && pit.Area.Z1 >= hd + m - 0.01f);
            // the lane is in the pit, on the street side of the building, the full length of the pit
            var front = new Vector2(s.Front.Dot(s.Box.AxisU), s.Front.Dot(s.Box.AxisV));
            var laneMid = new Vector2((pit.Lane.X0 + pit.Lane.X1) / 2, (pit.Lane.Z0 + pit.Lane.Z1) / 2);
            Assert.True(laneMid.Dot(front) > 0, $"turn {turn}: the lane at {laneMid}, the front {front}");
            Assert.True(pit.Area.Contains(laneMid.X, laneMid.Y));
            Assert.Equal(ConstructionSites.PitLane, pit.LaneWidth, 2);
            Assert.Equal(pit.LaneAlongX ? pit.Area.Width : pit.Area.Depth, pit.LaneLength, 2);
        }
    }

    [Fact]
    public void A_house_digs_a_cellar_and_a_big_block_two_storeys()
    {
        Assert.Equal(1, Site("2500_1120_3", 14, 11, 1.0f, 2).Pit!.Basements);
        Assert.Equal(2, Site("2500_1120_4", 50, 34, 1.2f, 6).Pit!.Basements);
        Assert.Equal(ConstructionSites.PitLaneSmall, Site("2500_1120_3", 14, 11, 1.0f, 2).Pit!.LaneWidth, 2);
        // a block in between: one or two, by key, and the same every time
        for (int i = 0; i < 30; i++)
        {
            var a = Site($"2500_1120_{100 + i}", 24, 15, 1.2f, 4).Pit!;
            Assert.InRange(a.Basements, 1, 2);
            Assert.Equal(a, Site($"2500_1120_{100 + i}", 24, 15, 1.2f, 4).Pit);
        }
    }

    [Fact]
    public void A_pit_too_deep_for_every_lane_has_no_ramp_and_is_closed_all_round()
    {
        // 14 x 11 m with six floors: two basements, 6 m down, and no lane longer than 17 m
        var (s, _, pit) = Dig(Site("2500_1120_3", 14, 11, 1.2f, 6));
        Assert.Equal(2, s.Pit!.Basements);
        Assert.False(pit.HasRamp);
        Assert.Empty(pit.Route);
        // no mouth: the guard rail's solid sheet runs the whole way round
        var a = s.Pit.Area;
        float railed = pit.Boxes.Where(b => b.Part == ShellPart.Invisible).Sum(b => Math.Max(b.Max.X - b.Min.X, b.Max.Z - b.Min.Z) - 0.04f);
        Assert.True(railed > 2 * (a.Width + a.Depth) - 2f, $"{railed:F1} m of rail round {2 * (a.Width + a.Depth):F1} m");
    }

    [Fact]
    public void A_footpath_on_the_street_side_puts_the_lane_along_a_side_its_ramp_up_at_the_street()
    {
        // a house, a cellar under it; a path 2.5 m in front of its front wall (z grows to the street):
        // through the front lane, clear of the margin
        var s = Site("2500_1120_3", 14, 11, 1.0f, 2, blocked: p => p.Y > 507.5f && p.Y < 508.5f && Math.Abs(p.X - 500) < 60);
        var pit = s.Pit!;
        var front = new Vector2(s.Front.Dot(s.Box.AxisU), s.Front.Dot(s.Box.AxisV));
        Assert.True(Math.Abs(pit.Side.Dot(front)) < 0.5f, $"the lane's side {pit.Side}, the front {front}");
        Assert.Equal(1, ConstructionSites.LaneWall(pit, s.Box, s.Front));
        // its mouth faces the street, so a machine comes to it from the yard
        var (_, _, down) = PitPlans.RampTop(pit);
        Assert.True(down.Dot(front) < -0.9f, $"the ramp runs down {down}, the front is {front}");
        // and nothing of the yard on its apron
        var apron = ConstructionSites.InPlan(s.Box, ConstructionSites.Apron(pit));
        Assert.DoesNotContain(s.Zones, z => z.Rect.Overlaps(apron));
    }

    [Fact]
    public void No_pit_at_a_tile_seam_or_under_a_road()
    {
        // 4 m from the tile's west edge: the margin alone crosses it
        Assert.Null(Site("2500_1120_5", 20, 14, 1.2f, 4, cx: 14, cz: 500).Pit);
        // a footpath along the building's east wall, a metre off it
        Assert.Null(Site("2500_1120_5", 20, 14, 1.2f, 4, blocked: p => p.X > 511 && p.X < 512 && Math.Abs(p.Y - 500) < 30).Pit);
        // the same well inside the tile, nothing near: a pit
        Assert.NotNull(Site("2500_1120_5", 20, 14, 1.2f, 4).Pit);
    }

    [Theory]
    [InlineData(24f, 15f, 0f)]
    [InlineData(14f, 11f, 0.6f)]
    [InlineData(48f, 22f, -0.3f)]
    public void Nothing_of_the_yard_stands_in_the_pit_or_on_the_ramps_apron(float w, float d, float turn)
    {
        var s = Site("2537_1152_40", w, d, 1.2f, 5, turn);
        var pit = s.Pit!;
        var apron = ConstructionSites.Apron(pit);
        var area = ConstructionSites.InPlan(s.Box, pit.Area);
        var aprons = ConstructionSites.InPlan(s.Box, apron);
        foreach (var z in s.Zones)
        {
            Assert.False(z.Rect.Overlaps(area), $"{z.Kind} in the pit");
            Assert.False(z.Rect.Overlaps(aprons), $"{z.Kind} on the apron");
        }
        foreach (var m in s.Machines)
        {
            var (mw, ml) = ConstructionSites.MachineSize(m.Role);
            var f = new Vector2(-MathF.Sin(m.Yaw), -MathF.Cos(m.Yaw));
            var r = new SiteRect(m.At, new Vector2(f.Y, -f.X), mw, ml);
            Assert.False(r.Overlaps(area), $"{m.Role} in the pit");
            Assert.False(r.Overlaps(aprons), $"{m.Role} on the apron");
        }
        foreach (var c in s.Cranes.Where(c => !c.Inside))
            Assert.False(area.Contains(c.Base), "a crane's base in the pit");
    }

    /// <summary>The pit's plan on flat ground at 0, its floor as the builder works it out.</summary>
    private static (ConstructionSite Site, ShellPlan Shell, PitPlan Pit) Dig(ConstructionSite s, Func<float, float, float>? ground = null)
    {
        ground ??= (_, _) => 0f;
        float groundFloor = 0.12f;
        float floor = PitPlans.FloorOf(s, groundFloor, 0f);
        var shell = ShellPlans.Plan(s, null, groundFloor, 0f, floor);
        return (s, shell, PitPlans.Plan(s, shell.Wings, floor, shell.Levels[shell.Basements], ground)!);
    }

    [Theory]
    [InlineData("2500_1120_3", 14f, 11f, (byte)2)]   // a house: a cellar, the mini machines' lane
    [InlineData("2500_1120_8", 24f, 15f, (byte)6)]   // a block with two basements: 6 m down
    [InlineData("2500_1120_4", 50f, 34f, (byte)6)]
    public void The_ramp_goes_down_to_the_floor_within_its_lane(string key, float w, float d, byte floors)
    {
        var (s, shell, pit) = Dig(Site(key, w, d, 1.2f, floors));
        Assert.True(pit.HasRamp);
        // a storey down per basement, under the ground floor's slab
        Assert.Equal(0.12f - s.Pit!.Basements * s.StoreyHeight, shell.Levels[0], 3);
        Assert.Equal(shell.Levels[0] - PitPlans.Blinding, pit.Floor, 3);
        Assert.True(pit.Grade >= PitPlans.RampGrade - 1e-4f);
        // steeper than the cap only where the cap would run it past the lane's end
        Assert.True(pit.Grade <= PitPlans.MaxGrade + 1e-4f || MathF.Abs(pit.RampLength - (s.Pit.LaneLength - 1f)) < 0.01f, $"{100 * pit.Grade:F0} %");
        Assert.True(pit.RampLength <= s.Pit.LaneLength - 1f + 0.01f, $"{pit.RampLength:F1} m of ramp in a {s.Pit.LaneLength:F1} m lane");
        Assert.Equal(0f - pit.Floor, pit.RampLength * pit.Grade, 2);
        // the slope and room at its foot fit in the lane
        Assert.True(pit.RampLength + PitPlans.FootRoom <= s.Pit.LaneLength + 0.01f || pit.Grade >= PitPlans.MaxGrade - 1e-4f,
            $"{pit.RampLength:F1} m of ramp in a {s.Pit.LaneLength:F1} m lane");
        // its top is on the pit's edge, its mouth the lane's width
        Assert.Equal(s.Pit.LaneWidth, new Vector2(pit.TopOuter.X - pit.TopInner.X, pit.TopOuter.Z - pit.TopInner.Z).Length(), 2);
        // the way in ends on the floor beside the slab
        Assert.Equal(pit.Floor, pit.Route[^1].Y, 3);
    }

    [Fact]
    public void A_shell_over_its_pit_has_its_basements_and_a_door_where_each_bridge_lands()
    {
        var (s, shell, pit) = Dig(Site("2500_1120_8", 24, 15, 6.3f, 6));
        Assert.Equal(SitePhase.Shell, s.Phase);
        int b = s.Pit!.Basements;
        Assert.Equal(b, shell.Basements);
        Assert.Equal(b + s.BuiltStoreys + 1, shell.Levels.Count);
        Assert.Equal(0.12f, shell.Levels[b], 3);
        // a flight up every storey, the basements' too
        Assert.Equal(2 * (shell.Levels.Count - 1), shell.Ramps.Count);
        Assert.Equal(shell.Levels[0], shell.Route[0].Y, 3);
        // bridges on the sides, never on the side the ramp's lane is on
        Assert.NotEmpty(pit.Bridges);
        foreach (var br in pit.Bridges)
        {
            Assert.True(br.Normal.Dot(s.Pit.Side) < 0.5f);
            // the doorway it lands at: nothing solid in the wall there, a body's height up
            for (float up = 0.2f; up < 2.0f; up += 0.3f)
            {
                var p = new Vector3(br.To.X - br.Normal.X * 0.12f, shell.Levels[b] + up, br.To.Y - br.Normal.Y * 0.12f);
                Assert.False(shell.Boxes.Any(x => x.Solid && p.X > x.Min.X && p.X < x.Max.X && p.Y > x.Min.Y && p.Y < x.Max.Y
                    && p.Z > x.Min.Z && p.Z < x.Max.Z), $"a wall where the bridge lands at {br.To}, {up:F1} m up");
            }
            // and a deck on it at the ground floor's height
            Assert.Contains(pit.Boxes, x => x.Part == ShellPart.Deck && MathF.Abs(x.Max.Y - shell.Levels[b]) < 0.01f);
        }
        // no scaffold lift down in the pit: the first is over the ground floor
        foreach (var deck in shell.Boxes.Where(x => x.Part == ShellPart.Deck))
            Assert.True(deck.Max.Y > shell.Levels[b] + 0.5f);
    }

    [Fact]
    public void A_pit_without_a_floor_given_is_the_old_slab_on_the_ground()
    {
        // the shell planner alone (the walk probe's flat world, the old tests) keeps today's shell
        var s = Site("2500_1120_8", 24, 15, 1.2f, 6);
        var shell = ShellPlans.Plan(s, null, 0.4f, 0f);
        Assert.Single(shell.Levels);
        Assert.Equal(0.4f, shell.Levels[0], 3);
    }

    // ---- the ground's cut -----------------------------------------------------------------------

    /// <summary>A grid of m x m vertices at <paramref name="quad"/> m, as BuildSurfaceCore lays it out, heights from a function.</summary>
    private static (Vector3[] V, Color[] C, int[] I) Grid(int m, float quad, Func<float, float, float> h)
    {
        var v = new Vector3[m * m];
        var c = new Color[m * m];
        for (int r = 0; r < m; r++)
            for (int k = 0; k < m; k++)
            {
                v[r * m + k] = new Vector3(k * quad, h(k * quad, r * quad), r * quad);
                c[r * m + k] = new Color(0.2f, 0.5f, 0.2f, 0.25f);
            }
        var idx = new List<int>();
        for (int r = 0; r < m - 1; r++)
            for (int k = 0; k < m - 1; k++)
            {
                int v00 = r * m + k, v10 = v00 + 1, v01 = v00 + m, v11 = v01 + 1;
                idx.AddRange(new[] { v00, v10, v01, v10, v11, v01 });
            }
        return (v, c, idx.ToArray());
    }

    private static PitCut Cut(float turn, float floor = -6f)
    {
        var u = Vector2.FromAngle(turn);
        var rect = new SiteRect(new Vector2(47.3f, 52.9f), u, 23.4f, 17.7f);
        var c = rect.Corners();
        // the ramp's mouth on the side from corner 1 to corner 2, 4.6 m wide, running back along -u
        var outer = c[1];
        var inner = c[1] + rect.AxisV * 4.6f;
        return new PitCut(rect, floor, outer, inner, -u, 18f);
    }

    private static float PlanArea(Vector3 a, Vector3 b, Vector3 c) =>
        MathF.Abs((b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z)) / 2;

    [Theory]
    [InlineData(1, 0.53f)]
    [InlineData(2, 0.53f)]
    [InlineData(4, -0.91f)]
    [InlineData(10, 0.2f)]
    [InlineData(20, 1.3f)]
    public void The_ground_is_cut_exactly_round_the_pit_at_every_stride(int stride, float turn)
    {
        float quad = stride;
        int m = 100 / stride + 1;
        Func<float, float, float> slope = (x, z) => 500f + 0.07f * x - 0.04f * z;
        var (v, c, i) = Grid(m, quad, slope);
        var pit = Cut(turn);
        var (cv, _, ci) = PitTerrain.Cut(v, c, i, m, quad, new[] { pit });
        float ground = 0, floorArea = 0;
        for (int t = 0; t < ci.Length; t += 3)
        {
            var a = cv[ci[t]]; var b = cv[ci[t + 1]]; var d = cv[ci[t + 2]];
            float area = PlanArea(a, b, d);
            var centre = (a + b + d) / 3;
            if (MathF.Abs(a.Y - pit.Floor) < 1e-3f && MathF.Abs(b.Y - pit.Floor) < 1e-3f && MathF.Abs(d.Y - pit.Floor) < 1e-3f)
            {
                floorArea += area;
                continue;
            }
            // on the ground's plane: the original ground and the collar, never in the pit
            bool onGround = new[] { a, b, d }.All(p => MathF.Abs(p.Y - slope(p.X, p.Z)) < 2e-3f);
            if (!onGround) continue;
            Assert.False(pit.Rect.Contains(new Vector2(centre.X, centre.Z), -0.01f), $"ground in the pit at {centre}");
            ground += area;
        }
        float pitArea = pit.Rect.Width * pit.Rect.Depth;
        // every square metre of the tile is ground or pit: no gap round it, no overlap
        Assert.Equal(100f * 100f - pitArea, ground, 0);
        Assert.Equal(pitArea, floorArea, 0);
    }

    [Fact]
    public void The_cut_keeps_the_triangles_away_from_the_pit_as_they_were()
    {
        var (v, c, i) = Grid(51, 2, (_, _) => 500f);
        var (cv, cc, ci) = PitTerrain.Cut(v, c, i, 51, 2, new[] { Cut(0.3f) });
        // the grid's own vertices are kept, and the first rows of triangles too
        for (int k = 0; k < v.Length; k++) Assert.Equal(v[k], cv[k]);
        for (int k = 0; k < 6 * 10; k++) Assert.Equal(i[k], ci[k]);
        Assert.True(cv.Length > v.Length);
        Assert.Equal(cv.Length, cc.Length);
    }

    [Theory]
    [InlineData(0.53f)]
    [InlineData(-1.1f)]
    public void The_collision_drops_every_quad_in_the_pit_and_puts_the_rest_of_them_back(float turn)
    {
        var pit = Cut(turn, floor: 494f);
        var holes = PitTerrain.Holes(new[] { pit });
        // every quad whose middle is in the pit is a hole, and no quad far from it
        for (int r = 30; r < 75; r++)
            for (int k = 25; k < 70; k++)
            {
                if (pit.Rect.Contains(new Vector2(k + 0.5f, r + 0.5f))) Assert.Contains(r * PitTerrain.QuadsPerSide + k, holes);
                if (!pit.Rect.Contains(new Vector2(k + 0.5f, r + 0.5f), 1.5f)) Assert.DoesNotContain(r * PitTerrain.QuadsPerSide + k, holes);
            }
        Func<int, int, float> height = (c, r) => 500f + 0.05f * c + 0.02f * r;
        var faces = PitTerrain.CollisionFaces(new[] { pit }, holes, height);
        Assert.Equal(0, faces.Count % 3);
        // the ground put back: every quad Jolt drops (a hole and the three round its NaN corner), less the pit
        var dropped = new HashSet<int>();
        foreach (int h in holes)
            for (int dr = -1; dr <= 0; dr++)
                for (int dc = -1; dc <= 0; dc++)
                    dropped.Add(h + dr * PitTerrain.QuadsPerSide + dc);
        float ground = 0;
        for (int t = 0; t < faces.Count; t += 3)
        {
            var a = faces[t]; var b = faces[t + 1]; var d = faces[t + 2];
            bool onGround = new[] { a, b, d }.All(p => MathF.Abs(p.Y - (500f + 0.05f * p.X + 0.02f * p.Z)) < 2e-3f);
            if (onGround) ground += PlanArea(a, b, d);
        }
        Assert.Equal(dropped.Count - pit.Rect.Width * pit.Rect.Depth, ground, 0);
    }

    [Fact]
    public void The_lining_closes_the_pit_from_the_grounds_edge_to_the_floor()
    {
        var pit = Cut(0.4f, floor: 494f);
        var sampler = new PitTerrain.Sampler(1f, 101, (c, r) => 500f + 0.05f * c + 0.02f * r);
        var walls = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        float ramp = 0;
        PitTerrain.Lining(pit, sampler, (a, b, c, _, facing) =>
        {
            if (facing == Vector3.Up && !(a.Y == pit.Floor && b.Y == pit.Floor && c.Y == pit.Floor)) ramp += PlanArea(a, b, c);
            else if (facing != Vector3.Up) walls.Add((a, b, c));
        });
        // the walls stand on the rectangle's edges, reach the floor and the ground
        foreach (var (a, b, c) in walls)
            foreach (var p in new[] { a, b, c })
            {
                Assert.True(p.Y >= pit.Floor - 1e-3f);
                if (p.Y > pit.Floor + 1e-3f && !RampSide(pit, p))
                    Assert.Equal(sampler.At(p.X, p.Z), p.Y, 3);
            }
        // the ramp: its mouth's width all the way down its length
        Assert.Equal(4.6f * 18f, ramp, 0);
    }

    /// <summary>Whether a point is on the ramp's open side (its top edge descends: not the ground's height).</summary>
    private static bool RampSide(PitCut pit, Vector3 p)
    {
        var d = new Vector2(p.X, p.Z) - pit.TopInner;
        return MathF.Abs(d.Dot(new Vector2(-pit.Down.Y, pit.Down.X))) < 0.01f && d.Dot(pit.Down) > 0.01f;
    }
}
