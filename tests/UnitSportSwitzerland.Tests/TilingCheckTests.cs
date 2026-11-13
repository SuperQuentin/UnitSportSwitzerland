using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Diagnostics;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Rewrite;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The tiling check's pieces (#1660): <see cref="GridUnits"/> (a shifted or turned grid of cells, each with its plane-aligned
/// square), <see cref="LineJoin"/> (raw pieces joined back across tile cuts) and <see cref="TilingCheck.Cut"/> (a whole line cut
/// at another tiling's cells).
/// </summary>
public class TilingCheckTests
{
    /// <summary>A wavy line across a few km tiles, in the LV95 plane.</summary>
    private static List<(double X, double Y)> Wavy()
    {
        var pts = new List<(double, double)>();
        for (int i = 0; i <= 60; i++)
        {
            double t = i / 60.0;
            pts.Add((2600400 + 2300 * t, 1200700 + 900 * t + 60 * Math.Sin(t * 9)));
        }
        return pts;
    }

    private static RoadSegment Template => new() { Class = RoadClass.Major, Surface = RoadSurface.Paved, Width = 7, Points = [] };

    /// <summary>The line cut at the km lines as the extractor cuts it: each piece in its tile's frame, keyed along the line.</summary>
    private static List<LineJoin.Piece> KmPieces(List<(double X, double Y)> line, string? uuid, Func<int, RoadSegment>? template = null)
    {
        var whole = new LineJoin.Line
        {
            Template = Template, Key = uuid is null ? null : new RawRoads.Key(uuid, 1, 0),
            Points = line.Select(p => new LineJoin.Point(p.X, p.Y, 400)).ToList(), Parts = [],
        };
        var km = new GridUnits(0, 0, 0, 1000);
        var pieces = new List<LineJoin.Piece>();
        int k = 0;
        foreach (var (cell, seg, key) in TilingCheck.Cut(whole, km))
        {
            // the km grid's cells are LV95 tiles; their frames are the LV95 tile's (west, north)
            var id = new TileId(cell.E, cell.N);
            var pts = seg.Points;
            for (int i = 0; i < seg.PointCount; i++)
            {
                double x = km.West(cell) + pts[i * 3], y = km.North(cell) - pts[i * 3 + 2];
                pts[i * 3] = (float)(x - id.MinE);
                pts[i * 3 + 2] = (float)(id.MaxN - y);
            }
            var t = template?.Invoke(k) ?? Template;
            var s = new RoadSegment { Class = t.Class, Surface = t.Surface, Width = t.Width, Points = pts };
            pieces.Add(new LineJoin.Piece(id, k++, s, key));
        }
        return pieces;
    }

    [Fact]
    public void A_turned_grid_puts_every_cell_in_its_square()
    {
        var u = new GridUnits(100, 200, 28.5, 610);
        foreach (var (x, y) in new[] { (2600000.0, 1200000.0), (2600305.2, 1200991.7), (2499999.0, 1117000.0) })
        {
            var id = u.TileAt(x, y);
            double lx = x - u.West(id), lz = u.North(id) - y;
            Assert.InRange(lx, 0, GridUnits.Box);
            Assert.InRange(lz, 0, GridUnits.Box);
            Assert.True(u.Inside(id, lx, lz));
            // a cell corner is on its edge; a point beyond the cell is not inside
            var (cx, cy) = u.FromGrid(id.E, id.N);
            Assert.True(u.OnEdge(id, cx - u.West(id), u.North(id) - cy, 0.01));
            var (fx, fy) = u.FromGrid(id.E + 1.2, id.N + 0.5);
            Assert.False(u.Inside(id, fx - u.West(id), u.North(id) - fy));
            Assert.Equal(0, u.West(id) % 2, 9);
        }
    }

