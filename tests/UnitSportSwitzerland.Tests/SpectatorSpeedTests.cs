using System;
using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The spectator camera's speed rules (#1796, src/Player/SpectatorSpeed.cs).</summary>
public class SpectatorSpeedTests
{
    [Theory]
    [InlineData(-400, 3000)]       // under the sea level: the ground's cap
    [InlineData(1500, 3000)]       // over Geneva: the old cap
    [InlineData(6000, 3000)]
    [InlineData(20_000, 10_000)]
    [InlineData(60_000, 30_000)]   // into Space
    [InlineData(200_000, 100_000)]
    [InlineData(400_000, 100_000)] // an orbit's height: the top
    [InlineData(50_000_000, 100_000)]
    public void TheCapGrowsWithTheAltitude(double altitude, float cap) =>
        Assert.Equal(cap, SpectatorSpeed.AltitudeCap(altitude), 1);

    [Fact]
    public void TheWheelReachesTheTopAndTheFloor()
    {
        float speed = 150f;
        int notches = 0;
        while (speed < SpectatorSpeed.MaxSpeed && notches < 100) { speed = SpectatorSpeed.Faster(speed); notches++; }
        Assert.Equal(SpectatorSpeed.MaxSpeed, speed);
        Assert.InRange(notches, 20, 40);
        Assert.Equal(SpectatorSpeed.MaxSpeed, SpectatorSpeed.Faster(speed));

        while (speed > SpectatorSpeed.MinSpeed && notches < 200) { speed = SpectatorSpeed.Slower(speed, SpectatorSpeed.MaxSpeed); notches++; }
        Assert.Equal(SpectatorSpeed.MinSpeed, speed);
    }

    [Theory]
    [InlineData(1500, 3000)]                 // on the ground as ever
    [InlineData(400_000, 200_000)]           // an orbit: no 100 km/s top between bodies
    [InlineData(190_000_000, 95_000_000)]    // half way to the Moon
    [InlineData(1e12, SpectatorSpeed.LightSpeed)]
    public void BetweenBodiesTheCapIsHalfTheNearestSurfaceUpToLight(double distance, float cap) =>
        Assert.Equal(cap, SpectatorSpeed.SurfaceCap(distance), 0);

    [Fact]
    public void BetweenBodiesTheWheelGoesAsFarAsTheCap()
    {
        float cap = SpectatorSpeed.SurfaceCap(190_000_000);
        float speed = SpectatorSpeed.MaxSpeed;
        int notches = 0;
        while (speed < cap && notches < 100) { speed = SpectatorSpeed.Faster(speed, cap); notches++; }
        Assert.Equal(cap, speed);
        Assert.InRange(notches, 25, 35);
        // near the ground the top is the old one
        Assert.Equal(SpectatorSpeed.MaxSpeed, SpectatorSpeed.Faster(SpectatorSpeed.MaxSpeed, SpectatorSpeed.SurfaceCap(1500)));
    }

    [Fact]
    public void FromEarthOrbitToTheMoonInUnderAMinuteAtTheTop()
    {
        // the wheel at the cap all the way: out at half the distance a second, in again as the Moon's ground comes up
        double from = 400_000, to = 100_000, gap = 384_400_000 - 6_378_137 - 1_737_400, x = 0, t = 0;
        while (x < gap - from - to && t < 600)
        {
            double d = Math.Min(from + x, gap - x);
            x += SpectatorSpeed.SurfaceCap(d) * 0.1;
            t += 0.1;
        }
        Assert.InRange(t, 10, 60);
    }

    [Fact]
    public void ANotchDownPastTheCapIsFeltAtOnce()
    {
        // 100 km/s asked for near the ground flies at 3 km/s: a notch down slows from 3 km/s, not from 100
        float cap = SpectatorSpeed.AltitudeCap(1500);
        Assert.Equal(cap / SpectatorSpeed.Notch, SpectatorSpeed.Slower(SpectatorSpeed.MaxSpeed, cap), 1);
    }

    [Theory]
    [InlineData(2f, "2.0 m/s")]
    [InlineData(2.5f, "2.5 m/s")]
    [InlineData(95_000_000f, "95000 km/s")]
    [InlineData(299_792_458f, "1.00 c")]
    [InlineData(150f, "150 m/s")]
    [InlineData(999f, "999 m/s")]
    [InlineData(1000f, "1.0 km/s")]
    [InlineData(3200f, "3.2 km/s")]
    [InlineData(18_000f, "18.0 km/s")]
    [InlineData(100_000f, "100 km/s")]
    [InlineData(600_000f, "600 km/s")]
    public void SpeedsReadNaturally(float speed, string text) =>
        Assert.Equal(text, SpectatorSpeed.Format(speed));
}
