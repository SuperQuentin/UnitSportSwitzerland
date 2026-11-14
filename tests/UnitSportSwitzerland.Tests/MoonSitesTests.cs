using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The Moon's sites and the ground stamps under them (#1939, docs/plans/moon-surface.md sections 1 and 2).</summary>
public class MoonSitesTests
{
    private static readonly double R = Ellipsoid.Moon.A;

    private static EcefPos At(double lat, double lon) => CubeSphere.NormalAt(lat, lon);

    /// <summary>A ground of data from a smooth function, and the generated craters.</summary>
    private static BodyHeights Ground(SurfaceStamps? stamps) =>
        new(CubeShape.Moon, 2, _ => null, new CraterField(1, R), stamps);

    [Fact]
    public void Outside_every_stamp_the_ground_is_unchanged_to_the_bit()
    {
        var plain = Ground(null);
        var stamped = Ground(new SurfaceStamps(MoonSites.Stamps(), R));
        var rng = new Random(3);
        for (int k = 0; k < 2000; k++)
        {
            var n = At(rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180);
            if (stamped.Stamps!.Touches(n)) continue;
            Assert.Equal(plain.At(n), stamped.At(n));
        }
    }

    [Fact]
    public void A_calm_stamp_keeps_the_data_and_drops_the_generated_craters()
    {
        var c = At(10, 20);
        var stamps = new SurfaceStamps([new SurfaceStamp(c, null, 50, 30, StampMode.Calm)], R);
        var g = Ground(stamps);
        // inside the radius: the data alone (0 here: no tiles)
        for (int k = 0; k < 40; k++)
        {
            var n = Offset(c, Math.Cos(k) * 45, Math.Sin(k) * 45);
            Assert.Equal(0, g.At(n), 9);
        }
    }

    [Fact]
    public void A_level_stamp_is_one_height_inside_and_eases_out()
    {
        var c = At(-30, 40);
        // a tilted data ground: the level is the data's height at the centre plus the offset
        Func<EcefPos, double> slope = n => 1000 * n.X;
        var stamps = new SurfaceStamps([new SurfaceStamp(c, null, 200, 100, StampMode.Level, 0.5)], R);
        double level = slope(c) + 0.5;
        for (int k = 0; k < 40; k++)
        {
            var n = Offset(c, Math.Cos(k) * 190, Math.Sin(k) * 190);
            Assert.Equal(level, stamps.Apply(n, slope(n), 3.0, slope), 9);
        }
        var far = Offset(c, 301, 0);
        Assert.Equal(slope(far) + 3.0, stamps.Apply(far, slope(far), 3.0, slope), 9);
        var mid = Offset(c, 250, 0);
        double h = stamps.Apply(mid, slope(mid), 3.0, slope);
        Assert.InRange(h, Math.Min(level, slope(mid) + 3) - 1e-9, Math.Max(level, slope(mid) + 3) + 1e-9);
    }

    [Fact]
    public void A_strip_is_level_along_its_length_between_its_ends()
    {
        var a = At(-89.5, 0);
        var b = Offset(a, 0, 3000);
        Func<EcefPos, double> data = n => 0;
        var stamps = new SurfaceStamps([new SurfaceStamp(a, b, 30, 20, StampMode.Level, 1)], R);
        for (int k = 0; k <= 10; k++)
        {
            var n = Offset(a, 25 * Math.Sin(k), 3000.0 * k / 10);
            Assert.Equal(1, stamps.Apply(n, 0, 5, data), 6);
        }
        Assert.Equal(5, stamps.Apply(Offset(a, 60, 1500), 0, 5, data), 9);
    }

    [Fact]
    public void An_impact_digs_a_fresh_bowl_with_a_rim()
    {
        var c = At(-2.55, -27.88);
        var stamps = new SurfaceStamps([new SurfaceStamp(c, null, 17, 0, StampMode.Crater)], R);
        Func<EcefPos, double> data = _ => 0;
        Assert.Equal(-0.2 * 34, stamps.Apply(c, 0, 0, data), 6);
        Assert.Equal(0.04 * 34, stamps.Apply(Offset(c, 17, 0), 0, 0, data), 3);
        Assert.Equal(0, stamps.Apply(Offset(c, 35, 0), 0, 0, data), 9);
    }

