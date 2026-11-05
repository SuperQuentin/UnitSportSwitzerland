using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// A position that means the same place whatever the origin is (#185): a point on the Earth, held as three ECEF
/// doubles (WGS84, metres; #1443 G2, A1 #1502). Anything that keeps a position across frames should keep one of these
/// rather than a world-space <c>Vector3</c>, which changes meaning at every origin shift. Converted with
/// <see cref="WorldOrigin.ToWorld(GlobalPos)"/> and <see cref="WorldOrigin.ToGlobal"/>.
///
/// <para>
/// <b>Its maths are the Earth's</b>, in the local east-up-south at the place (x east, y up, z south; true north):
/// <see cref="OffsetTo"/> / <see cref="Moved"/> (metres east, north and up, the horizontal kept level), the
/// operators <c>+ Vector3</c> / <c>-</c> (a vector in the local ENU), <see cref="BearingTo"/> (from true north), the
/// distances (<see cref="DistanceTo"/> the chord, <see cref="HorizontalDistanceTo"/> the level distance, the geodesic
/// to a fraction of a millimetre within a few km). None of them is a flat frame's world vector: a world vector of the
/// flat LV95 frame is in the grid's axes and metres (the meridian convergence, 0.95° at Geneva; the grid's scale,
/// 80-190 ppm), so it goes through the frame (<see cref="OriginFrame.EnuOf"/> / <see cref="OriginFrame.WorldOfEnu"/>).
/// </para>
///
/// <para>
/// LV95 is only a notation: <see cref="FromLv95"/> in (a Swiss source, a literal, what a person types),
/// <see cref="Lv95"/> out (the debug readout, a Swiss file, the LV95 tile key until G2 D), the rigorous
/// <see cref="SwissGeodesy"/> chain both ways (~1 µs). <see cref="Alt"/> is the height above sea level (LHN95).
/// </para>
///
/// <para>
/// On the network it is the same three doubles (<see cref="ToWire"/> / <see cref="FromWire"/>, protocol 93); in a
/// JSON save <c>{"x", "y", "z"}</c> (<see cref="GlobalPosJson"/>). Two positions are equal when their doubles are:
/// compare places that went through a conversion with a tolerance, never with <c>==</c>.
/// </para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(GlobalPosJson))]
public readonly record struct GlobalPos
{
    private readonly double _x, _y, _z;

    private GlobalPos(double x, double y, double z)
    {
        _x = x;
        _y = y;
        _z = z;
    }

    /// <summary>This place on the Earth: ECEF metres, WGS84.</summary>
    public EcefPos Ecef => new(_x, _y, _z);

    /// <summary>The same as <see cref="Ecef"/>.</summary>
    public EcefPos ToEcef() => new(_x, _y, _z);

    /// <summary>The place at an ECEF point.</summary>
    public static GlobalPos FromEcef(EcefPos p) => new(p.X, p.Y, p.Z);

    /// <summary>A place given in LV95 notation (a Swiss source, a literal, a typed coordinate), its height LHN95.</summary>
    public static GlobalPos FromLv95(double e, double n, double alt) => FromEcef(SwissGeodesy.ToEcef(e, n, alt));

    /// <summary>This place in LV95 notation, for a person or a Swiss file: never for arithmetic between places (~1 µs).</summary>
    public Lv95Coord Lv95
    {
        get
        {
            var (e, n, h) = SwissGeodesy.FromEcef(Ecef);
            return new(e, n, h);
        }
    }

    /// <summary>
    /// The height above sea level (LHN95, <see cref="SwissGeodesy.Lhn95Height"/>), computed (~0.5 µs): what
    /// <see cref="FromLv95"/> and <see cref="WithAlt"/> take, and a flat frame's world Y.
    /// </summary>
    public double Alt => SwissGeodesy.Lhn95Height(Ecef);

    /// <summary>Latitude, longitude and the WGS84 ellipsoidal height.</summary>
    public Geodetic Geodetic => Ellipsoid.Wgs84.ToGeodetic(Ecef);

    /// <summary>The local east-up-south here (<see cref="EnuFrame.ToLocal"/>: x east, y up, z south, its origin this place).</summary>
    public EnuFrame Enu => new(Geodetic);

    /// <summary>
    /// Where <paramref name="other"/> is from here, in metres east, north and up of the local horizon at this place
    /// (true north): east and north are where <paramref name="other"/>'s plumb line meets the level of this place
    /// (its ellipsoidal height), in this place's ENU; up is the difference of the ellipsoidal heights. So a place
    /// straight above another has the same east and north, and a level place 3 km away is up 0, not the 0.7 m the
    /// tangent plane falls below the curve. The inverse of <see cref="Moved"/>.
    /// </summary>
    public (double East, double North, double Up) OffsetTo(GlobalPos other)
    {
        var here = Geodetic;
        var there = other.Geodetic;
        var foot = Ellipsoid.Wgs84.ToEcef(there with { H = here.H });
        var (x, _, z) = new EnuFrame(here).ToLocal(foot);
        return (x, -z, there.H - here.H);
    }

    /// <summary>
    /// This place moved by metres east, north and up of its local horizon (see <see cref="OffsetTo"/>, whose inverse
    /// it is): east and north in the local ENU, kept level (at this place's height), then <paramref name="up"/> along
    /// the plumb line there. Gameplay offsets given in metres east and north (a stall 5 m east of a site) are these.
    /// </summary>
    public GlobalPos Moved(double east, double north, double up = 0)
    {
        var here = Geodetic;
        var f = new EnuFrame(here);
        // the point of this place's level whose ENU east and north are these: down the ENU vertical from the tangent
        // plane (each round takes the height error off along f's up, which keeps east and north exact; the error
        // shrinks by the angle's square each round: micrometres after two at 10 km)
        var p = f.ToEcef(east, 0, -north);
        var g = Ellipsoid.Wgs84.ToGeodetic(p);
        for (int i = 0; i < 8 && System.Math.Abs(g.H - here.H) > 1e-7; i++)
        {
            double dh = g.H - here.H;
            p = new EcefPos(p.X - f.Up.X * dh, p.Y - f.Up.Y * dh, p.Z - f.Up.Z * dh);
            g = Ellipsoid.Wgs84.ToGeodetic(p);
        }
        return FromEcef(Ellipsoid.Wgs84.ToEcef(g with { H = here.H + up }));
    }

    /// <summary>This place at another altitude (above sea level, LHN95), straight up or down its plumb line.</summary>
    public GlobalPos WithAlt(double alt)
    {
        var g = Geodetic;
        return FromEcef(Ellipsoid.Wgs84.ToEcef(g with { H = g.H + (alt - Alt) }));
    }

    /// <summary>The compass bearing to <paramref name="other"/>, radians clockwise from true north here.</summary>
    public double BearingTo(GlobalPos other)
    {
        var (e, n, _) = OffsetTo(other);
        return System.Math.Atan2(e, n);
    }

    /// <summary>
    /// The LV95 grid's meridian convergence here (#1504), radians clockwise: the bearing of true north on the grid, what a
    /// heading kept in grid north (a Swiss source's, a record's until G2 D: <see cref="Lv95Grid"/>) is turned by. Notation
    /// (~2 µs); <see cref="GlobalPos"/>'s own maths are from true north and need none.
    /// </summary>
    public double GridConvergence
    {
        get
        {
            var (e, n, h) = SwissGeodesy.FromEcef(Ecef);
            return SwissGeodesy.GridConvergence(e, n, h);
        }
    }

    /// <summary>The terrain tile under this place (the LV95 kilometre of its notation until G2 D, the cube leaf then).</summary>
    public TileId Tile
    {
        get
        {
            var (e, n, _) = SwissGeodesy.FromEcef(Ecef);
            return TileId.FromLv95(e, n);
        }
    }

    /// <summary>
    /// The level distance to <paramref name="other"/>: the chord less its part along the mean plumb line of the two
    /// places, which is the distance at their mean height along the Earth's curve to s³/24R² (0.13 mm at 5 km), and
    /// the same both ways. No trigonometry: the ellipsoid's normal is its gradient.
    /// </summary>
    public double HorizontalDistanceTo(GlobalPos other) => System.Math.Sqrt(HorizontalDistanceSquaredTo(other));

    /// <summary>The square of <see cref="HorizontalDistanceTo"/>, for comparisons that skip the root.</summary>
    public double HorizontalDistanceSquaredTo(GlobalPos other)
    {
        double dx = other._x - _x, dy = other._y - _y, dz = other._z - _z;
        var (ux, uy, uz) = Normal(_x, _y, _z);
        var (vx, vy, vz) = Normal(other._x, other._y, other._z);
        double mx = ux + vx, my = uy + vy, mz = uz + vz;
        double m2 = mx * mx + my * my + mz * mz;
        double along = dx * mx + dy * my + dz * mz;
        double h2 = dx * dx + dy * dy + dz * dz - along * along / m2;
        return h2 > 0 ? h2 : 0;
    }

    // the WGS84 normal at a point near the surface, the ellipsoid's gradient (x/a², y/a², z/b²) made a unit vector:
    // within 10⁻⁶ rad of the true normal 10 km up
    private static (double X, double Y, double Z) Normal(double x, double y, double z)
    {
        const double a2 = 6_378_137.0 * 6_378_137.0, b2 = 6_356_752.314245179 * 6_356_752.314245179;
        double gx = x / a2, gy = y / a2, gz = z / b2;
        double l = System.Math.Sqrt(gx * gx + gy * gy + gz * gz);
        return (gx / l, gy / l, gz / l);
    }

    /// <summary>The straight-line distance to <paramref name="other"/> (the chord through the Earth, metres).</summary>
    public double DistanceTo(GlobalPos other)
    {
        double dx = other._x - _x, dy = other._y - _y, dz = other._z - _z;
        return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// The level distance between two places held as raw LV95 eastings and northings (a Swiss source's points, not
    /// <see cref="GlobalPos"/> values): the grid's distance, which is the Earth's to LV95's scale (80-190 ppm). Code
    /// whose places are on the Earth holds <see cref="GlobalPos"/> values and asks <see cref="HorizontalDistanceTo"/>.
    /// </summary>
    public static double HorizontalDistance(double e1, double n1, double e2, double n2)
    {
        double de = e1 - e2, dn = n1 - n2;
        return System.Math.Sqrt(de * de + dn * dn);
    }

    /// <summary>The square of <see cref="HorizontalDistance"/>, for comparisons that skip the root.</summary>
    public static double HorizontalDistanceSquared(double e1, double n1, double e2, double n2)
    {
        double de = e1 - e2, dn = n1 - n2;
        return de * de + dn * dn;
    }

    /// <summary>
    /// The midpoint of two places: half way along the chord, at the mean of their heights (so the middle of two
    /// places on the ground is on the ground, not the chord's sag below it).
    /// </summary>
    public static GlobalPos Midpoint(GlobalPos a, GlobalPos b)
    {
        var m = Ellipsoid.Wgs84.ToGeodetic(new EcefPos((a._x + b._x) / 2, (a._y + b._y) / 2, (a._z + b._z) / 2));
        double h = (a.Geodetic.H + b.Geodetic.H) / 2;
        return FromEcef(Ellipsoid.Wgs84.ToEcef(m with { H = h }));
    }

    /// <summary>
    /// This place moved by <paramref name="d"/>, a vector in the local east-up-south at <paramref name="p"/> (x east,
    /// y up, z south, true metres): a velocity times a time, a docking offset. Straight, so a long one leaves the
    /// ground (d²/2R: 8 cm at 1 km); use <see cref="Moved"/> for metres along the ground. A world vector of a flat
    /// frame is no ENU vector: convert it with the frame (<see cref="OriginFrame.EnuOf"/>).
    /// </summary>
    public static GlobalPos operator +(GlobalPos p, Vector3 d)
    {
        var (e, u, n) = p.Axes();
        return new(p._x + e.X * d.X + u.X * d.Y - n.X * d.Z, p._y + e.Y * d.X + u.Y * d.Y - n.Y * d.Z, p._z + u.Z * d.Y - n.Z * d.Z);
    }

    /// <summary>
    /// The vector from <paramref name="b"/> to <paramref name="a"/> in the local east-up-south at <paramref name="b"/>,
    /// so <c>b + (a - b)</c> is <paramref name="a"/>: small and precise when they are near.
    /// </summary>
    public static Vector3 operator -(GlobalPos a, GlobalPos b)
    {
        var (e, u, n) = b.Axes();
        double dx = a._x - b._x, dy = a._y - b._y, dz = a._z - b._z;
        return new((float)(e.X * dx + e.Y * dy), (float)(u.X * dx + u.Y * dy + u.Z * dz), (float)-(n.X * dx + n.Y * dy + n.Z * dz));
    }

    // the local east, up and north here without the iterated geodetic latitude (what + and - run on, per frame): up the
    // ellipsoid's gradient (within 10⁻⁶ rad of the plumb line 10 km up: micrometres on a metre-scale vector), east
    // square to the axis, north up × east. Rounds the same both ways, so b + (a - b) is a.
    private ((double X, double Y, double Z) East, (double X, double Y, double Z) Up, (double X, double Y, double Z) North) Axes()
    {
        var up = Normal(_x, _y, _z);
        double rho = System.Math.Sqrt(_x * _x + _y * _y);
        var east = rho > 1e-9 ? (-_y / rho, _x / rho, 0.0) : (0.0, 1.0, 0.0);
        var north = (up.Y * east.Item3 - up.Z * east.Item2, up.Z * east.Item1 - up.X * east.Item3, up.X * east.Item2 - up.Y * east.Item1);
        return (east, up, north);
    }

    public bool IsFinite => double.IsFinite(_x) && double.IsFinite(_y) && double.IsFinite(_z);

    /// <summary>This place as it crosses the network (#1503, protocol 93): its ECEF doubles.</summary>
    public (double X, double Y, double Z) ToWire() => (_x, _y, _z);

    /// <summary>The place <see cref="ToWire"/> sent.</summary>
    public static GlobalPos FromWire(double x, double y, double z) => new(x, y, z);

    /// <summary>Puts this position in a network dictionary under <paramref name="key"/>, as three ECEF doubles (<see cref="ToWire"/>).</summary>
    public void Write(Godot.Collections.Dictionary d, string key = "pos")
    {
        d[key + "_x"] = _x;
        d[key + "_y"] = _y;
        d[key + "_z"] = _z;
    }

    /// <summary>
    /// The position <see cref="Write"/> put in <paramref name="d"/>. A dictionary without the ECEF keys is a record the
    /// object containers saved before G2 A2b (LV95 <c>_e</c>, <c>_n</c>, <c>_alt</c>): read for its migration only.
    /// </summary>
    public static GlobalPos Read(Godot.Collections.Dictionary d, string key = "pos") => d.ContainsKey(key + "_x")
        ? FromWire(d[key + "_x"].AsDouble(), d[key + "_y"].AsDouble(), d[key + "_z"].AsDouble())
        : FromLv95(d[key + "_e"].AsDouble(), d[key + "_n"].AsDouble(), d[key + "_alt"].AsDouble());

    /// <summary>Whether <paramref name="d"/> holds a position as <see cref="Write"/> puts it (the wire, and saves since A2b), not an older saved record's.</summary>
    public static bool IsWire(Godot.Collections.Dictionary d, string key = "pos") => d.ContainsKey(key + "_x");

    public override string ToString()
    {
        if (!IsFinite) return $"ECEF {_x}/{_y}/{_z}";
        var (e, n, h) = SwissGeodesy.FromEcef(Ecef);
        return $"LV95 {e:F2}/{n:F2} alt {h:F2}";
    }
}

/// <summary>A place in LV95 notation (#1443 G2): metres east and north on the Swiss grid and an LHN95 height. For a person or a Swiss source only.</summary>
public readonly record struct Lv95Coord(double E, double N, double H)
{
    public override string ToString() => System.FormattableString.Invariant($"{E:F1}/{N:F1}");
}
