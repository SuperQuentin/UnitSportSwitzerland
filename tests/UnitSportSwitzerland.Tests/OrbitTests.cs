using UnitSport.Core;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The US Standard Atmosphere 1976 (#1430 H5, src/Core/Atmosphere.cs).</summary>
public class AtmosphereTests
{
    /// <summary>The standard's own table: geometric km, K, Pa, kg/m³.</summary>
    [Theory]
    [InlineData(0, 288.15, 101_325, 1.2250)]
    [InlineData(5, 255.68, 54_048, 0.73643)]
    [InlineData(11, 216.77, 22_700, 0.36480)]
    [InlineData(20, 216.65, 5_529.3, 0.088910)]
    [InlineData(32, 228.49, 889.06, 0.013555)]
    [InlineData(40, 250.35, 287.14, 0.0039957)]
    [InlineData(50, 270.65, 79.779, 0.0010269)]
    [InlineData(60, 247.02, 21.958, 0.00030968)]
    [InlineData(70, 219.58, 5.2209, 0.000082829)]
    [InlineData(80, 198.64, 1.0524, 0.000018458)]
    public void Matches_the_1976_table_to_the_edge_of_the_layers(double km, double t, double p, double rho)
    {
        double z = km * 1000;
        Assert.Equal(t, Atmosphere.Temperature(z), 0.1);
        Assert.Equal(1, Atmosphere.Pressure(z) / p, 3);
        Assert.Equal(1, Atmosphere.Density(z) / rho, 3);
    }

    [Fact]
    public void Thins_out_into_space_and_stops_at_150_km()
    {
        Assert.Equal(1, Atmosphere.Density(86_000) / 6.958e-6, 2);
        Assert.Equal(1, Atmosphere.Density(100_000) / 5.604e-7, 6);
        Assert.Equal(1, Atmosphere.Density(150_000) / 2.076e-9, 6);
        Assert.Equal(0, Atmosphere.Density(150_001));
        Assert.Equal(0, Atmosphere.Pressure(400_000));
        // the layered part meets the table at 86 km
        Assert.Equal(1, Atmosphere.Density(85_999) / Atmosphere.Density(86_000), 2);
        // never thicker higher up
        double last = double.MaxValue;
        for (double z = 0; z <= 160_000; z += 250)
        {
            double rho = Atmosphere.Density(z);
            Assert.True(rho <= last, $"thicker at {z} m");
            last = rho;
        }
    }

    [Fact]
    public void The_troposphere_is_the_formula_the_airliners_flew_on()
    {
        // AirlinerFlight.Density's ISA formula, the same air below 11 km but for geopotential against
        // geometric altitude (0.24 % at 11 km)
        for (double z = 0; z <= 11_000; z += 500)
            Assert.InRange(Atmosphere.Density(z) / (1.225 * Math.Pow(1 - 2.25577e-5 * z, 4.2559)), 0.9999, 1.0025);
    }

    [Fact]
    public void Airliners_and_their_instruments_fly_the_old_air_below_11_km_and_the_standard_above()
    {
        for (float z = -500; z <= 11_000; z += 250)
        {
            // bit for bit what they flew on before H5
            Assert.Equal(1.225f * Godot.Mathf.Pow(Godot.Mathf.Max(1f - 2.25577e-5f * z, 0.05f), 4.2559f), UnitSport.Player.AirlinerFlight.Density(z));
            Assert.Equal(MathF.Pow(Math.Clamp(1f - 2.25577e-5f * z, 0.2f, 1.2f), 4.2559f), UnitSport.Avatar.CockpitInstruments.Sigma(z));
        }
        // the old formula was 60x too thin at 40 km and stuck at 3.6e-6 above 42 km
        Assert.Equal(1, UnitSport.Player.AirlinerFlight.Density(40_000) / 3.9957e-3, 3);
        Assert.Equal(1, UnitSport.Player.AirlinerFlight.Density(100_000) / 5.604e-7, 4);
        Assert.Equal(0f, UnitSport.Player.AirlinerFlight.Density(200_000));
        Assert.Equal(1, UnitSport.Avatar.CockpitInstruments.Sigma(20_000) / (0.08891 / 1.225), 3);
    }
}

/// <summary>The orbit integrator (#1430 H5, src/Core/Orbit.cs): the phase's acceptance.</summary>
public class OrbitTests
{
    private static readonly CelestialBody Earth = CelestialBody.Earth;

    /// <summary>A 400 km circular orbit, inclined like the ISS's, stepped at the physics rate for one period.</summary>
    private static (OrbitState Start, OrbitState End, double Period) OnePeriod(double dt, int laps = 1)
    {
        double r = Earth.Shape.A + 400_000;
        double inc = 51.6 * Math.PI / 180;
        var start = Orbit.Circular(Earth, new DVec3(r, 0, 0), new DVec3(0, Math.Cos(inc), Math.Sin(inc)));
        double period = Orbit.Period(Earth, r) * laps;
        var s = start;
        int steps = (int)Math.Floor(period / dt);
        for (int i = 0; i < steps; i++) s = Orbit.Step(Earth, s, DVec3.Zero, dt);
        s = Orbit.Step(Earth, s, DVec3.Zero, period - steps * dt);
        return (start, s, period);
    }

    [Fact]
    public void A_400_km_circular_orbit_closes_after_one_period()
    {
        var (start, end, period) = OnePeriod(1.0 / 60);
        Assert.Equal(5_554, period, 0);
        Assert.Equal(7_669, start.V.Length, 0);
        Assert.True((end.R - start.R).Length < 10, $"{(end.R - start.R).Length:F3} m from where it started");
        Assert.True((end.V - start.V).Length < 0.01, $"{(end.V - start.V).Length:F5} m/s off");
    }

    [Fact]
    public void Energy_drifts_less_than_a_millionth_per_orbit()
    {
        var (start, end, _) = OnePeriod(1.0 / 60);
        double drift = Math.Abs((end.Energy(Earth) - start.Energy(Earth)) / start.Energy(Earth));
        Assert.True(drift < 1e-6, $"energy drift {drift:E2}");
    }

    [Fact]
    public void Holds_at_the_fastest_time_scale_over_ten_orbits()
    {
        // SimClock.MaxScale at 60 Hz: 8/60 s a step
        var (start, end, _) = OnePeriod(SimClock.MaxScale / 60, laps: 10);
        double drift = Math.Abs((end.Energy(Earth) - start.Energy(Earth)) / start.Energy(Earth));
        Assert.True(drift < 1e-6, $"energy drift {drift:E2}");
        Assert.True((end.R - start.R).Length < 10, $"{(end.R - start.R).Length:F3} m from where it started");
    }

    [Fact]
    public void An_elliptic_orbit_keeps_its_apsides()
    {
        double r = Earth.Shape.A + 300_000;
        var s = new OrbitState(new DVec3(r, 0, 0), new DVec3(0, 8_000, 0));
        var (peri0, apo0, a) = Orbit.Apsides(Earth, s);
        Assert.Equal(r, peri0, 3);
        Assert.True(apo0 > r + 500_000);
        double period = Orbit.Period(Earth, a);
        for (double t = 0; t < period; t += 1.0 / 60) s = Orbit.Step(Earth, s, DVec3.Zero, 1.0 / 60);
        var (peri1, apo1, _) = Orbit.Apsides(Earth, s);
        Assert.Equal(peri0, peri1, 0);
        Assert.Equal(apo0, apo1, 0);
    }

    [Fact]
    public void Thrust_along_the_way_raises_the_orbit()
    {
        double r = Earth.Shape.A + 400_000;
        var s = Orbit.Circular(Earth, new DVec3(r, 0, 0), new DVec3(0, 1, 0));
        for (int i = 0; i < 600; i++) s = Orbit.Step(Earth, s, s.V.Normalized() * 1.0, 1.0 / 60);   // 10 s at 1 m/s²
        var (peri, apo, _) = Orbit.Apsides(Earth, s);
        Assert.True(apo > r + 30_000, $"apoapsis {(apo - Earth.Shape.A) / 1000:F1} km");
        Assert.True(peri > r - 100, $"periapsis {(peri - Earth.Shape.A) / 1000:F1} km");
    }

    [Fact]
    public void Earth_fixed_and_inertial_round_trip_and_the_ground_turns_with_the_earth()
    {
        // Bern on the ground, standing still
        var bern = UnitSport.Terrain.Format.SwissGeodesy.ToEcef(2_600_000, 1_200_000, 550);
        foreach (double t in new[] { 0.0, 1234.5, 86_164.1 / 4, 1e6 })
        {
            double angle = Orbit.RotationAngle(Earth, t);
            var s = Orbit.FromEcef(Earth, bern, DVec3.Zero, angle);
            // a still point on the ground moves at ω r cos(lat) in space: 318 m/s at Bern
            Assert.Equal(318, s.V.Length, 0);
            var (r, v) = Orbit.ToEcef(Earth, s, angle);
            Assert.True((r - bern).Length < 1e-6);
            Assert.True(v.Length < 1e-9);
        }
        // a quarter of a sidereal day turns ECEF by 90° under ECI
        var x = Orbit.ToEcef(new DVec3(1, 0, 0), Orbit.RotationAngle(Earth, 86_164.0905 / 4));
        Assert.Equal(0, x.X, 6);
        Assert.Equal(-1, x.Y, 6);
    }

    [Fact]
    public void A_circular_orbit_flies_where_it_is_aimed_on_the_horizontal()
    {
        var r = new DVec3(0, 0, Earth.Shape.B + 400_000);   // over the pole, aimed up and along X
        var s = Orbit.Circular(Earth, r, new DVec3(1, 0, 5));
        Assert.Equal(0, s.V.Dot(r), 6);
        Assert.True(s.V.X > 0);
        // straight up: east, whichever way that is
        var e = Orbit.Circular(Earth, new DVec3(Earth.Shape.A + 400_000, 0, 0), new DVec3(1, 0, 0));
        Assert.True(e.V.Y > 7_000);
    }
}
