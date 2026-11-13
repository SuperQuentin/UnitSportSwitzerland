using System.Buffers.Binary;
using System.IO.Compression;

namespace UnitSport.Terrain.Format;

/// <summary>
/// One cube-sphere tile (#1443, G1; docs/plans/global-tiling.md §2): the cell's heights on its
/// (u, v) lattice and the features standing on it, every position relative to the tile's anchor
/// (<see cref="CubeKey.Anchor"/>, the ENU frame at its centre).
///
/// <para>
/// <b>Heights</b> are WGS84 ellipsoidal heights (the geometry; sea level is for display) at the
/// (2^<see cref="Bits"/> + 1)² vertices of the cell's lattice, row-major from its south-west corner:
/// index <c>j * (2^Bits + 1) + i</c>, i along u, j along v. Vertex (i, j) is
/// <c>Key.Vertex(Bits, i, j, h)</c>; the outer rows are the neighbours' outer rows, the same points.
/// </para>
///
/// <para>
/// <b>Features</b> are floats in the anchor frame, the game's axes (x east, y up, z south): road
/// centrelines and building footprints, each from its source through the rigorous geodesy
/// (<see cref="SwissGeodesy"/>, never <see cref="SwissProjection"/>).
/// </para>
/// </summary>
public sealed class CubeTile
{
    public required CubeKey Key { get; init; }

    /// <summary>log2 of the cells a side: 10 at level 13 and 9 at level 14 (~1.2 m at a face centre).</summary>
    public required int Bits { get; init; }

    /// <summary>(2^Bits + 1)² ellipsoidal heights, metres; NaN where no source has data.</summary>
    public required float[] Heights { get; init; }

    /// <summary>The height quantum, metres: 1 cm at the leaves, coarser on the pyramid's levels (<see cref="CubeKey.QuantumFor"/>).</summary>
    public float Quantum { get; init; } = CubeTileCodec.Quantum;

    public List<CubeRoad> Roads { get; init; } = new();

    public List<CubeBuilding> Buildings { get; init; } = new();

    /// <summary>Vertices a side.</summary>
    public int Side => (1 << Bits) + 1;

    public float HeightAt(int i, int j) => Heights[j * Side + i];
}

/// <summary>Where a feature came from.</summary>
public enum CubeSource : byte
{
    SwissTlm = 0,
    BdTopo = 1,
}

/// <summary>A road centreline in the tile's anchor frame, draped on the tile's surface.</summary>
public sealed record CubeRoad(CubeSource Source, RoadClass Class, float Width, float[] Points)
{
    public int Count => Points.Length / 3;
}

/// <summary>A building footprint on the ground (points in the anchor frame, each at its ground height) and its height above the lowest of them.</summary>
public sealed record CubeBuilding(CubeSource Source, float Height, float[] Points)
{
    public int Count => Points.Length / 3;
}

/// <summary>
/// The tile file (<c>CUBT</c>, little-endian) and the height encoding.
///
/// <para>
/// <b>Heights</b>: quantised to <see cref="Quantum"/> above the tile's own base (an integer number
/// of quanta), then the planar prediction <c>a + b − c</c> from the west, south and south-west
/// neighbours, zigzag varints, Brotli. Lossless at the quantum: every height comes back within
/// half of it (5 mm at 1 cm), whatever the tile's range: the old ushort band (−50..4900 m, 7.55 cm)
/// and a per-tile offset plus scale over 16 bits (over 1 cm once a tile spans 1,310 m, as Alpine
/// tiles do) do not. Missing heights are a run of their own (<see cref="MissingCode"/>).
/// </para>
///
/// <para>
/// Layout: magic, version, flags, face, level, bits, x, y, base (quanta, int64), quantum (float),
/// min and max height, the height blob's length and bytes, then the roads and buildings.
/// </para>
/// </summary>
public static class CubeTileCodec
{
    public const uint Magic = 0x54425543; // "CUBT"
    public const ushort Version = 1;

    /// <summary>The height step: 1 cm, so every height is within 5 mm of what the tiler computed.</summary>
    public const float Quantum = 0.01f;

