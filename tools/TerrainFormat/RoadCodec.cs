using System.Buffers.Binary;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Binary encode/decode of .road tile files. Shared by the preprocessor and the game.
///
/// Layout (little-endian):
///   header 24 B: magic u32, version u16, flags u16, tileE i32, tileN i32,
///                segmentCount u32, junctionCount u32 (0 in v1, where the word was reserved)
///   per segment: class u8, surface u8, flags u16, width f32, pointCount u16, pad u16,
///                then pointCount * 3 f32 (x, y=altitude, z) local to the tile NW corner
///   v2 only, after the segments —
///   per junction: class u8, layer i8, vertexCount u16, indexCount u16, pad u16,
///                 then vertexCount * 3 f32, then indexCount u16
/// </summary>
public static class RoadCodec
{
    public static void Encode(RoadTile tile, Stream output)
    {
        Span<byte> header = stackalloc byte[RoadFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], RoadFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], RoadFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], tile.Id.E);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], tile.Id.N);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)tile.Segments.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)tile.Junctions.Count);
        output.Write(header);

        Span<byte> rec = stackalloc byte[12];
        Span<byte> f = stackalloc byte[4];
        foreach (var seg in tile.Segments)
        {
            rec[0] = (byte)seg.Class;
            rec[1] = (byte)seg.Surface;
            BinaryPrimitives.WriteUInt16LittleEndian(rec[2..], (ushort)seg.Flags);
            BinaryPrimitives.WriteSingleLittleEndian(rec[4..], seg.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(rec[8..], (ushort)seg.PointCount);
            BinaryPrimitives.WriteUInt16LittleEndian(rec[10..], 0);
            output.Write(rec);

            foreach (float v in seg.Points)
            {
                BinaryPrimitives.WriteSingleLittleEndian(f, v);
                output.Write(f);
            }
        }

        Span<byte> jrec = stackalloc byte[8];
        Span<byte> u16 = stackalloc byte[2];
        foreach (var junction in tile.Junctions)
        {
            jrec[0] = (byte)junction.Class;
            jrec[1] = unchecked((byte)junction.Layer);
            BinaryPrimitives.WriteUInt16LittleEndian(jrec[2..], (ushort)junction.VertexCount);
            BinaryPrimitives.WriteUInt16LittleEndian(jrec[4..], (ushort)junction.Indices.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(jrec[6..], 0);
            output.Write(jrec);

            foreach (float v in junction.Vertices)
            {
                BinaryPrimitives.WriteSingleLittleEndian(f, v);
                output.Write(f);
            }
            foreach (ushort i in junction.Indices)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(u16, i);
                output.Write(u16);
            }
        }
    }

    public static RoadTile Decode(Stream input)
    {
        Span<byte> header = stackalloc byte[RoadFormat.HeaderSize];
        input.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != RoadFormat.Magic)
            throw new InvalidDataException($"Bad road magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version < RoadFormat.MinReadableVersion || version > RoadFormat.Version)
            throw new InvalidDataException($"Unsupported road version {version}");

        var id = new TileId(
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        // v1 wrote a zero here, so a v1 file simply reports no junctions
        uint junctionCount = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);

        var segments = new List<RoadSegment>((int)count);
        Span<byte> rec = stackalloc byte[12];
        for (uint i = 0; i < count; i++)
        {
            input.ReadExactly(rec);
            var cls = (RoadClass)rec[0];
            var surface = (RoadSurface)rec[1];
            var flags = (RoadFlags)BinaryPrimitives.ReadUInt16LittleEndian(rec[2..]);
            float width = BinaryPrimitives.ReadSingleLittleEndian(rec[4..]);
            int pointCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[8..]);

            var points = new float[pointCount * 3];
            var bytes = new byte[points.Length * 4];
            input.ReadExactly(bytes);
            for (int p = 0; p < points.Length; p++)
                points[p] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(p * 4));

            segments.Add(new RoadSegment
            {
                Class = cls, Surface = surface, Flags = flags, Width = width, Points = points,
            });
        }

        var junctions = new List<RoadJunction>((int)junctionCount);
        Span<byte> jrec = stackalloc byte[8];
        for (uint j = 0; j < junctionCount; j++)
        {
            input.ReadExactly(jrec);
            var cls = (RoadClass)jrec[0];
            sbyte layer = unchecked((sbyte)jrec[1]);
            int vertexCount = BinaryPrimitives.ReadUInt16LittleEndian(jrec[2..]);
            int indexCount = BinaryPrimitives.ReadUInt16LittleEndian(jrec[4..]);

            var vertices = new float[vertexCount * 3];
            var vbytes = new byte[vertices.Length * 4];
            input.ReadExactly(vbytes);
            for (int p = 0; p < vertices.Length; p++)
                vertices[p] = BinaryPrimitives.ReadSingleLittleEndian(vbytes.AsSpan(p * 4));

            var indices = new ushort[indexCount];
            var ibytes = new byte[indexCount * 2];
            input.ReadExactly(ibytes);
            for (int p = 0; p < indexCount; p++)
                indices[p] = BinaryPrimitives.ReadUInt16LittleEndian(ibytes.AsSpan(p * 2));

            junctions.Add(new RoadJunction
            {
                Class = cls, Layer = layer, Vertices = vertices, Indices = indices,
            });
        }

        return new RoadTile { Id = id, Segments = segments, Junctions = junctions };
    }
}
