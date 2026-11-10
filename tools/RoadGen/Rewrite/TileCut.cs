namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// A block's finished records cut back into its real tiles (#1660, "junctions should be generated the same, no matter where the
/// tile edges are"). The stage plans a block in one frame (<see cref="BlockFrame"/>) over whole lines; here, and only here, the
/// tiles come back:
/// <list type="bullet">
/// <item>Lines are cut where they cross a tile edge: segments (heights and side shifts carried, a yield only at the line's own
/// ends), the paint along them (a dashed line keeping its phase), transitions and the paint along them, linear props.</item>
/// <item>Records under <see cref="BlockFrame.Lines"/> that stand at a point (paint as geometry, signs, area props, supports) go
/// to the tile they stand in; a wire to the tile of its middle.</item>
/// <item>Records under a real tile (a junction's, by its home: its record, signals, approaches, paths, corners, arms' paint)
/// stay there whole, as a junction's core always did.</item>
/// </list>
/// Every record ends in its tile's own frame. Two blocks that share a line cut it at the same points: both planned the same
/// whole line.
/// </summary>
public static class TileCut
{
    public sealed class Stats
    {
        public int Segments, Cut, Pieces, PaintCut, TransitionsCut, PropsCut, Orphans;
    }

    /// <summary>A piece of a cut segment: its tile, the new segment (block frame), its stations on the whole one.</summary>
    private readonly record struct Piece(TileId Tile, RoadSegment Segment, double From, double To);

