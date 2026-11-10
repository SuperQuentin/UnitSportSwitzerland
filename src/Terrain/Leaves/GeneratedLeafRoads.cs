using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The generated world's roads on cube leaves (#1509, G2 B5): RoadGen's network stage on a work frame, through the seams
/// of B3 4a and 4b (<see cref="FrameUnits"/>, an <see cref="IGround"/>, <see cref="LeafRecords"/>), as
/// <c>--cube-network</c> runs it on a region's raw roads. A leaf is rewritten with a halo of <see cref="Halo"/> leaves
/// (~1.2 km, the region stage's), as the LV95 source rewrites a tile with one tile round it.
///
/// <para>
/// The raw input of a leaf is the generator's keyed segments (<see cref="ProceduralWorld.BuildRoadsKeyed"/>) of the LV95
/// tiles under it, cut at its edges (<see cref="LeafSplit"/>), each piece keyed by its line and the metre along it it
/// starts at (the stage hashes per-street choices on them, so a street is laid out the same from every leaf that sees
/// it), moved onto the frame's plane. The ground is the generator's function at a plane point; the walls are its
/// buildings moved onto the plane. One leaf at a time (the stage's caches are not shared across threads); the leaves of a
/// frame share its raw roads, walls and RoadGen's walls and density (#1699: a village leaf ~5 s before, ~1.5 s after).
/// </para>
/// </summary>
public sealed class GeneratedLeafRoads(ProceduralWorld world, LeafFrames? frames = null)
{
    /// <summary>Leaves of halo round the leaf rewritten.</summary>
    public const int Halo = 2;

    private static readonly TileRewriter.Options Stage = new(BlockSize: 1, Halo: Halo, CloseTiles: 0);   // the generator makes leaves on demand: no reads past the halo (#1660)

    private readonly LeafFrames _frames = frames ?? LeafFrames.Swiss;
    private readonly object _gate = new();
    private readonly Dictionary<TileId, (RoadTile? Tile, List<ProceduralWorld.RoadKey> Keys)> _raw = new();
    private readonly Queue<TileId> _rawOrder = new();

    /// <summary>The leaf's finished roads in its anchor frame; null when it has none. The raw pieces if the stage throws.</summary>
    public RoadTile? Build(CubeKey leaf)
    {
        lock (_gate)
        {
            var frame = _frames.FrameOf(leaf);
            var state = StateOf(frame);
            var units = state.Units;
            var id = units.Tile(leaf);
            var (own, _) = state.Raw(id);
            if (own is null) return null;
            RoadTile tile;
            try
            {
                var built = TileRewriter.Rewrite([id], Stage, new TileRewriter.Inputs
                {
                    Units = units,
                    Roads = state.Raw,
                    Ground = _ => new Ground(world, frame),
                    Buildings = state.Walls,
                    Facades = state.Facades,
                    Field = state.Field,
                });
                tile = built.Count > 0 ? built[0] : own;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the shared pair may hold a half-built tile: start the frame over
                _states.Remove(frame.Cell);
                Failed?.Invoke(leaf, e);
                tile = state.Raw(id).Tile!;
            }
            LeafRecords.ToLeaf(tile, units);
            return tile;
        }
    }

    /// <summary>Frames whose shared stage state is held (a frame is ~19 km: the play area is one or two).</summary>
    private const int KeptFrames = 2;

    /// <summary>
    /// Tiles a frame's shared walls and density may hold before they start over (~1.5 MB a tile), as the LV95 source's
    /// (<c>StageSharedTiles</c>). Both are pure functions of position and walls, so starting over changes no output.
    /// </summary>
    private const int SharedTiles = 64;

    private readonly Dictionary<CubeKey, FrameState> _states = new();
    private readonly Queue<CubeKey> _stateOrder = new();

    private FrameState StateOf(WorkFrame frame)
    {
        if (_states.TryGetValue(frame.Cell, out var state) && state.Fresh <= SharedTiles) return state;
        _states.Remove(frame.Cell);
        state = new FrameState(this, frame);
        _states[frame.Cell] = state;
        if (!_stateOrder.Contains(frame.Cell)) _stateOrder.Enqueue(frame.Cell);
        while (_states.Count > KeptFrames && _stateOrder.TryDequeue(out var old))
            if (old == frame.Cell) _stateOrder.Enqueue(old);
            else _states.Remove(old);
        return state;
    }

