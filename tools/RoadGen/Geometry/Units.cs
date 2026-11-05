namespace UnitSport.Tools.RoadGen.Geometry;

using UnitSport.Terrain.Format;

/// <summary>
/// The plane the road network stage works on and how its output is cut into tiles (#1507, G2 B3 4a).
/// Every place the stage used to assume the LV95 grid asks this instead: which tile holds a plane point,
/// a tile's corners on the plane (a tile's local x = plane x − <see cref="West"/>, local z =
/// <see cref="North"/> − plane y), the tile's side (the km caches and the edge tests), and a plan-view
/// direction in a tile's own axes (the headings written into the tiles). Plane units are metres: the LV95
/// grid's own (80-190 ppm long, the LV95 build's convention), or a work frame's orthographic plane (within
/// 3 ppm, <c>WorkPlane</c>), so lengths need no scale. Two instances: <see cref="Lv95Units"/> (the plane
/// is LV95, tiles are its km tiles: the LV95 build, byte-identical to before), and the frame instance of
/// 4b. The stage reaches the instance it runs on through <see cref="Plane"/>.
/// </summary>
public interface IUnits
{
    /// <summary>A tile's side on the plane, metres.</summary>
    double TileSizeM { get; }

    /// <summary>The tile holding a plane point (west and south edges in, east and north out).</summary>
    TileId TileAt(double x, double y);

    /// <summary>A tile's west edge on the plane: its local x origin.</summary>
    double West(TileId id);

    /// <summary>A tile's south edge on the plane.</summary>
    double South(TileId id);

    /// <summary>A tile's north edge on the plane: its local z origin (z southward).</summary>
    double North(TileId id);

    /// <summary>
    /// A plan-view direction at plane point <paramref name="at"/> in tile <paramref name="id"/>'s own axes
    /// (x east, y north), not normalised again: what a heading written into the tile is measured from. Both
    /// instances' tiles are plane-aligned (the identity): a frame's loop turns its records into each leaf's
    /// anchor frame, headings by the leaf's Jacobian, when it writes them (<c>LeafRecords</c>).
    /// </summary>
    Vec2 DirectionToTile(TileId id, Vec2 at, Vec2 direction);

    /// <summary>
    /// A plan-view angle at <paramref name="at"/>, counter-clockwise seen from above from any zero direction
    /// (radians), in tile <paramref name="id"/>'s own axes: <see cref="DirectionToTile"/> for headings kept as angles.
    /// </summary>
    double AngleToTile(TileId id, Vec2 at, double angle);

    /// <summary>Whether tile-local (x, z) lies on tile <paramref name="id"/>'s own edge, within <paramref name="eps"/> metres (a cut there).</summary>
    bool OnEdge(TileId id, double x, double z, double eps);

    /// <summary>Whether tile-local (x, z) lies on tile <paramref name="id"/> itself (edges in): past it is the next tile's.</summary>
    bool Inside(TileId id, double x, double z);

    /// <summary>Whether a plane point lies on any tile's edge, within <paramref name="eps"/> metres.</summary>
    bool OnAnyEdge(double x, double y, double eps);

    /// <summary>
    /// The tiles within <paramref name="ring"/> steps of <paramref name="id"/>, itself included (the halo; LV95: the
    /// (2r+1)² square, east outer, north inner; a frame: its leaves' neighbours, across face edges).
    /// </summary>
    IEnumerable<TileId> Around(TileId id, int ring);
}

/// <summary>
/// The LV95 instance: the plane is LV95 (x = E, y = N), tiles are its kilometre tiles (<see cref="TileId"/>'s
/// own arithmetic, the same bits as before #1507), directions on the grid are the tile's.
/// </summary>
public sealed class Lv95Units : IUnits
{
    public static readonly Lv95Units Instance = new();

    private Lv95Units() { }

    public double TileSizeM => 1000.0;
    public TileId TileAt(double x, double y) => TileId.FromLv95(x, y);
    public double West(TileId id) => id.MinE;
    public double South(TileId id) => id.MinN;
    public double North(TileId id) => id.MaxN;
    public Vec2 DirectionToTile(TileId id, Vec2 at, Vec2 direction) => direction;
    public double AngleToTile(TileId id, Vec2 at, double angle) => angle;

    public bool OnEdge(TileId id, double x, double z, double eps) =>
        Math.Abs(x) < eps || Math.Abs(z) < eps || Math.Abs(x - 1000) < eps || Math.Abs(z - 1000) < eps;

    public bool Inside(TileId id, double x, double z) => !(x < 0 || z < 0 || x > 1000 || z > 1000);

    public bool OnAnyEdge(double x, double y, double eps)
    {
        double fx = x / 1000 - Math.Round(x / 1000), fy = y / 1000 - Math.Round(y / 1000);
        return Math.Abs(fx) * 1000 < eps || Math.Abs(fy) * 1000 < eps;
    }

    public IEnumerable<TileId> Around(TileId id, int ring)
    {
        for (int de = -ring; de <= ring; de++)
            for (int dn = -ring; dn <= ring; dn++)
                yield return new TileId(id.E + de, id.N + dn);
    }
}

/// <summary>
/// The units the stage runs on (#1507): <see cref="Lv95Units"/> unless a run says otherwise with
/// <see cref="Use"/>, per thread (the stage is synchronous: a block runs on the thread that called it, and
/// the game runs several on its workers at once). The helpers are the instance's members, and the two
/// conversions between a plane point and a tile's local x, z.
/// </summary>
public static class Plane
{
    [ThreadStatic] private static IUnits? _current;

    public static IUnits Units => _current ?? Lv95Units.Instance;

    /// <summary>Runs on <paramref name="units"/> until the scope is disposed (scopes nest).</summary>
    public static Scope Use(IUnits units)
    {
        var scope = new Scope(_current);
        _current = units;
        return scope;
    }

    public readonly struct Scope(IUnits? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    public static double TileSizeM => Units.TileSizeM;
    public static TileId TileAt(double x, double y) => Units.TileAt(x, y);
    public static double West(TileId id) => Units.West(id);
    public static double South(TileId id) => Units.South(id);
    public static double North(TileId id) => Units.North(id);
    public static Vec2 DirectionToTile(TileId id, Vec2 at, Vec2 direction) => Units.DirectionToTile(id, at, direction);
    public static double AngleToTile(TileId id, Vec2 at, double angle) => Units.AngleToTile(id, at, angle);
    /// <summary><see cref="AngleToTile(TileId, Vec2, double)"/> in the tile holding <paramref name="at"/>.</summary>
    public static double AngleToTile(Vec2 at, double angle) => Units.AngleToTile(TileAt(at.X, at.Y), at, angle);
    public static bool OnEdge(TileId id, double x, double z, double eps) => Units.OnEdge(id, x, z, eps);
    public static bool Inside(TileId id, double x, double z) => Units.Inside(id, x, z);
    public static bool OnAnyEdge(double x, double y, double eps) => Units.OnAnyEdge(x, y, eps);
    public static IEnumerable<TileId> Around(TileId id, int ring) => Units.Around(id, ring);

    /// <summary>A segment's vertex <paramref name="i"/> on the plane (<c>RoadSegment.Lv95</c> on <see cref="Lv95Units"/>).</summary>
    public static (double X, double Y) PointOf(RoadSegment seg, TileId tile, int i) =>
        (West(tile) + seg.Points[i * 3], North(tile) - seg.Points[i * 3 + 2]);
}
