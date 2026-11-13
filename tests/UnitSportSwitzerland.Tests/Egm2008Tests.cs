using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The EGM2008 grid the fill's sea stands on (#1808, <see cref="Egm2008"/>): embedded, close to Switzerland's own sea level
/// where that one holds, and far from the Swiss chain extrapolated where it does not.
/// </summary>
public class Egm2008Tests
{
    [Fact]
    public void The_grid_is_embedded_and_agrees_with_LHN95_in_Switzerland()
    {
        Assert.True(Egm2008.Available);
        foreach (var (lat, lon) in new[] { (46.2, 6.15), (46.95, 7.44), (47.37, 8.54), (46.0, 8.95) })
            Assert.InRange(Egm2008.Undulation(lat, lon) - SwissSeaLevel.Instance.Undulation(lat, lon), -1.5, 1.5);
    }

    [Fact]
    public void Far_away_it_is_the_geoid_not_the_swiss_chain()
    {
        // Ladakh, where the user flew (#1800): EGM2008 about -29 m, the Swiss chain extrapolated -345 m
        Assert.InRange(Egm2008.Undulation(33.23, 75.91), -35, -23);
        Assert.InRange(SwissSeaLevel.Instance.Undulation(33.23, 75.91), -360, -330);
        // the geoid's known extremes: the Indian Ocean low, New Guinea's high
        Assert.InRange(Egm2008.Undulation(4.7, 78.8), -108, -95);
        Assert.InRange(Egm2008.Undulation(-4, 147), 70, 90);
    }

    [Fact]
    public void It_is_continuous_across_the_date_line_and_at_the_poles()
    {
        Assert.Equal(Egm2008.Undulation(10, 179.999), Egm2008.Undulation(10, -180), 0.05);
        Assert.Equal(Egm2008.Undulation(10, 180), Egm2008.Undulation(10, -180), 1e-9);
        Assert.False(double.IsNaN(Egm2008.Undulation(90, 0)));
        Assert.False(double.IsNaN(Egm2008.Undulation(-90, 0)));
    }
}