    [Fact]
    public void Every_site_has_a_unique_id_parts_and_a_place_on_the_moon()
    {
        var ids = new HashSet<string>();
        foreach (var s in MoonSites.All)
        {
            Assert.True(ids.Add(s.Id), s.Id);
            Assert.NotEmpty(s.Parts);
            Assert.InRange(s.Lat, -90, 90);
            Assert.InRange(s.Lon, -180, 180);
        }
        // the six Apollo landings, three rovers, five S-IVBs
        Assert.Equal(6, MoonSites.All.Count(s => s.Kind == MoonSiteKind.Apollo));
        Assert.Equal(3, MoonSites.All.Sum(s => s.Parts.Count(p => p.Part == MoonPart.Lrv)));
        Assert.Equal(5, MoonSites.All.Count(s => s.Kind == MoonSiteKind.Impact));
    }

    [Fact]
    public void The_traces_are_the_same_every_time_and_start_at_the_ladder()
    {
        var a11 = MoonSites.Find("apollo11")!;
        var one = MoonSites.Paths(a11);
        var two = MoonSites.Paths(a11);
        Assert.Equal(one.Count, two.Count);
        for (int i = 0; i < one.Count; i++) Assert.Equal(one[i].Points, two[i].Points);
        Assert.All(one, t => Assert.False(t.Rover));
        Assert.Contains(MoonSites.Paths(MoonSites.Find("apollo17")!), t => t.Rover);
    }

    [Fact]
    public void An_offset_is_that_many_metres_on_the_sphere()
    {
        var s = MoonSites.Find("apollo15")!;
        var n = MoonSites.Offset(s, 300, -400);
        double d = Math.Acos(Dot(n, s.Normal)) * R;
        Assert.Equal(500, d, 0);
    }

    private static EcefPos Offset(EcefPos c, double east, double north)
    {
        var g = Ellipsoid.Moon.ToGeodetic(new EcefPos(c.X * R, c.Y * R, c.Z * R));
        var s = new MoonSite("t", "t", MoonSiteKind.Flag, g.LatDeg, g.LonDeg, []);
        return MoonSites.Offset(s, east, north);
    }

    private static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

/// <summary>The colonies' plans (#1940, docs/plans/moon-surface.md section 4).</summary>
public class ColonyPlanTests
{
    [Fact]
    public void A_plan_is_the_same_every_time()
    {
        var c = MoonColonies.Find("artemis")!;
        var a = ColonyPlan.For(c);
        var b = ColonyPlan.For(c);
        Assert.Equal(a.Modules, b.Modules);
        Assert.Equal(a.Doors, b.Doors);
        Assert.Equal(a.Links, b.Links);
        Assert.Equal(a.Pads, b.Pads);
    }

    [Theory]
    [InlineData(ColonyTheme.Artemis)]
    [InlineData(ColonyTheme.MoonVillage)]
    [InlineData(ColonyTheme.Ilrs)]
    public void Modules_never_overlap_and_tubes_are_short(ColonyTheme theme)
    {
        for (int seed = 1; seed < 40; seed++)
        {
            var plan = ColonyPlan.For(new MoonColony("t", "t", theme, 0, 0, seed, 420, seed * 17 % 360, true));
            var m = plan.Modules;
            for (int i = 0; i < m.Count; i++)
                for (int j = i + 1; j < m.Count; j++)
                    Assert.False(Overlap(m[i], m[j], 1.5), $"seed {seed}: {m[i].Kind} {i} and {m[j].Kind} {j} overlap");
            foreach (var l in plan.Links)
            {
                var a = plan.Doors[l.DoorA];
                var b = plan.Doors[l.DoorB];
                double len = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
                Assert.InRange(len, 4, 16);
            }
            // everything on the plateau
            Assert.True(plan.Extent < 420, $"seed {seed}: reaches {plan.Extent:F0} m");
            Assert.Equal(3, plan.Pads.Count);
        }
    }

