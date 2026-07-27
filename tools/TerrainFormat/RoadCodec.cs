using System.Buffers.Binary;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Binary encode/decode of .road tile files. Shared by the preprocessor and the game.
///
/// Layout (little-endian):
///   header 24 B: magic u32, version u16, flags u16, tileE i32, tileN i32,
///                segmentCount u32, reserved u32
///   per segment: class u8, surface u8, flags u16, width f32, pointCount u16, pad u16,
///                then pointCount * 3 f32 (x, y=altitude, z) local to the tile NW corner
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
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0);
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
    }

    public static RoadTile Decode(Stream input)
    {
        Span<byte> header = stackalloc byte[RoadFormat.HeaderSize];
        input.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != RoadFormat.Magic)
            throw new InvalidDataException($"Bad road magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != RoadFormat.Version)
            throw new InvalidDataException($"Unsupported road version {version}");

        var id = new TileId(
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);

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

        return new RoadTile { Id = id, Segments = segments };
    }
}
