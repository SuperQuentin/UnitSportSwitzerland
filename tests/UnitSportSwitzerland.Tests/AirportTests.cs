using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The airport stand planner, runway profiles and the airports file (#422).</summary>
public class AirportTests
{
    private static readonly List<IReadOnlyList<(double E, double N)>> Taxiway = new() { new List<(double, double)> { (-1000, 100), (1000, 100) } };

    [Fact]
    public void LeadInLineNoseIsTheEndAwayFromTheTaxiway()
    {
        // drawn either way round: from the taxiway south to the stop, or back
        foreach (var line in new[] { new List<(double, double)> { (0, 95), (0, 20) }, new List<(double, double)> { (0, 20), (0, 95) } })
        {
            var s = AirportPlanner.FromLine(line, Taxiway);
            Assert.NotNull(s);
            Assert.Equal(0, s!.Value.E, 6);
            Assert.Equal(20, s.Value.N, 6);
            Assert.Equal(180, s.Value.Heading, 6);
        }
    }

    [Fact]
    public void DriveThroughLineHasNoHeading()
    {
        var both = new List<IReadOnlyList<(double E, double N)>> { Taxiway[0], new List<(double, double)> { (-1000, 0), (1000, 0) } };
        Assert.Null(AirportPlanner.FromLine(new List<(double, double)> { (0, 95), (0, 5) }, both));
    }

    [Fact]
    public void NodeStandFacesAwayFromTheTaxiway()
    {
        Assert.Equal(180, AirportPlanner.FromPoint(10, 40, Taxiway)!.Value, 6);
        Assert.Equal(0, AirportPlanner.FromPoint(10, 160, Taxiway)!.Value, 6);
    }

    [Fact]
    public void FixtureAirportParksSixA320sAndTheCargoPair()
    {
        var index = FixtureAirport.Plan(2600000, 1200000);
        var airport = Assert.Single(index.Airports);
        var stands = airport.Stands;
        Assert.Equal(6, stands.Count(s => s.Use == StandUse.Airliner));
        var heavy = Assert.Single(stands, s => s.Use == StandUse.Heavy);
        var military = Assert.Single(stands, s => s.Use == StandUse.Military);
        Assert.Equal("C1", heavy.Ref);
        Assert.Equal("C2", military.Ref);
        // nose gear on the stop: the origin 12.64 m behind it (north: the A320s face south)
        var one = stands.Single(s => s.Ref == "1");
        Assert.Equal(2600030, one.E, 3);
        Assert.Equal(1200040 + AirportPlanner.A320.NoseGear, one.N, 3);
        Assert.Equal(180, one.Heading, 3);
        Assert.Equal(FixtureCourse.FlatHeight, one.Ground, 3);
        // nothing overlaps
        for (int i = 0; i < stands.Count; i++)
            for (int j = i + 1; j < stands.Count; j++)
                Assert.False(AirportPlanner.Overlap(stands[i].E, stands[i].N, stands[i].Heading, AirportPlanner.SizeOf(stands[i].Use),
                    stands[j].E, stands[j].N, stands[j].Heading, AirportPlanner.SizeOf(stands[j].Use)), $"{stands[i].Id} / {stands[j].Id}");
        var runway = Assert.Single(airport.Runways);
        Assert.True(runway.Usable);
        Assert.Equal(2700, runway.Length);
    }

    [Fact]
    public void StandsOffThePavementOrTooCloseAreSkipped()
    {
        bool Paved(double e, double n) => n > -200 && n < 100 && e > -50;   // nothing west of E -50
        var c = new List<AirportPlanner.Candidate>
        {
            new("1", -40, 20, 180, "A", false),   // its left wing over the grass
            new("2", 20, 20, 180, "A", false),
            new("3", 40, 20, 180, "A", false),    // 20 m from 2: the wings would touch
            new("4", 70, 20, 180, "A", false),
            new("C1", 500, 20, 180, "Cargo", true),
            new("C2", 650, 20, 180, "Cargo", true),
        };
        var chosen = AirportPlanner.Choose("T", c, Paved, airliners: true);
        var refs = chosen.Where(s => s.Use == StandUse.Airliner).Select(s => s.Ref).OrderBy(r => r).ToList();
        Assert.DoesNotContain("1", refs);
        Assert.False(refs.Contains("2") && refs.Contains("3"));
        Assert.Contains("4", refs);
    }

    [Fact]
    public void SmallAirportsGetNoAirliners()
    {
        var c = new List<AirportPlanner.Candidate> { new("1", 0, 0, 180, "A", false) };
        Assert.Empty(AirportPlanner.Choose("T", c, (_, _) => true, airliners: false));
    }

    [Fact]
    public void RunwayProfileFindsGradeAndBumps()
    {
        var flat = AirportPlanner.Profile("09/27", 0, 0, 3000, 0, 45, (_, _) => 400);
        Assert.True(flat.Usable);
        Assert.Equal(0, flat.Grade, 6);
        var steep = AirportPlanner.Profile("09/27", 0, 0, 3000, 0, 45, (e, _) => 400 + e * 0.03);
        Assert.False(steep.Usable);
        Assert.Equal(3, steep.Grade, 2);
        Assert.Equal(0, steep.Bump, 6);   // a straight slope is no bump
        var bumpy = AirportPlanner.Profile("09/27", 0, 0, 3000, 0, 45, (e, _) => 400 + (Math.Abs(e - 1500) < 30 ? 1.5 : 0));
        Assert.False(bumpy.Usable);
        Assert.True(bumpy.Bump >= 1.4);
        Assert.True(double.IsNaN(AirportPlanner.Profile("x", 0, 0, 3000, 0, 45, (_, _) => double.NaN).Grade));
    }

    [Fact]
    public void RefsSortNaturally()
    {
        var refs = new List<string> { "10", "", "E10", "2", "E2", "3A", "3" };
        refs.Sort(AirportPlanner.RefOrder.Instance);
        Assert.Equal(new[] { "2", "3", "3A", "10", "E2", "E10", "" }, refs);
    }

    [Fact]
    public void FileRoundTrips()
    {
        var index = FixtureAirport.Plan(2600000, 1200000);
        index.Airports[0].Stands[0].Ground = double.NaN;
        var back = AirportIndex.FromJson(index.ToJson());
        Assert.Equal(index.Stands().Count(), back.Stands().Count());
        Assert.True(double.IsNaN(back.Airports[0].Stands[0].Ground));
        Assert.Equal(index.Airports[0].Stands[1].Use, back.Airports[0].Stands[1].Use);
        Assert.Equal("LSXX", back.Airports[0].Code);
    }
}
