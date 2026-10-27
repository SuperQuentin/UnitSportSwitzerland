using UnitSport.Terrain.Format;
using UnitSport.World.Locate;
using Xunit;

namespace UnitSport.Tests;

/// <summary><c>/locate</c> (#1277): the words, the chat's coordinates, the building index and the search.</summary>
public class LocateTests
{
    private static LocateQuery Parse(string s)
    {
        var q = LocateVocabulary.Parse(s, out string error);
        Assert.True(q != null, error);
        return q!;
    }

    [Theory]
    [InlineData("ikea", LocateTarget.Ikea)]
    [InlineData("appartement building", LocateTarget.Apartments)]
    [InlineData("an apartment building", LocateTarget.Apartments)]
    [InlineData("castle", LocateTarget.Castle)]
    [InlineData("construction zone", LocateTarget.Construction)]
    [InlineData("Cinéma", LocateTarget.Cinema)]
    [InlineData("a lake", LocateTarget.Lake)]
    [InlineData("a harbour", LocateTarget.Harbour)]
    [InlineData("a steamer", LocateTarget.Steamer)]
    [InlineData("castles", LocateTarget.Castle)]
    [InlineData("bus stop", LocateTarget.BusStop)]
    [InlineData("tower", LocateTarget.Tower)]
    public void Nouns_are_read_with_articles_plurals_and_accents(string text, LocateTarget target) =>
        Assert.Equal(target, Parse(text).Target);

    [Fact]
    public void Selectors_stack_with_plus_with_and()
    {
        var q = Parse("church + spire");
        Assert.Equal(LocateTarget.Church, q.Target);
        Assert.True(q.Has(LocateModifier.Spire));

        q = Parse("building with a parking entrance");
        Assert.Equal(LocateTarget.Building, q.Target);
        Assert.True(q.Has(LocateModifier.Parking));

        q = Parse("tall old apartment building + parking entrance and 8 floors");
        Assert.Equal(LocateTarget.Apartments, q.Target);
        Assert.Equal(LocateModifier.Tall | LocateModifier.Old | LocateModifier.Parking, q.Modifiers);
        Assert.Equal(8, q.MinFloors);

        // "tower" after a church is its spire, not a second noun
        Assert.True(Parse("church tower").Has(LocateModifier.Spire));
        Assert.Equal(40f, Parse("building + 40m").MinHeight);
        Assert.Equal(6, Parse("apartments + 6+ floors").MinFloors);
    }

    [Fact]
    public void In_a_town_takes_the_rest_of_the_line()
    {
        var q = Parse("castle in Gruyères");
        Assert.Equal(LocateTarget.Castle, q.Target);
        Assert.Equal("gruyeres", q.Town);
        Assert.Equal("la chaux-de-fonds", Parse("cinema in La Chaux-de-Fonds").Town);
    }

