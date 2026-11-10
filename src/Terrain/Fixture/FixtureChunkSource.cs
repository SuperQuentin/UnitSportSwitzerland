using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// Serves a <see cref="FixtureCourse"/> as ordinary tiles (#221): <c>--chunks fixture:&lt;course&gt;</c>
/// or <c>--world fixture</c>. The course starts at <c>(startE, startN)</c> (the spawn, so probes that
/// start at <c>--at</c> start on it) and covers the tiles round its roads; nothing exists outside
/// them. Every tile it has answers every asset, empty rather than null, so nothing above it (the
/// streaming cache) fills a gap with real data. No horizon, no buildings, open ground cover.
/// Under <c>--frame earth</c> the tiles are laid where the course's leaves are (<see cref="Place"/>, #1649): the
/// course's metres turned by the grid's convergence, so what reads the tiles meets what the leaves draw.
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
    private readonly Dictionary<TileId, List<Building>> _buildings = new();
    // a laid-out car park (#499), by the tile each piece falls in
    private readonly Dictionary<TileId, List<RoadAreaProp>> _parkAreas = new();
    private readonly Dictionary<TileId, List<RoadPaint>> _parkPaint = new();
    private readonly Dictionary<TileId, List<RoadPointProp>> _parkProps = new();
    private readonly Dictionary<TileId, List<ParkingBay>> _parkBays = new();
    private readonly ConcurrentDictionary<TileId, Lazy<double[]>> _lattices = new();

    public FixtureCourse Course => _course;

    /// <summary>Where the course's metres start, in this source's LV95 tiles (the leaves place it by latitude and longitude, #1509).</summary>
    public (double E, double N) Start => (_startE, _startN);

    /// <summary>A tile's tile-local point (x east, z south) as course metres from the start (#1509).</summary>
    public Func<double, double, (double X, double Y)> CourseOf(TileId tile) => (x, z) =>
    {
        var (e, n) = tile.Global(x, z);
        return Place.Course(e, n);
    };

    /// <summary>The course's tiles a box of course metres reaches (#1509).</summary>
    public IEnumerable<TileId> TilesUnder(double minX, double minY, double maxX, double maxY) =>
        Extent(minX, minY, maxX, maxY) is var (e0, n0, e1, n1) ? TileRect.Covering(e0, n0, e1, n1).Tiles().Where(_tiles.Contains) : [];

    /// <summary>The course's ground height at an LV95 point, m.</summary>
    public double GroundAt(double e, double n) => Place.Course(e, n) is var (x, y) ? _course.Ground(x, y) : 0;

    /// <summary>
    /// Where the course's metres are on the Earth (#1649): the flat world's LV95 grid metres, or the earth world's
    /// tangent plane (<see cref="CoursePlace.OnEarth"/>). Every record of the tiles is laid through it, so under earth a
    /// reader of the tiles (a route, a car park, a stand, the fleet) finds each thing where the course's leaves have it.
    /// </summary>
    public CoursePlace Place { get; }

    /// <summary>The LV95 box (notation) round a box of course metres: its four corners, laid as the course is.</summary>
    private (double MinE, double MinN, double MaxE, double MaxN) Extent(double minX, double minY, double maxX, double maxY)
    {
        var (ae, an) = Place.Lv95(minX, minY);
        var (be, bn) = Place.Lv95(maxX, minY);
        var (ce, cn) = Place.Lv95(minX, maxY);
        var (de, dn) = Place.Lv95(maxX, maxY);
        return (Math.Min(Math.Min(ae, be), Math.Min(ce, de)), Math.Min(Math.Min(an, bn), Math.Min(cn, dn)),
                Math.Max(Math.Max(ae, be), Math.Max(ce, de)), Math.Max(Math.Max(an, bn), Math.Max(cn, dn)));
    }

    public FixtureChunkSource(FixtureCourse course, double startE, double startN, bool earth = false)
    {
        _course = course;
        (_startE, _startN) = (startE, startN);
        Place = earth ? CoursePlace.OnEarth(startE, startN, course.Ground(0, 0)) : CoursePlace.Flat(startE, startN);
        CoursePlace.Active = Place;
        var (minX, minY, maxX, maxY) = course.Bounds(course.Roads.Count == 0 ? 100 : 400);
        var (loE, loN, hiE, hiN) = Extent(minX, minY, maxX, maxY);
        var lo = TileId.FromLv95(loE, loN);
        var hi = TileId.FromLv95(hiE, hiN);
        for (int e = lo.E; e <= hi.E; e++)
            for (int n = lo.N; n <= hi.N; n++)
                _tiles.Add(new TileId(e, n));

        for (int road = 0; road < course.Roads.Count; road++)
        {
            var (cls, pts) = course.Roads[road];
            float width = course.RoadWidths.TryGetValue(road, out float w) ? w : RoadFormat.DefaultWidth(cls);
            foreach (var (tile, piece) in SplitAtTiles(pts.Select(p => Place.Lv95(p.X, p.Y) is var (e, n) ? (e, n, p.Z) : default).ToList()))
            {
                var xyz = new float[piece.Count * 3];
                for (int i = 0; i < piece.Count; i++)
                {
                    var (x, z) = tile.Local(piece[i].E, piece[i].N);
                    xyz[i * 3] = (float)x;
                    xyz[i * 3 + 1] = (float)piece[i].Z;
                    xyz[i * 3 + 2] = (float)z;
                }
                Add(_roads, tile, new RoadSegment
                {
                    Class = cls, Surface = RoadSurface.Paved, Width = width, Points = xyz,
                });
            }
        }
        PlanCarParks();
        PaintLines();
        PlanSlopes();
        Fleet = PlanFleet();
        // the course's venues (#1396) are the venue list while it is played, on the server and every client alike
        if (course.Venues.Count > 0)
        {
            Interiors.Venues.Override = course.Venues.Select((v, i) => Place.Lv95(v.X, v.Y) is var (ve, vn) ? new VenueRecord($"fixture-{course.Name}-{i}", v.Type,
                ve, vn, VenueFix.Inside, v.Flags, v.Count, "", v.Name) : null!).ToList();
            _overrodeVenues = true;
        }
        if (course.StadiumAt is { } stadium)
        {
            Stadiums.StadiumCatalog.UseWorld(stadium(Place));
            _addedStadium = true;
        }
        // buildings (#558), filed by the tile their middle is in
        Interiors.GarageRule.AlwaysRolls = course.AlwaysGarage;
        foreach (var block in course.Blocks)
        {
            var (e, n) = Place.Lv95(block.X, block.Y);
            var tile = TileId.FromLv95(e, n);
            var (bx, bz) = tile.Local(e, n);
            // square to the course, which the earth world turns on the grid by the convergence
            Add(_buildings, tile, FixtureBlocks.Solid(block with { Turn = block.Turn - (float)(Place.Turn * 180 / Math.PI) }, (float)bx, (float)bz));
        }
        foreach (var (x, y, _, height) in course.Trees)
        {
            var (e, n) = Place.Lv95(x, y);
            var tile = TileId.FromLv95(e, n);
            var (tx, tz) = tile.Local(e, n);
            Add(_trees, tile, new TreeInstance((float)tx, (float)course.Ground(x, y), (float)tz, height, 0));
        }
    }

    /// <summary>Whether a course's venues replaced the venue list (#1396): <see cref="ForgetWorld"/> gives the embedded one back.</summary>
    private static bool _overrodeVenues, _addedStadium;

    /// <summary>
    /// What a played course set for the whole process (its venues and fleet, #1396), undone as the world is left; the
    /// venue list only when a course set it, so a check's own hand-made venues stay.
    /// </summary>
    public static void ForgetWorld()
    {
        if (_overrodeVenues) Interiors.Venues.Override = null;
        if (_addedStadium) Stadiums.StadiumCatalog.UseWorld(null);
        _overrodeVenues = _addedStadium = false;
        Fleet = new Dictionary<TileId, List<Vehicles.VehicleSlot>>();
        CoursePlace.Active = null;
    }

    /// <summary>
    /// The parked vehicles of the course last built in this process (#1396), by tile: the dormant layer reads them on
    /// every peer, as it works out a car park's from the tile, so the same slots wake on the server.
    /// </summary>
    public static IReadOnlyDictionary<TileId, List<Vehicles.VehicleSlot>> Fleet { get; private set; } = new Dictionary<TileId, List<Vehicles.VehicleSlot>>();

    private Dictionary<TileId, List<Vehicles.VehicleSlot>> PlanFleet()
    {
        var fleet = new Dictionary<TileId, List<Vehicles.VehicleSlot>>();
        for (int i = 0; i < _course.Fleet.Count; i++)
        {
            var p = _course.Fleet[i];
            // the course point on the Earth in either world (#1649): its LV95 notation, the heading on the grid there
            var (e, n) = Place.Lv95(p.X, p.Y);
            float yaw = Place.GridYaw(p.HeadingDeg);
            Add(fleet, TileId.FromLv95(e, n), new Vehicles.VehicleSlot($"fix_{_course.Name}", i, e, n, _course.Ground(p.X, p.Y), yaw,
                p.Kind, (byte)(i * 37 % 8), false, p.Train, p.Load, Respawns: true));
        }
        return fleet;
    }

    /// <summary>The course's painted lines (#1396, the yard's bays): white, as a car park's are.</summary>
    private void PaintLines()
    {
        foreach (var (x0, y0, x1, y1) in _course.Lines)
        {
            var (e0, n0) = Place.Lv95(x0, y0);
            var (e1, n1) = Place.Lv95(x1, y1);
            var at = TileId.FromLv95((e0 + e1) / 2, (n0 + n1) / 2);
            if (!_tiles.Contains(at)) continue;
            float Y(double x, double y) => (float)_course.Ground(x, y) + 0.02f;
            var (ax, az) = at.Local(e0, n0);
            var (bx, bz) = at.Local(e1, n1);
            Add(_parkPaint, at, new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = PaintType.WhiteSolid, Rgba = 0xE0DED1FF, Width = 0.15f,
                Vertices = [(float)ax, Y(x0, y0), (float)az, (float)bx, Y(x1, y1), (float)bz],
                Indices = [],
            });
        }
    }

    // ---- ski slopes (#1396) ---------------------------------------------------------------------

    private readonly Dictionary<TileId, PisteTile> _pistes = new();

    /// <summary>
    /// The course's ski slopes as the piste stage would write them: one area record per slope in every tile it touches,
    /// the slot raster inside its outline, the edge mask ramping in over <see cref="PisteFormat.EdgeRampM"/>, the groom
    /// phase across the slope (east-west: a course's slopes run north-south).
    /// </summary>
    private void PlanSlopes()
    {
        if (_course.Slopes.Count == 0) return;
        int size = PisteFormat.Size;
        foreach (var tile in _tiles)
        {
            var records = new List<PisteRecord>();
            var slot = new byte[size * size];
            var aux = new ushort[size * size];
            foreach (var slope in _course.Slopes)
            {
                double minX = slope.Outline.Min(p => p.X), maxX = slope.Outline.Max(p => p.X);
                double minY = slope.Outline.Min(p => p.Y), maxY = slope.Outline.Max(p => p.Y);
                var (minE, minN, maxE, maxN) = Extent(minX, minY, maxX, maxY);
                if (!tile.Touches(minE, minN, maxE, maxN)) continue;
                var path = slope.Outline.SelectMany(p =>
                {
                    var (x, y) = Place.Lv95(p.X, p.Y) is var (pe, pn) ? FromSouthWest(tile, pe, pn) : default;
                    return new[] { (float)x, (float)y };
                }).ToArray();
                var (low, high, sun, steep) = SlopeTerrain(_course, slope);
                records.Add(new PisteRecord(0xF0F0_0000UL + (ulong)_course.Slopes.IndexOf(slope), PisteType.Downhill, slope.Difficulty,
                    PisteGrooming.Classic, true, (float)(maxX - minX), slope.Name, "", path, low, high, sun, steep));
                byte index = (byte)records.Count;
                for (int r = 0; r < size; r++)
                    for (int col = 0; col < size; col++)
                    {
                        var (ce, cn) = tile.Global(col, r);
                        var (x, y) = Place.Course(ce, cn);
                        if (!Inside(slope.Outline, x, y)) continue;
                        double edge = Math.Min(Math.Min(x - minX, maxX - x), Math.Min(y - minY, maxY - y));
                        slot[r * size + col] = index;
                        aux[r * size + col] = PisteFormat.PackAux((float)Math.Clamp(edge / PisteFormat.EdgeRampM, 0, 1), x - minX);
                    }
            }
            if (records.Count > 0) _pistes[tile] = new PisteTile(tile, records, slot, aux);
        }
    }

    /// <summary>Whether a course point is inside a slope's outline (even-odd); its leaves rasterise the same (#1649).</summary>
    internal static bool Inside((double X, double Y)[] ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            if ((ring[i].Y > y) != (ring[j].Y > y) && x < (ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>A slope's record fields from the course's ground: its altitudes, sun side and steepness (as <see cref="PlanSlopes"/>).</summary>
    internal static (float Low, float High, float Sun, float Steep) SlopeTerrain(FixtureCourse course, FixtureCourse.Slope slope)
    {
        double minX = slope.Outline.Min(p => p.X), maxX = slope.Outline.Max(p => p.X);
        double minY = slope.Outline.Min(p => p.Y), maxY = slope.Outline.Max(p => p.Y);
        double low = course.Ground((minX + maxX) / 2, minY), high = course.Ground((minX + maxX) / 2, maxY);
        double steep = Math.Atan2(Math.Abs(high - low), maxY - minY) * 180 / Math.PI;
        return ((float)Math.Min(low, high), (float)Math.Max(low, high), high > low ? 0.85f : 0.15f, (float)steep);
    }

    public Task<PisteTile?> LoadPistesAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(_pistes.TryGetValue(id, out var t) ? t : null);

    /// <summary>The course <c>--chunks fixture:&lt;name&gt;</c> names, starting at the spawn; null for an unknown name.</summary>
    /// <param name="earth">The course is played on its leaves (<see cref="Place"/>).</param>
    public static FixtureChunkSource? Create(string name, double startE, double startN, bool earth = false) =>
        FixtureCourse.Create(name) is { } course ? new FixtureChunkSource(course, startE, startN, earth) : null;

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

    /// <summary>
    /// An LV95 point as the field and piste formats hold a ring: metres east and north of the tile's
    /// south-west corner, from the tile's own frame (#1443; its z runs south from the north-west corner).
    /// </summary>
    private static (double X, double Y) FromSouthWest(TileId tile, double e, double n)
    {
        var (x, z) = tile.Local(e, n);
        return (x, ChunkFormat.TileSizeM - z);
    }

    /// <summary>The ground every 10 m over a tile (row 0 the north edge), built once per tile.</summary>
    private double[] Lattice(TileId id) => _lattices.GetOrAdd(id, t => new Lazy<double[]>(() =>
    {
        var h = new double[LatticeSize * LatticeSize];
        for (int r = 0; r < LatticeSize; r++)
            for (int c = 0; c < LatticeSize; c++)
            {
                var (e, n) = t.Global(c * LatticeStride, r * LatticeStride);
                var (x, y) = Place.Course(e, n);
                h[r * LatticeSize + c] = _course.Ground(x, y);
            }
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
                // the tiles' south-west and north-east corners
                MinE = _tiles.Min(t => t.Global(0, ChunkFormat.TileSizeM).E), MinN = _tiles.Min(t => t.Global(0, ChunkFormat.TileSizeM).N),
                MaxE = _tiles.Max(t => t.Global(ChunkFormat.TileSizeM, 0).E), MaxN = _tiles.Max(t => t.Global(ChunkFormat.TileSizeM, 0).N),
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
                {
                    var (e, n) = id.Global(c, r);
                    var (x, y) = Place.Course(e, n);
                    q[r * size + c] = ChunkFormat.Quantize(terrain(x, y));
                }
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
    private sealed class CourseGround(FixtureCourse course, CoursePlace place) : IParkingGround
    {
        public double Ground(double e, double n) => place.Course(e, n) is var (x, y) ? course.Ground(x, y) : 0;

        public bool Occupied(double e, double n)
        {
            var (x, y) = place.Course(e, n);
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
        var ground = new CourseGround(_course, Place);

        foreach (var ring in _course.CarParks)
        {
            var lv95 = ring.Select(p => Place.Lv95(p.X, p.Y)).ToList();
            var borders = new List<ParkingPlanner.Border>();
            foreach (var (cls, pts) in _course.Roads)
            {
                if (pts.Count < 2) continue;
                // the course's roads are straight runs: the nearest point of each to the lot
                var (cE, cN) = (lv95.Average(p => p.E), lv95.Average(p => p.N));
                double best = double.MaxValue, bE = 0, bN = 0, heading = 0;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    var (ax, ay) = Place.Lv95(pts[i].X, pts[i].Y);
                    var (bx, by) = Place.Lv95(pts[i + 1].X, pts[i + 1].Y);
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

            ParkingPlanner.Frontage? frontage = _course.Store is { } st && Place.Lv95(st.DoorX, st.DoorY) is var (doorE, doorN)
                ? new ParkingPlanner.Frontage(doorE, doorN, st.FloorAreaM2, st.Retail)
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
                var (x, z) = at.Local(area.Ring[i * 2], area.Ring[i * 2 + 1]);
                v[i * 3] = (float)x;
                v[i * 3 + 1] = (float)area.Y;
                v[i * 3 + 2] = (float)z;
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
                var (x, z) = at.Local(mark.Line[i * 2], mark.Line[i * 2 + 1]);
                v[i * 3] = (float)x;
                v[i * 3 + 1] = (float)mark.Y;
                v[i * 3 + 2] = (float)z;
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
            var (fx, fz) = at.Local(f.E, f.N);
            if (f.Kind == ParkingPlanner.FixtureKind.Tree)
            {
                Add(_trees, at, new TreeInstance((float)fx, (float)f.Y, (float)fz, 5.5f, 3));
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
                (float)fx, (float)f.Y, (float)fz,
                (float)ParkingPlanner.ToGodotHeading(f.HeadingRad), height));
        }

        foreach (var b in lot.Bays)
        {
            var at = TileId.FromLv95(b.E, b.N);
            if (!_tiles.Contains(at)) continue;
            var (bx, bz) = at.Local(b.E, b.N);
            Add(_parkBays, at, new ParkingBay(
                (float)bx, (float)b.Y, (float)bz,
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
        Task.FromResult(_tiles.Contains(id) ? new BuildingTile { Id = id, Buildings = _buildings.TryGetValue(id, out var b) ? b : new() } : null);

    /// <summary>The course's cover; all open ground (grass, the cover's zero class) by default.</summary>
    public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default)
    {
        if (!_tiles.Contains(id)) return Task.FromResult<byte[]?>(null);
        var cover = new byte[CoverFormat.Size * CoverFormat.Size];
        if (_course.Cover is { } fn)
            for (int r = 0; r < CoverFormat.Size; r++)
                for (int c = 0; c < CoverFormat.Size; c++)
                {
                    var (e, n) = id.Global(c, r);
                    var (x, y) = Place.Course(e, n);
                    cover[r * CoverFormat.Size + c] = (byte)fn(x, y);
                }
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
                var (e, nn) = id.Global(c * WaterTile.Stride, r * WaterTile.Stride);
                var (wx, wy) = Place.Course(e, nn);
                var (l, f) = water(wx, wy);
                level[r * n + c] = (float)l;
                fetch[r * n + c] = (float)f;
                any |= !double.IsNaN(l);
            }
        return Task.FromResult(any ? new WaterTile { Level = level, FetchM = fetch } : null);
    }

    public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) =>
        Task.FromResult(_tiles.Contains(id) ? (_trees.TryGetValue(id, out var t) ? t : new()) : null);


    /// <summary>
    /// The course's farm fields (#494), each written whole into every tile its box touches, ring
    /// points in metres from the tile's south-west corner (<see cref="FieldFormat"/>); empty, not null.
    /// </summary>
    public Task<List<FieldPolygon>?> LoadFieldsAsync(TileId id, CancellationToken ct = default)
    {
        if (!_tiles.Contains(id)) return Task.FromResult<List<FieldPolygon>?>(null);
        var fields = new List<FieldPolygon>();
        foreach (var (fid, crop, outline) in _course.Fields)
        {
            var (minE, minN, maxE, maxN) = Extent(outline.Min(p => p.X), outline.Min(p => p.Y), outline.Max(p => p.X), outline.Max(p => p.Y));
            if (!id.Touches(minE, minN, maxE, maxN)) continue;
            var ring = new float[outline.Length * 2];
            for (int i = 0; i < outline.Length; i++)
            {
                var (x, y) = Place.Lv95(outline[i].X, outline[i].Y) is var (oe, on) ? FromSouthWest(id, oe, on) : default;
                ring[i * 2] = (float)x;
                ring[i * 2 + 1] = (float)y;
            }
            fields.Add(new FieldPolygon(fid, crop, FieldSource.Osm, 0, [ring]));
        }
        return Task.FromResult<List<FieldPolygon>?>(fields);
    }
    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) => Task.FromResult<HorizonIndex?>(null);

    /// <summary>The course's stops and jetties (#377), planned over its own ground and water as the preprocessor plans the real ones.</summary>
    public Task<LandingIndex?> LoadLandingsAsync(CancellationToken ct = default)
    {
        if (_course.Stops.Count == 0 && _course.Jetties.Count == 0) return Task.FromResult<LandingIndex?>(null);
        return Task.Run(() =>
        {
            var shore = new Shore(_course, Place);
            var index = new LandingIndex();
            foreach (var (name, x, y) in _course.Stops)
                if (Place.Lv95(x, y) is var (le, ln) && LandingPlanner.PlanLanding(name, le, ln, shore) is { } landing) index.Landings.Add(landing);
            int k = 0;
            foreach (var line in _course.Jetties)
                if (LandingPlanner.PlanJetty($"fixture-{k++}", line.Select(p => Place.Lv95(p.X, p.Y) is var (je, jn) ? (je, jn, p.Z) : default).ToList(), shore) is { } jetty)
                    index.Jetties.Add(jetty);
            return (LandingIndex?)index;
        }, ct);
    }

    /// <summary>The course's airports (#422), planned as the preprocessor plans the real ones.</summary>
    public Task<AirportIndex?> LoadAirportsAsync(CancellationToken ct = default) =>
        Task.FromResult(_course.Airports?.Invoke(Place));

    /// <summary>The course's ground and water for the pier planner, in LV95.</summary>
    private sealed class Shore(FixtureCourse course, CoursePlace place) : IShoreSampler
    {
        public double Ground(double e, double n) => place.Course(e, n) is var (x, y) ? course.Ground(x, y) : 0;

        public double Level(double e, double n) =>
            course.Water is { } water && place.Course(e, n) is var (x, y) ? water(x, y).Level : double.NaN;
    }
}
