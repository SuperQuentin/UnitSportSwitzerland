using System.Collections.Concurrent;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World;

/// <summary>
/// Where a road tile's records lie, for the traffic's graphs (#1636, G2 C6): an LV95 tile's through the frame's LV95
/// (<see cref="OriginFrame.TileToWorld"/>, as the graphs always placed them), a leaf's stand-in tile
/// (<see cref="LeafStandIn"/>) through its leaf's anchor frame, in doubles. Also the snap key of a record point that joins
/// edges across tiles (<see cref="LaneGraph.Key(double, double)"/>): LV95 metres for a tile, the point's place on the
/// Earth for a leaf (<see cref="EarthKey"/>), so a road cut at a leaf's edge meets its other half in the next leaf.
/// One per tile per build; a builder takes it with <see cref="Of"/>.
/// </summary>
public readonly struct TilePlace
{
    private readonly OriginFrame _frame;
    private readonly TileId _id;
    private readonly LeafAnchor? _leaf;

    private TilePlace(OriginFrame frame, TileId id, LeafAnchor? leaf) => (_frame, _id, _leaf) = (frame, id, leaf);

    public static TilePlace Of(OriginFrame frame, RoadTile tile)
    {
        if (frame.Axes != null && Anchored.TryGetValue(tile, out var leaf)) return new(frame, tile.Id, leaf);
        return Of(frame, tile.Id);
    }

    public static TilePlace Of(OriginFrame frame, TileId id) =>
        LeafStandIn.TryLeaf(id, out var leaf) && frame.Axes != null ? new(frame, id, LeafAnchor.Of(leaf, LeafStandIn.Offset)) : new(frame, id, null);

    /// <summary>The tiles <see cref="OnAnchor"/> named: a leaf's own records, in its anchor frame.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoadTile, LeafAnchor> Anchored = new();

    /// <summary>
    /// A leaf's roads as a tile for the graphs without copying them: named as its stand-in (<see cref="LeafStandIn.Id"/>) but
    /// its records left in the leaf's anchor frame, not shifted as a stand-in's are (the copy cost ~0.5 s a dense leaf). Only
    /// a <see cref="TilePlace"/> places it right: never hand it to what places stand-in tiles by their name.
    /// </summary>
    public static RoadTile OnAnchor(RoadTile leafRoads, CubeKey leaf)
    {
        var tile = leafRoads.WithId(LeafStandIn.Id(leaf));
        Anchored.AddOrUpdate(tile, LeafAnchor.Of(leaf, 0));
        return tile;
    }

    /// <summary>Whether the tile is a leaf's stand-in.</summary>
    public bool OnLeaf => _leaf != null;

    /// <summary>A record point (tile-local x, y, z) in the world.</summary>
    public Vector3 World(float x, float y, float z)
    {
        if (_leaf is not { } l) return _frame.TileToWorld(_id, new Vector3(x, y, z));
        var (wx, wy, wz) = _frame.Axes!.FromEcef(l.ToEcef(x, y, z));
        return new((float)wx, (float)wy, (float)wz);
    }

    public Vector3 World(Vector3 local) => World(local.X, local.Y, local.Z);

    /// <summary>A record point's snap key (its plan place alone: x and z, and y only for a leaf, whose plane is its own).</summary>
    public long Key(float x, float y, float z)
    {
        if (_leaf is { } l) return EarthKey(l.ToEcef(x, y, z));
        var (e, n) = _id.Global(x, z);
        return Lv95Key(e, n);
    }

    /// <summary>
    /// A record heading (radians from the tile's east, counterclockwise seen from above) as a world direction. An LV95
    /// tile's goes in as the graphs always took it (the frame's x east, −z north); a leaf's turns from its anchor's axes.
    /// </summary>
    public Vector3 Out(double heading) => Turn(new Vector3((float)Math.Cos(heading), 0f, -(float)Math.Sin(heading)));

    /// <summary>A plan direction in the tile's axes (x east, z south) in the world: unchanged on an LV95 tile, as the graphs took it.</summary>
    public Vector3 Turn(Vector3 local)
    {
        if (_leaf is not { } l) return local;
        var a = _frame.Axes!;
        var d = l.DirectionToEcef(local.X, local.Z);
        return new Vector3((float)FrameAxes.Dot(d, a.X), (float)FrameAxes.Dot(d, a.Y), (float)FrameAxes.Dot(d, a.Z)).Normalized();
    }

    /// <summary>A world point's snap key in a graph built in <paramref name="frame"/>: on leaves by its Earth place, else LV95.</summary>
    public static long KeyOfWorld(OriginFrame frame, Vector3 world, bool onLeaves)
    {
        if (onLeaves && frame.Axes is { } a) return EarthKey(a.ToEcef(world.X, world.Y, world.Z));
        var (e, n) = frame.ToLv95(world);
        return Lv95Key(e, n);
    }

    /// <summary>The snap cell of an LV95 point, laid out like world X/Z (east, then south): <see cref="LaneGraph.Key(double, double)"/>.</summary>
    public static long Lv95Key(double e, double n) =>
        ((long)(int)Math.Round(e / Snap) << 32) ^ (uint)(int)Math.Round(-n / Snap);


    /// <summary>The snap cell (m), the LV95 key's and <see cref="EarthKey"/>'s.</summary>
    private const double Snap = 0.5;

    private const double R = 6_371_000.0;

    /// <summary>
    /// The snap cell of an Earth point, whatever its height: its (geodetic) latitude in <see cref="Snap"/> steps of a sphere's
    /// arc, then its longitude in steps as wide at that row's latitude (square cells, ~0.5 m). Laid out like the LV95 key (high
    /// word north-south, low word east-west), so a ±1 cell neighbourhood reads the same.
    /// </summary>
    public static long EarthKey(EcefPos p)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(p);
        double lat = g.LatDeg * Math.PI / 180.0, lon = g.LonDeg * Math.PI / 180.0;
        int row = (int)Math.Round(lat * R / Snap);
        double c = Math.Cos(row * Snap / R);
        int col = (int)Math.Round(lon * R * c / Snap);
        return ((long)-row << 32) ^ (uint)col;
    }

    /// <summary>A leaf's anchor frame with the stand-in offset folded in, kept per leaf (the anchor's trigonometry once).</summary>
    private sealed class LeafAnchor
    {
        private static readonly ConcurrentDictionary<(CubeKey, double), LeafAnchor> Kept = new();

        private readonly EnuFrame _anchor;
        private readonly double _offset;

        private LeafAnchor(CubeKey leaf, double offset) => (_anchor, _offset) = (leaf.Anchor, offset);

        /// <summary>A leaf's anchor, its records shifted by <paramref name="offset"/> in x and z (a stand-in's <see cref="LeafStandIn.Offset"/>, or 0).</summary>
        public static LeafAnchor Of(CubeKey leaf, double offset)
        {
            if (Kept.Count > 4096) Kept.Clear();
            return Kept.GetOrAdd((leaf, offset), k => new LeafAnchor(k.Item1, k.Item2));
        }

        /// <summary>A tile point (the anchor frame shifted by the offset) in ECEF.</summary>
        public EcefPos ToEcef(double x, double y, double z) => _anchor.ToEcef(x - _offset, y, z - _offset);

        /// <summary>A plan direction (x east, z south) in ECEF.</summary>
        public EcefPos DirectionToEcef(double x, double z)
        {
            var e = _anchor.East;
            var n = _anchor.North;
            return new(e.X * x - n.X * z, e.Y * x - n.Y * z, e.Z * x - n.Z * z);
        }
    }
}
