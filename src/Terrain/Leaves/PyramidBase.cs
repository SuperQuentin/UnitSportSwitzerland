using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The globe's pyramid (<c>cube_global</c>) as the ground the earth world's fill is built on (#1678): its height at any
/// point of a cube face's global lattice, read bilinearly from the finest level that holds the point, and its lakes
/// (<see cref="CubeManifest.Lakes"/>: a height at one of them is that lake's surface, as <c>--cube-swiss</c> puts it there).
/// A function of position alone, so leaves that share an edge read the same heights without coordinating. Heights are
/// ellipsoidal, as the leaves'. Thread-safe; Godot-free (the tests and BlendCheck compile it); decoded tiles are held, the
/// last <see cref="Kept"/>.
/// </summary>
public sealed class PyramidBase
{
    /// <summary>
    /// A pyramid whose finest level is coarser than this (the levels the game ships, 0-2: a vertex every ~5 km) knows
    /// less than the generator's own 500 m relief: the fill keeps the generator's ground then.
    /// </summary>
    public const int MinFinestLevel = 9;

    /// <summary>Decoded tiles held (~1 MB each at 9 bits).</summary>
    public const int Kept = 96;

    /// <summary>How near a lake's level a height stands on it, metres: the stored quantum and float rounding (#1669).</summary>
    public const float LakeTolM = 0.02f;

    private readonly Func<CubeKey, CubeTile?> _load;
    private readonly ConcurrentDictionary<CubeKey, Lazy<CubeTile?>> _tiles = new();

    /// <summary>The pyramid's lake levels (ellipsoidal), none when its manifest names none.</summary>
    public float[] Lakes { get; }

    /// <summary>The finest level it holds.</summary>
    public int FinestLevel { get; }

    /// <param name="load">A tile's heights, null where the pyramid has none; called once a tile while it is held.</param>
    public PyramidBase(Func<CubeKey, CubeTile?> load, IEnumerable<float>? lakes, int finestLevel)
    {
        _load = load;
        Lakes = lakes?.ToArray() ?? [];
        FinestLevel = finestLevel;
    }

    /// <summary>
    /// A pyramid on disk (the full one on D:), or null where there is none, or its finest level is coarser than
    /// <see cref="MinFinestLevel"/>, or <paramref name="dir"/> is inside the game's pack (<c>res://</c>: the shipped levels).
    /// </summary>
    public static PyramidBase? Open(string? dir)
    {
        if (dir is null || dir.StartsWith("res://", StringComparison.Ordinal)) return null;
        string manifestPath = Path.Combine(dir, CubeManifest.FileName);
        if (!File.Exists(manifestPath)) return null;
        var manifest = CubeManifest.FromJson(File.ReadAllText(manifestPath));
        int finest = manifest.LevelsOrLeaf.Max(l => l.Level);
        if (finest < MinFinestLevel) return null;
        var levels = manifest.LevelsOrLeaf.ToDictionary(l => l.Level);
        var bundles = new HashSet<CubeKey>();
        foreach (var t in manifest.Tiles)
        {
            var k = CubeKey.Parse(t.Key);
            bundles.Add(k.AncestorAt(levels.GetValueOrDefault(k.Level)?.BundleLevel ?? CubeKey.PyramidBundleLevel(k.Level)));
        }
        var index = new Dictionary<CubeKey, (string Path, long Offset, int Length)>();
        foreach (var b in bundles)
        {
            string path = Path.Combine(dir, CubeBundle.FileName(b));
            if (!File.Exists(path)) continue;
            using var fs = File.OpenRead(path);
            foreach (var (key, at) in CubeBundle.ReadIndex(fs)) index[key] = (path, at.Offset, at.Length);
        }
        return new PyramidBase(key =>
        {
            if (!index.TryGetValue(key, out var at)) return null;
            using var handle = File.OpenHandle(at.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[at.Length];
            RandomAccess.Read(handle, bytes, at.Offset);
            return CubeTileCodec.Decode(bytes);
        }, manifest.Lakes, finest) { SeaSurface = Egm2008.Available ? Egm2008.Undulation : null };
    }

    private CubeTile? Tile(CubeKey key)
    {
        if (_tiles.TryGetValue(key, out var held)) return held.Value;
        if (_tiles.Count > Kept) _tiles.Clear();
        return _tiles.GetOrAdd(key, k => new Lazy<CubeTile?>(() => _load(k))).Value;
    }

    /// <summary>
    /// The height at point (<paramref name="gi"/>, <paramref name="gj"/>) of face <paramref name="face"/>'s global lattice
    /// of 2^<paramref name="latticeLevel"/> cells a side (a leaf's level plus its bits: 23 at the leaves'), bilinear in the
    /// finest tile that holds the point with all four of its samples; NaN where none does.
    /// </summary>
    public double HeightAt(int face, long gi, long gj, int latticeLevel) => HeightAt(face, gi, gj, latticeLevel, out _);

    /// <summary><see cref="HeightAt(int, long, long, int)"/>, and the level that answered (-1 when none did).</summary>
    public double HeightAt(int face, long gi, long gj, int latticeLevel, out int answered, int finest = int.MaxValue)
    {
        answered = -1;
        for (int level = Math.Min(Math.Min(FinestLevel, finest), latticeLevel); level >= 0; level--)
        {
            int bits = CubeKey.LatticeBits(level), shift = latticeLevel - level - bits;
            if (shift < 0) continue;
            long side = 1L << level, cell = 1L << (bits + shift);
            long x = Math.Clamp(gi / cell, 0, side - 1), y = Math.Clamp(gj / cell, 0, side - 1);
            if (Tile(new CubeKey(face, level, (int)x, (int)y)) is not { } tile) continue;
            double scale = 1.0 / (1L << shift);
            double fi = (gi - x * cell) * scale, fj = (gj - y * cell) * scale;
            int last = tile.Side - 1, tb = tile.Bits;
            if (tb != bits)
            {
                // a tile stored at other bits than the level's rule: its own lattice
                double r = Math.Pow(2, tb - bits);
                fi *= r;
                fj *= r;
            }
            int i0 = Math.Min((int)fi, last - 1), j0 = Math.Min((int)fj, last - 1);
            double u = fi - i0, v = fj - j0;
            float a = tile.HeightAt(i0, j0), b = tile.HeightAt(i0 + 1, j0), c = tile.HeightAt(i0, j0 + 1), d = tile.HeightAt(i0 + 1, j0 + 1);
            if (float.IsNaN(a) || float.IsNaN(b) || float.IsNaN(c) || float.IsNaN(d)) continue;
            // exact at a sample: a vertex on it reads it to the bit
            answered = level;
            if (u == 0 && v == 0) return a;
            double south = a + (b - a) * u, north = c + (d - c) * u;
            return south + (north - south) * v;
        }
        return double.NaN;
    }

    /// <summary>
    /// The sea's surface over the ellipsoid at a latitude and longitude, degrees (#1808): <see cref="Egm2008.Undulation"/>
    /// for the pyramid <see cref="Open"/> reads, the surface its sea stands at (<c>CubeGlobal</c>: <c>max(H, 0) + N</c>);
    /// null, no sea (the fill laid the generator's land over the oceans).
    /// </summary>
    public Func<double, double, double>? SeaSurface { get; init; }

    /// <summary>
    /// How far over the sea's surface the pyramid's ground still is sea, metres: the coarse geoid grid's error (0.3 m at
    /// the 99th percentile, 1.2 m at worst) and a coast's lattice samples, which average a little land in.
    /// </summary>
    public const double SeaTolM = 1.5;

    /// <summary>
    /// The sea over a square of a face's global lattice (#1808): the geoid at its four corners, bilinear between (it bends
    /// by millimetres over a few kilometres), so a fill leaf asks the geodesy four times, not once a vertex. Null without a
    /// sea or where the geoid is unknown.
    /// </summary>
    public SeaPatch? SeaOver(int face, long gi0, long gj0, long span, int latticeLevel)
    {
        if (SeaSurface is not { } sea) return null;
        double N(long gi, long gj)
        {
            var g = CubeSphere.ToGeodetic(face, CubeSphere.Lattice(latticeLevel, gi), CubeSphere.Lattice(latticeLevel, gj));
            return sea(g.LatDeg, g.LonDeg);
        }
        var patch = new SeaPatch(gi0, gj0, span, N(gi0, gj0), N(gi0 + span, gj0), N(gi0, gj0 + span), N(gi0 + span, gj0 + span));
        return double.IsNaN(patch.N00 + patch.N10 + patch.N01 + patch.N11) ? null : patch;
    }

    /// <summary>The sea's surface over a square of a face's lattice, and whether the pyramid's ground at a point is sea.</summary>
    public sealed record SeaPatch(long Gi0, long Gj0, long Span, double N00, double N10, double N01, double N11)
    {
        /// <summary>The sea's surface at a lattice point, ellipsoidal.</summary>
        public double SurfaceAt(long gi, long gj)
        {
            double u = (double)(gi - Gi0) / Span, v = (double)(gj - Gj0) / Span;
            double south = N00 + (N10 - N00) * u, north = N01 + (N11 - N01) * u;
            return south + (north - south) * v;
        }

        /// <summary>The sea's level where ground of height <paramref name="h"/> at the point is sea (within <see cref="SeaTolM"/> of it), else NaN.</summary>
        public float LevelAt(long gi, long gj, double h)
        {
            double n = SurfaceAt(gi, gj);
            return h <= n + SeaTolM ? (float)n : float.NaN;
        }
    }

    /// <summary>The lake level a height stands at (within <see cref="LakeTolM"/>), or NaN when it is no lake's.</summary>
    public float LakeAt(double h)
    {
        foreach (float l in Lakes)
            if (Math.Abs(h - l) <= LakeTolM) return l;
        return float.NaN;
    }
}
