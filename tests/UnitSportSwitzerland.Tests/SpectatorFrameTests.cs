using System;
using UnitSport.Core;
using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The fly camera's frame blended by distance (#2053): the weights' curves, the nested rest frame, the body's turn.</summary>
public class SpectatorFrameTests
{
    [Fact]
    public void Co_rotation_and_up_are_whole_low_down_and_gone_soon_after_space()
    {
        var earth = CelestialBody.Earth;
        Assert.Equal(1, SpectatorFrame.SpinWeight(earth, 1_000));
        Assert.Equal(1, SpectatorFrame.UpWeight(earth, 100_000));          // the air's top
        Assert.Equal(0, SpectatorFrame.SpinWeight(earth, 230_000));
        Assert.Equal(0, SpectatorFrame.UpWeight(earth, 400_000));          // the ISS's height: free
        double halfway = SpectatorFrame.UpWeight(earth, 243_000);
        Assert.InRange(halfway, 0.4, 0.6);
        // the Moon, no air: within tens of km
        var moon = CelestialBody.Moon;
        Assert.Equal(1, SpectatorFrame.SpinWeight(moon, 3_000));
        Assert.Equal(0, SpectatorFrame.SpinWeight(moon, 36_000));
        Assert.Equal(1, SpectatorFrame.UpWeight(moon, 8_000));
        Assert.Equal(0, SpectatorFrame.UpWeight(moon, 70_000));
        // co-rotation goes first
        for (double h = 0; h < 400_000; h += 5_000)
            Assert.True(SpectatorFrame.SpinWeight(earth, h) <= SpectatorFrame.UpWeight(earth, h) + 1e-12);
    }

    [Fact]
    public void Among_saturns_rings_the_camera_turns_with_them()
    {
        var saturn = CelestialBody.Saturn;
        // 2 km over the B ring's face, 40,000 km over the cloud tops: the rings are its surface
        var place = new DVec3(100_000_000, 0, 2_200);
        double height = place.Length - saturn.Shape.A;
        Assert.Equal(0, SpectatorFrame.SpinWeight(saturn, height));
        Assert.Equal(1, SpectatorFrame.SpinWeight(saturn, height, place));
        // 5,000 km over the ring plane: free of them
        Assert.Equal(0, SpectatorFrame.SpinWeight(saturn, height, new DVec3(100_000_000, 0, 5_000_000)));
    }

    [Fact]
    public void The_weights_are_smooth()
    {
        var earth = CelestialBody.Earth;
        double last = 1, lastStep = 0;
        for (double h = 100_000; h < 400_000; h += 100)
        {
            double w = SpectatorFrame.UpWeight(earth, h);
            Assert.True(w <= last + 1e-12);
            // no step: the change between 100 m apart never more than 0.2 %
            Assert.True(Math.Abs(w - last) < 0.002);
            Assert.True(Math.Abs((last - w) - lastStep) < 1e-4);
            lastStep = last - w;
            last = w;
        }
    }

    [Fact]
    public void Standing_still_near_the_moon_is_the_moons_frame_and_far_out_the_suns()
    {
        var m0 = SkyClock.Now;
        var m1 = m0 with { Utc = m0.Utc.AddSeconds(1) };
        // 2,000 km over the Moon, anchored on it: no drift at all
        var near = SpectatorFrame.RestStep(BodyId.Moon, new DVec3(3_737_400, 0, 0), m0, m1);
        Assert.Equal(0, near.Length, 9);
        // anchored on the Earth, 10,000 km over it: none either (the Moon's sphere is far)
        Assert.Equal(0, SpectatorFrame.RestStep(BodyId.Earth, new DVec3(16_371_000, 0, 0), m0, m1).Length, 9);
        // anchored on the Earth 5 million km out: the Sun's frame, the Earth moving 30 km/s past it
        var far = SpectatorFrame.RestStep(BodyId.Earth, new DVec3(5e9, 0, 0), m0, m1);
        var sun = Frames.Offset(BodyId.Earth, BodyId.Sun, m1) - Frames.Offset(BodyId.Earth, BodyId.Sun, m0);
        Assert.Equal(sun.Length, far.Length, 3);
        Assert.InRange(far.Length, 28_000, 31_000);
    }

    [Fact]
    public void Between_the_earth_and_the_moon_the_rest_frame_passes_from_one_to_the_other_without_a_step()
    {
        var m0 = SkyClock.Now;
        var m1 = m0 with { Utc = m0.Utc.AddSeconds(1) };
        // along the line from the Moon's centre outward, away from the Earth, anchored on the Earth
        var moon = Frames.Offset(BodyId.Earth, BodyId.Moon, m1);
        var outward = moon / moon.Length;
        var moonMoves = Frames.Offset(BodyId.Earth, BodyId.Moon, m1) - Frames.Offset(BodyId.Earth, BodyId.Moon, m0);
        double soi = CelestialBody.Moon.SphereOfInfluence;
        double lastShare = 1;
        for (double d = 0.3 * soi; d < 1.8 * soi; d += 0.01 * soi)
        {
            var step = SpectatorFrame.RestStep(BodyId.Earth, moon + outward * d, m0, m1);
            // the Earth's frame moves 0, the Moon's moonMoves: the step is a share of the Moon's
            double share = step.Dot(moonMoves) / moonMoves.Dot(moonMoves);
            Assert.InRange(share, -1e-9, 1 + 1e-9);
            Assert.True(share <= lastShare + 1e-9);
            Assert.True(lastShare - share < 0.03);
            lastShare = share;
        }
        Assert.Equal(0, lastShare, 6);
    }

    [Fact]
    public void Unspin_is_the_earths_turn_about_its_pole_backward()
    {
        var m0 = SkyClock.Now;
        var m1 = m0 with { EarthAngle = m0.EarthAngle + 0.001 };
        var (axis, angle) = SpectatorFrame.Unspin(BodyId.Earth, m0, m1);
        // about the pole (the body-fixed Z), by the turn, west: a point fixed against the stars moves west in the ECEF
        Assert.InRange(Math.Abs(axis.Z), 0.9999, 1.0);
        Assert.Equal(0.001, Math.Abs(angle), 6);
        var p = new DVec3(6_378_137, 0, 0);
        var q = SpectatorFrame.Turn(p, axis, angle);
        Assert.True(q.Y < 0);
    }
}
