using UnitSport.Adventure.Together;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Stirring la huit (#1266 part 5.3): the eights a trace makes, their quality, the split meter.</summary>
public class StirRulesTests
{
    /// <summary>Feeds <paramref name="eights"/> lemniscates of <paramref name="seconds"/> each at 30 Hz; the qualities reported.</summary>
    private static List<float> Trace(Func<double, (float X, float Y)> at, double seconds, int eights, double noise = 0, int seed = 1)
    {
        var rng = new Random(seed);
        var t = new StirTracker();
        var qs = new List<float>();
        int n = (int)(seconds * eights * 30);
        for (int i = 0; i <= n; i++)
        {
            double time = i / 30.0;
            var (x, y) = at(time / seconds);
            x += (float)((rng.NextDouble() - 0.5) * noise);
            y += (float)((rng.NextDouble() - 0.5) * noise);
            if (t.Add(x, y, time, out float q)) qs.Add(q);
        }
        return qs;
    }

    private static (float, float) Lemniscate(double u)
    {
        double a = 2 * Math.PI * u, s = Math.Sin(a), c = Math.Cos(a), d = 1 + s * s;
        return ((float)(c / d), (float)(s * c / d));
    }

    [Fact]
    public void APerfectEightIsGood()
    {
        var qs = Trace(Lemniscate, 2.4, 4);
        Assert.True(qs.Count >= 3, $"eights found: {qs.Count}");
        Assert.All(qs.Skip(1), q => Assert.True(q > 0.9f, $"q {q}"));
    }

    [Fact]
    public void ACircleIsPoor()
    {
        var qs = Trace(u => ((float)Math.Cos(2 * Math.PI * u), (float)Math.Sin(2 * Math.PI * u)), 2.4, 4);
        Assert.NotEmpty(qs);
        Assert.All(qs, q => Assert.True(q < 0.3f, $"q {q}"));
    }

    [Fact]
    public void AStillStickMakesNothing()
    {
        Assert.Empty(Trace(_ => (0.2f, 0.1f), 2.4, 3, noise: 0.001));
    }

    [Fact]
    public void TheTempoWindow()
    {
        var slow = Trace(Lemniscate, 3.2, 4);
        var fast = Trace(Lemniscate, 1.0, 6);
        Assert.All(slow.Skip(1), q => Assert.InRange(q, 0.5f, 0.9f));
        Assert.All(fast.Skip(1), q => Assert.True(q <= 0.1f + 1e-6f, $"too fast: q {q}"));
        var right = Trace(Lemniscate, 2.4, 4);
        Assert.True(slow.Skip(1).Average() < right.Skip(1).Average());
    }

    [Fact]
    public void AJitteryEightIsWorse()
    {
        var clean = Trace(Lemniscate, 2.4, 4).Skip(1).Average();
        // stop-and-go: the hand pauses every few samples
        var jerky = Trace(u => Lemniscate(Math.Floor(u * 24) / 24 + 0.6 * (u * 24 - Math.Floor(u * 24)) / 24), 2.4, 4).Skip(1).DefaultIfEmpty(0).Average();
        Assert.True(jerky < clean, $"jerky {jerky} vs clean {clean}");
    }

    [Fact]
    public void TheSplitMeter()
    {
        float s = 0;
        for (int i = 0; i < 10; i++) s = StirRules.Step(s, 1f, stirred: false, hot: false);
        Assert.Equal(0.2f, s, 4);
        s = StirRules.Step(s, 1f, stirred: true, hot: true);
        Assert.Equal(0.24f, s, 4);
        s = StirRules.AfterEight(s, 0.2f);
        Assert.Equal(0.32f, s, 4);
        s = StirRules.AfterEight(s, 0.8f);
        Assert.Equal(0.27f, s, 4);
        s = StirRules.AfterEight(s, 0.5f);
        Assert.Equal(0.27f, s, 4);
        for (int i = 0; i < 100; i++) s = StirRules.Step(s, 1f, false, true);
        Assert.Equal(1f, s);
        Assert.Equal(1f, StirRules.AfterEight(s, 1f));   // split stays split
        Assert.Equal(0f, StirRules.AfterEight(0.02f, 0.9f));
    }

    [Fact]
    public void TheGuideIsAnEight()
    {
        var (x0, y0) = StirRules.Guide(0);
        Assert.Equal(1f, x0, 4);
        Assert.Equal(0f, y0, 4);
        var (x2, _) = StirRules.Guide(32);
        Assert.Equal(-1f, x2, 4);
    }
}
