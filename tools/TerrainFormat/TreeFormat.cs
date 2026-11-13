using System.Buffers.Binary;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Tree instances for a tile, in tile-local metres. Rendered as MultiMesh instances, so
/// only a transform and a size are needed per tree.
/// </summary>
public readonly record struct TreeInstance(float X, float Y, float Z, float Height, byte Kind);

public static class TreeFormat
{
    /// <summary>"USTR" little-endian.</summary>
    public const uint Magic = 0x52545355;

    public const ushort Version = 1;
    public const int HeaderSize = 20;

    public static string FileName(TileId id) => $"trees_{id.E}_{id.N}.trees";

    public static void Encode(TileId id, IReadOnlyList<TreeInstance> trees, Stream output)
    {
        TileHeader.WriteHead(output, Magic, Version, 0, id);
        EncodeBody(trees, output);
    }

    /// <summary>
    /// The key-free body (#1505, G2 B1): the count and the records, what follows the header's tile id.
    /// The .trees file is its 16-byte head and then this, byte for byte as before the split; a cube
    /// leaf's tree section (<see cref="LeafTrees"/>) is this alone, compressed. Inside a leaf a tree's
    /// X, Y, Z are in the leaf's anchor frame (<see cref="CubeKey.Anchor"/>: x east, y ENU up, z south,
    /// metres from the cell centre on the ellipsoid), not tile-local from the NW corner with Y the altitude.
    /// </summary>
    public static void EncodeBody(IReadOnlyList<TreeInstance> trees, Stream output)
    {
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)trees.Count);
        output.Write(count);

        Span<byte> rec = stackalloc byte[20];
        foreach (var t in trees)
        {
            BinaryPrimitives.WriteSingleLittleEndian(rec[0..], t.X);
            BinaryPrimitives.WriteSingleLittleEndian(rec[4..], t.Y);
            BinaryPrimitives.WriteSingleLittleEndian(rec[8..], t.Z);
            BinaryPrimitives.WriteSingleLittleEndian(rec[12..], t.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[16..], t.Kind);
            output.Write(rec);
        }
    }

    public static List<TreeInstance> Decode(Stream input)
    {
        // no version check: there never was one, and tree files on disk must keep loading
        TileHeader.ReadHead(input, Magic, "tree");
        return DecodeBody(input);
    }

    /// <summary>Reads what <see cref="EncodeBody"/> wrote.</summary>
    public static List<TreeInstance> DecodeBody(Stream input)
    {
        Span<byte> head = stackalloc byte[4];
        input.ReadExactly(head);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(head);

        var trees = new List<TreeInstance>((int)count);
        var bytes = new byte[count * 20];
        input.ReadExactly(bytes);
        for (uint i = 0; i < count; i++)
        {
            var s = bytes.AsSpan((int)i * 20);
            trees.Add(new TreeInstance(
                BinaryPrimitives.ReadSingleLittleEndian(s),
                BinaryPrimitives.ReadSingleLittleEndian(s[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(s[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(s[12..]),
                (byte)BinaryPrimitives.ReadUInt32LittleEndian(s[16..])));
        }
        return trees;
    }
}
