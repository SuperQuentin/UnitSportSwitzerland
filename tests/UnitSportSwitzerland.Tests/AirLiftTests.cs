using Godot;
using UnitSport.Terrain.Format;
using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The air's lift (#1225, src/World/AirLift.cs) and the sun it reads (src/World/SunPath.cs).</summary>
public class AirLiftTests
{
    /// <summary>A terrain given as a function, a fixed hour and day.</summary>
    private sealed class FakeTerrain(Func<double, double, double> height, double hour, long day = 3,
        CoverClass cover = CoverClass.Rock) : IAirLiftTerrain
    {
        public double Hour = hour;
        public long Day = day;
        public bool TryHeight(double e, double n, out float h) { h = (float)height(e, n); return true; }
        public bool TryCover(double e, double n, out CoverClass c) { c = cover; return true; }
        public double HourAt(double serverNow) => Hour;
        public long DayAt(double serverNow) => Day;
    }

    private const double E0 = 2_600_000, N0 = 1_200_000;

    /// <summary>A 35° face: rising north (it looks south) or rising south (it looks north).</summary>
    private static Func<double, double, double> Slope(bool facingSouth) =>
        (_, n) => 1000 + (facingSouth ? 1 : -1) * (n - N0) * Math.Tan(35 * Math.PI / 180);

    /// <summary>The summed strength of the thermals round the point over ten minutes.</summary>
    private static float StrengthOverTime(IAirLiftTerrain t)
    {
        float sum = 0f;
        for (double now = 0; now < 600; now += 20)
            foreach (var th in AirLift.ThermalsNear(E0, N0, 1500, now, t)) sum += th.Strength;
        return sum;
    }

    [Fact]
    public void The_same_inputs_give_the_same_air()
    {
        var a = new FakeTerrain(Slope(true), 13);
        var b = new FakeTerrain(Slope(true), 13);
        for (double now = 0; now < 900; now += 37)
            Assert.Equal(AirLift.At(E0 + 123, N0 - 77, 1200, now, a), AirLift.At(E0 + 123, N0 - 77, 1200, now, b));
    }

    [Fact]
    public void Night_has_no_thermal()
    {
        var t = new FakeTerrain(Slope(true), 22);
        Assert.Equal(0f, StrengthOverTime(t));
        Assert.Empty(AirLift.ThermalsNear(E0, N0, 1500, 100, t));
    }

    [Fact]
    public void A_south_face_at_noon_beats_a_north_face()
    {
        float south = StrengthOverTime(new FakeTerrain(Slope(true), 12));
        float north = StrengthOverTime(new FakeTerrain(Slope(false), 12));
        Assert.True(south > 0f);
        Assert.True(south > north * 1.3f, $"south {south}, north {north}");
    }

    [Fact]
    public void Ice_gives_no_thermal()
    {
        var t = new FakeTerrain(Slope(true), 12, cover: CoverClass.Glacier);
        Assert.Equal(0f, StrengthOverTime(t));
    }

    [Fact]
    public void A_thermal_breathes()
    {
        // dead for part of its cycle, full for part of it, never more than 1
        float min = 1f, max = 0f;
        for (double now = 0; now < 480; now += 2)
        {
            float l = AirLift.Life(480f, 0.3f, now);
            min = Math.Min(min, l);
            max = Math.Max(max, l);
            Assert.InRange(l, 0f, 1f);
        }
        Assert.Equal(0f, min);
        Assert.Equal(1f, max);
    }

    [Fact]
    public void The_core_lifts_the_ring_sinks_and_the_top_ends_it()
    {
        Assert.True(AirLift.Profile(0.8f, 70f, 1000f, 0f, 200f) > 3f);
        Assert.True(AirLift.Profile(0.8f, 70f, 1000f, 105f, 200f) < 0f);
        Assert.Equal(0f, AirLift.Profile(0.8f, 70f, 1000f, 0f, 1001f));
        Assert.True(AirLift.Profile(0.8f, 70f, 1000f, 0f, 950f) < AirLift.Profile(0.8f, 70f, 1000f, 0f, 500f));
    }

