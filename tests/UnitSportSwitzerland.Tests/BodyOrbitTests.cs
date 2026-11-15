using System;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Orbits round any body and across spheres of influence (#1851, src/Player/SpaceOrbit.cs, docs/plans/planets.md
/// sections 2 and 7): the Moon's orbit closes, a craft keeps its true velocity across the boundary, never the sky's swing.
/// </summary>
public class BodyOrbitTests
{
    private static readonly CelestialBody Moon = CelestialBody.Moon, Earth = CelestialBody.Earth;
    private static readonly SkyMoment Sky = SkyMoment.At(SwissTime.ToUtc(new DateOnly(2027, 7, 15), 10));

    [Fact]
    public void A_100_km_lunar_orbit_closes_after_one_period()
    {
        double r = Moon.Shape.A + 100_000;
        var start = Orbit.Circular(Moon, new DVec3(r, 0, 0), new DVec3(0, 0.6, 0.8));
        double period = Orbit.Period(Moon, r);
        Assert.Equal(1_633.5, start.V.Length, 1);
        Assert.InRange(period / 60, 117, 119);
        const double dt = 1.0 / 60;
        var s = start;
        int steps = (int)Math.Floor(period / dt);
        for (int i = 0; i < steps; i++) s = Orbit.Step(Moon, s, DVec3.Zero, dt);
        s = Orbit.Step(Moon, s, DVec3.Zero, period - steps * dt);
        Assert.True((s.R - start.R).Length < 10, $"{(s.R - start.R).Length:F3} m from where it started");
        Assert.True(Math.Abs((s.Energy(Moon) - start.Energy(Moon)) / start.Energy(Moon)) < 1e-6);
    }

    [Fact]
    public void A_craft_moving_with_the_moon_crosses_into_its_sphere_at_rest()
    {
        // 50,000 km from the Moon's centre on the Earth's side, moving as the Moon truly moves about the Earth
        double simTime = 1234.5;
        var p = Frames.Convert(BodyId.Moon, new DVec3(-50_000_000, 0, 0), BodyId.Earth, Sky);
        var vIcrf = Frames.Velocity(BodyId.Earth, BodyId.Moon, Sky.Utc);
        var vFixed = Frames.Orientation(BodyId.Earth, Sky).ApplyInverse(vIcrf) - new DVec3(0, 0, Earth.Spin).Cross(p);
        var state = Orbit.FromEcef(Earth, p, vFixed, Orbit.RotationAngle(Earth, simTime));

        var crossed = SpaceOrbit.Cross(Earth, Moon, state, simTime, Sky);
        Assert.True(crossed.V.Length < 1e-3, $"{crossed.V.Length:E2} m/s against the Moon");
        Assert.Equal(50_000_000, crossed.R.Length, 0);
        // not the sky clock's swing: the Moon's apparent speed in the Earth's turning frame is far more than this
        Assert.InRange(vIcrf.Length, 960, 1090);
    }

    [Fact]
    public void An_orbit_carried_to_the_earths_sphere_and_back_is_itself()
    {
        double simTime = 600;
        double r = Moon.Shape.A + 100_000;
        var lunar = Orbit.Circular(Moon, new DVec3(0, r, 0), new DVec3(0, 0, 1));
        var there = SpaceOrbit.Cross(Moon, Earth, lunar, simTime, Sky);
        var back = SpaceOrbit.Cross(Earth, Moon, there, simTime, Sky);
        Assert.True((back.R - lunar.R).Length < 1e-3, $"{(back.R - lunar.R).Length:E2} m");
        Assert.True((back.V - lunar.V).Length < 1e-6, $"{(back.V - lunar.V).Length:E2} m/s");
        // round the Earth it moves at the Moon's speed plus its own, about a kilometre a second either way
        Assert.InRange(there.V.Length, 0, 3_000);
    }

    [Fact]
    public void Each_body_has_its_own_space_regime_and_air()
    {
        Assert.True(Moon.SpaceRegime(false, 10_500));
        Assert.False(Moon.SpaceRegime(false, 9_500));
        Assert.True(Moon.SpaceRegime(true, 8_500));
        Assert.False(Moon.SpaceRegime(true, 7_500));
        Assert.True(Earth.SpaceRegime(false, 60_500));
        Assert.False(Earth.SpaceRegime(true, 49_000));
        Assert.True(Earth.HasAir);
        Assert.False(Moon.HasAir);
    }
}