    /// <summary>The block's records by key (real tiles and <see cref="BlockFrame.Lines"/>) into real tiles; only those <paramref name="keep"/> holds.</summary>
    public static Dictionary<TileId, RoadTile> Cut(IReadOnlyDictionary<TileId, RoadTile> sources, BlockFrame frame, Func<TileId, bool> keep, Stats stats)
    {
        var real = frame.Real;
        var outTiles = new Dictionary<TileId, RoadTile>();
        RoadTile? Into(TileId t)
        {
            if (!keep(t)) return null;
            if (!outTiles.TryGetValue(t, out var tile))
                outTiles[t] = tile = new RoadTile { Id = t, Segments = new(), Flags = RoadTileFlags.Network | RoadTileFlags.Bikes };
            return tile;
        }
        (double X, double Y) PlaneOf(float x, float z) => (frame.West(BlockFrame.Lines) + x, frame.North(BlockFrame.Lines) - z);
        TileId At(float x, float z)
        {
            var (px, py) = PlaneOf(x, z);
            return real.TileAt(px, py);
        }
        TileId Centroid(float[] v, int stride = 3)
        {
            double x = 0, z = 0;
            int n = 0;
            for (int k = 0; k + 2 < v.Length; k += stride)
            {
                if (float.IsNaN(v[k])) continue;
                x += v[k];
                z += v[k + 2];
                n++;
            }
            return n == 0 ? BlockFrame.Lines : At((float)(x / n), (float)(z / n));
        }

        // 1. segments
        var piecesOf = new Dictionary<RoadSegment, List<Piece>>(ReferenceEqualityComparer.Instance);
        foreach (var (_, src) in sources)
            foreach (var seg in src.Segments)
            {
                if (piecesOf.ContainsKey(seg)) continue;
                var pieces = CutSegment(seg, frame);
                piecesOf[seg] = pieces;
                stats.Segments++;
                if (pieces.Count > 1) stats.Cut++;
                stats.Pieces += pieces.Count;
                foreach (var p in pieces) Into(p.Tile)?.Segments.Add(p.Segment);
            }

        // 2. transitions, cut with their segments
        var transitionPieces = new Dictionary<RoadTransition, List<(TileId Tile, RoadTransition T, double From, double To)>>(ReferenceEqualityComparer.Instance);
        foreach (var (_, src) in sources)
            foreach (var t in src.Transitions)
            {
                if (!piecesOf.TryGetValue(t.Segment, out var pieces)) { stats.Orphans++; continue; }
                var list = new List<(TileId, RoadTransition, double, double)>();
                foreach (var p in pieces) list.Add((p.Tile, Slice(t, p), p.From, p.To));
                if (pieces.Count > 1) stats.TransitionsCut++;
                transitionPieces[t] = list;
                foreach (var (tile, piece, _, _) in list) Into(tile)?.Transitions.Add(piece);
            }

        // 3. everything else: lines' records by where they stand, a real tile's where they are
        var along = new List<(TileId Tile, RoadPaint Paint)>();   // paint along a segment or transition: laid on its pieces last
        foreach (var (key, src) in sources)
        {
            bool lines = key == BlockFrame.Lines;
            foreach (var p in src.Paint)
            {
                if (p.Segment is not null || p.Transition is not null) { along.Add((key, p)); continue; }
                Into(lines ? Centroid(p.Vertices) : key)?.Paint.Add(p);
            }
            foreach (var j in src.Junctions) Into(lines ? Centroid(j.Vertices) : key)?.Junctions.Add(j);
            foreach (var pp in src.PointProps) Into(lines ? At(pp.X, pp.Z) : key)?.PointProps.Add(pp);
            foreach (var a in src.AreaProps) Into(lines ? Centroid(a.Vertices) : key)?.AreaProps.Add(a);
            foreach (var s in src.Signals) Into(lines ? At(s.X, s.Z) : key)?.Signals.Add(s);
            foreach (var a in src.Approaches) Into(lines ? At(a.X, a.Z) : key)?.Approaches.Add(a);
            foreach (var b in src.Parking) Into(lines ? At(b.X, b.Z) : key)?.Parking.Add(b);
            foreach (var jp in src.JunctionPaths) Into(lines ? Centroid(jp.Points) : key)?.JunctionPaths.Add(jp);
            foreach (var c in src.CatenarySupports) Into(lines ? At(c.AX, c.AZ) : key)?.CatenarySupports.Add(c);
            foreach (var w in src.CatenaryWires) Into(lines ? Centroid(w.Points) : key)?.CatenaryWires.Add(w);
            foreach (var l in src.LinearProps)
            {
                if (!lines) { Into(key)?.LinearProps.Add(l); continue; }
                var cut = CutLinear(l, frame);
                if (cut.Count > 1) stats.PropsCut++;
                foreach (var (tile, piece) in cut) Into(tile)?.LinearProps.Add(piece);
            }
        }

        // 4. every tile into its own frame (a translation: every tile frame is one of the plane), then the paint along its pieces
        foreach (var (id, tile) in outTiles)
        {
            LeafRecords.Map(tile, new Translate(frame.West(BlockFrame.Lines) - real.West(id), real.North(id) - frame.North(BlockFrame.Lines)));
            if (tile.Segments.Any(x => x.Attributes.Has(RoadAttrFlags.Osm))) tile.Flags |= RoadTileFlags.Osm;
        }
        foreach (var (key, p) in along)
        {
            if (p.Transition is { } t)
            {
                if (!transitionPieces.TryGetValue(t, out var tps)) { stats.Orphans++; continue; }
                foreach (var (tile, piece, from, to) in tps)
                    if (outTiles.TryGetValue(tile, out var into))
                        foreach (var q in Slice(p, from, to, len => RoadPaint.AlongTransition(piece, p.Boundary, p.Type, p.Rgba, p.Width, p.Dash, p.Gap, p.Offset, len.From, len.To),
                                     (f, e) => RoadPaint.AlongTransition(piece, p.Boundary, p.Type, p.Rgba, p.Width, 0, 0, p.Offset, f, e), p.From, p.To))
                            into.Paint.Add(q);
                if (tps.Count > 1) stats.PaintCut++;
                continue;
            }
            if (!piecesOf.TryGetValue(p.Segment!, out var pieces)) { stats.Orphans++; continue; }
            // stations along the offset line: each piece's own offset line, one after another
            double s = 0;
            foreach (var piece in pieces)
            {
                double len = RoadPaintGeometry.Length(RoadPaintGeometry.Offset(piece.Segment, p.Offset));
                if (outTiles.TryGetValue(piece.Tile, out var into))
                    foreach (var q in Slice(p, s, s + len, r => RoadPaint.AlongSegment(piece.Segment, p.Type, p.Rgba, p.Width, p.Dash, p.Gap, p.Offset, r.From, r.To, p.Variant),
                                 (f, e) => RoadPaint.AlongSegment(piece.Segment, p.Type, p.Rgba, p.Width, 0, 0, p.Offset, f, e, p.Variant), p.From, p.To))
                        if (q.Vertices.Length >= 6) into.Paint.Add(q);
                s += len;
            }
            if (pieces.Count > 1) stats.PaintCut++;
        }
        return outTiles;
    }

