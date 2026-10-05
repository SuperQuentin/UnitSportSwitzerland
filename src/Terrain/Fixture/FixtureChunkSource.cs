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
    // a laid-out car park (#499), by the tile each piece falls in
    private readonly Dictionary<TileId, List<RoadAreaProp>> _parkAreas = new();
    private readonly Dictionary<TileId, List<RoadPaint>> _parkPaint = new();
    private readonly Dictionary<TileId, List<RoadPointProp>> _parkProps = new();
    private readonly Dictionary<TileId, List<ParkingBay>> _parkBays = new();
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
        PlanCarParks();

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


    /// <summary>Flat ground over the course, and nothing standing on it but its own roads.</summary>
    private sealed class CourseGround(FixtureCourse course, double startE, double startN) : IParkingGround
    {
        public double Ground(double e, double n) => course.Ground(e - startE, n - startN);

        public bool Occupied(double e, double n)
        {
            double x = e - startE, y = n - startN;
            foreach (var (cls, pts) in course.Roads)
            {
                double half = RoadFormat.DefaultWidth(cls) * 0.5 + 2.0;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    double ax = pts[i].X, ay = pts[i].Y, bx = pts[i + 1].X, by = pts[i + 1].Y;
                    double dx = bx - ax, dy = by - ay;
                    double len2 = dx * dx + dy * dy;
                    double t = len2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1);
                    double px = ax + dx * t - x, py = ay + dy * t - y;
                    if (px * px + py * py <= half * half) return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Runs the real <c>ParkingPlanner</c> over the course's rings and files the pieces by tile,
    /// exactly as <c>TileRewriter.PlanParking</c> does for the map (#499). The course exists so the
    /// layout can be walked, driven and screenshotted with no terrain data at all.
    /// </summary>
    private void PlanCarParks()
    {
        if (_course.CarParks.Count == 0) return;
        var ground = new CourseGround(_course, _startE, _startN);

        foreach (var ring in _course.CarParks)
        {
            var lv95 = ring.Select(p => (E: _startE + p.X, N: _startN + p.Y)).ToList();
            var borders = new List<ParkingPlanner.Border>();
            foreach (var (cls, pts) in _course.Roads)
            {
                if (pts.Count < 2) continue;
                // the course's roads are straight runs: the nearest point of each to the lot
                var (cE, cN) = (lv95.Average(p => p.E), lv95.Average(p => p.N));
                double best = double.MaxValue, bE = 0, bN = 0, heading = 0;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    double ax = _startE + pts[i].X, ay = _startN + pts[i].Y;
                    double bx = _startE + pts[i + 1].X, by = _startN + pts[i + 1].Y;
                    double dx = bx - ax, dy = by - ay;
                    double len2 = dx * dx + dy * dy;
                    double t = len2 < 1e-9 ? 0 : Math.Clamp(((cE - ax) * dx + (cN - ay) * dy) / len2, 0, 1);
                    double pe = ax + dx * t, pn = ay + dy * t;
                    double d = (pe - cE) * (pe - cE) + (pn - cN) * (pn - cN);
                    if (d < best) { best = d; bE = pe; bN = pn; heading = Math.Atan2(dy, dx); }
                }
                if (best > 40 * 40) continue;
                borders.Add(new ParkingPlanner.Border(bE, bN, heading,
                    cls <= RoadClass.Major ? 3 : 1, RoadFormat.DefaultWidth(cls) * 0.5, false));
            }

            ParkingPlanner.Frontage? frontage = _course.Store is { } st
                ? new ParkingPlanner.Frontage(_startE + st.DoorX, _startN + st.DoorY, st.FloorAreaM2, st.Retail)
                : null;

            var lot = ParkingPlanner.Plan(lv95, borders, ground, frontage);
            if (lot.Rejected != null) continue;
            FileLot(lot);
        }
    }

    /// <summary>One planned lot into the tile records, by the tile each piece falls in.</summary>
    private void FileLot(ParkingPlanner.Lot lot)
    {
        foreach (var area in lot.Areas)
        {
            var at = TileId.FromLv95(
                (area.Ring[0] + area.Ring[4]) * 0.5, (area.Ring[1] + area.Ring[5]) * 0.5);
            if (!_tiles.Contains(at)) continue;
            var v = new float[12];
            for (int i = 0; i < 4; i++)
            {
                v[i * 3] = (float)(area.Ring[i * 2] - at.MinE);
                v[i * 3 + 1] = (float)area.Y;
                v[i * 3 + 2] = (float)(at.MaxN - area.Ring[i * 2 + 1]);
            }
            Add(_parkAreas, at, new RoadAreaProp
            {
                Type = area.Kind switch
                {
                    ParkingPlanner.AreaKind.Island => AreaPropType.ParkingIsland,
                    ParkingPlanner.AreaKind.Walk => AreaPropType.Sidewalk,
                    _ => AreaPropType.ParkingPad,
                },
                Flags = area.Height > 0 ? PropFlags.Solid : PropFlags.None,
                Height = (float)area.Height, Vertices = v, Indices = [0, 1, 2, 0, 2, 3],
            });
        }

        foreach (var mark in lot.Marks)
        {
            double mE = (mark.Line[0] + mark.Line[^2]) * 0.5, mN = (mark.Line[1] + mark.Line[^1]) * 0.5;
            var at = TileId.FromLv95(mE, mN);
            if (!_tiles.Contains(at)) continue;
            var v = new float[mark.Line.Length / 2 * 3];
            for (int i = 0; i < mark.Line.Length / 2; i++)
            {
                v[i * 3] = (float)(mark.Line[i * 2] - at.MinE);
                v[i * 3 + 1] = (float)mark.Y;
                v[i * 3 + 2] = (float)(at.MaxN - mark.Line[i * 2 + 1]);
            }
            // the glyphs are built by the paint layer in the tools; the fixture only needs the
            // lines, so a glyph becomes a short line across its bay rather than nothing at all
            Add(_parkPaint, at, new RoadPaint
            {
                Shape = PaintShape.Polyline,
                Type = mark.Type == PaintType.Arrow ? PaintType.WhiteSolid : mark.Type,
                Rgba = 0xE0DED1FF, Width = (float)Math.Max(mark.Width, 0.12), Vertices = v, Indices = [],
            });
        }

        foreach (var f in lot.Fixtures)
        {
            var at = TileId.FromLv95(f.E, f.N);
            if (!_tiles.Contains(at)) continue;
            if (f.Kind == ParkingPlanner.FixtureKind.Tree)
            {
                Add(_trees, at, new TreeInstance(
                    (float)(f.E - at.MinE), (float)f.Y, (float)(at.MaxN - f.N), 5.5f, 3));
                continue;
            }
            var (type, height) = f.Kind switch
            {
                ParkingPlanner.FixtureKind.Barrier => (PointPropType.TicketBarrier, 1.1f),
                ParkingPlanner.FixtureKind.Kiosk => (PointPropType.TicketKiosk, 1.5f),
                ParkingPlanner.FixtureKind.Shelter => (PointPropType.CartShelter, 2.4f),
                _ => (PointPropType.ParkingSign, 2.1f),
            };
            Add(_parkProps, at, new RoadPointProp(type, f.Variant, PropFlags.Solid,
                (float)(f.E - at.MinE), (float)f.Y, (float)(at.MaxN - f.N),
                (float)ParkingPlanner.ToGodotHeading(f.HeadingRad), height));
        }

        foreach (var b in lot.Bays)
        {
            var at = TileId.FromLv95(b.E, b.N);
            if (!_tiles.Contains(at)) continue;
            Add(_parkBays, at, new ParkingBay(
                (float)(b.E - at.MinE), (float)b.Y, (float)(at.MaxN - b.N),
                (float)ParkingPlanner.ToGodotHeading(b.HeadingRad), b.Kind, b.Flags));
        }
    }

    public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(_tiles.Contains(id)
            ? new RoadTile
            {
                Id = id,
                Segments = _roads.TryGetValue(id, out var s) ? s : new(),
                AreaProps = _parkAreas.TryGetValue(id, out var pa) ? pa : new(),
                Paint = _parkPaint.TryGetValue(id, out var pp) ? pp : new(),
                PointProps = _parkProps.TryGetValue(id, out var px) ? px : new(),
                Parking = _parkBays.TryGetValue(id, out var pb) ? pb : new(),
            }
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

    /// <summary>The course's stops and jetties (#377), planned over its own ground and water as the preprocessor plans the real ones.</summary>
    public Task<LandingIndex?> LoadLandingsAsync(CancellationToken ct = default)
    {
        if (_course.Stops.Count == 0 && _course.Jetties.Count == 0) return Task.FromResult<LandingIndex?>(null);
        return Task.Run(() =>
        {
            var shore = new Shore(_course, _startE, _startN);
            var index = new LandingIndex();
            foreach (var (name, x, y) in _course.Stops)
                if (LandingPlanner.PlanLanding(name, _startE + x, _startN + y, shore) is { } landing) index.Landings.Add(landing);
            int k = 0;
            foreach (var line in _course.Jetties)
                if (LandingPlanner.PlanJetty($"fixture-{k++}", line.Select(p => (_startE + p.X, _startN + p.Y, p.Z)).ToList(), shore) is { } jetty)
                    index.Jetties.Add(jetty);
            return (LandingIndex?)index;
        }, ct);
    }

    /// <summary>The course's ground and water for the pier planner, in LV95.</summary>
    private sealed class Shore(FixtureCourse course, double startE, double startN) : IShoreSampler
    {
        public double Ground(double e, double n) => course.Ground(e - startE, n - startN);

        public double Level(double e, double n) =>
            course.Water is { } water ? water(e - startE, n - startN).Level : double.NaN;
    }
}
