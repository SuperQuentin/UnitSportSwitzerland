using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// Serves a <see cref="FixtureCourse"/> as ordinary tiles (#221): <c>--chunks fixture:&lt;course&gt;</c>
/// or <c>--world fixture</c>. The course starts at <c>(startE, startN)</c> (the spawn, so probes that
/// start at <c>--at</c> start on it) and covers the tiles round its roads; nothing exists outside
/// them. Every tile it has answers every asset, empty rather than null, so nothing above it (the
/// streaming cache) fills a gap with real data. No horizon, no buildings, open ground cover.
/// </summary>
public sealed class FixtureChunkSource : IChunkSource
{
    private const int LatticeStride = ChunkFormat.CoarseStride;                    // 10 m
    private const int LatticeSize = (ChunkFormat.GridSize - 1) / LatticeStride + 1; // 101

    private readonly FixtureCourse _course;
    private readonly double _startE, _startN;
    private readonly HashSet<TileId> _tiles = new();
    private readonly Dictionary<TileId, List<RoadSegment>> _roads = new();
    private readonly Dictionary<TileId, List<TreeInstance>> _trees = new();
    private readonly ConcurrentDictionary<TileId, Lazy<double[]>> _lattices = new();

    public FixtureCourse Course => _course;

    public FixtureChunkSource(FixtureCourse course, double startE, double startN)
    {
        _course = course;
        (_startE, _startN) = (startE, startN);
        var (minX, minY, maxX, maxY) = course.Bounds(course.Roads.Count == 0 ? 100 : 400);
        var lo = TileId.FromLv95(startE + minX, startN + minY);
        var hi = TileId.FromLv95(startE + maxX, startN + maxY);
        for (int e = lo.E; e <= hi.E; e++)
            for (int n = lo.N; n <= hi.N; n++)
                _tiles.Add(new TileId(e, n));

        foreach (var (cls, pts) in course.Roads)
            foreach (var (tile, piece) in SplitAtTiles(pts.Select(p => (startE + p.X, startN + p.Y, p.Z)).ToList()))
            {
                var xyz = new float[piece.Count * 3];
                for (int i = 0; i < piece.Count; i++)
                {
                    xyz[i * 3] = (float)(piece[i].E - tile.MinE);
                    xyz[i * 3 + 1] = (float)piece[i].Z;
                    xyz[i * 3 + 2] = (float)(tile.MaxN - piece[i].N);
                }
                Add(_roads, tile, new RoadSegment
                {
                    Class = cls, Surface = RoadSurface.Paved, Width = RoadFormat.DefaultWidth(cls), Points = xyz,
                });
            }
        foreach (var (x, y, _, height) in course.Trees)
        {
            double e = startE + x, n = startN + y;
            var tile = TileId.FromLv95(e, n);
            Add(_trees, tile, new TreeInstance((float)(e - tile.MinE), (float)course.Ground(x, y),
                (float)(tile.MaxN - n), height, 0));
        }
    }

    /// <summary>The course <c>--chunks fixture:&lt;name&gt;</c> names, starting at the spawn; null for an unknown name.</summary>
    public static FixtureChunkSource? Create(string name, double startE, double startN) =>
        FixtureCourse.Create(name) is { } course ? new FixtureChunkSource(course, startE, startN) : null;

    public IReadOnlyCollection<TileId> Tiles => _tiles;

    private static void Add<T>(Dictionary<TileId, List<T>> into, TileId tile, T item)
    {
        if (!into.TryGetValue(tile, out var list)) into[tile] = list = new();
        list.Add(item);
    }

    /// <summary>
    /// Cuts a polyline (LV95) where it crosses a tile edge, the crossing point ending one piece and
    /// starting the next, so pieces in two tiles share an end (the lane graph links ends).
    /// </summary>
    public static List<(TileId Tile, List<(double E, double N, double Z)> Piece)> SplitAtTiles(List<(double E, double N, double Z)> line)
    {
        var pieces = new List<(TileId Tile, List<(double E, double N, double Z)> Piece)>();
        for (int i = 1; i < line.Count; i++)
        {
            var (a, b) = (line[i - 1], line[i]);
            var cuts = new List<double> { 0, 1 };
            Crossings(a.E, b.E, cuts);
            Crossings(a.N, b.N, cuts);
            cuts.Sort();
            for (int k = 1; k < cuts.Count; k++)
            {
                var p = At(cuts[k - 1]);
                var q = At(cuts[k]);
                // each bit lies in one tile: the one its middle is in (a point on an edge belongs to both)
                var tile = TileId.FromLv95((p.E + q.E) / 2, (p.N + q.N) / 2);
                if (pieces.Count == 0 || pieces[^1].Tile != tile) pieces.Add((tile, new() { p }));
                pieces[^1].Piece.Add(q);
            }

            (double E, double N, double Z) At(double t) =>
                t == 0 ? a : t == 1 ? b : (a.E + (b.E - a.E) * t, a.N + (b.N - a.N) * t, a.Z + (b.Z - a.Z) * t);
        }
        return pieces;

        static void Crossings(double from, double to, List<double> into)
        {
            for (double k = Math.Floor(Math.Min(from, to) / 1000) + 1; k * 1000 < Math.Max(from, to); k++)
                into.Add((k * 1000 - from) / (to - from));
        }
    }