    /// <summary>
    /// A line from <paramref name="from"/> to <paramref name="to"/> (stations on the whole) laid on a piece covering
    /// [<paramref name="p0"/>, <paramref name="p1"/>]: its share, in the piece's stations; a dashed line keeps the pattern it
    /// has on the whole (a dash the cut halves goes on as a solid lead, as at a seam: <c>PaintEmitter.AddDashed</c>).
    /// </summary>
    private static IEnumerable<RoadPaint> Slice(RoadPaint p, double p0, double p1, Func<(double From, double To), RoadPaint> dashed,
        Func<double, double, RoadPaint> solid, double from, double to)
    {
        double a = Math.Max(from, p0), b = Math.Min(double.IsPositiveInfinity(to) ? double.MaxValue : to, p1);
        if (b - a < 0.01) yield break;
        bool toEnd = b >= p1 - 1e-6;
        double End(double v) => toEnd && v >= p1 - 1e-6 ? double.PositiveInfinity : v - p0;
        double period = p.Dash + p.Gap;
        if (p.Dash <= 0 || period <= 0 || a <= from + 1e-6)
        {
            yield return dashed((a - p0, End(b)));
            yield break;
        }
        double phase = (a - from) % period;
        if (phase < p.Dash - 1e-6)
        {
            double lead = Math.Min(a + (p.Dash - phase), b);
            yield return solid(a - p0, End(lead));
            a = a + (period - phase);
        }
        else a = a + (period - phase);
        if (b - a > 0.01) yield return dashed((a - p0, End(b)));
    }

    /// <summary>A transition's share over a piece of its segment: its stations within the piece, widths and centre carried.</summary>
    private static RoadTransition Slice(RoadTransition t, Piece p)
    {
        var stations = new List<float> { 0 };
        foreach (float s in t.Stations)
            if (s > p.From + 1e-3 && s < p.To - 1e-3) stations.Add((float)(s - p.From));
        stations.Add((float)(p.To - p.From));
        float[] Values(Func<double, float> at) => stations.Select(s => at(s + p.From)).ToArray();
        return new RoadTransition
        {
            Segment = p.Segment, Stations = stations.ToArray(), Centre = Values(s => t.CentreAt(s)), Anchor = t.Anchor,
            Strips = t.Strips.Select((x, k) => new RoadTransitionStrip { Kind = x.Kind, Id = x.Id, Travel = x.Travel, Moves = x.Moves, Flags = x.Flags, Widths = Values(s => t.WidthAt(k, s)) }).ToList(),
        };
    }

    /// <summary>
    /// The parameters along a straight stretch a→b (plane) where the tile changes, with the tiles either side: found by halving
    /// to a micrometre. A stretch that leaves a tile and comes back into it (a corner clipped) is not seen: stretches are a few
    /// metres.
    /// </summary>
    private static void Crossings(IUnits real, double ax, double ay, double bx, double by, double t0, double t1, TileId ta, TileId tb,
        double length, List<(double T, TileId From, TileId To)> into)
    {
        if (ta == tb) return;
        if ((t1 - t0) * length < 1e-6)
        {
            into.Add((0.5 * (t0 + t1), ta, tb));
            return;
        }
        double tm = 0.5 * (t0 + t1);
        var m = real.TileAt(ax + (bx - ax) * tm, ay + (by - ay) * tm);
        Crossings(real, ax, ay, bx, by, t0, tm, ta, m, length, into);
        Crossings(real, ax, ay, bx, by, tm, t1, m, tb, length, into);
    }

