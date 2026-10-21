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
    public void Two_tracks_mapped_as_lines_of_their_own_share_the_spans()
    {
        // Geneva maps each tram track as its own line: the second hangs its wire from the first's spans, on a grid of its own
        var north = new CatenaryPlanner.Rail([new Vec2(2600000, 1200504), new Vec2(2600100, 1200504)], [500f, 500f], RoadFlags.Tramway, true, false, 13, Tile);
        // a 16 m road over both tracks: the spans reach past it
        var (supports, wires, stats) = Plan([Straight(0, 100, RoadFlags.Tramway, street: true), north], clear: p => Math.Abs(p.Y - 1200502) > 8);
        Assert.Equal(2, wires.Count);
        Assert.Equal(4, supports.Count);
        // the second track's supports from 30 m on (its grid starts 13 m in) hang on the first's; the first span carries one wire
        Assert.Equal(3, supports.Count(s => s.Over.Length == 4));
        Assert.Equal(3, stats.SpansShared);
    }

    [Fact]
    public void A_reserved_double_tram_track_in_the_open_gets_centre_masts()
    {
        // nothing built near: one mast on the centre line, an arm out to each track
        var (supports, wires, stats) = Plan([Straight(0, 70, RoadFlags.Tramway | RoadFlags.DoubleTrack)]);
        Assert.Equal(2, wires.Count);
        Assert.Equal(3, stats.CentreMasts);
        Assert.All(supports, s => Assert.Equal(CatenarySupportKind.CentreMast, s.Kind));
        Assert.All(supports, s => Assert.Equal(500f, s.AZ, 2));
        Assert.All(supports, s => Assert.Equal(4, s.Over.Length));
    }

    [Fact]
    public void Two_single_tram_tracks_side_by_side_share_centre_masts()
    {
        // Geneva maps each tram track as its own line (#1094): 3.6 m apart, drawn in opposite directions, nothing built near
        var south = new CatenaryPlanner.Rail([new Vec2(2600000, 1200498.2), new Vec2(2600070, 1200498.2)], [500f, 500f], RoadFlags.Tramway, false, false, 0, Tile);
        var north = new CatenaryPlanner.Rail([new Vec2(2600070, 1200501.8), new Vec2(2600000, 1200501.8)], [500f, 500f], RoadFlags.Tramway, false, false, 0, Tile);
        var (supports, wires, stats) = Plan([south, north]);
        Assert.Equal(2, wires.Count);
        // the south track's grid (0, 30, 60 m) puts the masts; every north support hangs from one of them
        Assert.Equal(3, stats.CentreMasts);
        Assert.Equal(3, stats.CentreMastsShared);
        Assert.Equal(0, stats.Cantilevers);
        Assert.All(supports, s => Assert.Equal(CatenarySupportKind.CentreMast, s.Kind));
        Assert.All(supports, s => Assert.Equal(500f, s.AZ, 2));
        // a house near: each track keeps its own cantilever outside
        var (_, _, built) = Plan([south, north], building: p => p.Y > 1200510 && p.Y < 1200520);
        Assert.Equal(0, built.CentreMasts);
        Assert.Equal(6, built.Cantilevers);
    }

    [Fact]
    public void A_building_near_or_a_blocked_centre_line_keeps_masts_outside()
    {
        // a house 10 m north: no centre masts, masts outside the tracks instead
        var (built, _, builtStats) = Plan([Straight(0, 70, RoadFlags.Tramway | RoadFlags.DoubleTrack)], building: p => p.Y > 1200510 && p.Y < 1200520);
        Assert.Equal(0, builtStats.CentreMasts);
        Assert.All(built, s => Assert.Equal(CatenarySupportKind.Cantilever, s.Kind));
        // a bike path along the centre line (clear says no there): none on it
        var (blocked, _, blockedStats) = Plan([Straight(0, 70, RoadFlags.Tramway | RoadFlags.DoubleTrack)], clear: p => Math.Abs(p.Y - 1200500) > 1);
        Assert.Equal(0, blockedStats.CentreMasts);
        Assert.All(blocked, s => Assert.True(Math.Abs(s.AZ - 500f) > 1));
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
