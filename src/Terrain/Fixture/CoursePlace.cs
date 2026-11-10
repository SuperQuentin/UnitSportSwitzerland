using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// Where a fixture course's metres are on the Earth (#1649, #1648, G2 C7). A course is laid out in metres east (x) and
/// north (y) of its start. The flat world lays them on the LV95 grid: the start's LV95 point plus the metres. The earth
/// world lays them on the plane tangent at the start (<see cref="CourseSource"/>, what <see cref="FixtureLeaves"/>
/// builds the leaves on). The two part by the grid's convergence and scale: ~3.5 m a km at Riddes.
///
/// <para>
/// <see cref="FixtureChunkSource"/> lays its LV95 tiles out through the active placement, so under earth every reader
/// of a course's tiles (routes, car parks, stands, landings, the fleet) finds what it reads where the leaves have it,
/// and the leaves map the tiles' records back through it (<see cref="FixtureChunkSource.CourseOf"/>). Answers in LV95
/// as notation: the LV95 point of the course point's latitude and longitude, which <c>GlobalPos.FromLv95</c> and the
/// origin put on the Earth in either frame. Pure: no Godot.
/// </para>
///
/// <para>
/// The earth placement is the tangent plane's map onto the grid taken as linear about the start (a turn by the
/// convergence and a scale): over a course's few km it is the exact one to a few millimetres (2.6 mm across the fun
/// world: the grid's scale changes by ~6 ppm across 3 km near Riddes), and cheap enough for every vertex of a tile.
/// </para>
/// </summary>
public sealed class CoursePlace
{
    /// <summary>The start, LV95 (notation).</summary>
    public double StartE { get; }
    public double StartN { get; }

    /// <summary>The tangent plane the course is laid on in the earth world; null: the flat world's LV95 grid metres.</summary>
    public CourseSource? Earth { get; }

    /// <summary>Course metres to grid metres (E = a x + b y, N = c x + d y) and back; the identity on the flat world.</summary>
    private readonly double _a = 1, _b, _c, _d = 1, _ia = 1, _ib, _ic, _id = 1;

    /// <summary>The flat world's: course metres are LV95 grid metres from the start.</summary>
    public static CoursePlace Flat(double startE, double startN) => new(startE, startN, null);

    /// <summary>
    /// The earth world's: course metres on the plane tangent at the start's latitude and longitude, true at
    /// <paramref name="heightM"/> (the start's ground), as <see cref="FixtureLeaves.At"/> places a course.
    /// </summary>
    public static CoursePlace OnEarth(double startE, double startN, double heightM)
    {
        var (lat, lon) = Lv95Source.Instance.ToLatLon(startE, startN);
        return new(startE, startN, new CourseSource(lat, lon, heightM));
    }

    private CoursePlace(double startE, double startN, CourseSource? earth)
    {
        (StartE, StartN, Earth) = (startE, startN, earth);
        if (earth == null) return;
        // the map's columns: a kilometre east and north each way through the exact placement, central differences
        const double D = 1000;
        var (ee, en) = Exact(D, 0);
        var (we, wn) = Exact(-D, 0);
        var (ne, nn) = Exact(0, D);
        var (se, sn) = Exact(0, -D);
        (_a, _c) = ((ee - we) / (2 * D), (en - wn) / (2 * D));
        (_b, _d) = ((ne - se) / (2 * D), (nn - sn) / (2 * D));
        double det = _a * _d - _b * _c;
        (_ia, _ib, _ic, _id) = (_d / det, -_b / det, -_c / det, _a / det);
    }

    /// <summary>
    /// The course being played in this process: set as a world builds its fixture source (the earth world's offline,
    /// the flat world's otherwise), null outside a course (<see cref="FixtureChunkSource.ForgetWorld"/>).
    /// </summary>
    public static CoursePlace? Active { get; set; }

    /// <summary>A course point's LV95 notation.</summary>
    public (double E, double N) Lv95(double x, double y) =>
        Earth == null ? (StartE + x, StartN + y) : (StartE + _a * x + _b * y, StartN + _c * x + _d * y);

    /// <summary>The course point at an LV95 point (notation): metres east and north of the start.</summary>
    public (double X, double Y) Course(double e, double n)
    {
        if (Earth == null) return (e - StartE, n - StartN);
        double de = e - StartE, dn = n - StartN;
        return (_ia * de + _ib * dn, _ic * de + _id * dn);
    }

    /// <summary>The course point's LV95 notation through the tangent plane itself, not the map about the start (#1649).</summary>
    public (double E, double N) Exact(double x, double y)
    {
        if (Earth is not { } s) return Lv95(x, y);
        var (lat, lon) = s.ToLatLon(x, y);
        return Lv95Source.Instance.FromLatLon(lat, lon);
    }

    /// <summary>
    /// The angle the course's axes are turned on the grid, radians, counter-clockwise seen from above (east toward
    /// north): 0 on the flat world, about minus the grid's convergence on the earth one. A thing laid square to the
    /// course is turned so on the course's tiles.
    /// </summary>
    public double Turn => Math.Atan2(_c, _a);

    /// <summary>
    /// A heading on the course (degrees clockwise from the course's north) as a grid yaw: radians about +Y, 0 = grid
    /// north, turning the other way (as tile records and <c>VehicleSlot</c> hold one). The course's north is the grid's
    /// on the flat world and true north at the start on the earth one.
    /// </summary>
    public float GridYaw(double headingDeg)
    {
        double h = headingDeg * Math.PI / 180;
        if (Earth != null)
        {
            // the heading's direction carried through the map: its grid east and north
            double x = Math.Sin(h), y = Math.Cos(h);
            h = Math.Atan2(_a * x + _b * y, _c * x + _d * y);
        }
        // a heading is clockwise from north, a yaw turns the other way
        return (float)Math.IEEERemainder(-h, 2 * Math.PI);
    }
}
