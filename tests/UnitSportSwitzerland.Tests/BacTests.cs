using UnitSport.Adventure.Alcohol;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The blood alcohol model (#1271): grams, absorption, Widmark, elimination, thresholds, readings, modes.</summary>
public class BacTests
{
    private const double Beer = 330 * 0.05 * 0.789;
    private const double T0 = 1_000_000;

    private static List<Shot> Log(bool fed, params (double At, double Grams)[] drinks)
    {
        var log = new List<Shot>();
        foreach (var (at, g) in drinks) Bac.Add(log, T0 + at, g, fed);
        return log;
    }

    [Theory]
    [InlineData(330, 0.05, 13.0)]
    [InlineData(100, 0.12, 9.5)]
    [InlineData(20, 0.40, 6.3)]
    [InlineData(200, 0.10, 15.8)]
    [InlineData(330, 0.005, 1.3)]
    public void GramsPerDrink(double ml, double abv, double grams) => Assert.Equal(grams, Bac.Grams(ml, abv), 1);

    [Fact]
    public void NothingDrunkIsSober() => Assert.Equal(0, Bac.Level(new List<Shot>(), T0 + 100));

    [Fact]
    public void AbsorptionHalvesEveryFifteenMinutesFastingAndThirtyFed()
    {
        var empty = new Shot { T = 0, G = 10, F = false };
        var fed = new Shot { T = 0, G = 10, F = true };
        Assert.Equal(5, Bac.Absorbed(empty, 15 * 60), 6);
        Assert.Equal(7.5, Bac.Absorbed(empty, 30 * 60), 6);
        Assert.Equal(5, Bac.Absorbed(fed, 30 * 60), 6);
        Assert.Equal(0, Bac.Absorbed(empty, -5));
    }

    private static (double Peak, double At) Peak(List<Shot> log, double kg = 75)
    {
        double best = 0, at = 0;
        for (double t = 0; t <= 6 * 3600; t += 60)
        {
            double l = Bac.Level(log, T0 + t, kg);
            if (l > best) { best = l; at = t; }
        }
        return (best, at);
    }

    [Fact]
    public void ThePeakComesThirtyToNinetyMinutesAfterTheDrinkAndLaterWhenFed()
    {
        var (_, fasting) = Peak(Log(false, (0, 3 * Beer)));
        var (_, fed) = Peak(Log(true, (0, 3 * Beer)));
        Assert.InRange(fasting, 30 * 60, 90 * 60);
        Assert.InRange(fed, 45 * 60, 150 * 60);
        Assert.True(fed > fasting);
    }

    [Fact]
    public void ThreeBeersAnHourAtSeventyFiveKiloPeakNearThreeQuartersOfAPerMille()
    {
        var log = Log(false, (0, Beer), (30 * 60, Beer), (60 * 60, Beer));
        var (peak, _) = Peak(log);
        Assert.InRange(peak, 0.55, 0.85);
    }

    [Fact]
    public void WidmarkScalesWithBodyMass()
    {
        var log = Log(false, (0, 2 * Beer));
        var (p60, _) = Peak(log, 60);
        var (p75, _) = Peak(log, 75);
        var (p90, _) = Peak(log, 90);
        Assert.True(p60 > p75 && p75 > p90);
        // grams / (kg x 0.6), less a little eliminated on the way
        Assert.InRange(p75, 2 * Beer / (75 * 0.6) - 0.2, 2 * Beer / (75 * 0.6));
    }

    [Fact]
    public void EliminationIsLinearAndStopsAtZero()
    {
        var log = Log(false, (0, 3 * Beer));
        double a = Bac.Level(log, T0 + 4 * 3600), b = Bac.Level(log, T0 + 5 * 3600);
        Assert.True(a > 0.1);
        Assert.Equal(0.15, a - b, 2);
        Assert.Equal(0, Bac.Level(log, T0 + 12 * 3600));
        Assert.All(new[] { 7, 9, 11, 20 }, h => Assert.True(Bac.Level(log, T0 + h * 3600) >= 0));
    }

    [Fact]
    public void ABeerIsGoneInAboutTwoHours()
    {
        double sober = Bac.SoberIn(Log(false, (0, Beer)), T0);
        Assert.InRange(sober, 1.0 * 3600, 3 * 3600);
    }

    [Fact]
    public void ALogOverADayOldIsForgotten()
    {
        var log = new List<Shot>();
        Bac.Add(log, T0, Beer, false);
        Bac.Add(log, T0 + 25 * 3600, Beer, false);
        Assert.Single(log);
    }

    [Fact]
    public void MouthAlcoholLastsFifteenMinutesFromTheLastDrink()
    {
        var log = Log(false, (0, Beer));
        Assert.Equal(12 * 60, Bac.MouthLeft(log, T0 + 3 * 60), 0);
        Assert.Equal(0, Bac.MouthLeft(log, T0 + 16 * 60));
        Assert.Equal(0, Bac.MouthLeft(new List<Shot>(), T0));
    }

    [Theory]
    [InlineData(0.2, 0f, 0f, false, false, false)]
    [InlineData(0.4, 0.3f, 0f, false, false, false)]
    [InlineData(0.6, 0.6f, 0.1f, false, false, false)]
    [InlineData(1.0, 0.6f, 0.2f, false, false, false)]
    [InlineData(1.6, 0.9f, 0.3f, true, true, false)]
    [InlineData(2.6, 0.9f, 0.3f, true, true, true)]
    public void TheThresholdTable(double level, float sway, float lag, bool stumbles, bool noClimb, bool passesOut)
    {
        var fx = AlcoholRules.Effects(level, AlcoholMode.Real);
        Assert.Equal(sway, fx.SwayDegrees);
        Assert.Equal(lag, fx.Lag);
        Assert.Equal(stumbles, fx.Stumbles);
        Assert.Equal(noClimb, fx.NoClimb);
        Assert.Equal(passesOut, fx.PassesOut);
    }

    [Fact]
    public void OffHasNoEffectsAndMildFeelsHalfAndNeverPassesOut()
    {
        Assert.False(AlcoholRules.Effects(3.0, AlcoholMode.Off).Any);
        var mild = AlcoholRules.Effects(3.0, AlcoholMode.Mild);
        Assert.False(mild.PassesOut);
        Assert.Equal(AlcoholRules.Effects(1.5, AlcoholMode.Real).Lag, mild.Lag);
        Assert.Equal(0f, AlcoholRules.Effects(0.8, AlcoholMode.Mild).Lag);
    }

    [Fact]
    public void ModesParse()
    {
        Assert.Equal(AlcoholMode.Off, AlcoholRules.ParseMode("off"));
        Assert.Equal(AlcoholMode.Mild, AlcoholRules.ParseMode("MILD"));
        Assert.Equal(AlcoholMode.Real, AlcoholRules.ParseMode(null));
        Assert.Equal(AlcoholMode.Real, AlcoholRules.ParseMode("nonsense"));
    }

    [Fact]
    public void TheElectronicReadingShowsBothUnitsAndTheLimit()
    {
        var r = AlcoholRules.Electronic(0.62, 0);
        Assert.False(r.Waiting);
        Assert.Equal("0.62 ‰ · 0.31 mg/L", r.Main);
        Assert.Equal("> 0.5 ‰: do not drive", r.Limit);
        Assert.Equal("under the 0.5 ‰ limit", AlcoholRules.Electronic(0.3, 0).Limit);
    }

    [Fact]
    public void TheElectronicReadingWaitsOutMouthAlcohol()
    {
        var r = AlcoholRules.Electronic(0.62, 11.2 * 60);
        Assert.True(r.Waiting);
        Assert.Equal(12, r.WaitMinutes);
        Assert.Equal("WAIT 12 min", r.Main);
    }

    [Fact]
    public void TheReadingIsInvariantCulture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            Assert.Equal("0.62 ‰ · 0.31 mg/L", AlcoholRules.Electronic(0.62, 0).Main);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void TheSingleUseTubeIsGreenAt049AndAmberAt051()
    {
        Assert.Equal(TubeColour.Green, AlcoholRules.Tube(0.49));
        Assert.Equal(TubeColour.Amber, AlcoholRules.Tube(0.51));
        Assert.Contains("unreliable", AlcoholRules.TubeText(TubeColour.Green, true));
        Assert.DoesNotContain("unreliable", AlcoholRules.TubeText(TubeColour.Amber, false));
    }

    [Fact]
    public void TheDelayLineReturnsTheInputFromLagSecondsAgo()
    {
        var d = new DelayLine<float>(32);
        Assert.Equal(-1f, d.At(0, 0.2, -1f));
        for (int i = 0; i <= 30; i++) d.Push(i * 0.02, i);
        // now = 0.61 s, lag 0.2 s: the push at 0.4 s (i = 20)
        Assert.Equal(20f, d.At(0.61, 0.2, -1f));
        Assert.Equal(30f, d.At(0.61, 0, -1f));
        // older than the memory: the oldest kept
        Assert.Equal(0f, d.At(0.61, 5, -1f));
    }

    [Fact]
    public void StumblesComeEveryTwentyToFortySecondsAndPassingOutLastsAMinute()
    {
        var st = new DrunkState();
        var drunk = AlcoholRules.Effects(1.8, AlcoholMode.Real);
        var low = AlcoholRules.Effects(1.0, AlcoholMode.Real);
        Assert.Equal(DrunkEvent.None, st.Step(low, 1f, () => 0f));
        int stumbles = 0;
        double first = -1;
        for (double t = 0; t < 100; t += 0.5)
            if (st.Step(drunk, 0.5f, () => 0f) == DrunkEvent.Stumble) { stumbles++; if (first < 0) first = t; }
        Assert.InRange(first, 19, 21);
        Assert.InRange(stumbles, 3, 5);

        var out27 = AlcoholRules.Effects(2.7, AlcoholMode.Real);
        Assert.Equal(DrunkEvent.PassOut, st.Step(out27, 0.1f, () => 0f));
        Assert.True(st.Out);
        // still over 2.4 after the minute: stays out; under it: wakes
        for (int i = 0; i < 700; i++) Assert.Equal(DrunkEvent.None, st.Step(out27, 0.1f, () => 0f));
        Assert.Equal(DrunkEvent.WakeUp, st.Step(AlcoholRules.Effects(2.3, AlcoholMode.Real), 0.1f, () => 0f));
        Assert.False(st.Out);
    }

    [Fact]
    public void ThePassingOutMinuteIsNotCutShortByASobererLevel()
    {
        var st = new DrunkState();
        st.Step(AlcoholRules.Effects(2.6, AlcoholMode.Real), 0.1f, () => 0f);
        Assert.Equal(DrunkEvent.None, st.Step(AlcoholRules.Effects(1.0, AlcoholMode.Real), 30f, () => 0f));
        Assert.Equal(DrunkEvent.WakeUp, st.Step(AlcoholRules.Effects(1.0, AlcoholMode.Real), 31f, () => 0f));
    }

    [Fact]
    public void TheClockCanBeSkipped()
    {
        AlcoholClock.Reset();
        double a = AlcoholClock.Now;
        AlcoholClock.Skip(600);
        Assert.InRange(AlcoholClock.Now - a, 599, 602);
        AlcoholClock.Reset();
    }
}
