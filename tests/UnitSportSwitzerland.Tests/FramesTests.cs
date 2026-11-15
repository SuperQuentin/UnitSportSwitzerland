using System;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The bodies' registry (#1847, src/Core/CelestialBody.cs, docs/plans/planets.md section 1).</summary>
public class CelestialBodyTests
{
    [Fact]
    public void The_Earth_is_body_zero_and_keeps_its_numbers()
    {
        Assert.Equal(BodyId.Earth, default(BodyId));
        Assert.Equal(BodyId.Earth, default(GlobalPos).Body);
        var earth = CelestialBody.Earth;
        Assert.Same(Ellipsoid.Wgs84, earth.Shape);
        Assert.Equal(3.986004418e14, earth.GM);
        Assert.Equal(7.292115e-5, earth.Spin);
        Assert.Equal(9.81, earth.GroundGravity);
        Assert.Equal(14, earth.LeafLevel);
        Assert.Equal(60_000, earth.SpaceEnterM);
        Assert.Equal(50_000, earth.SpaceExitM);
    }

    [Theory]
    [InlineData(BodyId.Moon, 66_100e3)]
    [InlineData(BodyId.Earth, 924_600e3)]
    [InlineData(BodyId.Mars, 577_000e3)]
    [InlineData(BodyId.Mercury, 112_000e3)]
    [InlineData(BodyId.Venus, 616_000e3)]
    [InlineData(BodyId.Jupiter, 48.2e9)]
    [InlineData(BodyId.Titan, 43_300e3)]
    public void Spheres_of_influence_are_laplaces(BodyId body, double metres) =>
        Assert.Equal(1, CelestialBody.Of(body).SphereOfInfluence / metres, 2);

    [Fact]
    public void The_sun_holds_everything()
    {
        Assert.True(double.IsPositiveInfinity(CelestialBody.Sun.SphereOfInfluence));
        Assert.Null(CelestialBody.Sun.Parent);
    }

    [Theory]
    [InlineData(BodyId.Moon, 12, 1.63)]
    [InlineData(BodyId.Mars, 13, 3.72)]
    // Mercury on MESSENGER's 2,439.4 km sphere (#1854): GM/r² 3.702, the tuned scale makes it 3.71
    [InlineData(BodyId.Mercury, 13, 3.71)]
    public void Leaves_stay_about_six_hundred_metres_and_gravity_scales(BodyId body, int leafLevel, double g)
    {
        var b = CelestialBody.Of(body);
        Assert.Equal(leafLevel, b.LeafLevel);
        double leaf = b.Shape.A * Math.PI / 2 / (1 << b.LeafLevel);
        Assert.InRange(leaf, 430, 870);
        Assert.Equal(g, b.GroundGravity, 2);
    }

    [Fact]
    public void Every_body_but_the_earth_is_a_body_of_the_sky_and_ids_are_in_order()
    {
        int i = 0;
        foreach (var b in CelestialBody.All)
        {
            Assert.Equal((BodyId)i++, b.Id);
            if (b.Id == BodyId.Earth) Assert.Null(b.Sky);
            else Assert.Same(b, CelestialBody.Of(b.Sky!.Value));
        }
        Assert.Equal(Enum.GetValues<BodyId>().Length, i);
    }
}

