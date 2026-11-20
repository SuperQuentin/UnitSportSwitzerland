using System;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>What the spectator and the cruise drive measure (#1851, src/Player/SurfaceReach.cs): the nearest surface of any body.</summary>
public class SurfaceReachTests
{
    [Fact]
    public void Near_the_earth_it_is_the_altitude_and_near_the_moon_the_moons_ground()
    {
        var sky = SkyMoment.At(SwissTime.ToUtc(new DateOnly(2027, 7, 15), 10));
        var keep = SkyClock.Live;
        SkyClock.Live = () => sky;
        try
        {
            var leo = new OriginFrame(FrameAxes.Enu(Ellipsoid.Wgs84.ToEcef(new Geodetic(46.5, 6.6, 400_000))), 0);
            Assert.Equal(400_000, SurfaceReach.Of(leo, Vector3.Zero, 400_000));
            // a frame counted from the Earth, 50 km over the Moon's near side
            var over = UnitSport.Core.Frames.Convert(BodyId.Moon, new DVec3(1_787_400, 0, 0), BodyId.Earth, sky).AsEcef;
            var near = new OriginFrame(FrameAxes.Enu(over), 0);
            double altitude = Ellipsoid.Wgs84.ToGeodetic(over).H;
            Assert.Equal(50_000, SurfaceReach.Of(near, Vector3.Zero, altitude), 0);
        }
        finally
        {
            SkyClock.Live = keep;
        }
    }
}
