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
    /// The turn from these axes to the local east-up-south of <paramref name="enu"/> (x east, y up, z south, #1503):
    /// a vector in these axes times it is the same vector in the ENU. Its columns are X, Y and Z seen in the ENU.
    /// </summary>
    public Basis InEnu(in EnuFrame enu)
    {
        var f = enu;
        Vector3 Local(EcefPos v)
        {
            var (x, y, z) = f.DirectionToLocal(v);
            return new((float)x, (float)y, (float)z);
        }
        return new Basis(Local(X), Local(Y), Local(Z));
    }

    /// <summary>
    /// The east-up-south frame at an ECEF point (#1503): its origin there, X east, Y the ellipsoid's up, Z south.
    /// What a list of points on the wire is given in (<see cref="OriginFrame.AnchorAt"/>): both peers build it from
    /// the same doubles with the same code, so both get the same axes.
    /// </summary>
    public static FrameAxes Enu(EcefPos origin)
    {
        var enu = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(origin));
        var n = enu.North;
        return new FrameAxes(origin, enu.East, enu.Up, new EcefPos(-n.X, -n.Y, -n.Z));
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

    /// <summary>
    /// The Space anchor under an ECEF point (#1430 H5), with no LV95 in it, so it holds anywhere on the
    /// Earth (LV95 breaks down at two points 90° from Bern, which a polar orbit flies over): its origin on
    /// the WGS84 normal through <paramref name="at"/>, <paramref name="drop"/> metres below it; up the
    /// normal; X and Z <paramref name="previous"/>'s, turned by the least rotation that takes its up to
    /// the new one, so the world turns no more than the Earth's curve under the move.
    /// </summary>
    public static FrameAxes Tangent(EcefPos at, double drop, FrameAxes previous)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(at);
        double lat = g.LatDeg * System.Math.PI / 180, lon = g.LonDeg * System.Math.PI / 180;
        var up = new EcefPos(System.Math.Cos(lat) * System.Math.Cos(lon), System.Math.Cos(lat) * System.Math.Sin(lon), System.Math.Sin(lat));
        var o = new EcefPos(at.X - up.X * drop, at.Y - up.Y * drop, at.Z - up.Z * drop);
        // the least rotation from the old up to the new (Rodrigues, about their cross product)
        var u = previous.Y;
        var k = new EcefPos(u.Y * up.Z - u.Z * up.Y, u.Z * up.X - u.X * up.Z, u.X * up.Y - u.Y * up.X);
        double c = Dot(u, up);
        EcefPos Turn(EcefPos v)
        {
            // v cos + (k × v) + k (k·v) / (1 + cos), k = u × up (its length the sine)
            var kv = new EcefPos(k.Y * v.Z - k.Z * v.Y, k.Z * v.X - k.X * v.Z, k.X * v.Y - k.Y * v.X);
            double f = Dot(k, v) / (1 + c);
            return new EcefPos(v.X * c + kv.X + k.X * f, v.Y * c + kv.Y + k.Y * f, v.Z * c + kv.Z + k.Z * f);
        }
        var x = Turn(previous.X);
        // made square to up again, so rounding never builds up shift after shift
        double along = Dot(x, up);
        x = new EcefPos(x.X - up.X * along, x.Y - up.Y * along, x.Z - up.Z * along);
        double len = x.Length;
        x = new EcefPos(x.X / len, x.Y / len, x.Z / len);
        var z = new EcefPos(x.Y * up.Z - x.Z * up.Y, x.Z * up.X - x.X * up.Z, x.X * up.Y - x.Y * up.X);
        return new FrameAxes(o, x, up, z);
    }
}

/// <summary>
/// A frame's turn to the local east-up-south near a moving body (#1503): <see cref="OriginFrame.EnuBasis"/>, kept
/// while the body stays within 100 m in one frame (the meridian convergence turns 0.001° in that), for what publishes
/// a direction every frame. A mutable struct: keep it in a field and call <see cref="At"/> on the field.
/// </summary>
public struct EnuNear
{
    private OriginFrame? _frame;
    private Vector3 _at;
    private EnuAxes _axes;

    public EnuAxes At(OriginFrame frame, Vector3 world)
    {
        if (!ReferenceEquals(frame, _frame) || _at.DistanceSquaredTo(world) > 100f * 100f)
        {
            _frame = frame;
            _at = world;
            _axes = frame.EnuAt(world);
        }
        return _axes;
    }
}

