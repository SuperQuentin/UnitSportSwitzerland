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
        new TileHeader(Magic, Version, 0, id, (uint)trees.Count).Write(output);

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
        uint count = TileHeader.Read(input, Magic, "tree").Count;

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