    [Fact]
    public void Every_colony_stands_on_a_level_stamp()
    {
        var stamps = new SurfaceStamps(MoonColonies.Stamps(), Ellipsoid.Moon.A);
        Func<EcefPos, double> slope = n => 500 * n.Y + 200 * n.Z;
        foreach (var c in MoonColonies.All)
        {
            double level = slope(c.Normal);
            var probe = new MoonSite("p", "p", MoonSiteKind.Colony, c.Lat, c.Lon, []);
            for (int k = 0; k < 12; k++)
            {
                var n = MoonSites.Offset(probe, Math.Sin(k) * c.PlateauM * 0.9, Math.Cos(k) * c.PlateauM * 0.9);
                Assert.Equal(level, stamps.Apply(n, slope(n), 2, slope), 6);
            }
        }
    }

    /// <summary>Two modules' footprints (rectangles along their yaw, a circle for round ones) closer than <paramref name="gap"/>.</summary>
    private static bool Overlap(ColonyModule a, ColonyModule b, double gap)
    {
        // sample a's footprint outline and test against b's (inflated by the gap)
        for (int k = 0; k < 64; k++)
        {
            var (x, z) = Outline(a, k / 64.0);
            if (Inside(b, x, z, gap)) return true;
        }
        return Inside(b, a.X, a.Z, gap) || Inside(a, b.X, b.Z, gap);
    }

    private static bool Round(ColonyModule m) => m.Kind is ModuleKind.Hub or ModuleKind.Stack or ModuleKind.Dome or ModuleKind.Biodome;

    private static (double X, double Z) Outline(ColonyModule m, double t)
    {
        double a = t * Math.Tau;
        if (Round(m)) return (m.X + Math.Sin(a) * m.Width / 2, m.Z - Math.Cos(a) * m.Width / 2);
        // the rectangle's outline in its own axes, then turned
        double u = Math.Clamp(Math.Sin(a) * 1.5, -1, 1) * m.Width / 2, v = Math.Clamp(Math.Cos(a) * 1.5, -1, 1) * m.Length / 2;
        var (ax, az) = ColonyPlan.Along(m.Yaw);
        return (m.X + ax * v - az * u, m.Z + az * v + ax * u);
    }

    private static bool Inside(ColonyModule m, double x, double z, double gap)
    {
        double dx = x - m.X, dz = z - m.Z;
        if (Round(m)) return dx * dx + dz * dz < Math.Pow(m.Width / 2 + gap, 2);
        var (ax, az) = ColonyPlan.Along(m.Yaw);
        double v = dx * ax + dz * az, u = -dx * az + dz * ax;
        return Math.Abs(v) < m.Length / 2 + gap && Math.Abs(u) < m.Width / 2 + gap;
    }
}

/// <summary>The colonies' rooms and airlocks (#1941).</summary>
public class ColonyAirlockPlanTests
{
    [Fact]
    public void Every_spine_but_the_hangars_ends_in_an_airlock_and_every_module_has_a_room()
    {
        for (int seed = 1; seed < 30; seed++)
        {
            var plan = ColonyPlan.For(new MoonColony("t", "t", ColonyTheme.Artemis, 0, 0, seed, 420, seed * 13 % 360, true));
            Assert.Equal(3, plan.Airlocks.Count);
            Assert.Equal(plan.Modules.Count, plan.Rooms.Count);
            foreach (var a in plan.Airlocks)
            {
                Assert.Equal(ModuleKind.Airlock, plan.Modules[a.Module].Kind);
                Assert.Equal(RoomKind.Airlock, plan.Rooms[a.Module]);
                // the outer door faces away from the hub
                var o = plan.Doors[a.Outer];
                var i = plan.Doors[a.Inner];
                Assert.True(o.X * o.X + o.Z * o.Z > i.X * i.X + i.Z * i.Z);
            }
            Assert.Contains(RoomKind.Control, plan.Rooms);
            Assert.Contains(RoomKind.Garage, plan.Rooms);
        }
    }
}

/// <summary>The other staffed colonies and the industry (#1942).</summary>
public class MoonIndustryPlanTests
{
    private static double Metres(EcefPos a, EcefPos b) =>
        Math.Acos(Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1, 1)) * Ellipsoid.Moon.A;