    /// <summary>The ground every 10 m over a tile (row 0 the north edge), built once per tile.</summary>
    private double[] Lattice(TileId id) => _lattices.GetOrAdd(id, t => new Lazy<double[]>(() =>
    {
        var h = new double[LatticeSize * LatticeSize];
        for (int r = 0; r < LatticeSize; r++)
            for (int c = 0; c < LatticeSize; c++)
                h[r * LatticeSize + c] = _course.Ground(t.MinE + c * LatticeStride - _startE, t.MaxN - r * LatticeStride - _startN);
        return h;
    })).Value;

    private static ChunkGrid Grid(TileId id, ushort[] q, int stride) =>
        new(id, q, (float)ChunkFormat.Dequantize(q.Min()), (float)ChunkFormat.Dequantize(q.Max()), stride);

    // ---- IChunkSource --------------------------------------------------------------------

    public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default)
    {
        var m = new TerrainManifest
        {
            SuggestedOriginLv95 = new Lv95Point { E = _startE, N = _startN },
            BoundsLv95 = new Lv95Bounds
            {
                MinE = _tiles.Min(t => t.MinE), MinN = _tiles.Min(t => t.MinN),
                MaxE = _tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM, MaxN = _tiles.Max(t => t.MaxN),
            },
        };
        foreach (var t in _tiles.OrderBy(t => t.E).ThenBy(t => t.N))
        {
            var l = Lattice(t);
            m.Tiles.Add(new ManifestTile { E = t.E, N = t.N, Min = (float)l.Min(), Max = (float)l.Max() });
        }
        return Task.FromResult(m);
    }

    public Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default)
    {
        if (!_tiles.Contains(id)) return Task.FromResult<ChunkGrid?>(null);
        int size = ChunkFormat.GridSize;
        var q = new ushort[size * size];
        if (_course.Terrain is { } terrain)
        {
            // a course whose ground is a function (the lake's river and drop-off): every metre of it
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    q[r * size + c] = ChunkFormat.Quantize(terrain(id.MinE + c - _startE, id.MaxN - r - _startN));
            return Task.FromResult<ChunkGrid?>(Grid(id, q, 1));
        }
        // bilinear between the lattice's 10 m points
        var l = Lattice(id);
        for (int r = 0; r < size; r++)
        {
            int r0 = Math.Min(r / LatticeStride, LatticeSize - 2);
            double fv = (r - r0 * LatticeStride) / (double)LatticeStride;
            for (int c = 0; c < size; c++)
            {
                int c0 = Math.Min(c / LatticeStride, LatticeSize - 2);
                double fu = (c - c0 * LatticeStride) / (double)LatticeStride;
                int k = r0 * LatticeSize + c0;
                double north = l[k] + (l[k + 1] - l[k]) * fu;
                double south = l[k + LatticeSize] + (l[k + LatticeSize + 1] - l[k + LatticeSize]) * fu;
                q[r * size + c] = ChunkFormat.Quantize(north + (south - north) * fv);
            }
        }
        return Task.FromResult<ChunkGrid?>(Grid(id, q, 1));
    }

    public Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default)
    {
        if (!_tiles.Contains(id)) return Task.FromResult<ChunkGrid?>(null);
        return Task.FromResult<ChunkGrid?>(Grid(id, Lattice(id).Select(ChunkFormat.Quantize).ToArray(), LatticeStride));
    }

    public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(_tiles.Contains(id)
            ? new RoadTile { Id = id, Segments = _roads.TryGetValue(id, out var s) ? s : new() }
            : null);

    public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(_tiles.Contains(id) ? new HashSet<int>() : null);

    public Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(_tiles.Contains(id) ? new BuildingTile { Id = id, Buildings = new() } : null);

    /// <summary>The course's cover; all open ground (grass, the cover's zero class) by default.</summary>
    public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default)
    {
        if (!_tiles.Contains(id)) return Task.FromResult<byte[]?>(null);
        var cover = new byte[CoverFormat.Size * CoverFormat.Size];
        if (_course.Cover is { } fn)
            for (int r = 0; r < CoverFormat.Size; r++)
                for (int c = 0; c < CoverFormat.Size; c++)
                    cover[r * CoverFormat.Size + c] = (byte)fn(id.MinE + c - _startE, id.MaxN - r - _startN);
        return Task.FromResult<byte[]?>(cover);
    }

    /// <summary>The course's still water (#299), every 2 m; null for a course with none.</summary>
    public Task<WaterTile?> LoadWaterAsync(TileId id, CancellationToken ct = default)
    {
        if (!_tiles.Contains(id) || _course.Water is not { } water) return Task.FromResult<WaterTile?>(null);
        int n = WaterTile.Size;
        var level = new float[n * n];
        var fetch = new float[n * n];
        bool any = false;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                var (l, f) = water(id.MinE + c * WaterTile.Stride - _startE, id.MaxN - r * WaterTile.Stride - _startN);
                level[r * n + c] = (float)l;
                fetch[r * n + c] = (float)f;
                any |= !double.IsNaN(l);
            }
        return Task.FromResult(any ? new WaterTile { Level = level, FetchM = fetch } : null);
    }

    public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(_tiles.Contains(id) ? (_trees.TryGetValue(id, out var t) ? t : new()) : null);

    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) => Task.FromResult<HorizonIndex?>(null);
}
