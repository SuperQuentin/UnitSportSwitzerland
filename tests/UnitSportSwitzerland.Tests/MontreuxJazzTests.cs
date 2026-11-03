using UnitSport.Terrain.Stadiums;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Montreux Jazz festival (#1394): the venue row and its season window (src/Terrain/Stadiums/StadiumCatalog.cs, linked in).</summary>
public class MontreuxJazzTests
{
    private static readonly StadiumSpec Venue = StadiumCatalog.MontreuxJazzOpenAir;

    [Theory]
    [InlineData(2026, 7, 3, true)]     // the first Friday of July
    [InlineData(2026, 7, 18, true)]    // 15 days on
    [InlineData(2026, 7, 2, false)]
    [InlineData(2026, 7, 19, false)]
    [InlineData(2027, 7, 2, true)]
    [InlineData(2027, 7, 17, true)]
    [InlineData(2027, 7, 18, false)]
    [InlineData(2026, 10, 30, false)]
    public void The_stage_stands_for_sixteen_days_from_the_first_friday_of_July(int y, int m, int d, bool stands) =>
        Assert.Equal(stands, Venue.InSeason(new DateOnly(y, m, d)));

    [Fact]
    public void Every_other_venue_plays_every_night() =>
        Assert.All(StadiumCatalog.All.Where(s => s != Venue), s =>
        {
            Assert.Null(s.Season);
            Assert.True(s.InSeason(new DateOnly(2026, 10, 30)));
        });

    [Fact]
    public void The_venue_is_last_in_the_list_so_the_numbers_of_the_others_stay() =>
        Assert.Equal(34, StadiumLookup.Number(Venue));

    [Fact]
    public void The_venue_is_found_by_its_town_and_by_its_name()
    {
        Assert.Equal([Venue], StadiumLookup.Find("montreux"));
        Assert.Equal([Venue], StadiumLookup.Find("Montreux Jazz, Lake Stage"));
    }

    [Fact]
    public void The_venue_stands_on_the_Place_du_Marche_in_Montreux()
    {
        // the swisstopo address Grand-Rue 1 is 2559396 / 1142356; the square is within 100 m of it
        double d = Math.Sqrt(Math.Pow(Venue.E - 2559396, 2) + Math.Pow(Venue.N - 1142356, 2));
        Assert.InRange(d, 0, 100);
        Assert.Equal(VenueKind.OpenAir, Venue.Kind);
        Assert.Contains(StadiumSide.North, Venue.OpenSides);   // the lake side is not fenced
    }
}
