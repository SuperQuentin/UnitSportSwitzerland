using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// An anchor frame's place on the Earth (#1430 H3): its origin and its world X, Y, Z axes as
/// Earth-fixed unit vectors (metres, doubles). A world point w is ECEF <c>Origin + X·w.x + Y·w.y + Z·w.z</c>.
/// </summary>
public sealed record FrameAxes(EcefPos Origin, EcefPos X, EcefPos Y, EcefPos Z)
{
    public static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>A world point (in doubles) as ECEF.</summary>
    public EcefPos ToEcef(double x, double y, double z) => new(
        Origin.X + X.X * x + Y.X * y + Z.X * z,
        Origin.Y + X.Y * x + Y.Y * y + Z.Y * z,
        Origin.Z + X.Z * x + Y.Z * y + Z.Z * z);

    /// <summary>An ECEF point in this frame's world axes, in doubles.</summary>
    public (double X, double Y, double Z) FromEcef(EcefPos p)
    {
        var d = p - Origin;
        return (Dot(d, X), Dot(d, Y), Dot(d, Z));
    }

    /// <summary>
    /// The LV95 ground frame's own axes at a place, as Earth-fixed unit vectors: grid east, the
    /// ellipsoid's up, grid south (world +X, +Y, +Z there), orthonormal. Grid north turns from true
    /// north by the meridian convergence, so these turn with it. What a ground frame looks like seen
    /// from the Earth, near that place: the first Space anchor copies them (H3).
    /// </summary>
    public static FrameAxes GroundAt(double e, double n, double alt)
    {
        var o = SwissGeodesy.ToEcef(e, n, alt);
        var g = SwissGeodesy.ToWgs84(e, n, alt);
        double lat = g.LatDeg * System.Math.PI / 180, lon = g.LonDeg * System.Math.PI / 180;
        var up = new EcefPos(System.Math.Cos(lat) * System.Math.Cos(lon), System.Math.Cos(lat) * System.Math.Sin(lon), System.Math.Sin(lat));
        // grid east: a step along the grid, made square to up
        var east = SwissGeodesy.ToEcef(e + 1, n, alt) - o;
        double along = Dot(east, up);
        east = new EcefPos(east.X - up.X * along, east.Y - up.Y * along, east.Z - up.Z * along);
        double len = east.Length;
        east = new EcefPos(east.X / len, east.Y / len, east.Z / len);
        // +Z is south: X x Y
        var south = new EcefPos(east.Y * up.Z - east.Z * up.Y, east.Z * up.X - east.X * up.Z, east.X * up.Y - east.Y * up.X);
        return new FrameAxes(o, east, up, south);
    }
}

/// <summary>
/// One state of the world origin: the LV95 point that is world (0, 0, 0). Immutable, so a worker
/// thread that took one computes its whole result in one consistent frame, and the main thread
/// then maps that result into whatever the origin has become (<see cref="WorldOrigin.Since"/>).
///
/// <para>
/// Two kinds (#1430 H3). The ground frame (<see cref="Axes"/> null, everything today): the flat
/// LV95 plane, x east, y altitude, z south from a whole-km corner. The anchor frame: an origin
/// and axes on the Earth (<see cref="FrameAxes"/>), the frame of the Space regime; positions map
/// through ECEF in doubles, y is no longer altitude, and a move between two frames may turn the
/// world (<see cref="Since"/>). Its <see cref="E"/> / <see cref="N"/> are its origin's LV95 place.
/// </para>
/// </summary>
public sealed class OriginFrame
{
    public double E { get; }
    public double N { get; }

    /// <summary>Bumped by every move of the origin; two frames with the same epoch are the same.</summary>
    public int Epoch { get; }

    /// <summary>
    /// The ground frame's vertical origin (#1430 H3): the altitude that is world Y = 0. 0 on the ground,
    /// where Y is the altitude; high up (the Space regime, while tiles are flat LV95) the origin rises
    /// with the camera, so floats stay small up there too, and Y is altitude less this.
    /// </summary>
    public double Alt0 { get; }

    /// <summary>The anchor frame's place on the Earth; null for the flat LV95 ground frame.</summary>
    public FrameAxes? Axes { get; }

    public bool IsAnchor => Axes != null;

    public OriginFrame(double e, double n, int epoch)
    {
        E = e;
        N = n;
        Epoch = epoch;
    }

