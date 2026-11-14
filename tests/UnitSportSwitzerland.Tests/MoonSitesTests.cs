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