    [Theory]
    [InlineData(ColonyTheme.Artemis)]
    [InlineData(ColonyTheme.MoonVillage)]
    [InlineData(ColonyTheme.Ilrs)]
    public void The_industry_stands_clear_of_the_modules_the_road_and_the_pads(ColonyTheme theme)
    {
        for (int seed = 1; seed < 40; seed++)
        {
            var plan = ColonyPlan.For(new MoonColony("t", "t", theme, 0, 0, seed, 420, seed * 17 % 360, true));
            Assert.True(plan.Industry.FindAll(i => i.Kind == IndustryKind.SolarArray).Count >= 8, $"seed {seed}: few arrays");
            Assert.Single(plan.Industry, i => i.Kind == IndustryKind.Digger);
            foreach (var i in plan.Industry)
            {
                if (i.Kind == IndustryKind.Lander) continue;   // on its pad: MoonPadsTests
                double reach = ColonyPlan.IndustryReach(i);
                Assert.True(plan.Clear(i.X, i.Z, reach + 2), $"seed {seed}: {i.Kind} at {i.X:F0},{i.Z:F0} on a module");
                foreach (var p in plan.Pads)
                    Assert.True(Math.Sqrt((p.X - i.X) * (p.X - i.X) + (p.Z - i.Z) * (p.Z - i.Z)) > p.Radius + reach + 10, $"seed {seed}: {i.Kind} on a pad");
                // the road: from the hangar's door straight out
                var (a, b) = (plan.Road[0], plan.Road[^1]);
                double dx = b.X - a.X, dz = b.Z - a.Z, l = Math.Sqrt(dx * dx + dz * dz);
                double t = Math.Clamp(((i.X - a.X) * dx + (i.Z - a.Z) * dz) / (l * l), 0, 1);
                double ex = a.X + dx * t - i.X, ez = a.Z + dz * t - i.Z;
                Assert.True(Math.Sqrt(ex * ex + ez * ez) > reach + 6, $"seed {seed}: {i.Kind} on the road");
            }
        }
    }

    [Fact]
    public void Moon_village_keeps_out_of_tranquility_base_and_ilrs_of_change_4()
    {
        var mv = MoonColonies.Find("moonvillage")!;
        double d = Metres(mv.Normal, MoonSites.Find("apollo11")!.Normal);
        Assert.InRange(d, 1800, 3000);
        Assert.True(d > mv.PlateauM + 150 + 300, "the plateau's falloff reaches the heritage zone");
        Assert.True(Metres(MoonColonies.Find("ilrs")!.Normal, MoonSites.Find("change4")!.Normal) > 10000);
    }

    [Fact]
    public void Every_outpost_has_its_pieces_and_its_ground_touches_no_site_or_colony()
    {
        foreach (var o in MoonOutposts.All)
        {
            Assert.NotEmpty(MoonOutposts.Plan(o));
            Assert.NotNull(MoonColonies.Find(o.Colony));
            foreach (var c in MoonColonies.All)
                Assert.True(Metres(o.Normal, c.Normal) > c.PlateauM + 150 + o.RadiusM + 40, $"{o.Id} on {c.Id}'s plateau");
            foreach (var s in MoonSites.All)
                Assert.True(Metres(o.Normal, s.Normal) > o.Extent + 500, $"{o.Id} near {s.Id}");
        }
        var md = MoonOutposts.Find("massdriver")!;
        Assert.InRange(Metres(md.Normal, md.End!.Value), 1990, 2010);
        var dishes = MoonOutposts.Plan(MoonOutposts.Find("radiotelescope")!).FindAll(i => i.Kind == IndustryKind.Dish);
        Assert.Equal(19, dishes.Count);
        for (int i = 0; i < dishes.Count; i++)
            for (int j = i + 1; j < dishes.Count; j++)
                Assert.True(Math.Sqrt(Math.Pow(dishes[i].X - dishes[j].X, 2) + Math.Pow(dishes[i].Z - dishes[j].Z, 2)) > 20);
    }

