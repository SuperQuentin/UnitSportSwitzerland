using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// True north against the game's axes (#1504, G2 A3). The flat world's −Z is LV95 grid north, which turns from
/// true north by the meridian convergence: 0 on Bern's meridian, about 0.9° at Geneva and in the Engadine (the
/// other way), more further out. What a person reads as a heading (the compass strips, the cockpit's HDG and track,
/// a photo's bearing) comes from here, never from world −Z; data stored in grid north (berths, givers, traffic
/// endpoints, the LV95 literal catalogues) is turned to true north at load with <see cref="GridToTrue"/> / <see cref="GridYawToTrue"/> and placed
/// with <see cref="WorldYaw"/> (in world axes) or <see cref="OffsetYaw"/> (in a record). Correct in the flat world and in an anchor frame alike, so it is not behind
/// <c>--frame earth</c>.
/// <para>
/// Bearings are radians clockwise from north (a compass); yaws are Godot's (0 faces −Z, positive turns left), so a
/// yaw is minus a bearing. A grid bearing turns to a true one by subtracting the convergence; a grid yaw by adding it.
/// </para>
/// </summary>
public static class TrueNorth
{
    /// <summary>
    /// The meridian convergence at an LV95 place: the bearing of true north on the LV95 grid there, radians clockwise
    /// from grid north. 0 on Bern's meridian, positive west of it (Geneva, ~0.9°), negative east of it. A few
    /// microseconds (rigorous geodesy): per object at load, never per frame or vertex.
    /// </summary>
    public static double GridConvergence(double e, double n, double alt = 500)
    {
        var axes = FrameAxes.GroundAt(e, n, alt);
        var north = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(axes.Origin)).North;
        return System.Math.Atan2(FrameAxes.Dot(north, axes.X), -FrameAxes.Dot(north, axes.Z));
    }

    /// <summary>
    /// A bearing measured on the LV95 grid at LV95 <paramref name="e"/> / <paramref name="n"/> (a Swiss source's,
    /// a literal's) as one from true north, radians clockwise: what data stored in grid north is turned by at load.
    /// Always the grid's own convergence, whatever <see cref="GlobalPos"/> holds inside.
    /// </summary>
    public static double GridToTrue(double e, double n, double gridBearing) => gridBearing - GridConvergence(e, n);

    /// <summary>A Godot yaw on the LV95 grid (0 faces grid north) as one from true north (see <see cref="GridToTrue"/>).</summary>
    public static float GridYawToTrue(double e, double n, float gridYaw) => (float)(gridYaw + GridConvergence(e, n));

    /// <summary><see cref="GridToTrue(double, double, double)"/> at a place.</summary>
    public static double GridToTrue(GlobalPos at, double gridBearing) => gridBearing - at.GridConvergence;

    /// <summary><see cref="GridYawToTrue(double, double, float)"/> at a place.</summary>
    public static float GridYawToTrue(GlobalPos at, float gridYaw) => (float)(gridYaw + at.GridConvergence);

    /// <summary>
    /// A heading stored in grid north (a Godot yaw) at a world point as a world yaw in <paramref name="frame"/>: turned to
    /// true north at the place, then into the frame (<see cref="WorldYaw"/>). The same yaw on the flat ground (to float
    /// precision), the right one in an anchor frame.
    /// </summary>
    public static float GridYawToWorld(OriginFrame frame, Vector3 at, float gridYaw) =>
        WorldYaw(frame, at, GridYawToTrue(frame.ToGlobal(at), gridYaw));

    /// <summary>The inverse of <see cref="GridYawToWorld"/>: a world yaw at a world point as one from grid north, to store.</summary>
    public static float WorldYawToGrid(OriginFrame frame, Vector3 at, float worldYaw) =>
        (float)(TrueYaw(frame, at, worldYaw) - frame.ToGlobal(at).GridConvergence);

    /// <summary>
    /// A heading from true north (a Godot yaw) at <paramref name="at"/> as a yaw in <see cref="GlobalPos"/>'s offset
    /// axes, what a record keeps beside its position (<c>VehicleState</c>, a slot): the same as the grid yaw while those
    /// axes are the grid's, the true one once A1 puts ECEF inside.
    /// </summary>
    public static float OffsetYaw(GlobalPos at, float trueYaw) => (float)(trueYaw - at.Convergence);

    /// <summary>The inverse of <see cref="OffsetYaw"/>.</summary>
    public static float TrueYawOfOffset(GlobalPos at, float offsetYaw) => (float)(offsetYaw + at.Convergence);

    /// <summary>
    /// True north at a world point, as a horizontal unit direction in the frame's world axes: the local north at the
    /// point's place on the Earth (<c>frame.WorldDirection</c> of the ENU north there), laid flat on the local up.
    /// In the flat ground frame, −Z turned by the convergence at the point.
    /// </summary>
    public static Vector3 WorldNorth(OriginFrame frame, Vector3 at)
    {
        var north = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(frame.ToEcef(at))).North;
        var d = frame.WorldDirection(at, north);
        var up = frame.UpAt(at);
        d -= up * d.Dot(up);
        return d.Normalized();
    }

    /// <summary>The bearing of true north in world axes at a world point (radians clockwise from world −Z, X to the right).</summary>
    public static float NorthBearing(OriginFrame frame, Vector3 at)
    {
        var n = WorldNorth(frame, at);
        return Mathf.Atan2(n.X, -n.Z);
    }

    /// <summary>A world direction at a world point as a compass bearing from true north (radians, clockwise).</summary>
    public static float Bearing(OriginFrame frame, Vector3 at, Vector3 worldDir) =>
        Mathf.Wrap(Mathf.Atan2(worldDir.X, -worldDir.Z) - NorthBearing(frame, at), -Mathf.Pi, Mathf.Pi);

    /// <summary>A heading from true north at a world point (a Godot yaw: 0 faces north, positive turns left) as a world yaw.</summary>
    public static float WorldYaw(OriginFrame frame, Vector3 at, float trueYaw) =>
        Mathf.Wrap(trueYaw - NorthBearing(frame, at), -Mathf.Pi, Mathf.Pi);

    /// <summary>A world yaw at a world point as a heading from true north (the inverse of <see cref="WorldYaw"/>).</summary>
    public static float TrueYaw(OriginFrame frame, Vector3 at, float worldYaw) =>
        Mathf.Wrap(worldYaw + NorthBearing(frame, at), -Mathf.Pi, Mathf.Pi);

    /// <summary>
    /// An (east, north) direction turned from axes where true north bears <paramref name="fromNorth"/> to axes where
    /// it bears <paramref name="toNorth"/> (radians clockwise): a world heading into <see cref="GlobalPos"/>'s offset axes.
    /// </summary>
    public static Vector2 Turn(Vector2 eastNorth, float fromNorth, float toNorth)
    {
        float d = toNorth - fromNorth;
        if (d == 0f) return eastNorth;
        float c = Mathf.Cos(d), s = Mathf.Sin(d);
        return new Vector2(eastNorth.X * c + eastNorth.Y * s, -eastNorth.X * s + eastNorth.Y * c);
    }

    /// <summary>
    /// True north for a viewer read every frame (a compass, a cockpit): its bearing in world axes
    /// (<see cref="NorthBearing"/>) and in <see cref="GlobalPos"/>'s offset axes (<see cref="GlobalPos.Convergence"/>),
    /// kept while the viewer stays within 100 m in one frame (the convergence turns ~0.001° in that), so standing or
    /// flying costs two geodesy calls every 100 m. A mutable struct: keep it in a field and call it on the field.
    /// </summary>
    public struct Near
    {
        private OriginFrame? _frame;
        private Vector3 _at;
        private float _world, _offset;

        /// <summary>The bearing of true north in world axes near <paramref name="at"/>, radians clockwise.</summary>
        public float World(OriginFrame frame, Vector3 at)
        {
            Refresh(frame, at);
            return _world;
        }

        /// <summary>The bearing of true north in <see cref="GlobalPos"/>'s offset axes near <paramref name="at"/>, radians clockwise.</summary>
        public float Offset(OriginFrame frame, Vector3 at)
        {
            Refresh(frame, at);
            return _offset;
        }

        /// <summary>A world (east, north) direction near <paramref name="at"/> in <see cref="GlobalPos"/>'s offset axes (the identity on the flat ground).</summary>
        public Vector2 WorldToOffset(OriginFrame frame, Vector3 at, Vector2 eastNorth)
        {
            Refresh(frame, at);
            return Turn(eastNorth, _world, _offset);
        }

        private void Refresh(OriginFrame frame, Vector3 at)
        {
            if (ReferenceEquals(frame, _frame) && _at.DistanceSquaredTo(at) <= 100f * 100f) return;
            _frame = frame;
            _at = at;
            _world = NorthBearing(frame, at);
            // on the flat ground the world's axes are the offset axes: one call (the convergence at the point, the same maths)
            _offset = frame.IsAnchor ? (float)frame.ToGlobal(at).Convergence : _world;
        }
    }
}