/// <summary>The frame tree (#1847, src/Core/Frames.cs, docs/plans/planets.md section 3).</summary>
public class FramesTests
{
    private static readonly DateTime When = new(2027, 7, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly SkyMoment Now = SkyMoment.At(When);

    private static double AngleDeg(DVec3 a, DVec3 b) =>
        Math.Acos(Math.Clamp(a.Dot(b) / (a.Length * b.Length), -1, 1)) * 180 / Math.PI;

    [Fact]
    public void The_moons_centre_is_where_the_sky_draws_it()
    {
        // the sky's is apparent (nutation, the true obliquity); the frames' geometric: within ~20″ and the same distance
        foreach (var utc in new[] { When, new DateTime(2026, 12, 11, 23, 0, 0, DateTimeKind.Utc), new DateTime(2030, 3, 1, 4, 0, 0, DateTimeKind.Utc) })
        {
            var frames = Frames.Convert(BodyId.Moon, DVec3.Zero, BodyId.Earth, SkyMoment.At(utc));
            DVec3 sky = Ephemeris.Ecef(SkyBody.Moon, utc);
            Assert.True(AngleDeg(frames, sky) < 0.01, $"{AngleDeg(frames, sky):F5}° off at {utc}");
            Assert.Equal(sky.Length, frames.Length, 1);
        }
    }

    [Fact]
    public void Mars_is_where_the_sky_draws_it_less_the_light_time()
    {
        var frames = Frames.Convert(BodyId.Mars, DVec3.Zero, BodyId.Earth, Now);
        DVec3 sky = Ephemeris.Ecef(SkyBody.Mars, When);
        // light time (minutes) and aberration (20″) are the sky's only
        Assert.True(AngleDeg(frames, sky) < 0.05, $"{AngleDeg(frames, sky):F4}°");
        Assert.Equal(1, frames.Length / sky.Length, 3);
    }

    [Fact]
    public void The_earth_turns_as_the_stars_do()
    {
        var (x, y, z) = Ephemeris.CelestialAt(When, Ephemeris.SiderealTime(When));
        Assert.Equal(Ephemeris.Celestial(When), (x, y, z));
        // the ICRF's X in ECEF is the Earth's orientation's first row
        var o = Frames.Orientation(BodyId.Earth, Now);
        Assert.Equal(x.X, o.C0.X);
        Assert.Equal(x.Y, o.C1.X);
        Assert.Equal(x.Z, o.C2.X);
    }

    [Fact]
    public void Earth_fixed_to_moon_fixed_and_back_holds_to_a_micrometre()
    {
        var shuttle = Ellipsoid.Wgs84.ToEcef(new Geodetic(46.5, 6.6, 400_000));
        DVec3 p = shuttle;
        var onMoon = Frames.Convert(BodyId.Earth, p, BodyId.Moon, Now);
        var back = Frames.Convert(BodyId.Moon, onMoon, BodyId.Earth, Now);
        Assert.True((back - p).Length < 1e-6, $"{(back - p).Length:E2} m");
        // and a place on the moon's ground the other way
        DVec3 crater = CelestialBody.Moon.Shape.ToEcef(new Geodetic(26.1, 3.6, 0));
        var inEarth = Frames.Convert(BodyId.Moon, crater, BodyId.Earth, Now);
        Assert.True((Frames.Convert(BodyId.Earth, inEarth, BodyId.Moon, Now) - crater).Length < 1e-6);
    }

    [Fact]
    public void Earth_fixed_to_mars_fixed_and_back_holds_to_a_centimetre()
    {
        DVec3 p = Ellipsoid.Wgs84.ToEcef(new Geodetic(46.5, 6.6, 0));
        var onMars = Frames.Convert(BodyId.Earth, p, BodyId.Mars, Now);
        var back = Frames.Convert(BodyId.Mars, onMars, BodyId.Earth, Now);
        Assert.True((back - p).Length < 0.01, $"{(back - p).Length:E2} m");
    }

    [Fact]
    public void Body_fixed_axes_are_orthonormal_and_right_handed()
    {
        foreach (var b in CelestialBody.All)
        {
            var o = Frames.Orientation(b.Id, Now);
            Assert.Equal(1, o.C0.Length, 12);
            Assert.Equal(1, o.C1.Length, 12);
            Assert.Equal(1, o.C2.Length, 12);
            Assert.Equal(0, o.C0.Dot(o.C1), 12);
            Assert.Equal(0, o.C0.Dot(o.C2), 12);
            Assert.True((o.C0.Cross(o.C1) - o.C2).Length < 1e-12, b.Name);
        }
    }

    [Fact]
    public void The_moon_moves_at_a_kilometre_a_second_and_the_earth_at_thirty()
    {
        Assert.InRange(Frames.Velocity(BodyId.Earth, BodyId.Moon, When).Length, 960, 1090);
        Assert.InRange(Frames.Velocity(BodyId.Sun, BodyId.Earth, When).Length, 29_200, 30_300);
    }

    [Fact]
    public void The_moon_shows_the_earth_its_near_side()
    {
        // the moon's 0° N 0° E faces the Earth within the libration (±8°)
        var o = Frames.Orientation(BodyId.Moon, Now);
        var toEarth = Frames.Offset(BodyId.Moon, BodyId.Earth, When);
        Assert.True(AngleDeg(o.C0, toEarth) < 10, $"{AngleDeg(o.C0, toEarth):F2}°");
    }

    [Fact]
    public void A_big_moon_faces_its_planet()
    {
        var o = Frames.Orientation(BodyId.Io, Now);
        var toJupiter = Frames.Offset(BodyId.Io, BodyId.Jupiter, When);
        Assert.True(AngleDeg(o.C0, toJupiter) < 0.5);
        Assert.Equal(421_700_000, toJupiter.Length, 0);
    }

    [Fact]
    public void Offsets_go_through_the_nearest_common_ancestor()
    {
        var a = Frames.Offset(BodyId.Moon, BodyId.Mars, When);
        var b = Frames.Offset(BodyId.Earth, BodyId.Mars, When) - Frames.Offset(BodyId.Earth, BodyId.Moon, When);
        Assert.True((a - b).Length < 1e-3);
        Assert.Equal(DVec3.Zero, Frames.Offset(BodyId.Io, BodyId.Io, When));
        Assert.True((Frames.Offset(BodyId.Io, BodyId.Europa, When) + Frames.Offset(BodyId.Europa, BodyId.Io, When)).Length < 1e-6);
    }

    [Fact]
    public void A_place_belongs_to_the_deepest_sphere_that_holds_it_with_a_margin_to_leave()
    {
        DVec3 leo = Ellipsoid.Wgs84.ToEcef(new Geodetic(46.5, 6.6, 400_000));
        Assert.Equal(BodyId.Earth, Frames.SphereAt(BodyId.Earth, leo, Now));
        Assert.Equal(BodyId.Earth, Frames.SphereAt(BodyId.Moon, Frames.Convert(BodyId.Earth, leo, BodyId.Moon, Now), Now));

        // 100 km over the moon, given from the Earth
        var lunarOrbit = Frames.Convert(BodyId.Moon, new DVec3(1_837_400, 0, 0), BodyId.Earth, Now);
        Assert.Equal(BodyId.Moon, Frames.SphereAt(BodyId.Earth, lunarOrbit, Now));

        // 68,000 km from the moon: just out of its sphere, not out of its margin
        var edge = new DVec3(68_000_000, 0, 0);
        Assert.Equal(BodyId.Moon, Frames.SphereAt(BodyId.Moon, edge, Now));
        Assert.Equal(BodyId.Earth, Frames.SphereAt(BodyId.Earth, Frames.Convert(BodyId.Moon, edge, BodyId.Earth, Now), Now));

        // 2 million km out: the Sun's; next to Mars: Mars'
        Assert.Equal(BodyId.Sun, Frames.SphereAt(BodyId.Earth, new DVec3(0, 0, 2e9), Now));
        Assert.Equal(BodyId.Mars, Frames.SphereAt(BodyId.Earth, Frames.Convert(BodyId.Mars, new DVec3(4e6, 0, 0), BodyId.Earth, Now), Now));
    }
}

/// <summary>Places on other bodies (#1847, <see cref="GlobalPos.Body"/>) and the anchor across them.</summary>
public class BodyPlaceTests
{
    private static readonly SkyMoment Now = SkyMoment.At(new DateTime(2027, 7, 15, 8, 0, 0, DateTimeKind.Utc));

