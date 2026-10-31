using UnitSport.Adventure.Together;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Hidden crevasses (#1266 part 4, #1372): the density, the same everywhere, none off the glacier, the slot's rules.</summary>
public class CrevassesTests
{
    [Fact]
    public void FourPercentOfTheCells()
    {
        int with = 0, cells = 0;
        for (int i = 0; i < 200; i++)
            for (int j = 0; j < 200; j++)
            {
                cells++;
                if (Crevasses.InCell(2_620_000 + i * Crevasses.Cell + 3, 1_100_000 + j * Crevasses.Cell + 3) != null) with++;
            }
        Assert.InRange(with / (double)cells, 0.035, 0.045);
    }

    [Fact]
    public void DeterministicAndAStripOfItsWidth()
    {
        // the first crevasse cell east of a point, then its strip
        double e = 2_620_000, n = 1_100_000;
        (double E, double N, double Angle)? c = null;
        for (int i = 0; c == null && i < 1000; i++) c = Crevasses.InCell(e + i * Crevasses.Cell + 1, n + 1);
        Assert.NotNull(c);
        var cr = c!.Value;
        Assert.Equal(cr, Crevasses.InCell(cr.E, cr.N));                     // the same on every peer, every time
        Assert.True(Crevasses.At(cr.E, cr.N));
        // along the strip: still on it; across it by a metre: off it
        Assert.True(Crevasses.At(cr.E + Math.Cos(cr.Angle) * 1.5, cr.N + Math.Sin(cr.Angle) * 1.5));
        Assert.False(Crevasses.At(cr.E - Math.Sin(cr.Angle) * 1.0, cr.N + Math.Cos(cr.Angle) * 1.0));
        Assert.False(Crevasses.At(cr.E, cr.N, onGlacier: false));
    }

    [Fact]
    public void CellKeysDiffer()
    {
        Assert.NotEqual(Crevasses.CellKey(0, 0), Crevasses.CellKey(12.5, 0));
        Assert.NotEqual(Crevasses.CellKey(0, 0), Crevasses.CellKey(0, 12.5));
        Assert.Equal(Crevasses.CellKey(1, 1), Crevasses.CellKey(11, 11));
    }

    [Fact]
    public void TheStripIsHalfAMetreWideEitherSideOfItsLine()
    {
        var cr = FirstCrevasse();
        double se = -Math.Sin(cr.Angle), sn = Math.Cos(cr.Angle);
        Assert.True(Crevasses.At(cr.E + se * 0.7, cr.N + sn * 0.7));   // within the half width (0.75)
        Assert.False(Crevasses.At(cr.E + se * 0.8, cr.N + sn * 0.8));
        Assert.True(Crevasses.At(cr.E - se * 0.7, cr.N - sn * 0.7));
    }

    [Fact]
    public void TheSlotOpensOnTheLineUnderTheBody()
    {
        var cr = FirstCrevasse();
        double se = -Math.Sin(cr.Angle), sn = Math.Cos(cr.Angle);
        var (he, hn) = Crevasses.Heading(cr.Angle);
        // a step 0.5 m off the line and 4 m along it: the slot is centred on the line at that point
        double e = cr.E + he * 4 + se * 0.5, n = cr.N + hn * 4 + sn * 0.5;
        var (le, ln) = Crevasses.OnLine(cr, e, n);
        Assert.Equal(cr.E + he * 4, le, 6);
        Assert.Equal(cr.N + hn * 4, ln, 6);
        Assert.True(Crevasses.At(le, ln));
        Assert.Equal(1.0, he * he + hn * hn, 9);
    }

    [Fact]
    public void TheRopeHoldsOnlyAStandingPartnerWithinItsLength()
    {
        Assert.True(Crevasses.CaughtByRope(true, 5, 8));
        Assert.True(Crevasses.CaughtByRope(true, 8, 8));
        Assert.False(Crevasses.CaughtByRope(true, 8.5, 8));
        Assert.False(Crevasses.CaughtByRope(false, 2, 8));
    }

    [Fact]
    public void ARopedFallHangsThreeMetresUnderTheLip() => Assert.Equal(3.0, Crevasses.CatchDepth);

    [Fact]
    public void OutNeedsTheDepthAndTheGround()
    {
        Assert.False(Crevasses.Out(100, 100, 0));          // the first tick of the fall: down there is still to come
        Assert.False(Crevasses.Out(95, 100, -5));          // deep, still climbing
        Assert.False(Crevasses.Out(99.9, 100, -10));
        Assert.True(Crevasses.Out(100, 100, -10));         // back at the ground
        Assert.True(Crevasses.Out(100.3, 100, -Crevasses.DeepEnough));
        Assert.False(Crevasses.Out(100.3, 100, -Crevasses.DeepEnough + 0.5));
    }

    [Fact]
    public void AStuckBodyIsRescuedAfterAMinute()
    {
        Assert.False(Crevasses.Stuck(59.9));
        Assert.True(Crevasses.Stuck(Crevasses.RescueSeconds));
    }

    private static (double E, double N, double Angle) FirstCrevasse()
    {
        double e = 2_620_000, n = 1_100_000;
        (double E, double N, double Angle)? c = null;
        for (int i = 0; c == null && i < 1000; i++) c = Crevasses.InCell(e + i * Crevasses.Cell + 1, n + 1);
        return c!.Value;
    }
}
