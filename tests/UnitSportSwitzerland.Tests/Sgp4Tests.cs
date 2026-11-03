using System;
using System.Globalization;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>SGP4 (#1444, src/Core/Sgp4.cs) against Vallado's verification run (tcppver.out, WGS-72).</summary>
public class Sgp4Tests
{
    // Vanguard 1, the first case of SGP4-VER.TLE: near-Earth, eccentric (0.186), a 134-minute period
    private static readonly Tle Vanguard = Tle.Parse("VANGUARD 1",
        "1 00005U 58002B   00179.78495062  .00000023  00000-0  28098-4 0  4753",
        "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667")!;

    [Fact]
    public void Parses_the_elements()
    {
        Assert.Equal(5, Vanguard.Number);
        Assert.Equal(0.1859667, Vanguard.Eccentricity, 1e-9);
        Assert.Equal(0.28098e-4, Vanguard.BStar, 1e-12);
        Assert.Equal(10.82419157, Vanguard.RevsPerDay, 1e-8);
        Assert.Equal(new DateTime(2000, 6, 27, 18, 50, 19, DateTimeKind.Utc), Vanguard.Epoch, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0, 7022.46529266, -1400.08296755, 0.03995155)]
    [InlineData(360, -7154.03120202, -3783.17682504, -3536.19412294)]
    public void Matches_Vallados_verification_run(double minutes, double x, double y, double z)
    {
        var p = new Sgp4(Vanguard).Position(minutes)!.Value;
        Assert.Equal(x, p.X, 0.001);
        Assert.Equal(y, p.Y, 0.001);
        Assert.Equal(z, p.Z, 0.001);
    }

    // the ISS as CelesTrak published it on 2026-11-02 (epoch 18:10 UTC)
    private static readonly Tle Iss = Tle.Parse("ISS (ZARYA)",
        "1 25544U 98067A   26306.75682168  .00005437  00000+0  10620-3 0  9990",
        "2 25544  51.6331 334.3796 0007124 340.7366  19.3352 15.49154261593232")!;

    /// <summary>
    /// The ISS from Bern against JPL Horizons (-125544, its own trajectory, airless azimuth and
    /// elevation, range): TLE, SGP4, TEME to Earth-fixed, topocentric, 15 to 22 hours after the epoch.
    /// </summary>
    [Theory]
    [InlineData("2026-11-03 09:36:00", 63.650131077, -60.424445510, 0.00007734974617)]
    [InlineData("2026-11-03 10:36:02", 204.482789132, -39.365161532, 0.00005820185337)]
    [InlineData("2026-11-03 11:36:05", 344.770141721, -37.428079697, 0.00005638259475)]
    [InlineData("2026-11-03 12:36:08", 121.170214861, -46.025784567, 0.00006503973408)]
    [InlineData("2026-11-03 13:36:11", 259.937224931, -49.554903566, 0.00006849582621)]
    [InlineData("2026-11-03 14:36:14", 34.561105231, -33.981585549, 0.00005251091778)]
    [InlineData("2026-11-03 15:36:17", 176.435305457, -41.422480787, 0.00006030663710)]
    [InlineData("2026-11-03 16:36:20", 323.150495015, -61.675468946, 0.00007830638611)]
    public void The_ISS_matches_Horizons_from_Bern(string utc, double az, double el, double au)
    {
        var at = DateTime.SpecifyKind(DateTime.ParseExact(utc, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        // Horizons printed milliseconds this test's strings leave out
        at = at.AddSeconds(utc.EndsWith(":02") ? 0.88 : utc.EndsWith(":05") ? 0.76 : utc.EndsWith(":08") ? 0.64 : utc.EndsWith(":11") ? 0.52 : utc.EndsWith(":14") ? 0.4 : utc.EndsWith(":17") ? 0.28 : utc.EndsWith(":20") ? 0.16 : 0);
        var bern = new Geodetic(46.9511, 7.4386, 550);
        var sat = new Sgp4(Iss).Ecef(at)!.Value;
        var h = Ephemeris.Local(sat - Ellipsoid.Wgs84.ToEcef(bern), bern);
        const double deg = Math.PI / 180;
        double dot = h.East * Math.Sin(az * deg) * Math.Cos(el * deg) + h.North * Math.Cos(az * deg) * Math.Cos(el * deg) + h.Up * Math.Sin(el * deg);
        double off = Math.Acos(Math.Min(1, dot)) / deg;
        Assert.True(off < 0.15, $"{utc}: {off:F3} deg from Horizons");
        Assert.True(Math.Abs(h.Distance - au * Ephemeris.AuMetres) < 15_000, $"{utc}: {h.Distance / 1000:F1} km, Horizons {au * Ephemeris.AuMetres / 1000:F1}");
    }
}
