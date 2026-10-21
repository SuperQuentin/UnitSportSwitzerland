using UnitSport.Terrain.Stadiums;
using Xunit;

namespace UnitSport.Tests;

/// <summary><c>/stadium</c> finding a venue (src/Terrain/Stadiums/StadiumLookup.cs, linked in, #994).</summary>
public class StadiumLookupTests
{
    private static string[] Names(string query) => StadiumLookup.Find(query).Select(v => v.Name).ToArray();

    [Theory]
    [InlineData("wankdorf", "Stadion Wankdorf")]
    [InlineData("brugglifeld", "Stadion Brügglifeld")]    // accents ignored
    [InlineData("STADE DE GENEVE", "Stade de Genève")]    // a whole name wins over Palexpo Arena Genève
    [InlineData("#1", "Stade de Genève")]
    [InlineData("13", "Hallenstadion")]
    [InlineData("paleo", "Paléo Festival, Grande Scène")]
    [InlineData("st gallen open", "OpenAir St. Gallen, Sitterbühne")]
    public void Finds_one(string query, string name) => Assert.Equal([name], Names(query));

    [Fact]
    public void A_town_finds_every_venue_in_it() =>
        Assert.Equal(["Stadion Wankdorf", "PostFinance Arena", "Gurtenfestival, Hauptbühne"], Names("Bern"));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("nowhere")]
    public void Finds_none(string query) => Assert.Empty(StadiumLookup.Find(query));

    [Fact]
    public void Every_venue_has_its_town() => Assert.All(StadiumCatalog.All, v => Assert.NotEmpty(v.Town));

    [Fact]
    public void The_list_has_every_venue_once_by_kind()
    {
        var list = StadiumLookup.List().ToList();
        Assert.Equal(3, list.Count);
        // open airs have commas in their names: look for each row whole
        Assert.All(StadiumCatalog.All, v =>
            Assert.Single(list, l => l.Contains($"{StadiumLookup.Number(v)} {StadiumLookup.Label(v)}")));
        Assert.StartsWith("Arenas (", Assert.Single(StadiumLookup.List(VenueKind.Arena)));
    }

    [Theory]
    [InlineData("arena", VenueKind.Arena)]
    [InlineData("Stadiums", VenueKind.Stadium)]
    [InlineData("openair", VenueKind.OpenAir)]
    [InlineData("festival", VenueKind.OpenAir)]
    public void Kinds(string word, VenueKind kind)
    {
        Assert.True(StadiumLookup.TryKind(word, out var got));
        Assert.Equal(kind, got);
    }

    [Fact]
    public void Nearest_venue_and_distance()
    {
        var (venue, metres) = StadiumLookup.Nearest(2601900, 1201300);
        Assert.Equal("Stadion Wankdorf", venue.Name);
        Assert.InRange(metres, 0, 200);
        Assert.Equal("12 km", StadiumLookup.Distance(12_340));
        Assert.Equal("800 m", StadiumLookup.Distance(800));
    }
}