    private static GlobalPos OnMoon(double lat, double lon, double h) =>
        GlobalPos.On(BodyId.Moon, CelestialBody.Moon.Shape.ToEcef(new Geodetic(lat, lon, h)));

    [Fact]
    public void An_earth_place_is_what_it_was()
    {
        var p = GlobalPos.FromLv95(2_600_000, 1_200_000, 550);
        Assert.Equal(BodyId.Earth, p.Body);
        Assert.Equal(p, GlobalPos.On(BodyId.Earth, p.Ecef));
        Assert.Equal(Ellipsoid.Wgs84.ToGeodetic(p.Ecef), p.Geodetic);
        Assert.Equal(550, p.Alt, 3);
        Assert.Equal(p, p.In(BodyId.Earth, Now));
    }

    [Fact]
    public void A_moon_place_has_the_moons_heights_and_level()
    {
        var p = OnMoon(26.1, 3.6, 120);
        Assert.Equal(BodyId.Moon, p.Body);
        Assert.Equal(120, p.Alt, 6);
        Assert.Equal(26.1, p.Geodetic.LatDeg, 9);
        var q = p.Moved(300, -40, 5);
        Assert.Equal(BodyId.Moon, q.Body);
        var (e, n, u) = p.OffsetTo(q);
        Assert.Equal(300, e, 6);
        Assert.Equal(-40, n, 6);
        Assert.Equal(5, u, 6);
        Assert.Equal(125, p.WithAlt(125).Alt, 6);
        // the local vectors run on the moon's normal: b + (a - b) is a
        var d = q - p;
        Assert.True((p + d).DistanceTo(q) < 1e-3);
        Assert.StartsWith("Moon ", p.ToString());
    }

