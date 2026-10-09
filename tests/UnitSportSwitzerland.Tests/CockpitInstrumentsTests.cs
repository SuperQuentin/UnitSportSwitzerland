using UnitSport.Avatar;
using Xunit;
using static UnitSport.Avatar.CockpitInstruments;

namespace UnitSportSwitzerland.Tests;

/// <summary>The cockpit's instrument arithmetic (#421, src/Avatar/CockpitInstruments.cs, linked in).</summary>
public class CockpitInstrumentsTests
{
    private const float Deg = System.MathF.PI / 180f;

    [Fact]
    public void Indicated_speed_is_true_speed_at_sea_level_and_less_up_high()
    {
        Assert.Equal(100f, Ias(100f, 0f), 2);
        // FL350: sigma ~0.31, IAS ~0.56 of TAS
        Assert.InRange(Ias(230f, 10668f) / 230f, 0.53f, 0.59f);
    }

    [Fact]
    public void Tape_marks_cover_the_window_and_sit_at_their_offsets()
    {
        var (first, count) = TapeMarks(143f, 42f, 10);
        Assert.Equal(100, first);
        Assert.Equal(190, first + (count - 1) * 10);
        Assert.Equal(-43f * 2f, TapeOffset(100f, 143f, 2f), 3);
        Assert.Equal(0f, TapeOffset(143f, 143f, 2f), 3);
    }

    [Fact]
    public void Steam_needles_read_their_scales()
    {
        Assert.Equal(0f, AsiAngle(30f), 4);
        Assert.Equal(330f * Deg, AsiAngle(400f), 3);
        Assert.Equal(165f * Deg, AsiAngle(220f), 3);
        var (h, t) = AltimeterAngles(2500f);
        Assert.Equal(System.MathF.PI, h, 3);
        Assert.Equal(0.25f * System.MathF.PI * 2f * 1f, t, 3);
        Assert.Equal(-90f * Deg, VsiAngle(0f), 4);
        Assert.Equal(-30f * Deg, VsiAngle(1000f), 3);
        Assert.Equal(-150f * Deg, VsiAngle(-1000f), 3);
        Assert.Equal(VsiAngle(6000f), VsiAngle(9000f), 4);
        Assert.Equal(-135f * Deg, DialAngle(0f, 100f), 4);
        Assert.Equal(135f * Deg, DialAngle(100f, 100f), 4);
        Assert.Equal(359, Wrap360(-1f));
        Assert.Equal(0, Wrap360(360f));
    }

    [Fact]
    public void Warnings_follow_their_rules()
    {
        // on the ground, cold: nothing
        Assert.Equal(CockpitWarning.None, Warnings(false, false, false, 0f, 0f, 0f, 1f, 0.45f, true, 0f, false, 0f));
        // the parking brake with the levers forward
        Assert.Equal(CockpitWarning.ParkBrake, Warnings(false, false, false, 0f, 0f, 0f, 1f, 0.45f, true, 0.8f, true, 0.8f));
        // an approach with the gear up, sinking
        var w = Warnings(false, false, true, 150f, 140f, -700f, 0f, 0.3f, false, 0.4f, true, 0.5f);
        Assert.Equal(CockpitWarning.Gear, w);
        Assert.True(MasterWarning(w));
        Assert.False(MasterCaution(w));
        // a stall on the ground is no stall; an engine out and low fuel in the air are cautions
        Assert.Equal(CockpitWarning.None, Warnings(true, false, false, 0f, 50f, 0f, 1f, 0.5f, false, 0f, true, 0.3f));
        w = Warnings(false, false, true, 2000f, 250f, 0f, 0f, 0.05f, false, 0.7f, true, 0f);
        Assert.Equal(CockpitWarning.FuelLow | CockpitWarning.EngineOut, w);
        Assert.True(MasterCaution(w));
        Assert.False(MasterWarning(w));
    }

    [Fact]
    public void Thrust_levers_sit_in_their_detents()
    {
        Assert.Equal("IDLE", LeverDetent(0f, false));
        Assert.Equal("CL", LeverDetent(0.8f, false));
        Assert.Equal("FLX", LeverDetent(0.9f, false));
        Assert.Equal("TOGA", LeverDetent(1f, false));
        Assert.Equal("REV", LeverDetent(0f, true));
        Assert.True(LeverAngle(0f, true) < LeverAngle(0f, false));
        Assert.True(LeverAngle(1f, false) > LeverAngle(0.5f, false));
        Assert.Equal(0f, FuelFlow(0f, 0.22f, 4000f));
        Assert.Equal(4000f, FuelFlow(1f, 0.22f, 4000f), 1);
        Assert.InRange(Egt(0.22f), 400f, 450f);
    }
}
