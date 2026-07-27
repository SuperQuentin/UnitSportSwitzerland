using System.Buffers.Binary;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Binary encode/decode of .bldg tile files.
///
/// Layout (little-endian):
///   header 24 B: magic u32, version u16, flags u16, tileE i32, tileN i32,
///                buildingCount u32, reserved u32
///   per building: kind u8, floors u8, year u16, egid u32, minY f32, maxY f32,
///                 triangleCount u32, then triangleCount * 9 f32
/// </summary>
public static class BuildingCodec
{
    public static void Encode(BuildingTile tile, Stream output)
    {
        Span<byte> header = stackalloc byte[BuildingFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], BuildingFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], BuildingFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], tile.Id.E);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], tile.Id.N);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)tile.Buildings.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0);
        output.Write(header);

        Span<byte> rec = stackalloc byte[20];
        Span<byte> f = stackalloc byte[4];
        foreach (var b in tile.Buildings)
        {
            rec[0] = (byte)b.Kind;
            rec[1] = b.Floors;
            BinaryPrimitives.WriteUInt16LittleEndian(rec[2..], b.YearBuilt);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[4..], b.Egid);
            BinaryPrimitives.WriteSingleLittleEndian(rec[8..], b.MinY);
            BinaryPrimitives.WriteSingleLittleEndian(rec[12..], b.MaxY);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[16..], (uint)b.TriangleCount);
            output.Write(rec);

            foreach (float v in b.Triangles)
            {
                BinaryPrimitives.WriteSingleLittleEndian(f, v);
                output.Write(f);
            }
        }
    }

    public static BuildingTile Decode(Stream input)
    {
        Span<byte> header = stackalloc byte[BuildingFormat.HeaderSize];
        input.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != BuildingFormat.Magic)
            throw new InvalidDataException($"Bad building magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != BuildingFormat.Version)
            throw new InvalidDataException($"Unsupported building version {version}");

        var id = new TileId(
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);

        var buildings = new List<Building>((int)count);
        Span<byte> rec = stackalloc byte[20];
        for (uint i = 0; i < count; i++)
        {
            input.ReadExactly(rec);
            var kind = (BuildingKind)rec[0];
            byte floors = rec[1];
            ushort year = BinaryPrimitives.ReadUInt16LittleEndian(rec[2..]);
            uint egid = BinaryPrimitives.ReadUInt32LittleEndian(rec[4..]);
            float minY = BinaryPrimitives.ReadSingleLittleEndian(rec[8..]);
            float maxY = BinaryPrimitives.ReadSingleLittleEndian(rec[12..]);
            int tris = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[16..]);

            var verts = new float[tris * 9];
            var bytes = new byte[verts.Length * 4];
            input.ReadExactly(bytes);
            for (int v = 0; v < verts.Length; v++)
                verts[v] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(v * 4));

            buildings.Add(new Building
            {
                Kind = kind, Floors = floors, YearBuilt = year, Egid = egid,
                MinY = minY, MaxY = maxY, Triangles = verts,
            });
        }

        return new BuildingTile { Id = id, Buildings = buildings };
    }
}