    /// <summary>
    /// What the stage shares across the leaves of one frame (#1699): its units, its tiles' walls, and RoadGen's walls and
    /// density over them (<see cref="Facades"/>, <see cref="UrbanField"/>), as the LV95 source shares them across tiles.
    /// A frame's tiles are its leaves, so each is a pure function of the frame and the tile: every leaf's roads stay the
    /// same whichever leaves were built before it.
    /// </summary>
    private sealed class FrameState
    {
        public readonly FrameUnits Units;
        public readonly Facades Facades;
        public readonly UrbanField Field;
        private readonly GeneratedLeafRoads _owner;
        private readonly Dictionary<TileId, BuildingTile?> _walls = new();
        private readonly Queue<TileId> _wallOrder = new();

        /// <summary>Walls' tiles the shared pair asked for (it loads a tile's bits once), whether or not they had walls.</summary>
        public int Fresh => _asked.Count;
        private readonly HashSet<TileId> _asked = new();

        public FrameState(GeneratedLeafRoads owner, WorkFrame frame)
        {
            _owner = owner;
            Units = new FrameUnits(frame, k => owner._frames.TransformIn(frame, k), LeafFrames.LeafLevel);
            Facades = new Facades(t => { _asked.Add(t); return Walls(t); }, Units);
            Field = new UrbanField(Facades);
        }

        private readonly Dictionary<TileId, (RoadTile? Tile, RawRoads.Key?[]? Keys)> _raw = new();
        private readonly Queue<TileId> _rawOrder = new();

        /// <summary>
        /// A tile's raw roads and keys on the frame's plane, a fresh copy (the stage writes into the segments it is given),
        /// the last <see cref="SharedTiles"/> kept: every leaf is read by the 25 rewrites round it.
        /// </summary>
        public (RoadTile? Tile, RawRoads.Key?[]? Keys) Raw(TileId id)
        {
            if (!_raw.TryGetValue(id, out var held))
            {
                held = _owner.Raw(Units, id);
                _raw[id] = held;
                _rawOrder.Enqueue(id);
                while (_rawOrder.Count > SharedTiles) _raw.Remove(_rawOrder.Dequeue());
            }
            if (held.Tile is not { } tile) return (null, null);
            var segments = new List<RoadSegment>(tile.Segments.Count);
            foreach (var seg in tile.Segments)
                segments.Add(new RoadSegment
                {
                    Class = seg.Class, Surface = seg.Surface, Flags = seg.Flags, Width = seg.Width,
                    Attributes = seg.Attributes, Points = (float[])seg.Points.Clone(),
                });
            return (new RoadTile { Id = tile.Id, Segments = segments }, (RawRoads.Key?[])held.Keys!.Clone());
        }

        /// <summary>A tile's walls on the frame's plane, the last <see cref="SharedTiles"/> kept (every leaf reads its 5 x 5).</summary>
        public BuildingTile? Walls(TileId id)
        {
            if (_walls.TryGetValue(id, out var held)) return held;
            var built = _owner.Walls(Units, id);
            _walls[id] = built;
            _wallOrder.Enqueue(id);
            while (_wallOrder.Count > SharedTiles) _walls.Remove(_wallOrder.Dequeue());
            return built;
        }
    }

    /// <summary>Told when the stage throws on a leaf (its raw pieces are kept, as the LV95 source keeps a tile's).</summary>
    public Action<CubeKey, Exception>? Failed { get; set; }

    /// <summary>The generator's keyed segments of an LV95 tile, the last 48 kept.</summary>
    private (RoadTile? Tile, List<ProceduralWorld.RoadKey> Keys) Lv95Raw(TileId id)
    {
        if (_raw.TryGetValue(id, out var cached)) return cached;
        var built = world.BuildRoadsKeyed(id);
        _raw[id] = built;
        _rawOrder.Enqueue(id);
        while (_rawOrder.Count > 48) _raw.Remove(_rawOrder.Dequeue());
        return built;
    }

