using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Layout;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Gantries of lane signs with heads in their bands (#1110): Geneva's sign text, which approaches the rule gantries, how signs
/// share the lanes, which SITG gantry stands over an approach, and the <c>GNTY</c> round trip.
/// </summary>
public class GantryTests
{
    private const SignalMoves L = SignalMoves.Left, T = SignalMoves.Through, R = SignalMoves.Right;

    [Theory]
    // SITG CONTENU as written on the photos' gantries (Route de Lausanne, Route de Meyrin, Route de Saint-Julien)
    [InlineData("France OSR 4.01 OSR 5.50 Aéroport OSR 4.17+OSR 9.01 Les Tuileries", true, "France|Aéroport|Les Tuileries")]
    [InlineData("Lausanne Versoix", false, "Lausanne|Versoix")]
    [InlineData("France AUTOROUTE Meyrin St-Genis", true, "France|Meyrin|St-Genis")]
    [InlineData("Lausanne Route du lac Versoix", false, "Lausanne|Route du lac|Versoix")]
    [InlineData("AUTOROUTE Lausanne AVION TRAIN Genève-Aéroport Plan-les-Ouates Zone industrielle", true, "Lausanne|Genève-Aéroport|Plan-les-Ouates")]
    [InlineData("Ferney France AUTOROUTE AVION Aéroport TRAIN Palexpo Arena# AUTOROUTE Lausanne Versoix#450 m", true, "Ferney|France|Aéroport")]
    public void Geneva_sign_text_parses_into_destinations(string contenu, bool motorway, string names)
    {
        var (m, n) = SignText.Parse(contenu);
        Assert.Equal(motorway, m);
        Assert.Equal(names, string.Join('|', n));
    }

    private static GantryFacts Facts(int lanes, int street, int sets, int importance = 0, int owner = 0, double motorway = 5000) =>
        new(0, 0, 0, lanes, street, sets, importance, owner, RoadClass.Major, motorway);

    [Fact]
    public void The_rule_wants_wide_or_split_approaches_on_main_roads_or_near_a_motorway()
    {
        var rule = new GantryRule(MinLanes: 3, MinTurnLanes: 2, MinTurnSets: 2, MinStreetLanes: 2, MotorwayReach: 1000);
        Assert.True(rule.Wants(Facts(3, 2, 2, owner: 1)));          // a cantonal 2+2 road widened to three lanes
        Assert.False(rule.Wants(Facts(3, 1, 3, owner: 1)));         // a 1+1 road with its pockets
        Assert.False(rule.Wants(Facts(3, 2, 2)));                   // a communal road far from a motorway
        Assert.True(rule.Wants(Facts(2, 2, 2, motorway: 400)));     // two lanes going two ways by a motorway
        Assert.False(rule.Wants(Facts(2, 2, 1, motorway: 400)));    // two lanes straight on
        Assert.False(new GantryRule(NeedsBoth: true).Wants(Facts(3, 2, 2, owner: 1)));   // main road, but far from a motorway
    }

    [Fact]
    public void A_sign_over_each_run_of_lanes_going_the_same_way()
    {
        // the user's photo 1: the left lane to the motorway, two lanes straight on
        (double, double, SignalMoves)[] lanes = [(1.5, 3, L), (4.5, 3, T), (7.5, 3, T)];
        var runs = GantryLayout.Runs(lanes);
        Assert.Equal([(0, 0), (1, 2)], runs);
        var (mid, width) = GantryLayout.Sign(lanes, runs[1]);
        Assert.Equal(6.0, mid, 6);
        Assert.Equal(GantryLayout.MaxWidth, width, 6);
        Assert.Equal(SignColour.Green, GantryLayout.Colour(motorway: true, mainRoad: true));
        Assert.Equal(SignColour.White, GantryLayout.Colour(motorway: false, mainRoad: false));
    }

    [Fact]
    public void The_sitg_gantry_before_an_approach_is_found_and_read_left_to_right()
    {
        // photo 1's gantry as SITG has it (support 400009433, readers driving 354 deg), the stop line 30 m on
        var path = Path.Combine(Path.GetTempPath(), $"sitg_{Guid.NewGuid():N}.tsv");
        File.WriteAllLines(path,
        [
            "# sitg_signs v1",
            "kind\te\tn\tid\tsupport\tosr\torientation\tfixation\tformat\ttype\tname\ttext",
            "sign\t2500679.39\t1122710.86\t1\t400009433\t4.42\t354\tRail au dos\t\t\tRoute de Lausanne\tChamp-du-Château",
            "sign\t2500683.45\t1122711.26\t2\t400009433\t4.41\t354\tRail au dos\t\t\tRoute de Lausanne\tFrance OSR 4.01 Aéroport",
            "sign\t2500687.65\t1122711.72\t3\t400009433\t4.41\t354\tRail au dos\t\t\tRoute de Lausanne\tLausanne Versoix",
            "sign\t2500691.81\t1122712.14\t4\t400009433\t4.41\t354\tRail au dos\t\t\tRoute de Lausanne\tLausanne Versoix",
            "sign\t2500699.41\t1122721.46\t5\t400009433\t4.32\t185\tPotence sur les feux\t\t\tRoute de Lausanne\tVersoix",
        ]);
        try
        {
            var sitg = SitgGantries.Load(path)!;
            var g = Assert.Single(sitg.Gantries);
            Assert.NotNull(sitg.At(2500684, 1122741, 355));
            Assert.Null(sitg.At(2500684, 1122741, 175));    // the other way
            Assert.Null(sitg.At(2500684, 1122841, 355));    // a stop line 130 m on
            var signs = SitgGantries.LeftToRight(g);
            Assert.Equal(["Champ-du-Château", "France OSR 4.01 Aéroport", "Lausanne Versoix"], signs.Select(s => s.Text));
            Assert.Equal([0, 2], SitgGantries.Assign(2, 3));
            Assert.Equal([0, 1], SitgGantries.Assign(2, 2));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Gantries_round_trip_through_the_road_file()
    {
        var plan = SignalPlan.Build(
        [
            new SignalArm(0, true, true, LeftPocket: true), new SignalArm(Math.PI / 2, true, true),
            new SignalArm(Math.PI, true, true), new SignalArm(-Math.PI / 2, true, true),
        ], 1, true);
        var signal = new RoadSignal { Stops = new float[12], Plan = plan };
        signal.Gantries.Add(new SignGantry
        {
            Arm = 0, Lit = true, X = 1, Y = 2, Z = 3, EndX = 12, EndY = 2.5f, EndZ = 3, Bottom = 7, Heading = 0.5f,
            Signs =
            [
                new GantrySign { Along = 4.5f, Width = 5.4f, Colour = SignColour.Blue, Lines = ["Versoix", "Lausanne"], Lanes = [new(3f, T), new(6f, T)] },
                new GantrySign { Along = 9f, Width = 2.7f, Colour = SignColour.Green, Lines = [SignGantry.MotorwayMark + "Aéroport"], Lanes = [new(9f, L | R)] },
            ],
        });
        var tile = new RoadTile { Id = new TileId(2500, 1123), Segments = new() };
        tile.Signals.Add(signal);
        using var ms = new MemoryStream();
        RoadCodec.Encode(tile, ms);
        ms.Position = 0;
        var back = RoadCodec.Decode(ms);
        var g = Assert.Single(Assert.Single(back.Signals).Gantries);
        Assert.True(g.Lit);
        Assert.Equal(11f, g.Length, 3);
        Assert.Equal(["Versoix", "Lausanne"], g.Signs[0].Lines);
        Assert.Equal(SignColour.Green, g.Signs[1].Colour);
        Assert.Equal(signal.Gantries[0].Signs[0].Lanes, g.Signs[0].Lanes);
        Assert.Equal(L | R, g.Signs[1].Lanes[0].Moves);
    }
}
