using System.IO.Compression;

namespace UnitSport.Terrain.Format;

/// <summary>
/// What stands on a pyramid tile, baked for the far view (#1657): per vertex of the lattice the globe draws
/// (<see cref="Cells"/> + 1 a side, the tile's every 8th lattice point at 513²), how much of the ground round it is
/// under tree canopy, under roofs, and paved, and the height of what stands there averaged over that ground (the
/// skin the globe raises the vertex by). Built from the leaves' trees, buildings and cover (<c>--cube-features</c>),
/// coarser levels from the finer one; a function of the lattice point, so neighbours agree on their shared edge.
/// </summary>
public sealed class CubeFarFeatures
{
    /// <summary>Cells a side, as the globe draws a tile (<c>Globe.Cells</c>).</summary>
    public const int Cells = 64;

    public const int N = Cells + 1;

    /// <summary>The skin's step, metres: a byte holds up to 63.75 m.</summary>
    public const float SkinStepM = 0.25f;

    public required CubeKey Key { get; init; }

    /// <summary>Canopy, roof and paved shares, 0-255 each, and the skin in <see cref="SkinStepM"/> steps, row-major from the south-west.</summary>
    public required byte[] Forest { get; init; }
    public required byte[] Built { get; init; }
    public required byte[] Paved { get; init; }
    public required byte[] Skin { get; init; }

    public static CubeFarFeatures Empty(CubeKey key) => new()
    {
        Key = key, Forest = new byte[N * N], Built = new byte[N * N], Paved = new byte[N * N], Skin = new byte[N * N],
    };

    public float SkinM(int k) => Skin[k] * SkinStepM;

    /// <summary>Whether anything stands on the tile at all.</summary>
    public bool Any => Array.Exists(Forest, b => b > 0) || Array.Exists(Built, b => b > 0) || Array.Exists(Paved, b => b > 0);

    /// <summary>
    /// The values at a point of the tile's drawn lattice in fractional vertices (0..<see cref="Cells"/>), bilinear: a leaf-level
    /// globe tile takes its parent's this way.
    /// </summary>
    public (float Forest, float Built, float Paved, float SkinM) At(float i, float j)
    {
        int i0 = Math.Clamp((int)i, 0, Cells - 1), j0 = Math.Clamp((int)j, 0, Cells - 1);
        float u = Math.Clamp(i - i0, 0, 1), v = Math.Clamp(j - j0, 0, 1);
        float B(byte[] a)
        {
            float s = a[j0 * N + i0] + (a[j0 * N + i0 + 1] - a[j0 * N + i0]) * u;
            float n = a[(j0 + 1) * N + i0] + (a[(j0 + 1) * N + i0 + 1] - a[(j0 + 1) * N + i0]) * u;
            return s + (n - s) * v;
        }
        return (B(Forest) / 255f, B(Built) / 255f, B(Paved) / 255f, B(Skin) * SkinStepM);
    }
}

/// <summary>The features entry (<c>CUBF</c>): magic, version, face, level, x, y, then Brotli of the four planes.</summary>
public static class CubeFarFeaturesCodec
{
    public const uint Magic = 0x46425543; // "CUBF"
    public const ushort Version = 1;

    /// <summary>Bundles of features beside the heights' (<c>.cubf</c>, the heights' index layout).</summary>
    public const uint BundleMagic = 0x46415543; // "CUAF"

    public static string FileName(CubeKey bundle) => bundle.FileStem + ".cubf";

    public static byte[] Encode(CubeFarFeatures f)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write((byte)f.Key.Face);
            w.Write((byte)f.Key.Level);
            w.Write(f.Key.X);
            w.Write(f.Key.Y);
            w.Write((byte)CubeFarFeatures.N);
        }
        using (var b = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            // planes one after the other: each compresses on its own runs
            b.Write(f.Forest);
            b.Write(f.Built);
            b.Write(f.Paved);
            b.Write(f.Skin);
        }
        return ms.ToArray();
    }

    public static CubeFarFeatures Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a CUBF entry");
        if (r.ReadUInt16() is var v && v > Version) throw new InvalidDataException($"CUBF version {v}");
        int face = r.ReadByte(), level = r.ReadByte(), x = r.ReadInt32(), y = r.ReadInt32(), n = r.ReadByte();
        if (n != CubeFarFeatures.N) throw new InvalidDataException($"CUBF side {n}");
        using var b = new BrotliStream(ms, CompressionMode.Decompress);
        byte[] Plane()
        {
            var p = new byte[n * n];
            b.ReadExactly(p);
            return p;
        }
        return new CubeFarFeatures { Key = new CubeKey(face, level, x, y), Forest = Plane(), Built = Plane(), Paved = Plane(), Skin = Plane() };
    }
}
