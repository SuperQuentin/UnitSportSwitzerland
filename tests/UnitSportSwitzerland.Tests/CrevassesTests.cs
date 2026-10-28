using UnitSport.Adventure.Together;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Hidden crevasses (#1266 part 4): the density, the same everywhere, none off the glacier.</summary>
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
}
