using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// True north against the game's axes (#1504, G2 A3). The flat world's −Z is LV95 grid north, which turns from true
/// north by the meridian convergence (<see cref="SwissGeodesy.GridConvergence"/>: 0 on Bern's meridian, ~+0.93° at
/// Geneva, ~−2° at Scuol). True north is the one system: every heading the game holds is from true north. What a
/// person reads (the compass strips, the cockpit's HDG and track, a photo's bearing) comes from here, never from
/// world −Z; a heading stored from grid north (a file's, a literal catalogue's) is turned to true north at load
/// (<see cref="GridToTrue"/>, <see cref="GridYawToTrue"/>) and set down with <see cref="WorldYaw"/> (world axes) or
/// <see cref="OffsetYaw"/> (a record's). Correct in the flat world and in an anchor frame alike, so not behind
/// <c>--frame earth</c>.
/// <para>
/// Grid is left only as a local turn inside code that still does its maths in LV95 numbers or a tile's grid axes
/// (<see cref="TrueToGrid"/>: a stadium's mesh, a festival's site), until G2 C and D take LV95 out.
/// </para>
/// <para>
/// Bearings are radians clockwise from north (a compass); yaws are Godot's (0 faces −Z, positive turns left), so a
/// yaw is minus a bearing. A grid bearing turns to a true one by subtracting the convergence; a grid yaw by adding it.
/// </para>
/// </summary>
public static class TrueNorth
{
    /// <summary>
    /// A bearing stored from grid north at LV95 <paramref name="e"/> / <paramref name="n"/> (a file's, a literal's) as one
    /// from true north, radians clockwise: what such data is turned by at load.
    /// </summary>
    public static double GridToTrue(double e, double n, double gridBearing) => gridBearing - SwissGeodesy.GridConvergence(e, n);

    /// <summary>A Godot yaw stored from grid north (0 faces grid north) as one from true north (see <see cref="GridToTrue"/>).</summary>
    public static float GridYawToTrue(double e, double n, float gridYaw) => (float)(gridYaw + SwissGeodesy.GridConvergence(e, n));

    /// <summary>
    /// A bearing from true north as one on the LV95 grid at <paramref name="e"/> / <paramref name="n"/>: the local turn for
    /// maths still done in LV95 numbers or a tile's grid axes. Goes with that maths (G2 C, D).
    /// </summary>
    public static double TrueToGrid(double e, double n, double trueBearing) => trueBearing + SwissGeodesy.GridConvergence(e, n);

    /// <summary>
    /// A world yaw at a world point as one from grid north: only for writing a store whose format is still grid (the
    /// traffic endpoints' file and wire angle).
    /// </summary>
    public static float WorldYawToGrid(OriginFrame frame, Vector3 at, float worldYaw) =>
        (float)(TrueYaw(frame, at, worldYaw) - frame.ToGlobal(at).GridConvergence);

    /// <summary>
    /// A heading from true north (a Godot yaw) at <paramref name="at"/> as a record's yaw beside its position
    /// (<c>VehicleState</c>, a slot): from LV95 grid north until G2 D (#1502, <see cref="Lv95Grid"/>).
    /// </summary>
    public static float OffsetYaw(GlobalPos at, float trueYaw) => (float)(trueYaw - at.GridConvergence);

    /// <summary>The inverse of <see cref="OffsetYaw"/>.</summary>
    public static float TrueYawOfOffset(GlobalPos at, float offsetYaw) => (float)(offsetYaw + at.GridConvergence);

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
    /// (<see cref="NorthBearing"/>) and on the LV95 grid (<see cref="GlobalPos.GridConvergence"/>: a grid map's, a record's),
    /// kept while the viewer stays within 100 m in one frame (the convergence turns ~0.001° in that), so standing or
    /// flying costs two geodesy calls every 100 m. A mutable struct: keep it in a field and call it on the field.
    /// </summary>
    public struct Near
    {
        private OriginFrame? _frame;
        private Vector3 _at;
        private float _world, _grid;

        /// <summary>The bearing of true north in world axes near <paramref name="at"/>, radians clockwise.</summary>
        public float World(OriginFrame frame, Vector3 at)
        {
            Refresh(frame, at);
            return _world;
        }

        /// <summary>The bearing of true north on the LV95 grid near <paramref name="at"/> (the convergence), radians clockwise.</summary>
        public float Grid(OriginFrame frame, Vector3 at)
        {
            Refresh(frame, at);
            return _grid;
        }

        /// <summary>A world (east, north) direction near <paramref name="at"/> on the LV95 grid (the identity on the flat ground).</summary>
        public Vector2 WorldToGrid(OriginFrame frame, Vector3 at, Vector2 eastNorth)
        {
            Refresh(frame, at);
            return Turn(eastNorth, _world, _grid);
        }

        /// <summary>A world (east, north) direction near <paramref name="at"/> in true east and north (<see cref="GlobalPos"/>'s maths, #1502).</summary>
        public Vector2 WorldToEnu(OriginFrame frame, Vector3 at, Vector2 eastNorth)
        {
            Refresh(frame, at);
            return Turn(eastNorth, _world, 0f);
        }

        private void Refresh(OriginFrame frame, Vector3 at)
        {
            if (ReferenceEquals(frame, _frame) && _at.DistanceSquaredTo(at) <= 100f * 100f) return;
            _frame = frame;
            _at = at;
            _world = NorthBearing(frame, at);
            // on the flat ground the world's axes are the grid's: one call (the convergence at the point, the same maths)
            _grid = frame.IsAnchor ? (float)frame.ToGlobal(at).GridConvergence : _world;
        }
    }
}
