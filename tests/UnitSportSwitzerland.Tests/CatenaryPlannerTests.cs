using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Overhead lines' supports (#1052, <see cref="CatenaryPlanner"/>): spacing on curves, agreement across a seam, spans and cantilevers.</summary>
public class CatenaryPlannerTests
{
    private static readonly TileId Tile = new(2600, 1200);

    private static CatenaryPlanner.Rail Straight(double from, double to, RoadFlags flags, bool street = false) =>
        new([new Vec2(2600000 + from, 1200500), new Vec2(2600000 + to, 1200500)], [500f, 500f], flags, street, false, from, Tile);

    private static (List<CatenarySupport> Supports, List<CatenaryWire> Wires, CatenaryPlanner.Stats Stats) Plan(
        IReadOnlyList<CatenaryPlanner.Rail> rails, Func<Vec2, bool>? building = null, Func<Vec2, bool>? clear = null)
    {
        var supports = new Dictionary<TileId, List<CatenarySupport>>();
        var wires = new Dictionary<TileId, List<CatenaryWire>>();
        var stats = new CatenaryPlanner.Stats();
        CatenaryPlanner.Plan(rails, rails.Select(r => r.Plan).ToList(), building ?? (_ => false), clear ?? (_ => true), supports, wires, stats);
        return (supports.GetValueOrDefault(Tile) ?? [], wires.GetValueOrDefault(Tile) ?? [], stats);
    }

    [Fact]
    public void Spacing_halves_on_tight_curves()
    {
        // L <= sqrt(4.8 R): straight keeps the base, a 600 m main-line curve halves 60 to 30, a 30 m tram curve goes to 7.5
        Assert.Equal(30, CatenaryPlanner.SpacingAt(30, double.PositiveInfinity));
        Assert.Equal(30, CatenaryPlanner.SpacingAt(60, 600));
        Assert.Equal(7.5, CatenaryPlanner.SpacingAt(30, 30));
    }

    [Fact]
    public void A_line_split_at_a_seam_gets_the_same_supports()
    {
        // supports stand on the line's along-metre grid: one piece or two, the same places, none twice
        var whole = Plan([Straight(0, 100, RoadFlags.Tramway)]).Supports.Select(s => s.BX).Order().ToList();
        var split = Plan([Straight(0, 45, RoadFlags.Tramway), Straight(45, 100, RoadFlags.Tramway)]).Supports.Select(s => s.BX).Order().ToList();
        Assert.Equal([0f, 30f, 60f, 90f], whole.Select(x => MathF.Round(x - 0f)));
        Assert.Equal(whole, split);
    }

    [Fact]
    public void A_street_spans_to_a_facade_else_a_mast()
    {
        // a facade 8 m north of the track (y up = north), open ground south: one end on the wall, the other on a mast off the road
        var (supports, _, stats) = Plan([Straight(0, 60, RoadFlags.Tramway, street: true)], building: p => p.Y > 1200508, clear: p => Math.Abs(p.Y - 1200500) > 6);
        Assert.NotEmpty(supports);
        Assert.All(supports, s => Assert.Equal(CatenarySupportKind.Span, s.Kind));
        Assert.All(supports, s => Assert.True(s.AWall != s.BWall, "one end on the facade, one on a mast"));
        Assert.Equal(stats.Spans, stats.WallEnds);
    }

    [Fact]
    public void A_double_track_main_line_has_a_mast_outside_each_track_and_two_wires()
    {
        var (supports, wires, _) = Plan([Straight(0, 130, RoadFlags.DoubleTrack)]);
        Assert.Equal(2, wires.Count);
        Assert.All(wires, w => Assert.Equal(CatenaryLine.Main, w.Line));
        // 0, 60, 120 m, each a mast either side; masts 3 m outside their track (2.2 m off the centre line)
        Assert.Equal(6, supports.Count);
        Assert.All(supports, s => Assert.Equal(5.2f, MathF.Abs(s.AZ - 500f), 2));
        Assert.All(supports, s => Assert.Equal(500f + CatenaryPlanner.MainWire + CatenaryPlanner.RailTop, s.WireY, 3));
    }
}