    /// <summary>A ground frame whose world Y = 0 is at altitude <paramref name="alt0"/> (#1430 H3, the Space regime).</summary>
    public OriginFrame(double e, double n, double alt0, int epoch) : this(e, n, epoch) => Alt0 = alt0;

    /// <summary>An anchor frame (#1430 H3): world (0, 0, 0) at <paramref name="axes"/>' origin, its axes the world's.</summary>
    public OriginFrame(FrameAxes axes, int epoch)
    {
        var (e, n, _) = SwissGeodesy.FromEcef(axes.Origin);
        E = e;
        N = n;
        Epoch = epoch;
        Axes = axes;
    }

    /// <summary>The same place and axes (whatever the epoch): nothing to map between them.</summary>
    public bool SameFrame(OriginFrame other) =>
        ReferenceEquals(this, other) || (Axes == null
            ? other.Axes == null && E == other.E && N == other.N && Alt0 == other.Alt0
            : Axes.Equals(other.Axes));

    /// <summary>
    /// A frame that is no peer's origin: the anchor a list of points travels with on the network
    /// (a race road, a bird snapshot), each point a float offset from it, or the frame a server
    /// works one race in. The receiver maps the points with <see cref="WorldOrigin.Since"/>.
    /// Put near the points, so the floats stay small.
    /// </summary>
    public static OriginFrame Anchor(double e, double n) => new(e, n, -1);

    /// <summary>An anchor on the whole kilometre nearest <paramref name="p"/>.</summary>
    public static OriginFrame AnchorNear(GlobalPos p) =>
        Anchor(System.Math.Round(p.E / 1000) * 1000, System.Math.Round(p.N / 1000) * 1000);

    public Vector3 ToWorld(double lv95E, double lv95N, double altitude)
    {
        if (Axes == null) return new((float)(lv95E - E), (float)(altitude - Alt0), (float)-(lv95N - N));
        var (x, y, z) = Axes.FromEcef(SwissGeodesy.ToEcef(lv95E, lv95N, altitude));
        return new((float)x, (float)y, (float)z);
    }

    public Vector3 ToWorld(GlobalPos p) => ToWorld(p.E, p.N, p.Alt);

    public (double E, double N) ToLv95(Vector3 world)
    {
        if (Axes == null) return (E + world.X, N - world.Z);
        var g = ToGlobal(world);
        return (g.E, g.N);
    }

    public GlobalPos ToGlobal(Vector3 world) => Axes == null
        ? new(E + world.X, N - world.Z, world.Y + Alt0)
        : GlobalPos.FromEcef(Axes.ToEcef(world.X, world.Y, world.Z));

    /// <summary>
    /// The altitude (LV95 heights: above sea level) of a world point (#1430 H3): its Y in the ground
    /// frame, its place's height in an anchor frame, where Y is height over the anchor's tangent plane.
    /// </summary>
    public double AltitudeOf(Vector3 world) => Axes == null ? world.Y + Alt0 : ToGlobal(world).Alt;

    /// <summary>The way up at a world point, in world axes (#1430 H3): +Y in the ground frame, the ellipsoid's up in an anchor frame.</summary>
    public Vector3 UpAt(Vector3 world)
    {
        if (Axes == null) return Vector3.Up;
        var g = ToGlobal(world);
        var up = FrameAxes.GroundAt(g.E, g.N, g.Alt).Y;
        return Turn(up, Axes).Normalized();
    }

    /// <summary>
    /// This world in Earth positions (#1430 H4): the anchor's axes, or for the flat ground frame its
    /// tangent frame at its origin (grid axes there, world Y = 0 at <see cref="Alt0"/>), exact at the
    /// origin and the flat plane's own curvature away from it. What places real-geometry things (the
    /// globe) in a world of flat tiles.
    /// </summary>
    public FrameAxes EcefAxes => _ecefAxes ??= Axes ?? FrameAxes.GroundAt(E, N, Alt0);

    private FrameAxes? _ecefAxes;

    /// <summary>
    /// <see cref="EcefAxes"/> made exact at another place of the flat world, LV95 <paramref name="e"/> /
    /// <paramref name="n"/>: the ground frame's tangent frame there, its origin put where that place's
    /// world point (at sea level) is. The flat plane leaves a tangent plane by d²/2R (31 m 20 km out), so
    /// real geometry drawn among flat tiles (the globe) is placed through the embedding under the camera,
    /// where H1 wraps the flat world round. An anchor frame is its own axes everywhere.
    /// </summary>
    public FrameAxes EcefAxesAt(double e, double n) => Axes ?? Embedding(SwissGeodesy.ToEcef(e, n, 0));