    [Fact]
    public void Places_on_two_bodies_are_never_mixed_silently()
    {
        var earth = GlobalPos.FromLv95(2_600_000, 1_200_000, 550);
        var moon = OnMoon(0, 0, 0);
        Assert.NotEqual(earth, GlobalPos.On(BodyId.Moon, earth.Ecef));
        Assert.Throws<InvalidOperationException>(() => earth.DistanceTo(moon));
        Assert.Throws<InvalidOperationException>(() => earth - moon);
        Assert.Throws<InvalidOperationException>(() => earth.OffsetTo(moon));
        // through a sky moment they are: the moon's near side is ~384,000 km away less its radius
        double d = earth.DistanceTo(moon.In(BodyId.Earth, Now));
        Assert.InRange(d, 350_000e3, 410_000e3);
    }

    [Fact]
    public void Saves_carry_the_body_only_off_the_earth()
    {
        var earth = GlobalPos.FromLv95(2_600_000, 1_200_000, 550);
        string e = System.Text.Json.JsonSerializer.Serialize(earth);
        Assert.DoesNotContain("\"b\"", e);
        Assert.Equal(earth, System.Text.Json.JsonSerializer.Deserialize<GlobalPos>(e));
        var moon = OnMoon(26.1, 3.6, 120);
        string m = System.Text.Json.JsonSerializer.Serialize(moon);
        Assert.Contains("\"b\":1", m);
        Assert.Equal(moon, System.Text.Json.JsonSerializer.Deserialize<GlobalPos>(m));
    }

    [Fact]
    public void An_anchor_rebased_on_another_body_moves_nothing()
    {
        // a frame 60,000 km from the moon on the Earth's side, counted from the Earth, then from the moon
        var nearMoon = UnitSport.Core.Frames.Convert(BodyId.Moon, new DVec3(60_000_000, 0, 0), BodyId.Earth, Now);
        var a = FrameAxes.Enu(nearMoon.AsEcef);
        var b = a.Rebased(BodyId.Moon, Now);
        Assert.Equal(BodyId.Moon, b.Body);
        foreach (var w in new[] { (0.0, 0.0, 0.0), (15_000.0, -3_000.0, 9_000.0), (-20_000.0, 20_000.0, 0.0) })
        {
            DVec3 viaEarth = UnitSport.Core.Frames.Convert(BodyId.Earth, a.ToEcef(w.Item1, w.Item2, w.Item3), BodyId.Moon, Now);
            DVec3 direct = b.ToEcef(w.Item1, w.Item2, w.Item3);
            Assert.True((viaEarth - direct).Length < 1e-5, $"{(viaEarth - direct).Length:E2} m");
        }
        Assert.Same(a, a.Rebased(BodyId.Earth, Now));
    }

    [Fact]
    public void A_tangent_anchor_stays_on_its_body()
    {
        var at = CelestialBody.Moon.Shape.ToEcef(new Geodetic(10, 20, 15_000));
        var first = FrameAxes.Enu(at, BodyId.Moon);
        var next = FrameAxes.Tangent(CelestialBody.Moon.Shape.ToEcef(new Geodetic(10.3, 20, 15_000)), 15_000, first);
        Assert.Equal(BodyId.Moon, next.Body);
        // its up is the moon's normal there: the sphere's radius
        var o = next.Origin;
        DVec3 radial = new DVec3(o.X, o.Y, o.Z).Normalized();
        Assert.True((radial - (DVec3)next.Y).Length < 1e-9);
        Assert.Equal(CelestialBody.Moon.Shape.A, new DVec3(o.X, o.Y, o.Z).Length, 3);
    }
}