    /// <summary>A segment of the block's frame cut at real tile edges into pieces, each in the block's frame still.</summary>
    private static List<Piece> CutSegment(RoadSegment seg, BlockFrame frame)
    {
        var real = frame.Real;
        double west = frame.West(BlockFrame.Lines), north = frame.North(BlockFrame.Lines);
        var p = seg.Points;
        int n = seg.PointCount;
        if (n < 2) return [new Piece(real.TileAt(west + (n > 0 ? p[0] : 0), north - (n > 0 ? p[2] : 0)), seg, 0, 0)];
        // each stretch's tile just past its start and just before its end (a vertex on an edge belongs to both sides)
        var verts = new List<(float X, float Y, float Z, double S)>();
        var tiles = new List<TileId>();   // the tile of the stretch after each vertex
        double s = 0;
        verts.Add((p[0], p[1], p[2], 0));
        for (int i = 1; i < n; i++)
        {
            double ax = west + p[i * 3 - 3], ay = north - p[i * 3 - 1], bx = west + p[i * 3], by = north - p[i * 3 + 2];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            double e = len < 1e-9 ? 0.5 : Math.Min(0.5, 1e-5 / len);
            var ta = real.TileAt(ax + (bx - ax) * e, ay + (by - ay) * e);
            var tb = real.TileAt(bx - (bx - ax) * e, by - (by - ay) * e);
            var cuts = new List<(double T, TileId From, TileId To)>();
            Crossings(real, ax, ay, bx, by, e, 1 - e, ta, tb, len, cuts);
            tiles.Add(ta);
            foreach (var (t, _, to) in cuts)
            {
                verts.Add((p[i * 3 - 3] + (p[i * 3] - p[i * 3 - 3]) * (float)t, p[i * 3 - 2] + (p[i * 3 + 1] - p[i * 3 - 2]) * (float)t,
                    p[i * 3 - 1] + (p[i * 3 + 2] - p[i * 3 - 1]) * (float)t, s + len * t));
                tiles.Add(to);
            }
            s += len;
            verts.Add((p[i * 3], p[i * 3 + 1], p[i * 3 + 2], s));
        }
        double total = s;
        // runs of stretches in one tile
        var result = new List<Piece>();
        int start = 0;
        for (int k = 1; k <= tiles.Count; k++)
        {
            if (k < tiles.Count && tiles[k] == tiles[start]) continue;
            var run = verts.GetRange(start, k - start + 1);
            result.Add(new Piece(tiles[start], PieceOf(seg, run, total), run[0].S, run[^1].S));
            start = k;
        }
        if (result.Count == 1) return [result[0] with { Segment = seg }];
        // a piece shorter than a centimetre (a stretch grazing a corner) joins its neighbour
        for (int k = result.Count - 1; k >= 0 && result.Count > 1; k--)
        {
            if (result[k].To - result[k].From >= 0.01) continue;
            int other = k > 0 ? k - 1 : k + 1;
            var (a, b) = k > 0 ? (result[other], result[k]) : (result[k], result[other]);
            var merged = verts.Where(v => v.S >= a.From - 1e-9 && v.S <= b.To + 1e-9).ToList();
            var tile = result[other].Tile;
            result[Math.Min(k, other)] = new Piece(tile, PieceOf(seg, merged, total), a.From, b.To);
            result.RemoveAt(Math.Max(k, other));
        }
        return result;
    }