    [Fact]
    public void A_level_outpost_is_one_height_inside_its_disc()
    {
        var stamps = new SurfaceStamps(MoonColonies.Stamps(), Ellipsoid.Moon.A);
        Func<EcefPos, double> slope = n => 500 * n.Y + 200 * n.Z;
        foreach (var o in MoonOutposts.All)
        {
            if (o.LengthM > 0) continue;
            for (int k = 0; k < 8; k++)
            {
                var n = MoonSites.Offset(o.Probe, Math.Sin(k) * o.RadiusM * 0.9, Math.Cos(k) * o.RadiusM * 0.9);
                Assert.Equal(slope(o.Normal), stamps.Apply(n, slope(n), 2, slope), 6);
            }
        }
    }
}

/// <summary>The pads and the runway the shuttle may land on (#1943).</summary>
public class MoonPadsTests
{
    [Fact]
    public void Every_staffed_colony_has_three_pads_one_with_a_lander_and_artemis_a_runway()
    {
        foreach (var c in MoonColonies.All)
        {
            var pads = new List<MoonPad>();
            foreach (var p in MoonPads.All)
                if (p.Colony == c.Id && !p.IsRunway) pads.Add(p);
            Assert.Equal(3, pads.Count);
            Assert.Single(pads, p => p.Occupied);
        }
        var runway = MoonPads.Find("runway")!.Value;
        Assert.True(runway.IsRunway);
        double len = Math.Acos(Math.Clamp(runway.Centre.X * runway.End!.Value.X + runway.Centre.Y * runway.End.Value.Y + runway.Centre.Z * runway.End.Value.Z, -1, 1)) * Ellipsoid.Moon.A;
        Assert.InRange(len, 2990, 3010);
    }

    [Fact]
    public void A_pad_and_the_runway_are_level_on_their_stamps()
    {
        var stamps = new SurfaceStamps(MoonSites.Stamps().Concat(MoonColonies.Stamps()), Ellipsoid.Moon.A);
        Func<EcefPos, double> slope = n => 500 * n.Y + 200 * n.Z;
        foreach (var p in MoonPads.All)
        {
            if (p.IsRunway) continue;
            var c = MoonColonies.Find(p.Colony)!;
            // the pad is on its colony's plateau: the plateau's height
            Assert.Equal(slope(c.Normal), stamps.Apply(p.Centre, slope(p.Centre), 2, slope), 6);
        }
        // the runway: straight between its ends' heights (a level strip)
        var r = MoonPads.Find("runway")!.Value;
        var mid = new EcefPos((r.Centre.X + r.End!.Value.X) / 2, (r.Centre.Y + r.End.Value.Y) / 2, (r.Centre.Z + r.End.Value.Z) / 2);
        double l = Math.Sqrt(mid.X * mid.X + mid.Y * mid.Y + mid.Z * mid.Z);
        mid = new EcefPos(mid.X / l, mid.Y / l, mid.Z / l);
        double want = (slope(r.Centre) + slope(r.End.Value)) / 2;
        Assert.Equal(want, stamps.Apply(mid, slope(mid), 2, slope), 2);
    }

    [Fact]
    public void The_nearest_free_pad_skips_the_occupied_ones()
    {
        foreach (var p in MoonPads.All)
        {
            var near = MoonPads.NearestFree(p.Centre)!.Value;
            if (!p.Occupied) Assert.Equal(p.Id, near.Pad.Id);
            else Assert.NotEqual(p.Id, near.Pad.Id);
        }
    }
}
