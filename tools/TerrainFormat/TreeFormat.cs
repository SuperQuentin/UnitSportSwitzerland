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
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], id.E);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], id.N);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)trees.Count);
        output.Write(header);

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
        Span<byte> header = stackalloc byte[HeaderSize];
        input.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != Magic)
            throw new InvalidDataException($"Bad tree magic 0x{magic:X8}");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);

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