    /// <summary>The varint marking a missing height (no data): zigzag of a residual no real tile reaches.</summary>
    private const ulong MissingCode = ulong.MaxValue >> 1;

    public static byte[] Encode(CubeTile t)
    {
        using var ms = new MemoryStream();
        Encode(t, ms);
        return ms.ToArray();
    }

    public static void Encode(CubeTile t, Stream s)
    {
        var w = new BinaryWriter(s);
        int n = t.Side;
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (float h in t.Heights)
            if (!float.IsNaN(h)) { min = Math.Min(min, h); max = Math.Max(max, h); }
        if (float.IsPositiveInfinity(min)) min = max = 0;
        long baseQ = (long)Math.Floor(min / t.Quantum);
        byte[] blob = EncodeHeights(t.Heights, n, baseQ, t.Quantum);

        w.Write(Magic);
        w.Write(Version);
        w.Write((ushort)0);
        w.Write((byte)t.Key.Face);
        w.Write((byte)t.Key.Level);
        w.Write((byte)t.Bits);
        w.Write((byte)0);
        w.Write(t.Key.X);
        w.Write(t.Key.Y);
        w.Write(baseQ);
        w.Write(t.Quantum);
        w.Write(min);
        w.Write(max);
        w.Write(blob.Length);
        w.Write(blob);

        w.Write(t.Roads.Count);
        foreach (var r in t.Roads)
        {
            w.Write((byte)r.Source);
            w.Write((byte)r.Class);
            w.Write((ushort)0);
            w.Write(r.Width);
            WritePoints(w, r.Points);
        }
        w.Write(t.Buildings.Count);
        foreach (var b in t.Buildings)
        {
            w.Write((byte)b.Source);
            w.Write((byte)0);
            w.Write((ushort)0);
            w.Write(b.Height);
            WritePoints(w, b.Points);
        }
        w.Flush();
    }

    public static CubeTile Decode(byte[] data) => Decode(new MemoryStream(data, writable: false));

    public static CubeTile Decode(Stream s)
    {
        var r = new BinaryReader(s);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a cube tile (CUBT)");
        ushort version = r.ReadUInt16();
        if (version != Version) throw new InvalidDataException($"cube tile version {version}, this reads {Version}");
        r.ReadUInt16();
        int face = r.ReadByte(), level = r.ReadByte(), bits = r.ReadByte();
        r.ReadByte();
        var key = new CubeKey(face, level, r.ReadInt32(), r.ReadInt32());
        long baseQ = r.ReadInt64();
        float quantum = r.ReadSingle();
        r.ReadSingle();
        r.ReadSingle();
        byte[] blob = r.ReadBytes(r.ReadInt32());
        int n = (1 << bits) + 1;
        var tile = new CubeTile { Key = key, Bits = bits, Quantum = quantum, Heights = DecodeHeights(blob, n, baseQ, quantum) };

        int roads = r.ReadInt32();
        for (int k = 0; k < roads; k++)
        {
            var src = (CubeSource)r.ReadByte();
            var cls = (RoadClass)r.ReadByte();
            r.ReadUInt16();
            float width = r.ReadSingle();
            tile.Roads.Add(new CubeRoad(src, cls, width, ReadPoints(r)));
        }
        int buildings = r.ReadInt32();
        for (int k = 0; k < buildings; k++)
        {
            var src = (CubeSource)r.ReadByte();
            r.ReadByte();
            r.ReadUInt16();
            float height = r.ReadSingle();
            tile.Buildings.Add(new CubeBuilding(src, height, ReadPoints(r)));
        }
        return tile;
    }

