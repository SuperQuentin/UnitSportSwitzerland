namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Buffers.Binary;
using UnitSport.Terrain.Format;

/// <summary>
/// <c>--format-check</c>: the .road codec against itself. A v3 tile with every layer round-trips
/// byte for byte; the same bytes cut back to a v2 and a v1 file still decode (no attributes, no
/// layers); an unknown section is skipped. Non-zero exit on any failure.
/// </summary>
public static class FormatCheck
{
    public static bool Run(Action<string> log)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            log($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }

        var tile = Sample();
        byte[] v3 = Encode(tile);
        var back = Decode(v3);
        Check(Encode(back).AsSpan().SequenceEqual(v3), "v3 round trip is byte-identical");
        Check(back.Version == 3 && back.Flags == tile.Flags, "v3 header version and flags");
        Check(back.Segments.Select(s => s.Attributes).SequenceEqual(tile.Segments.Select(s => s.Attributes)),
            "per-segment attributes");
        Check(back.Paint.Count == 2 && back.Paint[1].Indices.SequenceEqual(tile.Paint[1].Indices)
              && back.Paint[0].Dash == 3f, "paint layer");
        Check(back.PointProps.SequenceEqual(tile.PointProps), "point props");
        Check(back.LinearProps.Count == 1 && back.LinearProps[0].Points.SequenceEqual(tile.LinearProps[0].Points),
            "linear props");
        Check(back.AreaProps.Count == 1 && back.AreaProps[0].Height == 0.12f, "area props");

