using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The neighbours' terrain (#936, #965): LV95 to their grids, and the gap fill a partly covered
/// tile goes through. The readers themselves are checked on the real files with
/// <c>--dem-probe</c> against GDAL.
/// </summary>
public class ForeignDemTests
{
    // GDAL (PROJ) EPSG:2056 -> each grid, for a point on each border
    [Theory]
    [InlineData(DemCrs.Lambert93, 2530000, 1135000, 971233.913, 6590816.181)]   // Chablais
    [InlineData(DemCrs.Utm32, 2615000, 1280000, 397781.834, 5280572.561)]       // Lörrach
    [InlineData(DemCrs.Utm32, 2838000, 1165000, 618360.235, 5161191.798)]       // Vinschgau
    [InlineData(DemCrs.Laea3035, 2775000, 1250000, 4302574.481, 2696027.264)]   // Vorarlberg
    public void Lv95_lands_where_GDAL_puts_it(DemCrs crs, double e, double n, double x, double y)
    {
        var (gx, gy) = DemProjection.FromLv95(crs, e, n);
        Assert.InRange(gx - x, -0.002, 0.002);
        Assert.InRange(gy - y, -0.002, 0.002);
    }

    private const int S = XyzParser.CellsPerSide;

    private static ushort[] Rows(Func<int, ushort> valueOfRow)
    {
        var g = new ushort[S * S];
        for (int r = 0; r < S; r++) Array.Fill(g, valueOfRow(r), r * S, S);
        return g;
    }

    [Fact]
    public void Empty_rows_at_the_foot_copy_the_nearest_row_above()
    {
        // data only in the top third: the old search skipped every row above an empty foot
        var g = Rows(r => r < 600 ? (ushort)(1000 + r) : XyzParser.MissingCell);
        XyzParser.FillMissing(g);
        Assert.DoesNotContain(XyzParser.MissingCell, g);
        Assert.Equal(1599, g[1999 * S]);
    }

    [Fact]
    public void Empty_rows_take_the_closer_of_the_rows_around_them()
    {
        var g = Rows(r => r is >= 100 and < 200 ? XyzParser.MissingCell : (ushort)r);
        XyzParser.FillMissing(g);
        Assert.Equal(99, g[120 * S]);
        Assert.Equal(200, g[180 * S]);
        Assert.Equal(200, g[199 * S]);
    }

    [Fact]
    public void A_row_with_any_data_is_carried_sideways()
    {
        var g = Rows(_ => XyzParser.MissingCell);
        g[10 * S + 700] = 42;
        XyzParser.FillMissing(g);
        Assert.Equal(42, g[10 * S]);
        Assert.Equal(42, g[10 * S + S - 1]);
        Assert.Equal(42, g[1999 * S + 3]); // and every empty row then copies it
    }
}