    [Fact]
    public void A_cell_that_cannot_fit_its_square_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new GridUnits(0, 0, 30, 1000));
        Assert.Throws<ArgumentException>(() => new GridUnits(1, 0, 0, 1000));   // centres off the 2 m lattice
        _ = new GridUnits(250, 0, 0, 1000);
    }

    [Fact]
    public void Pieces_cut_at_tile_edges_join_back_into_the_whole_line()
    {
        var line = Wavy();
        foreach (string? uuid in new[] { "{tlm}", null })
        {
            var pieces = KmPieces(line, uuid);
            Assert.True(pieces.Count >= 4);
            var joined = LineJoin.Join(pieces, Lv95Units.Instance);
            var one = Assert.Single(joined);
            Assert.Equal(pieces.Count, one.Parts.Count);
            Assert.False(one.OpenStart);
            Assert.False(one.OpenEnd);
            // every original vertex is there, in order, within float precision
            int j = 0;
            foreach (var (x, y) in line)
            {
                while (j < one.Points.Count && Math.Abs(one.Points[j].X - x) + Math.Abs(one.Points[j].Y - y) > 0.01) j++;
                Assert.True(j < one.Points.Count, $"vertex {x},{y} lost");
            }
            Assert.Equal(uuid is null ? null : 0.0, one.Key?.FromM);
        }
    }

    [Fact]
    public void Pieces_with_other_records_or_keys_stay_apart()
    {
        var line = Wavy();
        // a width change exactly at a km line: two lines, as TLM would have them
        var pieces = KmPieces(line, "{tlm}", k => k == 2 ? new RoadSegment { Class = RoadClass.Major, Surface = RoadSurface.Paved, Width = 9, Points = [] } : Template);
        Assert.Equal(3, LineJoin.Join(pieces, Lv95Units.Instance).Count);
        // another line's key at the cut
        var other = KmPieces(line, "{tlm}");
        other[1] = other[1] with { Key = new RawRoads.Key("{other}", 1, other[1].Key!.Value.FromM) };
        Assert.Equal(3, LineJoin.Join(other, Lv95Units.Instance).Count);
        // a gap in FromM: not the same stretch of line
        var gap = KmPieces(line, "{tlm}");
        gap[1] = gap[1] with { Key = gap[1].Key!.Value with { FromM = gap[1].Key!.Value.FromM + 50 } };
        Assert.True(LineJoin.Join(gap, Lv95Units.Instance).Count > 1);
    }

    [Fact]
    public void A_line_missing_its_next_piece_has_an_open_end()
    {
        var pieces = KmPieces(Wavy(), "{tlm}");
        var joined = LineJoin.Join(pieces.Take(2).ToList(), Lv95Units.Instance);
        var one = Assert.Single(joined);
        Assert.False(one.OpenStart);
        Assert.True(one.OpenEnd);
    }

    [Theory]
    [InlineData(250, 0, 0, 1000)]
    [InlineData(0, 0, 28.5, 610)]
    [InlineData(100, 200, 45, 400)]
    public void A_line_cut_at_another_tiling_lies_in_its_cells_and_meets_itself(double x, double y, double angle, double cell)
    {
        var u = new GridUnits(x, y, angle, cell);
        var whole = new LineJoin.Line
        {
            Template = Template, Key = new RawRoads.Key("{tlm}", 1, 10), Parts = [],
            Points = Wavy().Select(p => new LineJoin.Point(p.X, p.Y, 400)).ToList(),
        };
        var pieces = TilingCheck.Cut(whole, u);
        Assert.True(pieces.Count > 3);
        (double X, double Y)? lastEnd = null;
        double fromM = 10;
        foreach (var (id, seg, key) in pieces)
        {
            for (int i = 0; i < seg.PointCount; i++)
                Assert.True(u.Inside(id, seg.Points[i * 3], seg.Points[i * 3 + 2]) || u.OnEdge(id, seg.Points[i * 3], seg.Points[i * 3 + 2], 0.001),
                    $"a point outside cell {id}");
            var first = (u.West(id) + seg.Points[0], u.North(id) - seg.Points[2]);
            if (lastEnd is { } e) Assert.True(Math.Abs(e.X - first.Item1) + Math.Abs(e.Y - first.Item2) < 0.002);
            Assert.Equal(fromM, key!.Value.FromM, 3);
            double len = 0;
            for (int i = 1; i < seg.PointCount; i++)
                len += Math.Sqrt(Math.Pow(seg.Points[i * 3] - seg.Points[i * 3 - 3], 2) + Math.Pow(seg.Points[i * 3 + 2] - seg.Points[i * 3 - 1], 2));
            fromM += len;
            int n = seg.PointCount - 1;
            lastEnd = (u.West(id) + seg.Points[n * 3], u.North(id) - seg.Points[n * 3 + 2]);
        }
        // and joined back across those cuts, one line again
        var back = LineJoin.Join(pieces.Select((p, k) => new LineJoin.Piece(p.Cell, k, p.Segment, p.Key)).ToList(), u);
        Assert.Single(back);
    }

    [Theory]
    [InlineData(0, 0, 0, 1000)]
    [InlineData(0, 500, 0, 1000)]
    [InlineData(0, 0, 28.5, 610)]
    [InlineData(100, 200, 45, 400)]
    public void A_line_joined_back_has_its_own_vertices_whatever_the_tiling(double x, double y, double angle, double cell)
    {
        // (#1691) a source on the lattice, cut at a tiling's cells and joined back: the cut points are gone, the vertices exact
        var u = new GridUnits(x, y, angle, cell);
        var source = Wavy().Select(p => new LineJoin.Point(LineJoin.OnLattice(p.X), LineJoin.OnLattice(p.Y), 400)).ToList();
        var whole = new LineJoin.Line { Template = Template, Key = new RawRoads.Key("{tlm}", 1, 0), Parts = [], Points = source };
        var pieces = TilingCheck.Cut(whole, u);
        Assert.True(pieces.Count > 3);
        var back = Assert.Single(LineJoin.Join(pieces.Select((p, k) => new LineJoin.Piece(p.Cell, k, p.Segment, p.Key)).ToList(), u));
        Assert.Equal(source.Count, back.Points.Count);
        for (int i = 0; i < source.Count; i++)
        {
            Assert.Equal(source[i].X, back.Points[i].X);
            Assert.Equal(source[i].Y, back.Points[i].Y);
        }
    }

    [Fact]
    public void A_point_rounds_to_one_place_in_every_blocks_frame()
    {
        // (#1691) two blocks' frames, their centres 3 km and 500 m apart: a lattice point is exact in both, and a point computed
        // from two (a cut, a trim) rounds to the same place on the plane in both
        var frames = new[] { new BlockFrame(Lv95Units.Instance, 2601000, 1200500), new BlockFrame(Lv95Units.Instance, 2598000, 1201000),
            new BlockFrame(Lv95Units.Instance, 2601500, 1199500) };
        var rng = new Random(1691);
        for (int k = 0; k < 2000; k++)
        {
            double ax = LineJoin.OnLattice(2599000 + rng.NextDouble() * 3000), ay = LineJoin.OnLattice(1199000 + rng.NextDouble() * 3000);
            double bx = LineJoin.OnLattice(ax + rng.NextDouble() * 40), by = LineJoin.OnLattice(ay + rng.NextDouble() * 40);
            double t = rng.NextDouble();
            var seen = new HashSet<(double, double, double, double)>();
            foreach (var f in frames)
            {
                double w = f.West(BlockFrame.Lines), n = f.North(BlockFrame.Lines);
                float x = (float)(ax - w), z = (float)(n - ay), x2 = (float)(bx - w), z2 = (float)(n - by);
                Assert.Equal(ax, w + x);
                Assert.Equal(ay, n - z);
                double mx = x + (x2 - x) * t, mz = z + (z2 - z) * t;
                seen.Add((w + (float)mx, n - (float)mz, w + mx, n - mz));
            }
            Assert.Single(seen);
        }
    }
}
