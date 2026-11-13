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
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], BuildingFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], BuildingFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], tile.Id.E);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], tile.Id.N);
        output.Write(header);
        EncodeBody(tile, output);
    }

    /// <summary>
    /// The key-free body (#1505, G2 B1): the building count, the reserved word and the records, what
    /// follows the header's tile id. The .bldg file is its 16-byte head and then this, byte for byte as
    /// before the split; a cube leaf's building section (<see cref="LeafBuildings"/>) is this alone,
    /// compressed. Inside a leaf the triangles, <see cref="Building.MinY"/> and <see cref="Building.MaxY"/>
    /// are in the leaf's anchor frame (<see cref="CubeKey.Anchor"/>: x east, y ENU up, z south, metres
    /// from the cell centre on the ellipsoid), not tile-local from the NW corner with y the altitude.
    /// </summary>
    public static void EncodeBody(BuildingTile tile, Stream output)
    {
        Span<byte> count = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)tile.Buildings.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(count[4..], 0);
        output.Write(count);

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
        Span<byte> header = stackalloc byte[16];
        input.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != BuildingFormat.Magic)
            throw new InvalidDataException($"Bad building magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version < BuildingFormat.MinVersion || version > BuildingFormat.Version)
            throw new InvalidDataException($"Unsupported building version {version}");

        var id = new TileId(
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]));
        return DecodeBody(input, id);
    }

    /// <summary>Reads what <see cref="EncodeBody"/> wrote; <paramref name="id"/> is the LV95 tile's (default inside a leaf).</summary>
    public static BuildingTile DecodeBody(Stream input, TileId id = default)
    {
        Span<byte> head = stackalloc byte[8];
        input.ReadExactly(head);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(head);

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
