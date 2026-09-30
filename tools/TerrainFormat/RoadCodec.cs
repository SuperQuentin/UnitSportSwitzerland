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
    public static readonly uint TagAttributes = FourCC("ATTR");
    public static readonly uint TagPaint = FourCC("PANT");
    public static readonly uint TagPointProps = FourCC("PPRP");
    public static readonly uint TagLinearProps = FourCC("LPRP");
    public static readonly uint TagAreaProps = FourCC("APRP");

    private static uint FourCC(string s) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(s));

    public static void Encode(RoadTile tile, Stream output)
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
        var sections = new List<(uint Tag, byte[] Payload)>
        {
            (TagAttributes, Section(s =>
            {
                s.Write((uint)tile.Segments.Count);
                s.Write((ushort)RoadAttributes.RecordSize);
                s.Write((ushort)0);
                foreach (var seg in tile.Segments) WriteAttributes(s, seg.Attributes);
            })),
        };
        if (tile.Paint.Count > 0)
            sections.Add((TagPaint, Section(s =>
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
            })));
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
                    if (n != count || recordSize < RoadAttributes.RecordSize)
                        throw new InvalidDataException($"Bad attribute section ({n} records of {recordSize} B for {count} segments)");
                    attributes = new RoadAttributes[n];
                    for (int i = 0; i < n; i++)
                    {
                        attributes[i] = ReadAttributes(r);
                        Skip(r, recordSize - RoadAttributes.RecordSize);
                    }
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
                Attributes = attributes?[i] ?? default,
            });
        }
        return tile;
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
    }

    private static void WriteSide(BinaryWriter w, RoadSide s)
    {
        w.Write(s.SidewalkDm);
        w.Write((byte)s.Bike);
        w.Write(s.BikeDm);
        w.Write(s.KerbCm);
        w.Write(s.VergeDm);
        w.Write((byte)0);
    }

    private static RoadAttributes ReadAttributes(BinaryReader r)
    {
        var flags = (RoadAttrFlags)r.ReadUInt16();
        sbyte oneWay = r.ReadSByte(), layer = r.ReadSByte();
        byte fwd = r.ReadByte(), bwd = r.ReadByte(), priority = r.ReadByte();
        r.ReadByte();
        ushort width = r.ReadUInt16();
        var left = ReadSide(r);
        var right = ReadSide(r);
        r.ReadUInt16();
        return new RoadAttributes(flags, oneWay, layer, fwd, bwd, priority, width, left, right);
    }

    private static RoadSide ReadSide(BinaryReader r)
    {
        var side = new RoadSide(r.ReadByte(), (BikeKind)r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
        r.ReadByte();
        return side;
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