    /// <summary>A leaf's raw roads and keys in the stage's tile-local frame of <paramref name="id"/> (a fresh copy).</summary>
    private (RoadTile? Tile, RawRoads.Key?[]? Keys) Raw(FrameUnits units, TileId id)
    {
        var leaf = units.Leaf(id);
        var segments = new List<RoadSegment>();
        var keys = new List<RawRoads.Key?>();
        double west = units.West(id), north = units.North(id);
        foreach (var lv95 in TilesUnder(leaf))
        {
            var (tile, lineKeys) = Lv95Raw(lv95);
            if (tile is null) continue;
            for (int s = 0; s < tile.Segments.Count; s++)
            {
                var seg = tile.Segments[s];
                var line = new List<(double A, double B, double Z)>(seg.PointCount);
                for (int i = 0; i < seg.PointCount; i++)
                {
                    var (e, n) = lv95.Global(seg.Points[i * 3], seg.Points[i * 3 + 2]);
                    line.Add((e, n, seg.Points[i * 3 + 1]));
                }
                // a piece starts where the pieces before it end: the segment's station plus their length
                double along = 0;
                foreach (var (at, piece) in LeafSplit.AtLeaves(Lv95Source.Instance, line))
                {
                    double from = lineKeys[s].FromM + along;
                    if (at == leaf)
                    {
                        var xyz = new float[piece.Count * 3];
                        for (int i = 0; i < piece.Count; i++)
                        {
                            var (x, y) = PlaneOf(units.Frame, piece[i].A, piece[i].B);
                            (xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]) = ((float)(x - west), (float)piece[i].Z, (float)(north - y));
                        }
                        segments.Add(new RoadSegment
                        {
                            Class = seg.Class, Surface = seg.Surface, Flags = seg.Flags, Width = seg.Width,
                            Attributes = seg.Attributes, Points = xyz,
                        });
                        // to the centimetre, as the preprocessor's .keys files hold them
                        keys.Add(new RawRoads.Key(lineKeys[s].Line, 0, Math.Round(from, 2, MidpointRounding.AwayFromZero)));
                    }
                    along += Length(piece);
                }
            }
        }
        return segments.Count == 0 ? (null, null) : (new RoadTile { Id = id, Segments = segments }, keys.ToArray());
    }

    /// <summary>The generator's buildings near a leaf, on the frame's plane in the stage's tile-local frame of <paramref name="id"/>.</summary>
    private BuildingTile? Walls(FrameUnits units, TileId id)
    {
        var leaf = units.Leaf(id);
        double west = units.West(id), north = units.North(id);
        var list = new List<Building>();
        foreach (var lv95 in TilesUnder(leaf))
        {
            if (world.BuildBuildings(lv95) is not { } bt) continue;
            foreach (var b in bt.Buildings)
            {
                var t = (float[])b.Triangles.Clone();
                bool reaches = false;
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    var (e, n) = lv95.Global(t[k], t[k + 2]);
                    var (x, y) = PlaneOf(units.Frame, e, n);
                    (t[k], t[k + 2]) = ((float)(x - west), (float)(north - y));
                    reaches |= t[k] >= -50 && t[k] <= FrameUnits.Box + 50 && t[k + 2] >= -50 && t[k + 2] <= FrameUnits.Box + 50;
                }
                if (reaches)
                    list.Add(new Building { Kind = b.Kind, Egid = b.Egid, YearBuilt = b.YearBuilt, Floors = b.Floors, MinY = b.MinY, MaxY = b.MaxY, Triangles = t });
            }
        }
        return new BuildingTile { Id = id, Buildings = list };
    }

    /// <summary>The LV95 tiles a leaf's box reaches, its edges bowed a few mm.</summary>
    private IEnumerable<TileId> TilesUnder(CubeKey leaf) =>
        GeneratedLeaves.TilesUnder(new SourceOnLeaf(_frames, leaf, Lv95Source.Instance));

    private static (double X, double Y) PlaneOf(WorkFrame frame, double e, double n)
    {
        var (lat, lon) = Lv95Source.Instance.ToLatLon(e, n);
        return frame.Plane.ToPlane(lat, lon);
    }

    private static double Distance((double A, double B, double Z) p, (double A, double B, double Z) q) =>
        Math.Sqrt((q.A - p.A) * (q.A - p.A) + (q.B - p.B) * (q.B - p.B));

    private static double Length(List<(double A, double B, double Z)> piece)
    {
        double length = 0;
        for (int i = 1; i < piece.Count; i++) length += Distance(piece[i - 1], piece[i]);
        return length;
    }

    /// <summary>The generator's ground at a plane point, above sea level (its notation's latitude and longitude, as the lattice).</summary>
    private sealed class Ground(ProceduralWorld world, WorkFrame frame) : IGround
    {
        public double? Sample(double x, double y)
        {
            var (lat, lon) = frame.Plane.FromPlane(x, y);
            var (e, n) = Lv95Source.Instance.FromLatLon(lat, lon);
            return world.HeightIn(TileId.FromLv95(e, n), e, n);
        }
    }
}