    /// <summary>An Earth-fixed direction in this frame's world axes (the ground frame: grid axes at its own origin).</summary>
    public Vector3 DirectionOf(EcefPos ecef)
    {
        var axes = Axes ?? FrameAxes.GroundAt(E, N, 0);
        return Turn(ecef, axes);
    }

    public TileId TileAt(Vector3 world)
    {
        var (e, n) = ToLv95(world);
        return TileId.FromLv95(e, n);
    }

    /// <summary>
    /// Where a tile's local frame sits in this world (#1443, G0c): its north-west corner at altitude 0,
    /// its axes the world's. With the global tiling a tile's frame turns a little against the world's,
    /// so code that places tile-local points goes through this (or <see cref="TileToWorld"/>), never
    /// through the tile's LV95 corners.
    /// </summary>
    public Transform3D TileTransform(TileId id)
    {
        var corner = ToWorld(id.MinE, id.MaxN, 0);
        if (Axes == null) return new(Basis.Identity, corner);
        // in an anchor frame the tile's grid axes at its corner, seen in this world's
        var g = FrameAxes.GroundAt(id.MinE, id.MaxN, 0);
        return new(new Basis(Turn(g.X, Axes), Turn(g.Y, Axes), Turn(g.Z, Axes)), corner);
    }

    /// <summary>An Earth-fixed direction in the world axes of <paramref name="axes"/>.</summary>
    private static Vector3 Turn(EcefPos v, FrameAxes axes) =>
        new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));

    /// <summary>A point in a tile's local frame (x east, y up, z south from its north-west corner) in world space, through doubles.</summary>
    public Vector3 TileToWorld(TileId id, Vector3 local) => ToWorld(id.MinE + local.X, id.MaxN - local.Z, local.Y);

    /// <summary>The inverse of <see cref="TileToWorld"/>: a world point in a tile's local frame, through doubles.</summary>
    public Vector3 TileLocal(TileId id, Vector3 world)
    {
        if (Axes == null)
        {
            var (e, n) = ToLv95(world);
            var (x, z) = id.Local(e, n);
            return new Vector3((float)x, (float)(world.Y + Alt0), (float)z);
        }
        var p = ToGlobal(world);
        var (lx, lz) = id.Local(p.E, p.N);
        return new Vector3((float)lx, (float)p.Alt, (float)lz);
    }

    /// <summary>
    /// What turns a position in <paramref name="earlier"/>'s world space into this one's. Between two
    /// ground frames the exact translation; with an anchor frame on either side (#1430 H3) a rigid
    /// transform through the Earth, the ground frame taken as its own axes at the anchor's place
    /// (<see cref="FrameAxes.GroundAt"/>): exact at that place, the flat plane's own curvature away from it.
    /// </summary>
    public OriginShift Since(OriginFrame earlier)
    {
        if (Axes == null && earlier.Axes == null)
            return new(new Transform3D(Basis.Identity,
                new Vector3((float)(earlier.E - E), (float)(earlier.Alt0 - Alt0), (float)-(earlier.N - N))));
        var at = Axes?.Origin ?? earlier.Axes!.Origin;
        var a = earlier.Embedding(at);
        var b = Embedding(at);
        // w_b = R_b^T (O_a - O_b + R_a w_a)
        return new(new Transform3D(new Basis(Turn(a.X, b), Turn(a.Y, b), Turn(a.Z, b)), Turn(a.Origin - b.Origin, b)));
    }

    /// <summary>This frame as a rigid one on the Earth: an anchor its own axes, the ground frame its axes at <paramref name="at"/>.</summary>
    private FrameAxes Embedding(EcefPos at)
    {
        if (Axes != null) return Axes;
        var (e, n, h) = SwissGeodesy.FromEcef(at);
        var g = FrameAxes.GroundAt(e, n, h);
        // the ground world point there, and the origin that puts it at g's origin along g's axes
        double wx = e - E, wy = h - Alt0, wz = -(n - N);
        return g with { Origin = g.ToEcef(-wx, -wy, -wz) };
    }

    /// <summary>
    /// The same from an anchor given as LV95 (<see cref="Anchor"/> without the object): what turns a
    /// position relative to it into this frame. Nothing allocated, for code that runs every tick.
    /// </summary>
    public OriginShift SinceAnchor(double e, double n) => Axes == null
        ? new(new Transform3D(Basis.Identity, new Vector3((float)(e - E), (float)-Alt0, (float)-(n - N))))
        : Since(Anchor(e, n));
}