/// <summary>
/// Some axes at a place against the local east-up-south there (#1503; x east, y up, z south): the turn
/// <see cref="Turn"/> (about up, or any for an anchor frame's axes) and <see cref="Scale"/>, the physical metres a
/// metre along these axes' horizontal is (1 for true metres; the grid's scale for <see cref="GlobalPos"/>'s offset
/// axes and a flat world's). What directions, velocities, headings and rotations cross the wire through.
/// </summary>
public readonly record struct EnuAxes(Basis Turn, float Scale)
{
    /// <summary>A velocity or direction in these axes as the local east-up-south.</summary>
    public Vector3 ToEnu(Vector3 v)
    {
        var r = Turn * v;
        return new(r.X * Scale, r.Y, r.Z * Scale);
    }

    /// <summary>The inverse of <see cref="ToEnu(Vector3)"/>.</summary>
    public Vector3 FromEnu(Vector3 enu)
    {
        var r = Turn.Transposed() * enu;
        return new(r.X / Scale, r.Y, r.Z / Scale);
    }

    /// <summary>The heading of true north in these axes (radians, as <c>Rotation.Y</c>).</summary>
    public float NorthYaw
    {
        get
        {
            var north = Turn.Transposed() * new Vector3(0, 0, -1);
            return Mathf.Atan2(-north.X, -north.Z);
        }
    }

    /// <summary>A heading in these axes as one from true north.</summary>
    public float TrueYaw(float yaw) => Wrap(yaw - NorthYaw);

    /// <summary>A heading from true north in these axes.</summary>
    public float Yaw(float trueYaw) => Wrap(trueYaw + NorthYaw);

    /// <summary>A rotation (a body's basis) in these axes as one in the local east-up-south.</summary>
    public Quaternion ToEnu(Quaternion q) => (Turn * new Basis(q.Normalized())).Orthonormalized().GetRotationQuaternion();

    /// <summary>The inverse of <see cref="ToEnu(Quaternion)"/>.</summary>
    public Quaternion FromEnu(Quaternion enu) =>
        (Turn.Transposed() * new Basis(enu.Normalized())).Orthonormalized().GetRotationQuaternion();

    /// <summary>An angle in [−π, π).</summary>
    public static float Wrap(float yaw) => Mathf.Wrap(yaw, -Mathf.Pi, Mathf.Pi);
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

    /// <summary>
    /// The anchor a list of points travels with on the network since protocol 93 (#1503): world (0, 0, 0) at
    /// <paramref name="origin"/>, its axes the local east, up and south there (<see cref="FrameAxes.Enu"/>), no LV95
    /// in it. The sender maps its points into it with <c>AnchorAt(origin).Since(itsFrame)</c>, the receiver out of
    /// it with <see cref="SinceAnchor(EcefPos)"/>; between two flat frames the pair is exactly their translation,
    /// so nothing is lost on the way. Put near the points, so the floats stay small.
    /// </summary>
    public static OriginFrame AnchorAt(EcefPos origin) => new(FrameAxes.Enu(origin), -1);

    /// <summary>An anchor on the whole kilometre nearest <paramref name="p"/>.</summary>
    public static OriginFrame AnchorNear(GlobalPos p)
    {
        var l = p.Lv95;
        return Anchor(System.Math.Round(l.E / 1000) * 1000, System.Math.Round(l.N / 1000) * 1000);
    }

    public Vector3 ToWorld(double lv95E, double lv95N, double altitude)
    {
        if (Axes == null) return new((float)(lv95E - E), (float)(altitude - Alt0), (float)-(lv95N - N));
        var (x, y, z) = Axes.FromEcef(SwissGeodesy.ToEcef(lv95E, lv95N, altitude));
        return new((float)x, (float)y, (float)z);
    }

    /// <summary>
    /// A place in this world: through its LV95 notation in the flat ground frame (~1 µs, the rigorous projection: the
    /// flat world is LV95's plane), straight from its ECEF in an anchor frame.
    /// </summary>
    public Vector3 ToWorld(GlobalPos p)
    {
        if (Axes == null)
        {
            var (e, n, h) = SwissGeodesy.FromEcef(p.Ecef);
            return new((float)(e - E), (float)(h - Alt0), (float)-(n - N));
        }
        var (x, y, z) = Axes.FromEcef(p.Ecef);
        return new((float)x, (float)y, (float)z);
    }

    public (double E, double N) ToLv95(Vector3 world)
    {
        if (Axes == null) return (E + world.X, N - world.Z);
        var (e, n, _) = SwissGeodesy.FromEcef(Axes.ToEcef(world.X, world.Y, world.Z));
        return (e, n);
    }

    /// <summary>A world point as a place: through the LV95 projection in the flat ground frame (~1 µs), the axes in an anchor frame.</summary>
    public GlobalPos ToGlobal(Vector3 world) => Axes == null
        ? GlobalPos.FromLv95(E + world.X, N - world.Z, world.Y + Alt0)
        : GlobalPos.FromEcef(Axes.ToEcef(world.X, world.Y, world.Z));

    /// <summary>
    /// The altitude (LV95 heights: above sea level) of a world point (#1430 H3): its Y in the ground
    /// frame, its place's height in an anchor frame, where Y is height over the anchor's tangent plane.
    /// </summary>
    public double AltitudeOf(Vector3 world) => Axes == null ? world.Y + Alt0 : SwissGeodesy.Lhn95Height(Axes.ToEcef(world.X, world.Y, world.Z));

    /// <summary>The way up at a world point, in world axes (#1430 H3): +Y in the ground frame, the ellipsoid's up in an anchor frame.</summary>
    public Vector3 UpAt(Vector3 world)
    {
        if (Axes == null) return Vector3.Up;
        var up = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(Axes.ToEcef(world.X, world.Y, world.Z))).Up;
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

    /// <summary>A world point as ECEF (#1430 H5): through the anchor's axes, or the point's LV95 place in the ground frame.</summary>
    public EcefPos ToEcef(Vector3 world) => Axes?.ToEcef(world.X, world.Y, world.Z) ?? ToGlobal(world).Ecef;

    /// <summary>An ECEF point in this world (#1430 H5), the inverse of <see cref="ToEcef(Vector3)"/>.</summary>
    public Vector3 FromEcef(EcefPos p)
    {
        return ToWorld(GlobalPos.FromEcef(p));
    }

    /// <summary>
    /// A world direction at a world point as Earth-fixed (#1430 H5), in doubles: the anchor's axes, or the
    /// grid axes at the point's LV95 place in the ground frame (the flat plane's own axes there).
    /// </summary>
    public EcefPos EcefDirection(Vector3 at, Vector3 dir)
    {
        var axes = Axes;
        if (axes == null)
        {
            var (e, n) = ToLv95(at);
            axes = FrameAxes.GroundAt(e, n, at.Y + Alt0);
        }
        return new(axes.X.X * dir.X + axes.Y.X * dir.Y + axes.Z.X * dir.Z,
            axes.X.Y * dir.X + axes.Y.Y * dir.Y + axes.Z.Y * dir.Z,
            axes.X.Z * dir.X + axes.Y.Z * dir.Y + axes.Z.Z * dir.Z);
    }

    /// <summary>An Earth-fixed direction in world axes at a world point (#1430 H5), the inverse of <see cref="EcefDirection"/>.</summary>
    public Vector3 WorldDirection(Vector3 at, EcefPos dir)
    {
        var axes = Axes;
        if (axes == null)
        {
            var (e, n) = ToLv95(at);
            axes = FrameAxes.GroundAt(e, n, at.Y + Alt0);
        }
        return Turn(dir, axes);
    }

    /// <summary>
    /// This world's axes at a world point against the local east-up-south there (#1503, x east, y up, z south):
    /// in the ground frame the grid's axes at the point (<see cref="GridAxes"/>: a turn about up by the meridian
    /// convergence and the grid's scale, a flat world's metres being grid metres), in an anchor frame its own axes
    /// (a turn, its metres true ones). A few microseconds (rigorous geodesy): per packet, never per vertex.
    /// </summary>
    public EnuAxes EnuAt(Vector3 at)
    {
        if (Axes == null)
        {
            var (e, n) = ToLv95(at);
            return GridAxes(e, n, at.Y + Alt0);
        }
        var p = Axes.ToEcef(at.X, at.Y, at.Z);
        return new EnuAxes(Axes.InEnu(new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(p))), 1f);
    }

    /// <summary>This world's axes at a place against the local east-up-south there (<see cref="EnuAt(Vector3)"/>).</summary>
    public EnuAxes EnuAt(GlobalPos at)
    {
        if (Axes == null)
        {
            var (e, n, h) = SwissGeodesy.FromEcef(at.Ecef);
            return GridAxes(e, n, h);
        }
        return new EnuAxes(Axes.InEnu(at.Enu), 1f);
    }

    /// <summary>
    /// The LV95 grid's axes at a place (grid east, up, grid south: a flat world's) against the local east-up-south
    /// there: a turn about up by the meridian convergence (0 at Bern, ~0.95° at Geneva) and the grid's scale, the
    /// physical metres a grid metre is at that height (1 + 80 to 190·10⁻⁶ on the ground). Two projections (~2 µs).
    /// </summary>
    public static EnuAxes GridAxes(double e, double n, double alt)
    {
        // as FrameAxes.GroundAt(e, n, alt).InEnu(the ENU here), in half the time: grid east is a metre's step
        // along the grid, laid flat in the ENU (the grid's up is the ellipsoid's); grid south is east x up
        var o = SwissGeodesy.ToEcef(e, n, alt);
        var enu = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(o));
        var (x, _, z) = enu.DirectionToLocal(SwissGeodesy.ToEcef(e + 1, n, alt) - o);
        double len = System.Math.Sqrt(x * x + z * z);
        float c = (float)(x / len), s = (float)(z / len);
        return new EnuAxes(new Basis(new Vector3(c, 0, s), Vector3.Up, new Vector3(-s, 0, c)), (float)len);
    }

    /// <summary>A world direction or velocity at a world point as the local east-up-south there: what goes on the wire.</summary>
    public Vector3 EnuOf(Vector3 at, Vector3 worldDir) => EnuAt(at).ToEnu(worldDir);

    /// <summary>The inverse of <see cref="EnuOf"/>: a local east-up-south direction at a world point in world axes.</summary>
    public Vector3 WorldOfEnu(Vector3 at, Vector3 enu) => EnuAt(at).FromEnu(enu);

    /// <summary>
    /// A world heading at a world point (radians, as <c>Rotation.Y</c>) as a heading from true north there (#1503):
    /// the world yaw less the world yaw of true north at the point (<c>WorldOfEnu(at, (0, 0, -1))</c>).
    /// </summary>
    public float TrueYaw(Vector3 at, float worldYaw) => EnuAt(at).TrueYaw(worldYaw);

    /// <summary>The inverse of <see cref="TrueYaw"/>: a heading from true north at a world point as a world heading.</summary>
    public float WorldYaw(Vector3 at, float trueYaw) => EnuAt(at).Yaw(trueYaw);

    /// <summary>A world rotation (a body's) at a world point as a rotation in the local east-up-south there.</summary>
    public Quaternion EnuTurn(Vector3 at, Quaternion world) => EnuAt(at).ToEnu(world);

    /// <summary>The inverse of <see cref="EnuTurn"/>.</summary>
    public Quaternion WorldTurn(Vector3 at, Quaternion enu) => EnuAt(at).FromEnu(enu);

    /// <summary>A direction or velocity in the local east-up-south at a place (a record's, the wire's) in world axes.</summary>
    public Vector3 WorldOfEnu(GlobalPos at, Vector3 enu) => EnuAt(at).FromEnu(enu);

    /// <summary>A heading from true north at a place (a record's, the wire's) as a world heading.</summary>
    public float WorldYaw(GlobalPos at, float trueYaw) => EnuAt(at).Yaw(trueYaw);

    /// <summary>A rotation in the local east-up-south at a place (a record's, the wire's) in world axes.</summary>
    public Quaternion WorldTurn(GlobalPos at, Quaternion enu) => EnuAt(at).FromEnu(enu);

    // ---- LV95 grid records (#1502) ----------------------------------------------------------------------------------
    // A record that keeps a heading, a velocity or a rotation in the LV95 grid's axes at its place (what A2 called
    // GlobalPos's offset axes: the records and the server's flat-world maths, until G2 D moves them to true north) is
    // set into a world through these: free in the flat ground frame, whose axes the grid's are; through the ENU in an
    // anchor frame. GlobalPos's own maths are the ENU's, never these axes.

    /// <summary>A heading on the LV95 grid at <paramref name="at"/> (a record's) as a world heading here.</summary>
    public float WorldYawOfGrid(GlobalPos at, float gridYaw) =>
        Axes == null ? gridYaw : WorldYaw(at, Lv95Grid.TrueYaw(at, gridYaw));

    /// <summary>The inverse of <see cref="WorldYawOfGrid"/>: a world heading at a world point on the LV95 grid.</summary>
    public float GridYawOf(Vector3 at, float worldYaw) =>
        Axes == null ? worldYaw : Lv95Grid.GridYaw(ToGlobal(at), TrueYaw(at, worldYaw));

    /// <summary>A vector in the LV95 grid's axes at <paramref name="at"/> (a record's velocity) in world axes.</summary>
    public Vector3 WorldOfGrid(GlobalPos at, Vector3 grid) =>
        Axes == null ? grid : WorldOfEnu(at, Lv95Grid.Axes(at).ToEnu(grid));

    /// <summary>The inverse of <see cref="WorldOfGrid"/>.</summary>
    public Vector3 GridOfWorld(Vector3 at, Vector3 world) =>
        Axes == null ? world : Lv95Grid.Axes(ToGlobal(at)).FromEnu(EnuOf(at, world));

    /// <summary>A rotation in the LV95 grid's axes at <paramref name="at"/> (a record's) in world axes.</summary>
    public Quaternion WorldTurnOfGrid(GlobalPos at, Quaternion grid) =>
        Axes == null ? grid : WorldTurn(at, Lv95Grid.Axes(at).ToEnu(grid));

    /// <summary>The inverse of <see cref="WorldTurnOfGrid"/>.</summary>
    public Quaternion GridTurnOf(Vector3 at, Quaternion world) =>
        Axes == null ? world : Lv95Grid.Axes(ToGlobal(at)).FromEnu(EnuTurn(at, world));

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
        var p = SwissGeodesy.FromEcef(Axes.ToEcef(world.X, world.Y, world.Z));
        var (lx, lz) = id.Local(p.E, p.N);
        return new Vector3((float)lx, (float)p.H, (float)lz);
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

    /// <summary>
    /// What carries a body standing at <paramref name="from"/> in <paramref name="earlier"/>'s world to the place
    /// <paramref name="to"/> in this one (#1513: a server region's handover, or a teleport into another region): its
    /// position lands on <paramref name="to"/>, and its directions keep their bearing against the local
    /// east-up-south (a heading from true north stays the same, up stays up). Where the two places are one, that is the
    /// frames' rigid map there; across a teleport 300 km away a rigid map would turn the heading by the meridians' 2.8°.
    /// Between two ground frames, the plain translation of <see cref="Since"/> (grid headings kept, as before G2).
    /// A few microseconds: once per handover.
    /// </summary>
    public OriginShift Carry(OriginFrame earlier, Vector3 from, GlobalPos to)
    {
        if (Axes == null && earlier.Axes == null) return Since(earlier);
        var target = ToWorld(to);
        // old world -> local ENU at the old place -> new world at the new place, the grids' scale left out (a turn)
        var turn = (EnuAt(to).Turn.Transposed() * earlier.EnuAt(from).Turn).Orthonormalized();
        return new(new Transform3D(turn, target - turn * from));
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

    /// <summary>
    /// What turns a point given in the ENU at an ECEF origin (<see cref="AnchorAt"/>, the wire's point lists since
    /// protocol 93) into this frame. A few microseconds: once per list, then a transform per point.
    /// </summary>
    public OriginShift SinceAnchor(EcefPos origin) => Since(AnchorAt(origin));

    /// <summary>The inverse of <see cref="SinceAnchor(EcefPos)"/>: what a sender maps its points with to send them in the ENU at <paramref name="origin"/>.</summary>
    public OriginShift ToAnchor(EcefPos origin) => AnchorAt(origin).Since(this);
}

