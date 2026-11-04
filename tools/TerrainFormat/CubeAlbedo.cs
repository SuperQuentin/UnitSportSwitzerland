namespace UnitSport.Terrain.Format;

/// <summary>
/// A cube tile's colour (#1430 H4, agreed on #1455): what the globe shows from far out, one image
/// per month (Blue Marble's land changes with the seasons: snow, vegetation), carried in colour
/// bundles of their own (<see cref="CubeBundle.AlbedoMagic"/>). Leaves carry none.
///
/// <para>
/// Each image is <see cref="Size"/>² pixels over the cell, row-major from its south-west corner like
/// the heights: pixel (px, py) covers u in [U0 + px·s, U0 + (px+1)·s] and v likewise, s = Size⁻¹ of
/// the cell, so pixel centres are a half pixel in from the edges. The bytes are an encoded image
/// (<see cref="CubeAlbedoFormat"/>); this container never decodes them, the game does (Godot's Image).
/// </para>
/// </summary>
public sealed class CubeAlbedo
{
    public required CubeKey Key { get; init; }

    /// <summary>Pixels a side.</summary>
    public required int Size { get; init; }

    public CubeAlbedoFormat Format { get; init; } = CubeAlbedoFormat.Webp;

    /// <summary>January first; 12 images, or 1 for a colour that never changes.</summary>
    public required byte[][] Months { get; init; }

    /// <summary>The image for a month 1..12 (the only one when the tile has one).</summary>
    public byte[] ForMonth(int month) => Months.Length == 1 ? Months[0] : Months[Math.Clamp(month, 1, 12) - 1];
}

public enum CubeAlbedoFormat : byte
{
    Webp = 1,
    Png = 2,
    /// <summary>A leaf's aerial photo as SWISSIMAGE's WMS gives it (<see cref="CubePhoto"/>, #1505).</summary>
    Jpeg = 3,
}

/// <summary>
/// The colour entry (<c>CUBC</c>, little-endian): magic, version, format, month count, face,
/// level, size, x, y, then per month the image's length and bytes.
/// </summary>
public static class CubeAlbedoCodec
{
    public const uint Magic = 0x43425543; // "CUBC"
    public const ushort Version = 1;

    public static byte[] Encode(CubeAlbedo a)
    {
        if (a.Months.Length is not (1 or 12)) throw new ArgumentException($"{a.Key}: {a.Months.Length} months (1 or 12)");
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write((byte)a.Format);
            w.Write((byte)a.Months.Length);
            w.Write((byte)a.Key.Face);
            w.Write((byte)a.Key.Level);
            w.Write((ushort)a.Size);
            w.Write(a.Key.X);
            w.Write(a.Key.Y);
            foreach (var m in a.Months)
            {
                w.Write(m.Length);
                w.Write(m);
            }
        }
        return ms.ToArray();
    }

    public static CubeAlbedo Decode(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a cube colour entry (CUBC)");
        ushort version = r.ReadUInt16();
        if (version != Version) throw new InvalidDataException($"cube colour version {version}, this reads {Version}");
        var format = (CubeAlbedoFormat)r.ReadByte();
        int months = r.ReadByte();
        int face = r.ReadByte(), level = r.ReadByte(), size = r.ReadUInt16();
        var key = new CubeKey(face, level, r.ReadInt32(), r.ReadInt32());
        var images = new byte[months][];
        for (int m = 0; m < months; m++) images[m] = r.ReadBytes(r.ReadInt32());
        return new CubeAlbedo { Key = key, Size = size, Format = format, Months = images };
    }
}
