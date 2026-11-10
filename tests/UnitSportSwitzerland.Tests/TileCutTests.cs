using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Rewrite;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// A block's whole lines cut back into real tiles when written (#1660, <see cref="TileCut"/>): a segment meets itself across the
/// edge in each tile's own frame, its dashed paint keeps one pattern, its transition keeps its widths, a junction's records stay
/// with its home tile, a line's records go where they stand.
/// </summary>
public class TileCutTests
{
    private static readonly GridUnits Km = new(0, 0, 0, 1000);

    private static BlockFrame Frame => new(Km, 2601000, 1200500);

    /// <summary>A straight street along y = 1200300 from x0 to x1, in the block's frame.</summary>
    private static RoadSegment Street(BlockFrame f, double x0, double x1)
    {
        double w = f.West(BlockFrame.Lines), n = f.North(BlockFrame.Lines);
        var pts = new List<float>();
        for (double x = x0; x <= x1 + 1e-9; x += 100) pts.AddRange([(float)(x - w), 400f, (float)(n - 1200300)]);
        return new RoadSegment
        {
            Class = RoadClass.Major, Surface = RoadSurface.Paved, Width = 7, Points = pts.ToArray(),
            Attributes = new RoadAttributes { Flags = RoadAttrFlags.YieldAtStart | RoadAttrFlags.YieldAtEnd },
        };
    }

    private static Dictionary<TileId, RoadTile> Cut(RoadTile lines, params RoadTile[] homes)
    {
        var sources = new Dictionary<TileId, RoadTile> { [BlockFrame.Lines] = lines };
        foreach (var h in homes) sources[h.Id] = h;
        return TileCut.Cut(sources, Frame, _ => true, new TileCut.Stats());
    }

    [Fact]
    public void A_line_is_cut_where_it_crosses_a_tile_edge_and_meets_itself()
    {
        var f = Frame;
        var seg = Street(f, 2600200, 2601800);
        var tiles = Cut(new RoadTile { Id = BlockFrame.Lines, Segments = [seg] });
        Assert.Equal(new[] { new TileId(2600, 1200), new TileId(2601, 1200) }, tiles.Keys.OrderBy(t => t.E).ToArray());
        var a = Assert.Single(tiles[new TileId(2600, 1200)].Segments);
        var b = Assert.Single(tiles[new TileId(2601, 1200)].Segments);
        // in their own frames: a ends on its east edge, b starts on its west edge, at the same plane point
        Assert.Equal(1000, a.Points[^3], 3);
        Assert.Equal(0, b.Points[0], 3);
        Assert.Equal(a.Points[^1], b.Points[2], 3);
        // a yield only at the line's own ends
        Assert.True(a.Attributes.Has(RoadAttrFlags.YieldAtStart));
        Assert.False(a.Attributes.Has(RoadAttrFlags.YieldAtEnd));
        Assert.False(b.Attributes.Has(RoadAttrFlags.YieldAtStart));
        Assert.True(b.Attributes.Has(RoadAttrFlags.YieldAtEnd));
    }

    [Fact]
    public void Dashed_paint_keeps_its_pattern_across_the_cut()
    {
        var f = Frame;
        var seg = Street(f, 2600200, 2601800);
        // dash 3, gap 6 from 2 m: dashes start at x = 2600202 + 9k; the cut at 2601000 halves the dash from 2600993
        var paint = RoadPaint.AlongSegment(seg, PaintType.WhiteDashed, 0xFFFFFFFF, 0.1f, 3, 6, 0, 2);
        var tiles = Cut(new RoadTile { Id = BlockFrame.Lines, Segments = [seg], Paint = [paint] });
        double dashed = 0;
        var starts = new List<double>();
        foreach (var (id, tile) in tiles)
            foreach (var p in tile.Paint)
            {
                Assert.Same(tile.Segments.Single(), p.Segment);
                double from = Km.West(id) + p.Vertices[0], to = Km.West(id) + p.Vertices[^3];
                if (p.Dash == 0) { dashed += to - from; continue; }   // the halved dash's second half, solid
                starts.Add(from);
                double len = to - from;
                dashed += Math.Floor(len / 9) * 3 + Math.Min(3, len - Math.Floor(len / 9) * 9);
            }
        // every dash where the whole line has it
        foreach (double x in starts) Assert.Equal(0, (x - 2600202) % 9, 1);
        double whole = Math.Floor(1598 / 9.0) * 3 + Math.Min(3, 1598 - Math.Floor(1598 / 9.0) * 9);
        Assert.Equal(whole, dashed, 1);
    }

    [Fact]
    public void A_transition_keeps_its_widths_across_the_cut()
    {
        var f = Frame;
        var seg = Street(f, 2600700, 2601300);
        var t = new RoadTransition
        {
            Segment = seg, Stations = [0, 600], Centre = [0, 0], Anchor = 0,
            Strips = [new RoadTransitionStrip { Kind = StripKind.CarLane, Id = 1, Widths = [3, 4] }],
        };
        var tiles = Cut(new RoadTile { Id = BlockFrame.Lines, Segments = [seg], Transitions = [t] });
        var ta = Assert.Single(tiles[new TileId(2600, 1200)].Transitions);
        var tb = Assert.Single(tiles[new TileId(2601, 1200)].Transitions);
        Assert.Equal(300, ta.Stations[^1], 3);
        Assert.Equal(3.5f, ta.Strips[0].Widths[^1], 3);
        Assert.Equal(3.5f, tb.Strips[0].Widths[0], 3);
        Assert.Equal(4f, tb.Strips[0].Widths[^1], 3);
        Assert.Same(tiles[new TileId(2601, 1200)].Segments.Single(), tb.Segment);
    }

    [Fact]
    public void A_junctions_records_stay_home_and_a_lines_go_where_they_stand()
    {
        var f = Frame;
        double w = f.West(BlockFrame.Lines), n = f.North(BlockFrame.Lines);
        var home = new TileId(2600, 1200);
        // a sign of the junction homed in 2600 standing across the edge, in 2601; a line's sign there too
        var sign = new RoadPointProp { Type = PointPropType.YieldSign, X = (float)(2601010 - w), Z = (float)(n - 1200310) };
        var lineSign = sign with { Type = PointPropType.MainRoadSign };
        var tiles = Cut(new RoadTile { Id = BlockFrame.Lines, Segments = [], PointProps = [lineSign] },
            new RoadTile { Id = home, Segments = [], PointProps = [sign] });
        var kept = Assert.Single(tiles[home].PointProps);
        Assert.Equal(PointPropType.YieldSign, kept.Type);
        Assert.Equal(1010, kept.X, 2);   // in its home's frame, past its east edge
        Assert.Equal(PointPropType.MainRoadSign, Assert.Single(tiles[new TileId(2601, 1200)].PointProps).Type);
    }
}