    /// <summary>Reads only the key, bits and height range (32 bytes in).</summary>
    public static (CubeKey Key, int Bits, float Min, float Max) ReadHeader(ReadOnlySpan<byte> head)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic) throw new InvalidDataException("not a cube tile (CUBT)");
        var key = new CubeKey(head[8], head[9], BinaryPrimitives.ReadInt32LittleEndian(head[12..]), BinaryPrimitives.ReadInt32LittleEndian(head[16..]));
        return (key, head[10], BinaryPrimitives.ReadSingleLittleEndian(head[32..]), BinaryPrimitives.ReadSingleLittleEndian(head[36..]));
    }

    public const int HeaderBytes = 44;

    public static byte[] EncodeHeights(float[] h, int n, long baseQ, float quantum = Quantum)
    {
        var raw = new MemoryStream(h.Length * 2);
        Span<byte> buf = stackalloc byte[10];
        var q = new long[h.Length];
        for (int k = 0; k < h.Length; k++)
            q[k] = float.IsNaN(h[k]) ? long.MinValue : (long)Math.Round(h[k] / (double)quantum) - baseQ;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                long v = q[j * n + i];
                ulong code;
                if (v == long.MinValue) code = MissingCode;
                else
                {
                    long r = v - Predict(q, n, i, j);
                    code = (ulong)((r << 1) ^ (r >> 63));
                }
                int len = 0;
                while (code >= 0x80) { buf[len++] = (byte)(code | 0x80); code >>= 7; }
                buf[len++] = (byte)code;
                raw.Write(buf[..len]);
            }
        using var outMs = new MemoryStream();
        using (var br = new BrotliStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(br);
        return outMs.ToArray();
    }

    /// <summary>
    /// A thread's scratch for <see cref="DecodeHeights"/> (#1773): the varints and the quantised heights of a 513² leaf are
    /// ~0.5 and 2 MB, and the earth world decodes leaves on every worker as the view moves (at a 60 m/s flight these
    /// temporaries were a third of everything allocated, and the collector's pauses tanked the frame rate). Only the
    /// heights are new.
    /// </summary>
    [ThreadStatic] private static byte[]? _rawScratch;
    [ThreadStatic] private static long[]? _quantScratch;

    public static float[] DecodeHeights(byte[] blob, int n, long baseQ, float quantum)
    {
        var raw = _rawScratch ??= new byte[Math.Max(1 << 16, blob.Length * 8)];
        int written;
        while (!BrotliDecoder.TryDecompress(blob, raw, out written))
        {
            // too small (or corrupt: past any varint stream's bound, 10 bytes a vertex, it is not the room)
            if (raw.Length >= n * n * 10) throw new InvalidDataException("cube tile heights do not decompress");
            raw = _rawScratch = new byte[Math.Min(raw.Length * 2, n * n * 10)];
        }
        var q = _quantScratch is { } kept && kept.Length >= n * n ? kept : _quantScratch = new long[n * n];
        var h = new float[n * n];
        int p = 0;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                ulong code = 0;
                int shift = 0;
                byte b;
                do { b = raw[p++]; code |= (ulong)(b & 0x7F) << shift; shift += 7; } while (b >= 0x80);
                int k = j * n + i;
                if (code == MissingCode) { q[k] = long.MinValue; h[k] = float.NaN; continue; }
                long r = (long)(code >> 1) ^ -(long)(code & 1);
                q[k] = r + Predict(q, n, i, j);
                h[k] = (float)((q[k] + baseQ) * (double)quantum);
            }
        // the scratch outlasts the stream: a short one read stale bytes past its end
        if (p > written) throw new InvalidDataException("cube tile heights end early");
        return h;
    }

    /// <summary>The planar prediction from the already-decoded west, south and south-west neighbours; a missing one falls back to what is there.</summary>
    private static long Predict(long[] q, int n, int i, int j)
    {
        long a = i > 0 ? q[j * n + i - 1] : long.MinValue;
        long b = j > 0 ? q[(j - 1) * n + i] : long.MinValue;
        long c = i > 0 && j > 0 ? q[(j - 1) * n + i - 1] : long.MinValue;
        bool ha = a != long.MinValue, hb = b != long.MinValue, hc = c != long.MinValue;
        if (ha && hb && hc) return a + b - c;
        if (ha) return a;
        if (hb) return b;
        return 0;
    }

    private static void WritePoints(BinaryWriter w, float[] pts)
    {
        w.Write(pts.Length / 3);
        foreach (float f in pts) w.Write(f);
    }

    private static float[] ReadPoints(BinaryReader r)
    {
        var pts = new float[r.ReadInt32() * 3];
        for (int k = 0; k < pts.Length; k++) pts[k] = r.ReadSingle();
        return pts;
    }
}
