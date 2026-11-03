using System.Collections.Concurrent;
using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The world's own ground colour for the globe (#1430 H4, World/Globe/GlobeAlbedoBake): over a region's
/// LV95 tiles, what the flat tiles are painted (<see cref="CoverPalette.ColorFor"/> from each tile's cover
/// classes and heights, as <c>TerrainMeshBuilder</c> paints its vertices), averaged over a pixel's
/// footprint, with a weight that rises from 0 at the data's edge to 1 <see cref="FeatherM"/> inside, so
/// the globe's own colour blends into it instead of a seam. Linear colour throughout.
/// </summary>
public sealed class OurColours
{
    /// <summary>How far inside the edge of the data the world's colour takes over fully, metres.</summary>
    public const double FeatherM = 5000;

    private readonly string _dir;
    private readonly HashSet<TileId> _tiles = new();
    private readonly ConcurrentDictionary<TileId, (byte[] Cover, ChunkGrid? Coarse)> _cache = new();
    // distance inside the data, kilometre steps, for the feather
    private readonly Dictionary<TileId, int> _inside = new();

    public double MinE { get; }
    public double MinN { get; }
    public double MaxE { get; }
    public double MaxN { get; }

    public int Count => _tiles.Count;

    public OurColours(string dir)
    {
        _dir = dir;
        foreach (var file in Directory.GetFiles(dir, "cover_*.cover"))
        {
            var p = System.IO.Path.GetFileNameWithoutExtension(file).Split('_');
            _tiles.Add(new TileId(int.Parse(p[1]), int.Parse(p[2])));
        }
        MinE = _tiles.Min(t => t.MinE);
        MinN = _tiles.Min(t => t.MinN);
        MaxE = _tiles.Max(t => t.MinE) + 1000;
        MaxN = _tiles.Max(t => t.MaxN);
        // each tile's ring distance to the nearest tile without data, outward from the edge (breadth first)
        var queue = new Queue<TileId>();
        foreach (var t in _tiles)
        {
            bool edge = false;
            for (int dz = -1; dz <= 1 && !edge; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!_tiles.Contains(t.Neighbour(dx, dz))) { edge = true; break; }
            if (edge) { _inside[t] = 1; queue.Enqueue(t); }
        }
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var u = t.Neighbour(dx, dz);
                    if (!_tiles.Contains(u) || _inside.ContainsKey(u)) continue;
                    _inside[u] = _inside[t] + 1;
                    queue.Enqueue(u);
                }
        }
    }

    /// <summary>Whether an LV95 box touches the data at all.</summary>
    public bool Touches(double minE, double minN, double maxE, double maxN) =>
        maxE > MinE && minE < MaxE && maxN > MinN && minN < MaxN;

    /// <summary>
    /// The world's linear colour round LV95 (e, n), averaged over <paramref name="halfM"/> each way (up to
    /// 5 × 5 samples), and how much it should count (the share of samples on data, times the feather).
    /// </summary>
    public (Vector3 Colour, float Weight) At(double e, double n, double halfM)
    {
        int k = Math.Clamp((int)Math.Ceiling(2 * halfM / 2.0), 1, 5);
        var sum = Vector3.Zero;
        int hits = 0;
        double feather = 0;
        for (int b = 0; b < k; b++)
            for (int a = 0; a < k; a++)
            {
                double se = e - halfM + (a + 0.5) * 2 * halfM / k, sn = n - halfM + (b + 0.5) * 2 * halfM / k;
                var id = TileId.FromLv95(se, sn);
                if (!_tiles.Contains(id)) continue;
                var (cover, coarse) = Load(id);
                var (x, z) = id.Local(se, sn);
                int fc = Math.Clamp((int)Math.Round(x / ChunkFormat.SpacingM), 0, ChunkFormat.GridSize - 1);
                int fr = Math.Clamp((int)Math.Round(z / ChunkFormat.SpacingM), 0, ChunkFormat.GridSize - 1);
                float alt = coarse == null ? 500 : (float)ChunkFormat.Dequantize(coarse.Heights[
                    Math.Clamp(fr / ChunkFormat.CoarseStride, 0, coarse.Size - 1) * coarse.Size + Math.Clamp(fc / ChunkFormat.CoarseStride, 0, coarse.Size - 1)]);
                var c = CoverPalette.ColorFor((CoverClass)cover[fr * ChunkFormat.GridSize + fc], alt, 0.5f).SrgbToLinear();
                sum += new Vector3(c.R, c.G, c.B);
                hits++;
                // inside the edge: the tile's ring distance, less how far the sample is from the tile's inner side
                feather += Math.Clamp((_inside.TryGetValue(id, out int ring) ? ring - 0.5 : 0) * 1000 / FeatherM, 0, 1);
            }
        if (hits == 0) return (Vector3.Zero, 0);
        return (sum / hits, (float)(hits / (double)(k * k) * Mathf.SmoothStep(0, 1, (float)(feather / hits))));
    }

    private (byte[] Cover, ChunkGrid? Coarse) Load(TileId id) => _cache.GetOrAdd(id, t =>
    {
        byte[] cover;
        using (var fs = File.OpenRead(System.IO.Path.Combine(_dir, CoverFormat.FileName(t)))) cover = CoverFormat.Decode(fs);
        ChunkGrid? coarse = null;
        string terrc = System.IO.Path.Combine(_dir, ChunkFormat.CoarseFileName(t));
        if (File.Exists(terrc))
            using (var fs = File.OpenRead(terrc)) coarse = ChunkCodec.Decode(fs);
        return (cover, coarse);
    });

    /// <summary>
    /// What to store so the Cartoon globe draws <paramref name="linear"/>: its shader lifts the stored
    /// colour by a square root and saturates it by 1.25 (<c>globe.gdshader</c>'s <c>cartoon_earth</c>), so
    /// store the colour desaturated back by 0.8 and squared.
    /// </summary>
    public static Vector3 ForCartoonShader(Vector3 linear)
    {
        float l = linear.Dot(new Vector3(0.2126f, 0.7152f, 0.0722f));
        var d = new Vector3(l, l, l).Lerp(linear, 0.8f);
        d = d.Max(Vector3.Zero);
        return d * d;
    }
}