    private static long DayFrom(float bearing)
    {
        for (long d = 0; d < 1000; d++)
            if (AirLift.WindOf(d).FromBearingDeg == bearing) return d;
        throw new InvalidOperationException("no such day");
    }

    [Fact]
    public void The_windward_face_lifts_and_the_lee_sinks()
    {
        // a ridge rising east at 30°: its west face meets a west wind
        Func<double, double, double> ridge = (e, _) => 1000 + Math.Max(0, (e - E0)) * Math.Tan(30 * Math.PI / 180);
        var westDay = new FakeTerrain(ridge, 22, DayFrom(270f));
        var biseDay = new FakeTerrain(ridge, 22, DayFrom(45f));
        double e = E0 + 100;
        float ground = (float)ridge(e, N0);
        Assert.True(AirLift.At(e, N0, ground + 20, 0, westDay).Y > 0.5f);
        Assert.True(AirLift.At(e, N0, ground + 20, 0, biseDay).Y < 0f);
        // and fades out 200 m up
        Assert.Equal(0f, AirLift.At(e, N0, ground + 250, 0, westDay).Y);
    }

    [Fact]
    public void Winds_of_the_days_follow_the_shares()
    {
        int west = 0, bise = 0, foehn = 0;
        for (long d = 0; d < 4000; d++)
        {
            var w = AirLift.WindOf(d);
            Assert.InRange(w.Speed, 3f, 8f);
            if (w.FromBearingDeg is 270f or 225f) west++;
            else if (w.FromBearingDeg == 45f) bise++;
            else if (w.FromBearingDeg == 180f) foehn++;
        }
        Assert.InRange(west / 4000f, 0.55f, 0.65f);
        Assert.InRange(bise / 4000f, 0.2f, 0.3f);
        Assert.InRange(foehn / 4000f, 0.1f, 0.2f);
    }

    [Fact]
    public void A_burning_campfire_lifts_and_a_burnt_out_one_does_not()
    {
        const double e = 2_700_000, n = 1_100_000;
        const long key = 987_654_321;
        AirLift.Volumes.Set(key, e, n, 500, 4f, 25f, 6f);
        Assert.Equal(6f, AirLift.Volumes.LiftAt(e, n, 505, 0), 3);
        Assert.Equal(0f, AirLift.Volumes.LiftAt(e + 10, n, 505, 0));   // past 1.5 radius
        Assert.Equal(0f, AirLift.Volumes.LiftAt(e, n, 530, 0));        // over its top
        Assert.True(AirLift.Volumes.Remove(key));
        Assert.Equal(0f, AirLift.Volumes.LiftAt(e, n, 505, 0));

        // a bottled gust runs out on the server's clock
        long gust = AirLift.Volumes.Add(e, n, 500, 5f, 10, serverNow: 100);
        Assert.True(AirLift.Volumes.LiftAt(e, n, 510, 105) > 0f);
        Assert.Equal(0f, AirLift.Volumes.LiftAt(e, n, 510, 111));
        AirLift.Volumes.Prune(111);
        Assert.False(AirLift.Volumes.Contains(gust));
    }

    [Fact]
    public void SunPath_gives_the_old_DayNight_numbers_exactly()
    {
        for (float h = 0; h < 24; h += 0.25f)
        {
            // the formula DayNight had before #1225, copied
            float rise = 6f, set = 18f, noon = 62f, dayLength = set - rise;
            float phase = h >= rise && h <= set ? (h - rise) / dayLength : 1f + Mathf.PosMod(h - set, 24f) / (24f - dayLength);
            float az = Mathf.DegToRad(90f + phase * 180f);
            float elevation = noon * Mathf.Sin(phase * Mathf.Pi);
            float el = Mathf.DegToRad(elevation);
            var old = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el));

            var sun = SunPath.Direction(h, out float deg);
            Assert.Equal(old, sun);
            Assert.Equal(elevation, deg);
        }
        // due south and 62° up at noon, below the horizon at midnight
        var noonSun = SunPath.Direction(12f, out float noonDeg);
        Assert.Equal(62f, noonDeg, 3);
        Assert.True(noonSun.Z > 0f);   // +Z is south
        SunPath.Direction(0f, out float midnight);
        Assert.True(midnight < 0f);
    }
}