/// <summary>
/// LV95 grid headings and grid vectors at a place (#1502), for the code that still keeps a place as LV95 eastings and
/// northings with a grid-north yaw (a Swiss source's heading, a subsystem not on <see cref="GlobalPos"/> yet; they go
/// at G2 D): at its edge with true north and the local east-up-south. Notation, like <see cref="GlobalPos.Lv95"/>:
/// two projections (~2 µs), never per vertex.
/// </summary>
public static class Lv95Grid
{
    /// <summary>The grid's axes at <paramref name="at"/> against its east-up-south (<see cref="OriginFrame.GridAxes"/>).</summary>
    public static EnuAxes Axes(GlobalPos at)
    {
        var l = at.Lv95;
        return OriginFrame.GridAxes(l.E, l.N, l.H);
    }

    /// <summary>A heading on the LV95 grid at <paramref name="at"/> (radians, as <c>Rotation.Y</c>) as one from true north.</summary>
    public static float TrueYaw(GlobalPos at, float gridYaw) => Axes(at).TrueYaw(gridYaw);

    /// <summary>The inverse of <see cref="TrueYaw"/>: a heading from true north as one on the grid.</summary>
    public static float GridYaw(GlobalPos at, float trueYaw) => Axes(at).Yaw(trueYaw);