/// <summary>
/// Configurable LV95 anchor mapping geodata coordinates to Godot world space:
/// x = east offset, y = altitude, z = -north offset. All terrain math stays in double
/// LV95; floats appear only here at the render/physics boundary.
///
/// <para>
/// The anchor moves while the game runs (#185): <see cref="OriginShifter"/> keeps it near the
/// camera so world coordinates stay small and float32 stays precise however big the world is.
/// A world-space <c>Vector3</c> therefore means a place only in the frame it was computed in.
/// Keep a <see cref="GlobalPos"/> across frames, or handle <see cref="Shifted"/>. A worker thread
/// takes <see cref="Frame"/> once and works in it; the main thread maps the result with
/// <see cref="Since"/>.
/// </para>
/// </summary>
public sealed class WorldOrigin
{
    private volatile OriginFrame _frame;

    public WorldOrigin(double e, double n)
    {
        _frame = new OriginFrame(e, n, 0);
    }

    /// <summary>The current frame: a consistent snapshot, safe to hand to another thread.</summary>
    public OriginFrame Frame => _frame;

    public double E => _frame.E;
    public double N => _frame.N;
    public int Epoch => _frame.Epoch;

    /// <summary>
    /// Main thread, once per move, after every node has been moved (<see cref="OriginShifter"/>):
    /// for whatever keeps world positions outside the scene tree.
    /// </summary>
    public event Action<OriginShift>? Shifted;

    /// <summary>
    /// Fallback anchor for a copy of the game with no terrain data at all — roughly the
    /// centre of Switzerland. Anything is better than LV95 0/0, which is 2.6 million metres
    /// away and would destroy float precision the moment real data arrived.
    /// </summary>
    public static WorldOrigin SwissDefault() => new(2660000, 1190000);

    /// <summary>
    /// <see cref="OriginShifter"/>'s first step: the new frame, and how world space moved. The
    /// caller then moves every node and calls <see cref="RaiseShifted"/>.
    /// </summary>
    internal OriginShift MoveTo(double e, double n, double alt0 = 0)
    {
        var old = _frame;
        _frame = new OriginFrame(e, n, alt0, old.Epoch + 1);
        return _frame.Since(old);
    }

    /// <summary>The ground frame's vertical origin now (#1430 H3): 0 but in the Space regime.</summary>
    public double Alt0 => _frame.Alt0;

    /// <summary>The world in Earth positions now (<see cref="OriginFrame.EcefAxes"/>).</summary>
    public FrameAxes EcefAxes => _frame.EcefAxes;

    /// <summary>The same to an anchor frame (#1430 H3): world (0, 0, 0) and its axes at <paramref name="axes"/>.</summary>
    internal OriginShift MoveTo(FrameAxes axes)
    {
        var old = _frame;
        _frame = new OriginFrame(axes, old.Epoch + 1);
        return _frame.Since(old);
    }

    internal void RaiseShifted(OriginShift shift) => Shifted?.Invoke(shift);

    /// <summary>What turns a position in <paramref name="earlier"/>'s world space into the current one's.</summary>
    public OriginShift Since(OriginFrame earlier) => _frame.Since(earlier);

    /// <summary>What turns a position relative to an LV95 anchor into the current world space (<see cref="OriginFrame.SinceAnchor"/>).</summary>
    public OriginShift SinceAnchor(double e, double n) => _frame.SinceAnchor(e, n);

    public Vector3 ToWorld(double lv95E, double lv95N, double altitude) => _frame.ToWorld(lv95E, lv95N, altitude);

    public Vector3 ToWorld(GlobalPos p) => _frame.ToWorld(p);

    public (double E, double N) ToLv95(Vector3 world) => _frame.ToLv95(world);

    public GlobalPos ToGlobal(Vector3 world) => _frame.ToGlobal(world);

    public TileId TileAt(Vector3 world) => _frame.TileAt(world);

    public Transform3D TileTransform(TileId id) => _frame.TileTransform(id);

    public Vector3 TileToWorld(TileId id, Vector3 local) => _frame.TileToWorld(id, local);

    public Vector3 TileLocal(TileId id, Vector3 world) => _frame.TileLocal(id, world);

    public double AltitudeOf(Vector3 world) => _frame.AltitudeOf(world);

    public Vector3 UpAt(Vector3 world) => _frame.UpAt(world);
}
