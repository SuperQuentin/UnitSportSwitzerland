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
/// buildings moved onto the plane. One leaf at a time (the stage's caches are not shared across threads).
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
            var units = new FrameUnits(frame, k => _frames.TransformIn(frame, k), LeafFrames.LeafLevel);
            var id = units.Tile(leaf);
            var (own, _) = Raw(units, id);
            if (own is null) return null;
            RoadTile tile;
            try
            {
                var built = TileRewriter.Rewrite([id], Stage, new TileRewriter.Inputs
                {
                    Units = units,
                    Roads = t => Raw(units, t),
                    Ground = _ => new Ground(world, frame),
                    Buildings = t => Walls(units, t),
                });
                tile = built.Count > 0 ? built[0] : own;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Failed?.Invoke(leaf, e);
                tile = Raw(units, id).Tile!;
            }
            LeafRecords.ToLeaf(tile, units);
            return tile;
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