    [Theory]
    [InlineData("")]
    [InlineData("spaceship")]
    [InlineData("lake + spire")]
    [InlineData("cinema + parking entrance")]
    [InlineData("castle and lake")]
    [InlineData("old new house")]
    [InlineData("castle in")]
    public void Nonsense_is_refused_with_a_reason(string text)
    {
        Assert.Null(LocateVocabulary.Parse(text, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Describe_says_what_was_understood() =>
        Assert.Equal("tall apartment building with a parking entrance and 6+ floors",
            Parse("tall apartments + parking entrance + 6 floors").Describe());

    [Fact]
    public void Coordinates_are_found_in_any_line_and_round_trip()
    {
        var all = LocateCoords.FindAll("meet me at @2533450,1152300 or @2600000, 1200000.5 not @12,34");
        Assert.Equal(2, all.Count);
        Assert.Equal(2533450, all[0].E);
        Assert.Equal(1152300, all[0].N);
        Assert.Equal(1200000.5, all[1].N);
        Assert.Equal("@2533450,1152300", LocateCoords.Format(2533449.6, 1152300.4));
        Assert.True(LocateCoords.TryParse("2533450 1152300", out double e, out double n));
        Assert.Equal((2533450.0, 1152300.0), (e, n));
        // out of Switzerland: a phone number, not a place
        Assert.Empty(LocateCoords.FindAll("@9999999,9999999"));
    }

    [Fact]
    public void Compass_and_distance_read_like_a_person_would_say_them()
    {
        Assert.Equal("NE", LocateCoords.Compass(0, 0, 100, 100));
        Assert.Equal("S", LocateCoords.Compass(0, 0, 0, -500));
        Assert.Equal("here", LocateCoords.Compass(0, 0, 3, 4));
        Assert.Equal("640 m", LocateCoords.Distance(638));
        Assert.Equal("4.2 km", LocateCoords.Distance(4210));
        Assert.Equal("38 km", LocateCoords.Distance(38_400));
    }

    private static IndexedBuilding B(double e, double n, BuildingKind kind, IndexedFlags flags = 0, byte floors = 3, ushort year = 1980, float height = 12) =>
        new(e, n, (int)(e / 1000), (int)(n / 1000), 0, kind, floors, year, height, flags);

    [Fact]
    public void The_index_finds_the_nearest_across_tiles_and_round_trips()
    {
        var index = new BuildingIndex([
            B(2_600_100, 1_200_100, BuildingKind.House),
            B(2_600_900, 1_200_500, BuildingKind.Apartment, floors: 8),
            B(2_603_500, 1_200_500, BuildingKind.Apartment, IndexedFlags.GarageRoll, floors: 9),
            B(2_610_000, 1_210_000, BuildingKind.Sacral, IndexedFlags.Church | IndexedFlags.Spire, height: 40),
        ]);
        var near = index.Nearest(2_600_000, 1_200_000, b => b.Kind == BuildingKind.Apartment, 5);
        Assert.Equal(2, near.Count);
        Assert.True(near[0].Distance < near[1].Distance);
        Assert.Single(index.Nearest(2_600_000, 1_200_000, b => b.Is(IndexedFlags.Spire), 3));
        Assert.Empty(index.Nearest(2_600_000, 1_200_000, b => b.Is(IndexedFlags.Spire), 3, radius: 5000));

        using var ms = new MemoryStream();
        index.Write(ms);
        ms.Position = 0;
        var back = BuildingIndex.Read(ms)!;
        Assert.Equal(index.All, back.All);
    }

    [Fact]
    public void An_index_of_another_version_is_not_read()
    {
        using var ms = new MemoryStream(new byte[] { 0x4C, 0x43, 0x42, 0x31, 99, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Null(BuildingIndex.Read(ms));
    }

    [Fact]
    public void The_engine_answers_from_lists_and_buildings_and_names_churches()
    {
        var engine = new LocateEngine([
            new LocatePoint(LocateTarget.Castle, "Château de Chillon", 2_560_000, 1_140_000),
            new LocatePoint(LocateTarget.Castle, "Château de Chillon", 2_560_020, 1_140_010), // another solid of it
            new LocatePoint(LocateTarget.Castle, "Château d'Aigle", 2_565_000, 1_130_000),
            new LocatePoint(LocateTarget.Church, "Église Saint-Martin", 2_600_030, 1_200_000),
        ]);
        var castles = engine.Find(Parse("castle"), 2_559_000, 1_140_000);
        Assert.Equal(["Château de Chillon", "Château d'Aigle"], castles.Hits.Select(h => h.Title));

        // no index yet: building nouns wait, churches fall back to the survey's
        engine.BuildingsPending = () => "still indexing";
        Assert.Equal("still indexing", engine.Find(Parse("apartments"), 0, 0).Refusal);
        Assert.Equal("Église Saint-Martin", engine.Find(Parse("church"), 2_600_000, 1_200_000).Hits[0].Title);

        engine.Buildings = new BuildingIndex([
            B(2_600_000, 1_200_000, BuildingKind.Sacral, IndexedFlags.Church | IndexedFlags.Spire, height: 38),
            B(2_600_500, 1_200_000, BuildingKind.Sacral, IndexedFlags.Church),
        ]);
        var spires = engine.Find(Parse("church + spire"), 2_600_000, 1_199_000);
        Assert.Equal("Église Saint-Martin", Assert.Single(spires.Hits).Title);
        var lines = LocateEngine.Lines(Parse("church + spire"), spires, 2_600_000, 1_199_000, "you");
        Assert.Equal("Nearest church with a spire (from you):", lines[0]);
        Assert.Contains("@2600000,1200000", lines[1]);
        Assert.Contains("1.0 km N", lines[1]);
    }

    [Fact]
    public void Parking_entrances_come_back_as_candidates_for_the_footprint_to_decide()
    {
        var engine = new LocateEngine([]) { Buildings = new BuildingIndex(
            Enumerable.Range(0, 60).Select(i => B(2_600_000 + i * 100, 1_200_000, BuildingKind.Apartment, IndexedFlags.GarageRoll)).ToList()) };
        Assert.Equal(40, engine.Find(Parse("apartments + parking entrance"), 2_600_000, 1_200_000).Hits.Count);
        Assert.Equal(3, engine.Find(Parse("apartments"), 2_600_000, 1_200_000).Hits.Count);
    }
}
