using System.Text;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Binary encode/decode of .road tile files. Shared by the preprocessor and the game.
///
/// Layout (little-endian):
///   header 24 B: magic u32, version u16, flags u16 (<see cref="RoadTileFlags"/>, 0 before v3),
///                tileE i32, tileN i32, segmentCount u32, junctionCount u32 (0 in v1)
///   per segment: class u8, surface u8, flags u16, width f32, pointCount u16, pad u16,
///                then pointCount * 3 f32 (x, y=altitude, z) local to the tile NW corner
///   v2+, after the segments —
///   per junction: class u8, layer i8, vertexCount u16, indexCount u16, pad u16,
///                 then vertexCount * 3 f32, then indexCount u16
///   v3, after the junctions —
///   sectionCount u32, then per section: tag u32 (FourCC), byteLength u32, payload.
///   A reader skips tags it does not know, so adding a section needs no version bump.
///   Sections and their payloads: docs/notes/tools/road-format-v3.md.
/// </summary>
public static class RoadCodec
{
    /// <summary>Pre-#116b payloads: still read, written only for the format self-check.</summary>
    public static readonly uint TagAttributes = FourCC("ATTR");
    public static readonly uint TagPaint = FourCC("PANT");
    /// <summary>#116b: distinct attribute records once, a varint index per segment.</summary>
    public static readonly uint TagAttributePalette = FourCC("ATR2");
    /// <summary>#116b: a style palette, lines along a segment by reference, other geometry quantised.</summary>
    public static readonly uint TagPaintCompact = FourCC("PNT2");
    public static readonly uint TagPointProps = FourCC("PPRP");
    public static readonly uint TagLinearProps = FourCC("LPRP");
    public static readonly uint TagAreaProps = FourCC("APRP");
    /// <summary>#349: signalised junctions and their fixed-time plans (<see cref="RoadSignal"/>).</summary>
    public static readonly uint TagSignals = FourCC("SGNL");
    /// <summary>#353: the lanes of each approach with a pocket or traffic lights (<see cref="RoadApproach"/>).</summary>
    public static readonly uint TagApproaches = FourCC("LANE");

    /// <summary>Marked parking bays (#499).</summary>
    public static readonly uint TagParking = FourCC("PARK");

