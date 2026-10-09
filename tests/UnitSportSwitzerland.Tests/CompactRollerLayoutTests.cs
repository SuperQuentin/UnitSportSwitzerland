using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The compact roller (#614): frame steering shared with the wheel loader (the loader's numbers
/// unchanged by the move), a tandem roller's turning circle, the bend kept in the parked flags,
/// and its drums' hum a clean loop.
/// </summary>
public class CompactRollerLayoutTests
{
    [Fact]
    public void The_loader_steers_exactly_as_before_the_kinematics_were_shared()
    {
        // #612's formula, written out
        static float Old(float v, float a) => v * Mathf.Sin(a) / (WheelLoaderLayout.FrontAxle + -WheelLoaderLayout.RearAxle * Mathf.Cos(a));
        foreach (float a in new[] { -0.7f, -0.3f, 0.1f, 0.45f, 0.7f })
            Assert.Equal(Old(5f, a), WheelLoaderLayout.YawRate(5f, a), 6);
        Assert.Equal(4.5f, WheelLoaderLayout.TurnRadius(WheelLoaderLayout.MaxArticulation), 1);
    }

    [Fact]
    public void A_tandem_roller_turns_on_a_small_circle()
    {
        // a 2.6 t tandem's outer kerb radius is about 4 m; its drums' centre line a little less
        Assert.InRange(CompactRollerLayout.TurnRadius(CompactRollerLayout.MaxArticulation), 2.8f, 4.0f);
        Assert.True(float.IsPositiveInfinity(CompactRollerLayout.TurnRadius(0f)));
        // left (+) turns left (+ yaw rate) going forward, and the other way reversing
        Assert.True(CompactRollerLayout.YawRate(2f, 0.4f) > 0f);
        Assert.True(CompactRollerLayout.YawRate(-2f, 0.4f) < 0f);
    }

    [Theory]
    [InlineData(-0.5f)]
    [InlineData(-0.12f)]
    [InlineData(0f)]
    [InlineData(0.37f)]
    [InlineData(0.5f)]
    public void Parked_flags_keep_the_bend(float articulation)
    {
        int flags = CompactRollerLayout.Pack(articulation);
        Assert.NotEqual(0, flags);
        Assert.True(MathF.Abs(CompactRollerLayout.Unpack(flags) - articulation) < 0.003f);
    }

    [Fact]
    public void A_fresh_roller_parks_straight() => Assert.Equal(0f, CompactRollerLayout.Unpack(0));

    [Fact]
    public void The_drums_hum_at_their_rate_without_clipping_or_silence()
    {
        const int n = SiteSfx.Rate * 2;
        var s = SiteSfx.RollerDrum(new Random(614), n);
        Assert.Equal(n, s.Length);
        Assert.All(s, v => Assert.False(float.IsNaN(v)));
        float peak = s.Max(MathF.Abs);
        Assert.InRange(peak, 0.5f, 6f);
        // the strongest of a few candidate tones is the drums' own 55 Hz
        float Power(float hz)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 2 * Math.PI * hz * i / SiteSfx.Rate;
                re += s[i] * Math.Cos(w);
                im += s[i] * Math.Sin(w);
            }
            return (float)(re * re + im * im);
        }
        float at55 = Power(55f);
        Assert.All(new[] { 30f, 80f, 150f, 300f }, hz => Assert.True(at55 > Power(hz), $"{hz} Hz is louder than 55 Hz"));
    }
}