    /// <summary>The segment's record over a run of its points (stations <c>S</c> on the whole of length <paramref name="total"/>).</summary>
    private static RoadSegment PieceOf(RoadSegment seg, List<(float X, float Y, float Z, double S)> run, double total)
    {
        var pts = new float[run.Count * 3];
        for (int i = 0; i < run.Count; i++) (pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]) = (run[i].X, run[i].Y, run[i].Z);
        double f0 = total > 1e-9 ? run[0].S / total : 0, f1 = total > 1e-9 ? run[^1].S / total : 1;
        var at = seg.Attributes;
        RoadSide Side(RoadSide side) => side.ShiftStartCm == 0 && side.ShiftEndCm == 0 ? side : side with
        {
            ShiftStartCm = (ushort)Math.Round(side.ShiftAt(f0) * 100), ShiftEndCm = (ushort)Math.Round(side.ShiftAt(f1) * 100),
        };
        var flags = at.Flags;
        if (f0 > 1e-9) flags &= ~RoadAttrFlags.YieldAtStart;
        if (f1 < 1 - 1e-9) flags &= ~RoadAttrFlags.YieldAtEnd;
        return new RoadSegment
        {
            Class = seg.Class, Surface = seg.Surface, Flags = seg.Flags, Width = seg.Width, Points = pts,
            Attributes = at with { Flags = flags, Left = Side(at.Left), Right = Side(at.Right) },
        };
    }

    /// <summary>A linear prop (x, foot y, z, height per point) cut at real tile edges.</summary>
    private static List<(TileId Tile, RoadLinearProp Prop)> CutLinear(RoadLinearProp l, BlockFrame frame)
    {
        var real = frame.Real;
        double west = frame.West(BlockFrame.Lines), north = frame.North(BlockFrame.Lines);
        var p = l.Points;
        int n = l.PointCount;
        TileId At(int i) => real.TileAt(west + p[i * 4], north - p[i * 4 + 2]);
        if (n < 2) return [(n > 0 ? At(0) : BlockFrame.Lines, l)];
        var verts = new List<float[]> { new[] { p[0], p[1], p[2], p[3] } };
        var tiles = new List<TileId>();
        for (int i = 1; i < n; i++)
        {
            double ax = west + p[i * 4 - 4], ay = north - p[i * 4 - 2], bx = west + p[i * 4], by = north - p[i * 4 + 2];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            double e = len < 1e-9 ? 0.5 : Math.Min(0.5, 1e-5 / len);
            var ta = real.TileAt(ax + (bx - ax) * e, ay + (by - ay) * e);
            var tb = real.TileAt(bx - (bx - ax) * e, by - (by - ay) * e);
            var cuts = new List<(double T, TileId From, TileId To)>();
            Crossings(real, ax, ay, bx, by, e, 1 - e, ta, tb, len, cuts);
            tiles.Add(ta);
            foreach (var (t, _, to) in cuts)
            {
                var v = new float[4];
                for (int c = 0; c < 4; c++) v[c] = p[i * 4 - 4 + c] + (p[i * 4 + c] - p[i * 4 - 4 + c]) * (float)t;
                verts.Add(v);
                tiles.Add(to);
            }
            verts.Add(new[] { p[i * 4], p[i * 4 + 1], p[i * 4 + 2], p[i * 4 + 3] });
        }
        var result = new List<(TileId, RoadLinearProp)>();
        int start = 0;
        for (int k = 1; k <= tiles.Count; k++)
        {
            if (k < tiles.Count && tiles[k] == tiles[start]) continue;
            var pts = verts.GetRange(start, k - start + 1).SelectMany(v => v).ToArray();
            result.Add((tiles[start], new RoadLinearProp { Type = l.Type, Variant = l.Variant, Flags = l.Flags, Thickness = l.Thickness, Param = l.Param, Points = pts }));
            start = k;
        }
        return result.Count == 1 ? [(result[0].Item1, l)] : result;
    }

    /// <summary>A translation of the plane: x and z shifted, heights and headings kept.</summary>
    private sealed class Translate(double dx, double dz) : LeafRecords.IMap
    {
        public (float X, float Y, float Z) Point(float x, float y, float z) => ((float)(x + dx), y, (float)(z + dz));
        public float Godot(float heading, float x, float y, float z) => heading;
        public float Math(float angle, float x, float y, float z) => angle;
    }
}