    private static uint FourCC(string s) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(s));

    /// <param name="legacySections">Write the pre-#116b ATTR and PANT payloads (format self-check only).</param>
    public static void Encode(RoadTile tile, Stream output, bool legacySections = false)
    {
        using var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        w.Write(RoadFormat.Magic);
        w.Write(RoadFormat.Version);
        w.Write((ushort)tile.Flags);
        w.Write(tile.Id.E);
        w.Write(tile.Id.N);
        w.Write((uint)tile.Segments.Count);
        w.Write((uint)tile.Junctions.Count);

        foreach (var seg in tile.Segments)
        {
            w.Write((byte)seg.Class);
            w.Write((byte)seg.Surface);
            w.Write((ushort)seg.Flags);
            w.Write(seg.Width);
            w.Write(checked((ushort)seg.PointCount));
            w.Write((ushort)0);
            WriteFloats(w, seg.Points);
        }

        foreach (var junction in tile.Junctions)
        {
            w.Write((byte)junction.Class);
            w.Write(junction.Layer);
            w.Write(checked((ushort)junction.VertexCount));
            w.Write(checked((ushort)junction.Indices.Length));
            w.Write((ushort)0);
            WriteFloats(w, junction.Vertices);
            foreach (ushort i in junction.Indices) w.Write(i);
        }

        // ---- v3 sections. Attributes always; the layers only when they hold something.
        var sections = new List<(uint Tag, byte[] Payload)>();
        if (legacySections)
        {
            sections.Add((TagAttributes, Section(s => WriteLegacyAttributes(s, tile))));
            if (tile.Paint.Count > 0) sections.Add((TagPaint, Section(s => WriteLegacyPaint(s, tile))));
        }
        else
        {
            sections.Add((TagAttributePalette, Section(s => WriteAttributePalette(s, tile))));
            if (tile.Paint.Count > 0) sections.Add((TagPaintCompact, Section(s => WritePaint(s, tile))));
        }
        if (tile.PointProps.Count > 0)
            sections.Add((TagPointProps, Section(s =>
            {
                s.Write((uint)tile.PointProps.Count);
                s.Write((ushort)RoadPointProp.RecordSize);
                s.Write((ushort)0);
                foreach (var p in tile.PointProps)
                {
                    s.Write((byte)p.Type);
                    s.Write(p.Variant);
                    s.Write((ushort)p.Flags);
                    s.Write(p.X);
                    s.Write(p.Y);
                    s.Write(p.Z);
                    s.Write(p.Heading);
                    s.Write(p.Height);
                }
            })));
        if (tile.LinearProps.Count > 0)
            sections.Add((TagLinearProps, Section(s =>
            {
                s.Write((uint)tile.LinearProps.Count);
                foreach (var p in tile.LinearProps)
                {
                    s.Write((byte)p.Type);
                    s.Write(p.Variant);
                    s.Write((ushort)p.Flags);
                    s.Write(p.Thickness);
                    s.Write(p.Param);
                    s.Write(checked((ushort)p.PointCount));
                    s.Write((ushort)0);
                    WriteFloats(s, p.Points);
                }
            })));
        if (tile.AreaProps.Count > 0)
            sections.Add((TagAreaProps, Section(s =>
            {
                s.Write((uint)tile.AreaProps.Count);
                foreach (var p in tile.AreaProps)
                {
                    s.Write((byte)p.Type);
                    s.Write(p.Variant);
                    s.Write((ushort)p.Flags);
                    s.Write(p.Height);
                    s.Write(checked((ushort)(p.Vertices.Length / 3)));
                    s.Write(checked((ushort)p.Indices.Length));
                    WriteFloats(s, p.Vertices);
                    foreach (ushort i in p.Indices) s.Write(i);
                }
            })));

        if (tile.Signals.Count > 0)
            sections.Add((TagSignals, Section(s => RoadSignal.Write(s, tile.Signals))));
        if (tile.Approaches.Count > 0)
            sections.Add((TagApproaches, Section(s => RoadApproach.Write(s, tile.Approaches))));
        if (tile.Parking.Count > 0)
            sections.Add((TagParking, Section(s => ParkingBay.Write(s, tile.Parking))));

        w.Write((uint)sections.Count);
        foreach (var (tag, payload) in sections)
        {
            w.Write(tag);
            w.Write((uint)payload.Length);
            w.Write(payload);
        }
    }

    public static RoadTile Decode(Stream input)
    {
        using var r = new BinaryReader(input, Encoding.ASCII, leaveOpen: true);
        uint magic = r.ReadUInt32();
        if (magic != RoadFormat.Magic)
            throw new InvalidDataException($"Bad road magic 0x{magic:X8}");
        ushort version = r.ReadUInt16();
        if (version < RoadFormat.MinReadableVersion || version > RoadFormat.Version)
            throw new InvalidDataException($"Unsupported road version {version}");
        ushort headerFlags = r.ReadUInt16();   // reserved before v3
        var tileFlags = version >= 3 ? (RoadTileFlags)headerFlags : RoadTileFlags.None;

        var id = new TileId(r.ReadInt32(), r.ReadInt32());
        uint count = r.ReadUInt32();
        // v1 wrote a zero here, so a v1 file simply reports no junctions
        uint junctionCount = r.ReadUInt32();

        var raw = new List<(RoadClass, RoadSurface, RoadFlags, float, float[])>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var cls = (RoadClass)r.ReadByte();
            var surface = (RoadSurface)r.ReadByte();
            var flags = (RoadFlags)r.ReadUInt16();
            float width = r.ReadSingle();
            int pointCount = r.ReadUInt16();
            r.ReadUInt16();
            raw.Add((cls, surface, flags, width, ReadFloats(r, pointCount * 3)));
        }

        var junctions = new List<RoadJunction>((int)junctionCount);
        for (uint j = 0; j < junctionCount; j++)
        {
            var cls = (RoadClass)r.ReadByte();
            sbyte layer = r.ReadSByte();
            int vertexCount = r.ReadUInt16();
            int indexCount = r.ReadUInt16();
            r.ReadUInt16();
            var vertices = ReadFloats(r, vertexCount * 3);
            junctions.Add(new RoadJunction
            {
                Class = cls, Layer = layer, Vertices = vertices, Indices = ReadIndices(r, indexCount),
            });
        }

        RoadAttributes[]? attributes = null;
        List<(int Paint, int Segment)>? references = null;
        var tile = new RoadTile
        {
            Id = id, Segments = new List<RoadSegment>((int)count), Junctions = junctions,
            Version = version, Flags = tileFlags,
        };

        if (version >= 3)
        {
            uint sectionCount = r.ReadUInt32();
            for (uint s = 0; s < sectionCount; s++)
            {
                uint tag = r.ReadUInt32();
                long length = r.ReadUInt32();
                long end = input.Position + length;
                if (tag == TagAttributes)
                {
                    uint n = r.ReadUInt32();
                    int recordSize = r.ReadUInt16();
                    r.ReadUInt16();
                    if (n != count || recordSize < RoadAttributes.BaseRecordSize)
                        throw new InvalidDataException($"Bad attribute section ({n} records of {recordSize} B for {count} segments)");
                    attributes = new RoadAttributes[n];
                    for (int i = 0; i < n; i++) attributes[i] = ReadAttributes(r, recordSize);
                }
                else if (tag == TagAttributePalette)
                {
                    int distinct = r.Read7BitEncodedInt();
                    int recordSize = r.ReadByte();
                    if (recordSize < RoadAttributes.BaseRecordSize)
                        throw new InvalidDataException($"Bad attribute palette record size {recordSize}");
                    var palette = new RoadAttributes[distinct];
                    for (int i = 0; i < distinct; i++) palette[i] = ReadAttributes(r, recordSize);
                    attributes = new RoadAttributes[count];
                    for (int i = 0; i < count; i++) attributes[i] = palette[r.Read7BitEncodedInt()];
                }
                else if (tag == TagPaintCompact)
                {
                    references = ReadPaint(r, tile.Paint);
                }
                else if (tag == TagPaint)
                {
                    uint n = r.ReadUInt32();
                    for (uint i = 0; i < n; i++)
                    {
                        var shape = (PaintShape)r.ReadByte();
                        var type = (PaintType)r.ReadByte();
                        byte variant = r.ReadByte();
                        r.ReadByte();
                        uint rgba = r.ReadUInt32();
                        float width = r.ReadSingle(), dash = r.ReadSingle(), gap = r.ReadSingle();
                        int vertexCount = r.ReadUInt16(), indexCount = r.ReadUInt16();
                        tile.Paint.Add(new RoadPaint
                        {
                            Shape = shape, Type = type, Variant = variant, Rgba = rgba,
                            Width = width, Dash = dash, Gap = gap,
                            Vertices = ReadFloats(r, vertexCount * 3), Indices = ReadIndices(r, indexCount),
                        });
                    }
                }
                else if (tag == TagPointProps)
                {
                    uint n = r.ReadUInt32();
                    int recordSize = r.ReadUInt16();
                    r.ReadUInt16();
                    for (uint i = 0; i < n; i++)
                    {
                        tile.PointProps.Add(new RoadPointProp(
                            (PointPropType)r.ReadByte(), r.ReadByte(), (PropFlags)r.ReadUInt16(),
                            r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                        Skip(r, recordSize - RoadPointProp.RecordSize);
                    }
                }
                else if (tag == TagLinearProps)
                {
                    uint n = r.ReadUInt32();
                    for (uint i = 0; i < n; i++)
                    {
                        var type = (LinearPropType)r.ReadByte();
                        byte variant = r.ReadByte();
                        var flags = (PropFlags)r.ReadUInt16();
                        float thickness = r.ReadSingle(), param = r.ReadSingle();
                        int pointCount = r.ReadUInt16();
                        r.ReadUInt16();
                        tile.LinearProps.Add(new RoadLinearProp
                        {
                            Type = type, Variant = variant, Flags = flags, Thickness = thickness,
                            Param = param, Points = ReadFloats(r, pointCount * 4),
                        });
                    }
                }
                else if (tag == TagAreaProps)
                {
                    uint n = r.ReadUInt32();
                    for (uint i = 0; i < n; i++)
                    {
                        var type = (AreaPropType)r.ReadByte();
                        byte variant = r.ReadByte();
                        var flags = (PropFlags)r.ReadUInt16();
                        float height = r.ReadSingle();
                        int vertexCount = r.ReadUInt16(), indexCount = r.ReadUInt16();
                        tile.AreaProps.Add(new RoadAreaProp
                        {
                            Type = type, Variant = variant, Flags = flags, Height = height,
                            Vertices = ReadFloats(r, vertexCount * 3), Indices = ReadIndices(r, indexCount),
                        });
                    }
                }
                else if (tag == TagSignals)
                {
                    if (RoadSignal.Read(r) is { } signals) tile.Signals.AddRange(signals);
                    else input.Position = end;   // a newer section version: skipped whole
                }
                else if (tag == TagApproaches)
                {
                    if (RoadApproach.Read(r) is { } approaches) tile.Approaches.AddRange(approaches);
                    else input.Position = end;
                }
                else if (tag == TagParking)
                {
                    if (ParkingBay.Read(r) is { } bays) tile.Parking.AddRange(bays);
                    else input.Position = end;   // a newer section version: skipped whole
                }
                if (input.Position > end)
                    throw new InvalidDataException($"Road section 0x{tag:X8} overran its length");
                Skip(r, (int)(end - input.Position));   // unknown tag, or a longer future record
            }
        }

        for (int i = 0; i < raw.Count; i++)
        {
            var (cls, surface, flags, width, points) = raw[i];
            tile.Segments.Add(new RoadSegment
            {
                Class = cls, Surface = surface, Flags = flags, Width = width, Points = points,
                Attributes = attributes is null ? default
                    : (tile.Flags & RoadTileFlags.Bikes) != 0 ? attributes[i] : WithoutBikes(attributes[i]),
            });
        }
        if (references is not null)
            foreach (var (pi, si) in references)
            {
                if (si >= tile.Segments.Count) throw new InvalidDataException($"Paint references segment {si} of {tile.Segments.Count}");
                var p = tile.Paint[pi];
                tile.Paint[pi] = RoadPaint.AlongSegment(tile.Segments[si], p.Type, p.Rgba, p.Width, p.Dash, p.Gap, p.Offset, p.From, p.To, p.Variant);
            }
        return tile;
    }

    private static void WriteLegacyAttributes(BinaryWriter s, RoadTile tile)
    {
        s.Write((uint)tile.Segments.Count);
        s.Write((ushort)RoadAttributes.RecordSize);
        s.Write((ushort)0);
        foreach (var seg in tile.Segments) WriteAttributes(s, seg.Attributes);
    }

    private static void WriteLegacyPaint(BinaryWriter s, RoadTile tile)
    {
        s.Write((uint)tile.Paint.Count);
        foreach (var p in tile.Paint)
        {
            s.Write((byte)p.Shape);
            s.Write((byte)p.Type);
            s.Write(p.Variant);
            s.Write((byte)0);
            s.Write(p.Rgba);
            s.Write(p.Width);
            s.Write(p.Dash);
            s.Write(p.Gap);
            s.Write(checked((ushort)(p.Vertices.Length / 3)));
            s.Write(checked((ushort)p.Indices.Length));
            WriteFloats(s, p.Vertices);
            foreach (ushort i in p.Indices) s.Write(i);
        }
    }

    /// <summary>ATR2: <c>distinct varint, recordSize u8</c>, the distinct 24 B records, then a varint index per segment.</summary>
    private static void WriteAttributePalette(BinaryWriter s, RoadTile tile)
    {
        var index = new Dictionary<RoadAttributes, int>();
        var order = new List<RoadAttributes>();
        foreach (var seg in tile.Segments)
            if (index.TryAdd(seg.Attributes, order.Count)) order.Add(seg.Attributes);
        s.Write7BitEncodedInt(order.Count);
        s.Write((byte)RoadAttributes.RecordSize);
        foreach (var a in order) WriteAttributes(s, a);
        foreach (var seg in tile.Segments) s.Write7BitEncodedInt(index[seg.Attributes]);
    }

    private readonly record struct PaintStyle(PaintShape Shape, PaintType Type, byte Variant, uint Rgba, float Width, float Dash, float Gap);

    /// <summary>
    /// PNT2 (#116b). <c>styleCount varint</c>, styles of 19 B (<c>shape, type, variant u8, rgba u32,
    /// width, dash, gap f32</c>); <c>count varint</c>, then per primitive <c>style * 2 + kind</c>
    /// varint and: kind 1, a line along a segment: <c>segment varint, offset zigzag mm, from varint
    /// cm, to varint cm (0 = to the end)</c>, the decoder rebuilds its vertices
    /// (<see cref="RoadPaintGeometry.Along"/>); kind 0, geometry: <c>vertexCount, indexCount varint</c>,
    /// xyz as zigzag varint mm deltas from the previous vertex (the first from 0), indices varint.
    /// </summary>
    private static void WritePaint(BinaryWriter s, RoadTile tile)
    {
        var segments = new Dictionary<RoadSegment, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < tile.Segments.Count; i++) segments[tile.Segments[i]] = i;
        var styles = new Dictionary<PaintStyle, int>();
        var order = new List<PaintStyle>();
        int StyleOf(RoadPaint p)
        {
            var key = new PaintStyle(p.Shape, p.Type, p.Variant, p.Rgba, p.Width, p.Dash, p.Gap);
            if (!styles.TryGetValue(key, out int k)) { styles[key] = k = order.Count; order.Add(key); }
            return k;
        }
        var ids = tile.Paint.Select(StyleOf).ToArray();

        s.Write7BitEncodedInt(order.Count);
        foreach (var st in order)
        {
            s.Write((byte)st.Shape); s.Write((byte)st.Type); s.Write(st.Variant);
            s.Write(st.Rgba); s.Write(st.Width); s.Write(st.Dash); s.Write(st.Gap);
        }
        s.Write7BitEncodedInt(tile.Paint.Count);
        for (int k = 0; k < tile.Paint.Count; k++)
        {
            var p = tile.Paint[k];
            // a reference to a segment this tile does not hold (replaced since) falls back to geometry
            if (p.Segment is { } seg && p.Shape == PaintShape.Polyline && segments.TryGetValue(seg, out int si))
            {
                s.Write7BitEncodedInt(ids[k] * 2 + 1);
                s.Write7BitEncodedInt(si);
                WriteZigzag(s, (int)Math.Round(p.Offset * 1000.0));
                s.Write7BitEncodedInt((int)Math.Round(p.From * 100.0));
                s.Write7BitEncodedInt(float.IsPositiveInfinity(p.To) ? 0 : Math.Max(1, (int)Math.Round(p.To * 100.0)));
                continue;
            }
            s.Write7BitEncodedInt(ids[k] * 2);
            s.Write7BitEncodedInt(p.Vertices.Length / 3);
            s.Write7BitEncodedInt(p.Indices.Length);
            long x = 0, y = 0, z = 0;
            for (int i = 0; i + 2 < p.Vertices.Length; i += 3)
            {
                long qx = Mm(p.Vertices[i]), qy = Mm(p.Vertices[i + 1]), qz = Mm(p.Vertices[i + 2]);
                WriteZigzag(s, checked((int)(qx - x))); WriteZigzag(s, checked((int)(qy - y))); WriteZigzag(s, checked((int)(qz - z)));
                (x, y, z) = (qx, qy, qz);
            }
            foreach (ushort i in p.Indices) s.Write7BitEncodedInt(i);
        }
    }

    private static long Mm(float v) => (long)Math.Round(v * 1000.0);

    /// <summary>Reads PNT2 into <paramref name="into"/>; returns the lines along a segment, rebuilt once the segments are read.</summary>
    private static List<(int Paint, int Segment)> ReadPaint(BinaryReader r, List<RoadPaint> into)
    {
        int styleCount = r.Read7BitEncodedInt();
        var styles = new PaintStyle[styleCount];
        for (int i = 0; i < styleCount; i++)
            styles[i] = new PaintStyle((PaintShape)r.ReadByte(), (PaintType)r.ReadByte(), r.ReadByte(), r.ReadUInt32(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var references = new List<(int, int)>();
        int n = r.Read7BitEncodedInt();
        for (int k = 0; k < n; k++)
        {
            int head = r.Read7BitEncodedInt();
            var st = styles[head >> 1];
            if ((head & 1) != 0)
            {
                int segment = r.Read7BitEncodedInt();
                float offset = ReadZigzag(r) / 1000f;
                float from = (float)(r.Read7BitEncodedInt() / 100.0);
                int to = r.Read7BitEncodedInt();
                references.Add((into.Count, segment));
                into.Add(new RoadPaint
                {
                    Shape = st.Shape, Type = st.Type, Variant = st.Variant, Rgba = st.Rgba, Width = st.Width, Dash = st.Dash, Gap = st.Gap,
                    Offset = offset, From = from, To = to == 0 ? float.PositiveInfinity : (float)(to / 100.0), Vertices = [],
                });
                continue;
            }
            int vertexCount = r.Read7BitEncodedInt(), indexCount = r.Read7BitEncodedInt();
            var v = new float[vertexCount * 3];
            long x = 0, y = 0, z = 0;
            for (int i = 0; i < v.Length; i += 3)
            {
                x += ReadZigzag(r); y += ReadZigzag(r); z += ReadZigzag(r);
                v[i] = (float)(x / 1000.0); v[i + 1] = (float)(y / 1000.0); v[i + 2] = (float)(z / 1000.0);
            }
            var indices = new ushort[indexCount];
            for (int i = 0; i < indexCount; i++) indices[i] = checked((ushort)r.Read7BitEncodedInt());
            into.Add(new RoadPaint
            {
                Shape = st.Shape, Type = st.Type, Variant = st.Variant, Rgba = st.Rgba, Width = st.Width, Dash = st.Dash, Gap = st.Gap,
                Vertices = v, Indices = indices,
            });
        }
        return references;
    }

    private static void WriteZigzag(BinaryWriter w, int v) => w.Write7BitEncodedInt((v << 1) ^ (v >> 31));

    private static int ReadZigzag(BinaryReader r)
    {
        uint u = (uint)r.Read7BitEncodedInt();
        return (int)(u >> 1) ^ -(int)(u & 1);
    }

    private static void WriteAttributes(BinaryWriter w, RoadAttributes a)
    {
        w.Write((ushort)a.Flags);
        w.Write(a.OneWay);
        w.Write(a.Layer);
        w.Write(a.LanesForward);
        w.Write(a.LanesBackward);
        w.Write(a.Priority);
        w.Write((byte)0);
        w.Write(a.WidthCm);
        WriteSide(w, a.Left);
        WriteSide(w, a.Right);
        w.Write((ushort)0);
        // #120: each side's shift off the ribbon's edge, after the 24 B older readers stop at
        w.Write(a.Left.ShiftStartCm); w.Write(a.Left.ShiftEndCm);
        w.Write(a.Right.ShiftStartCm); w.Write(a.Right.ShiftEndCm);
    }

    private static void WriteSide(BinaryWriter w, RoadSide s)
    {
        w.Write(s.SidewalkDm);
        w.Write((byte)s.Bike);
        w.Write(s.BikeDm);
        w.Write(s.KerbCm);
        w.Write(s.VergeDm);
        w.Write(s.BufferDm);   // #120; a pad byte before, so older readers ignore it
    }

    /// <summary>One record of <paramref name="recordSize"/> bytes: the base 24, the side shifts when present, the rest skipped.</summary>
    private static RoadAttributes ReadAttributes(BinaryReader r, int recordSize)
    {
        var flags = (RoadAttrFlags)r.ReadUInt16();
        sbyte oneWay = r.ReadSByte(), layer = r.ReadSByte();
        byte fwd = r.ReadByte(), bwd = r.ReadByte(), priority = r.ReadByte();
        r.ReadByte();
        ushort width = r.ReadUInt16();
        var left = ReadSide(r);
        var right = ReadSide(r);
        r.ReadUInt16();
        int read = RoadAttributes.BaseRecordSize;
        if (recordSize >= RoadAttributes.RecordSize)
        {
            left = left with { ShiftStartCm = r.ReadUInt16(), ShiftEndCm = r.ReadUInt16() };
            right = right with { ShiftStartCm = r.ReadUInt16(), ShiftEndCm = r.ReadUInt16() };
            read = RoadAttributes.RecordSize;
        }
        Skip(r, recordSize - read);
        return new RoadAttributes(flags, oneWay, layer, fwd, bwd, priority, width, left, right);
    }

    /// <summary>A record from before #120: OSM's cycleway tags, never planned, are not drawn (<see cref="RoadTileFlags.Bikes"/>).</summary>
    private static RoadAttributes WithoutBikes(RoadAttributes a)
    {
        static RoadSide Clear(RoadSide s) => s.Bike == BikeKind.None && s.BikeDm == 0 ? s
            : s with { Bike = BikeKind.None, BikeDm = 0, VergeDm = 0, BufferDm = 0 };
        return a with { Left = Clear(a.Left), Right = Clear(a.Right) };
    }

    private static RoadSide ReadSide(BinaryReader r)
    {
        return new RoadSide(r.ReadByte(), (BikeKind)r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
    }

    private static byte[] Section(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    private static void WriteFloats(BinaryWriter w, float[] values)
    {
        foreach (float v in values) w.Write(v);
    }

    private static float[] ReadFloats(BinaryReader r, int n)
    {
        var bytes = r.ReadBytes(n * 4);
        if (bytes.Length != n * 4) throw new EndOfStreamException();
        var values = new float[n];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);   // every target is little-endian
        return values;
    }

    private static ushort[] ReadIndices(BinaryReader r, int n)
    {
        var bytes = r.ReadBytes(n * 2);
        if (bytes.Length != n * 2) throw new EndOfStreamException();
        var values = new ushort[n];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static void Skip(BinaryReader r, int n)
    {
        if (n > 0 && r.ReadBytes(n).Length != n) throw new EndOfStreamException();
    }
}