    /// <summary>A vector in the grid's axes at <paramref name="at"/> (a record's velocity) in the local east-up-south there.</summary>
    public static Vector3 ToEnu(GlobalPos at, Vector3 grid) => Axes(at).ToEnu(grid);

    /// <summary>The inverse of <see cref="ToEnu"/>.</summary>
    public static Vector3 FromEnu(GlobalPos at, Vector3 enu) => Axes(at).FromEnu(enu);
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

    /// <summary>
    /// The earth world's origin (#1510, G2 C1): an anchor frame from the start, the ENU frame at a point
    /// (<see cref="FrameAxes.Enu"/>), which <see cref="OriginShifter"/> moves without turning until C3.
    /// </summary>
    public WorldOrigin(FrameAxes axes)
    {
        _frame = new OriginFrame(axes, 0);
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

    /// <summary>What turns a position relative to an LV95 anchor into the current world space (<see cref="OriginFrame.SinceAnchor(double, double)"/>).</summary>
    public OriginShift SinceAnchor(double e, double n) => _frame.SinceAnchor(e, n);

    /// <summary>What turns a point in the ENU at an ECEF origin (a list on the wire, #1503) into the current world space.</summary>
    public OriginShift SinceAnchor(EcefPos origin) => _frame.SinceAnchor(origin);

    /// <summary>A world point as ECEF (<see cref="OriginFrame.ToEcef"/>).</summary>
    public EcefPos ToEcef(Vector3 world) => _frame.ToEcef(world);

    /// <summary>A world direction at a world point as the local east-up-south (<see cref="OriginFrame.EnuOf"/>).</summary>
    public Vector3 EnuOf(Vector3 at, Vector3 worldDir) => _frame.EnuOf(at, worldDir);

    /// <summary>A local east-up-south direction at a world point in world axes (<see cref="OriginFrame.WorldOfEnu"/>).</summary>
    public Vector3 WorldOfEnu(Vector3 at, Vector3 enu) => _frame.WorldOfEnu(at, enu);

    /// <summary>A world heading at a world point from true north (<see cref="OriginFrame.TrueYaw"/>).</summary>
    public float TrueYaw(Vector3 at, float worldYaw) => _frame.TrueYaw(at, worldYaw);

    /// <summary>A heading from true north at a world point as a world heading (<see cref="OriginFrame.WorldYaw"/>).</summary>
    public float WorldYaw(Vector3 at, float trueYaw) => _frame.WorldYaw(at, trueYaw);

    /// <summary>A world rotation at a world point in the local east-up-south (<see cref="OriginFrame.EnuTurn(Vector3, Quaternion)"/>).</summary>
    public Quaternion EnuTurn(Vector3 at, Quaternion world) => _frame.EnuTurn(at, world);

    /// <summary>A rotation in the local east-up-south at a world point in world axes.</summary>
    public Quaternion WorldTurn(Vector3 at, Quaternion enu) => _frame.WorldTurn(at, enu);

    public Vector3 ToWorld(double lv95E, double lv95N, double altitude) => _frame.ToWorld(lv95E, lv95N, altitude);

    public Vector3 ToWorld(GlobalPos p) => _frame.ToWorld(p);

    public (double E, double N) ToLv95(Vector3 world) => _frame.ToLv95(world);

    public GlobalPos ToGlobal(Vector3 world) => _frame.ToGlobal(world);

    public TileId TileAt(Vector3 world) => _frame.TileAt(world);

    public Transform3D TileTransform(TileId id) => StandIns?.Invoke(id) is { } t ? t : _frame.TileTransform(id);

    public Vector3 TileToWorld(TileId id, Vector3 local) => StandIns?.Invoke(id) is { } t ? t * local : _frame.TileToWorld(id, local);

    public Vector3 TileLocal(TileId id, Vector3 world) => StandIns?.Invoke(id) is { } t ? t.AffineInverse() * world : _frame.TileLocal(id, world);

    /// <summary>
    /// The earth world's leaves named as tiles (#1510, <c>LeafStandIn</c>): a stand-in tile's frame in the world, null
    /// for a real LV95 tile. Set by <c>EarthWorld</c>; null in the flat world. Goes with step D.
    /// </summary>
    public Func<TileId, Transform3D?>? StandIns { get; set; }

    public double AltitudeOf(Vector3 world) => _frame.AltitudeOf(world);

    public Vector3 UpAt(Vector3 world) => _frame.UpAt(world);
}