        // v2: the same bytes up to the end of the junctions, version 2, header flags zero
        int v2Length = RoadFormat.HeaderSize
            + tile.Segments.Sum(s => 12 + s.Points.Length * 4)
            + tile.Junctions.Sum(j => 8 + j.Vertices.Length * 4 + j.Indices.Length * 2);
        var v2 = v3[..v2Length];
        BinaryPrimitives.WriteUInt16LittleEndian(v2.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(v2.AsSpan(6), 0);
        var old = Decode(v2);
        Check(old.Version == 2 && old.Segments.Count == tile.Segments.Count && old.Junctions.Count == 1
              && old.Segments.All(s => s.Attributes == default) && old.Paint.Count == 0,
            "v2 file decodes: geometry kept, attributes default, no layers");
        Check(old.Segments[0].Points.SequenceEqual(tile.Segments[0].Points), "v2 geometry unchanged");

        // v1: no junctions, count word zero
        int v1Length = RoadFormat.HeaderSize + tile.Segments.Sum(s => 12 + s.Points.Length * 4);
        var v1 = v3[..v1Length];
        BinaryPrimitives.WriteUInt16LittleEndian(v1.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(v1.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(v1.AsSpan(20), 0);
        var oldest = Decode(v1);
        Check(oldest.Version == 1 && oldest.Junctions.Count == 0 && oldest.Segments.Count == tile.Segments.Count,
            "v1 file decodes");

        // a section a newer writer added: tag unknown here, skipped by its length
        var withUnknown = new byte[v3.Length + 12];
        v3.CopyTo(withUnknown, 0);
        int countAt = v2Length;
        BinaryPrimitives.WriteUInt32LittleEndian(withUnknown.AsSpan(countAt),
            BinaryPrimitives.ReadUInt32LittleEndian(v3.AsSpan(countAt)) + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(withUnknown.AsSpan(v3.Length), 0x5A5A5A5A);
        BinaryPrimitives.WriteUInt32LittleEndian(withUnknown.AsSpan(v3.Length + 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(withUnknown.AsSpan(v3.Length + 8), 0xDEADBEEF);
        Check(Encode(Decode(withUnknown)).AsSpan().SequenceEqual(v3), "unknown section skipped");

        // paint (#116): dash runs, and a centre dash that keeps its phase across a tile seam
        var runs = RoadPaintGeometry.Runs(new RoadPaint
        {
            Shape = PaintShape.Polyline, Dash = 6, Gap = 3, Vertices = [0, 0, 0, 30, 0, 0],
        });
        Check(runs.Count == 4 && runs[0][3] == 6 && runs[1][0] == 9 && runs[3][0] == 27 && runs[3][3] == 30,
            "paint dash runs");
        Check(PaintSeamIsContinuous(), "paint dashes continue across a tile seam, no stub at a dead end");
        Check(RailCrossingIsEmbedded(), "level crossing: rail embedded at road height, blended back, grooves, road paint cut");

        log(failures == 0 ? "format check passed" : $"format check: {failures} failure(s)");
        return failures == 0;
    }

    /// <summary>
    /// A 200 m two-way road cut at a seam after 100 m: west half at x 900..1000 of its tile
    /// (station 0), east half at x 0..100 of the next (station 100). Painted where
    /// (station mod 9) &lt; 6, except the last dash stub before the dead end.
    /// </summary>
    private static bool PaintSeamIsContinuous()
    {
        RoadSegment Seg(float x0) => new()
        {
            Class = RoadClass.Road, Surface = RoadSurface.Paved, Width = 6,
            Points = [x0, 0, 500, x0 + 50, 0, 500, x0 + 100, 0, 500],
        };
        var west = new List<RoadPaint>();
        var east = new List<RoadPaint>();
        Meshing.PaintEmitter.Emit(Seg(900), 0, west);
        Meshing.PaintEmitter.Emit(Seg(0), 100, east);
        var painted = west.SelectMany(RoadPaintGeometry.Runs).Select(r => (r[0] - 900, r[^3] - 900))
            .Concat(east.SelectMany(RoadPaintGeometry.Runs).Select(r => (r[0] + 100, r[^3] + 100))).ToList();
        for (double u = 0.25; u < 200; u += 0.5)
        {
            bool expected = u % 9 < 6 && u < 198;
            if (painted.Any(r => u >= r.Item1 && u <= r.Item2) != expected) return false;
        }
        return true;
    }

    /// <summary>
    /// #124: a 6 m road at 10 m crossed square by a rail at 9 m. The rail piece inside the road
    /// (6 m + 1 m each side) is embedded at 10 m, the rail is back at 9 m 8 m further out, two
    /// grooves are painted and the road's centre dash leaves the track zone free.
    /// </summary>
    private static bool RailCrossingIsEmbedded()
    {
        var id = new TileId(2600, 1200);
        var road = new RoadSegment
        {
            Class = RoadClass.Road, Surface = RoadSurface.Paved, Width = 6,
            Points = [400, 10, 500, 600, 10, 500],
        };
        var rail = new RoadSegment
        {
            Class = RoadClass.Railway, Width = 4.6f, Points = [500, 9, 400, 500, 9, 600],
        };
        var tiles = new Dictionary<TileId, RoadTile> { [id] = new() { Id = id, Segments = [road, rail] } };
        var overlap = new Network.RailRoadOverlap(tiles, new Network.RailRoadOverlap.Tally());
        var plan = new List<Geometry.Vec2> { new(id.MinE + 500, id.MaxN - 400), new(id.MinE + 500, id.MaxN - 600) };
        var pieces = overlap.Split(plan, _ => 9f, rail, count: true);
        if (pieces is null || pieces.Count(p => p.Embedded) != 1) return false;
        var inside = pieces.Single(p => p.Embedded);
        double length = Geometry.Polyline.Length(inside.Plan);
        if (Math.Abs(length - 8) > 0.6 || inside.Height.Any(h => Math.Abs(h - 10) > 1e-3)) return false;
        if (pieces[0].Height[0] != 9f || pieces[^1].Height[^1] != 9f) return false;
        // continuous: each piece starts where the last one ended, at the same height
        for (int i = 1; i < pieces.Count; i++)
            if (pieces[i].Plan[0] != pieces[i - 1].Plan[^1] || pieces[i].Height[0] != pieces[i - 1].Height[^1]) return false;

        var paint = new List<RoadPaint>();
        Meshing.PaintEmitter.Emit(road, 0, paint);
        overlap.EmitGrooves(inside, rail, id, paint);
        if (paint.Count(p => p.Type == PaintType.RailGroove) != 2) return false;
        overlap.ClearTrackZones(paint, id);
        return paint.Where(p => p.Type != PaintType.RailGroove)
            .All(p => Enumerable.Range(0, p.Vertices.Length / 3).All(i => Math.Abs(p.Vertices[i * 3] - 500) > 1.2f))
            && paint.Any(p => p.Type == PaintType.WhiteDashed);
    }

    private static byte[] Encode(RoadTile tile)
    {
        using var ms = new MemoryStream();
        RoadCodec.Encode(tile, ms);
        return ms.ToArray();
    }

    private static RoadTile Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var tile = RoadCodec.Decode(ms);
        if (ms.Position != bytes.Length) throw new InvalidDataException("trailing bytes not read");
        return tile;
    }

    private static RoadTile Sample() => new()
    {
        Id = new TileId(2583, 1113),
        Flags = RoadTileFlags.Network | RoadTileFlags.Osm,
        Segments = new()
        {
            new RoadSegment
            {
                Class = RoadClass.Road, Surface = RoadSurface.Paved, Flags = RoadFlags.Divided, Width = 3.3f,
                Points = [1, 480, 2, 50, 481, 3, 90, 482, 9],
                Attributes = new RoadAttributes(RoadAttrFlags.Urban | RoadAttrFlags.Osm, OneWay: -1, Layer: 1,
                    LanesForward: 0, LanesBackward: 2, Priority: 0x28, WidthCm: 650,
                    Left: new RoadSide(15, BikeKind.Lane, 15, 12, 5), Right: new RoadSide(20)),
            },
            new RoadSegment { Class = RoadClass.Watercourse, Width = 2.5f, Points = [0, 470, 0, 10, 470, 10] },
        },
        Junctions =
        {
            new RoadJunction { Class = RoadClass.Road, Layer = 0, Vertices = [0, 1, 0, 1, 1, 0, 0, 1, 1], Indices = [0, 1, 2] },
        },
        Paint =
        {
            new RoadPaint { Shape = PaintShape.Polyline, Type = PaintType.WhiteDashed, Rgba = 0xF0F0F0FF,
                Width = 0.15f, Dash = 3f, Gap = 6f, Vertices = [1, 480, 2, 50, 481, 3] },
            new RoadPaint { Shape = PaintShape.Triangles, Type = PaintType.Arrow,
                Variant = (byte)(PaintArrow.Left | PaintArrow.Straight), Rgba = 0xFFFFFFFF,
                Vertices = [0, 1, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1], Indices = [0, 1, 2, 2, 1, 3] },
        },
        PointProps = { new RoadPointProp(PointPropType.YieldSign, 0, PropFlags.Solid, 10, 480, 12, 1.5f, 2.2f) },
        LinearProps =
        {
            new RoadLinearProp { Type = LinearPropType.Guardrail, Flags = PropFlags.Solid, Thickness = 0.3f,
                Param = 4f, Points = [0, 480, 0, 0.75f, 10, 481, 0, 0.75f] },
        },
        AreaProps =
        {
            new RoadAreaProp { Type = AreaPropType.Island, Flags = PropFlags.Solid, Height = 0.12f,
                Vertices = [0, 1, 0, 1, 1, 0, 0, 1, 1], Indices = [0, 1, 2] },
        },
    };
}
