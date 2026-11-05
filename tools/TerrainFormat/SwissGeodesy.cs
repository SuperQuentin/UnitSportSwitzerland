namespace UnitSport.Terrain.Format;

/// <summary>
/// The rigorous way from LV95 and LHN95 heights to the Earth (#1430): swisstopo's Swiss oblique
/// conformal cylinder on Bessel 1841, then CH1903+ to CHTRS95 / ETRS89, which is a pure
/// translation by definition. Exactly invertible, so a position can go to <see cref="EcefPos"/>
/// and come back to the same LV95 point to well under a millimetre, however far it is from Bern.
///
/// <para>
/// <see cref="SwissProjection"/> keeps swisstopo's approximate polynomials for GPX: under a metre in
/// Switzerland, far worse a few hundred km out. This class is what the handover to the ellipsoid
/// (docs/plans/ellipsoid-handover.md) and anything that has to round-trip stands on. Checked against
/// swisstopo's REFRAME service: latitude and longitude to the millimetre.
/// </para>
///
/// <para>
/// <b>Heights.</b> swissALTI3D heights are LHN95 (above sea level, roughly). The ellipsoid height
/// is that plus the geoid undulation, ~44 to 58 m across the country: the separation of Bessel
/// from WGS84 (~47 to 53 m) plus the geoid above Bessel, <see cref="BesselGeoid"/> (a 20 km grid of
/// swisstopo's CHGeo2004, −6 to +6 m). Outside the grid it stays at its edge value, a few metres
/// off. The same model is used both ways, so continuity never depends on its accuracy.
/// </para>
/// </summary>
public static class SwissGeodesy
{
    public const double FalseEasting = 2_600_000.0, FalseNorthing = 1_200_000.0;

    /// <summary>CH1903+ to CHTRS95 (ETRS89), metres: the definition of CH1903+, not a fit.</summary>
    public static readonly EcefPos DatumShift = new(674.374, 15.056, 405.346);

    // the projection's centre, the old observatory in Bern: 46°57'08.66" N, 7°26'22.50" E (Bessel)
    private static readonly double Phi0 = Rad(46 + 57 / 60.0 + 8.66 / 3600.0);
    private static readonly double Lambda0 = Rad(7 + 26 / 60.0 + 22.50 / 3600.0);

    private static readonly Ellipsoid Bessel = Ellipsoid.Bessel1841;
    private static readonly double E = Math.Sqrt(Bessel.E2);

    /// <summary>Radius of the projection sphere, ~6,378,815.904 m.</summary>
    public static readonly double R = Bessel.A * Math.Sqrt(1 - Bessel.E2) / (1 - Bessel.E2 * Math.Sin(Phi0) * Math.Sin(Phi0));

    private static readonly double Alpha = Math.Sqrt(1 + Bessel.Ep2 * Math.Pow(Math.Cos(Phi0), 4));
    private static readonly double B0 = Math.Asin(Math.Sin(Phi0) / Alpha);
    private static readonly double K =
        Math.Log(Math.Tan(Math.PI / 4 + B0 / 2))
        - Alpha * Math.Log(Math.Tan(Math.PI / 4 + Phi0 / 2))
        + Alpha * E / 2 * Math.Log((1 + E * Math.Sin(Phi0)) / (1 - E * Math.Sin(Phi0)));

    /// <summary>LV95 to latitude and longitude on Bessel 1841 (CH1903+), radians.</summary>
    public static (double Lat, double Lon) ToBessel(double e, double n)
    {
        double y = e - FalseEasting, x = n - FalseNorthing;
        // on the sphere, in the oblique frame, then back to the equatorial one
        double lBar = y / R;
        double bBar = 2 * (Math.Atan(Math.Exp(x / R)) - Math.PI / 4);
        double b = Math.Asin(Math.Cos(B0) * Math.Sin(bBar) + Math.Sin(B0) * Math.Cos(bBar) * Math.Cos(lBar));
        double l = Math.Atan2(Math.Sin(lBar), Math.Cos(B0) * Math.Cos(lBar) - Math.Sin(B0) * Math.Tan(bBar));
        double lon = Lambda0 + l / Alpha;

        // sphere to ellipsoid: a fixed point, ~5 rounds to the last bit
        double lat = b, s0 = (Math.Log(Math.Tan(Math.PI / 4 + b / 2)) - K) / Alpha;
        for (int i = 0; i < 12; i++)
        {
            double s = s0 + E * Math.Log(Math.Tan(Math.PI / 4 + Math.Asin(E * Math.Sin(lat)) / 2));
            double next = 2 * Math.Atan(Math.Exp(s)) - Math.PI / 2;
            bool done = Math.Abs(next - lat) < 1e-15;
            lat = next;
            if (done) break;
        }
        return (lat, lon);
    }

    /// <summary>
    /// A longitude's angle from Bern's meridian, radians, wrapped to ±π (#1480): unwrapped, a longitude from
    /// 172.6° W to 180° came out as −187° to −180° and its LV95 point was up to 31 km off on the way back.
    /// Inside ±π it is the plain difference, to the bit.
    /// </summary>
    private static double FromCentre(double lon)
    {
        double d = lon - Lambda0;
        if (d > Math.PI) d -= 2 * Math.PI;
        else if (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    /// <summary>
    /// The widest angle from Bern's meridian, radians, that LV95 round-trips (#1480): the Gauss sphere's
    /// longitude is α times the ellipsoid's (α ≈ 1.00072), so it covers 2πα and the last 0.13° on either
    /// side of Bern's antimeridian (172.56° W, 15 km wide at the equator) overlap: there an LV95 point goes
    /// back to ECEF up to 31 km off. Everywhere else round trips hold to a tenth of a millimetre.
    /// </summary>
    public static readonly double MaxFromCentre = Math.PI / Alpha;

    /// <summary>Whether an ECEF point is where LV95 round-trips (#1480): everywhere but a 0.26° sliver on Bern's antimeridian.</summary>
    public static bool InDomain(EcefPos p)
    {
        var q = p - DatumShift;
        return Math.Abs(FromCentre(Math.Atan2(q.Y, q.X))) < MaxFromCentre;
    }

    /// <summary>
    /// Called by <see cref="FromEcef"/> with a point outside <see cref="InDomain"/> (#1480): that LV95 place is
    /// wrong, by up to 31 km. The game logs it (a far-side position must never land off quietly); the
    /// conversion still answers, as nearest it can.
    /// </summary>
    public static Action<EcefPos>? OutsideDomain;

    /// <summary>Latitude and longitude on Bessel 1841 (radians) to LV95.</summary>
    public static (double E, double N) FromBessel(double lat, double lon)
    {
        double es = E * Math.Sin(lat);
        double s = Alpha * Math.Log(Math.Tan(Math.PI / 4 + lat / 2)) - Alpha * E / 2 * Math.Log((1 + es) / (1 - es)) + K;
        double b = 2 * (Math.Atan(Math.Exp(s)) - Math.PI / 4);
        double l = Alpha * FromCentre(lon);
        double lBar = Math.Atan2(Math.Sin(l), Math.Sin(B0) * Math.Tan(b) + Math.Cos(B0) * Math.Cos(l));
        double bBar = Math.Asin(Math.Cos(B0) * Math.Sin(b) - Math.Sin(B0) * Math.Cos(b) * Math.Cos(l));
        double y = R * lBar;
        double x = R / 2 * Math.Log((1 + Math.Sin(bBar)) / (1 - Math.Sin(bBar)));
        return (FalseEasting + y, FalseNorthing + x);
    }

    /// <summary>
    /// The geoid above Bessel 1841 at an LV95 point, metres: what to add to an LHN95 height to get a
    /// Bessel ellipsoidal height (−0.63 m in Bern, −6 to +5.7 m over the grid). Bilinear in
    /// <see cref="GeoidCm"/>; outside the grid it keeps the value at its nearest edge.
    /// </summary>
    public static double BesselGeoid(double e, double n)
    {
        double gx = Math.Clamp((e - GeoidMinE) / GeoidStep, 0, GeoidCols - 1);
        double gy = Math.Clamp((n - GeoidMinN) / GeoidStep, 0, GeoidRows - 1);
        int x0 = Math.Min((int)gx, GeoidCols - 2), y0 = Math.Min((int)gy, GeoidRows - 2);
        double fx = gx - x0, fy = gy - y0;
        double At(int x, int y) => GeoidCm[y * GeoidCols + x];
        double south = At(x0, y0) + (At(x0 + 1, y0) - At(x0, y0)) * fx;
        double north = At(x0, y0 + 1) + (At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * fx;
        return (south + (north - south) * fy) / 100.0;
    }

    private const double GeoidMinE = 2_460_000, GeoidMinN = 1_060_000, GeoidStep = 20_000;
    private const int GeoidCols = 21, GeoidRows = 13;

    /// <summary>
    /// The geoid above Bessel in centimetres every 20 km, west to east and south to north from
    /// LV95 2,460,000 / 1,060,000: swisstopo's REFRAME <c>lhn95tobessel</c> (CHGeo2004), sampled
    /// 2026-11-03. Data © swisstopo.
    /// </summary>
    private static readonly short[] GeoidCm =
    {
        -483, -438, -335, -243,  -69,   35,   42,   54,   27,   17, -181, -417, -549, -599, -596, -561, -513, -433, -313, -248, -149,  // N 1,060,000
        -410, -405, -338, -194,  -89,   60,  131,  204,  230,  172,   90, -122, -284, -331, -317, -266, -214, -165,  -62,    7,   27,  // N 1,080,000
        -333, -341, -291, -202,  -87,   39,   71,  222,  288,  260,  125,  117,  -71, -112, -138,  -35,   15,   72,  117,  201,  171,  // N 1,100,000
        -287, -234, -260, -183,  -74,  -29,   41,   78,  186,  197,  154,  163,   89,   29,   39,   70,  155,  206,  237,  332,  303,  // N 1,120,000
        -239, -169, -153, -187, -144,  -78,   48,  109,  178,  228,  224,  214,  173,  109,  148,  217,  303,  338,  349,  401,  355,  // N 1,140,000
        -206, -125,  -64, -101, -123,  -83,    3,   67,   88,  187,  242,  228,  210,  189,  194,  222,  306,  374,  412,  404,  403,  // N 1,160,000
        -208, -124,  -49,  -34,  -95,  -99,  -64,  -22,  -10,   64,  122,  182,  171,  156,  121,  159,  267,  370,  428,  423,  497,  // N 1,180,000
        -218, -130,  -50,   -2,    3,  -59,  -70,  -63,  -34,   -1,   15,   33,   95,  120,  147,   98,  216,  356,  454,  465,  573,  // N 1,200,000
        -193, -135,  -68,   -4,   39,   46,   13,  -27,  -27,  -32,  -46,  -46,    7,   41,   72,   98,  197,  330,  413,  433,  527,  // N 1,220,000
        -175, -124,  -72,  -15,   31,   62,   68,   56,   35,    7,  -14,  -28,  -27,   17,   60,   53,  182,  299,  390,  446,  452,  // N 1,240,000
        -160, -121,  -70,  -14,   35,   61,   78,   86,   94,   89,   56,   34,   21,   24,   30,   43,  121,  235,  325,  387,  423,  // N 1,260,000
        -135, -105,  -56,   15,   82,  116,  115,  128,  176,  217,  181,  140,  101,   77,   59,   68,  121,  204,  262,  300,  345,  // N 1,280,000
         -94,  -62,  -19,   46,  140,  205,  187,  163,  226,  314,  301,  262,  222,  177,  150,  140,  173,  215,  254,  278,  297,  // N 1,300,000
    };

    /// <summary>LV95 and an LHN95 height to ECEF on the Earth (CHTRS95 / ETRS89, taken as WGS84).</summary>
    public static EcefPos ToEcef(double e, double n, double hLhn95)
    {
        var (lat, lon) = ToBessel(e, n);
        return Bessel.ToEcef(lat, lon, hLhn95 + BesselGeoid(e, n)) + DatumShift;
    }

    /// <summary>The inverse of <see cref="ToEcef"/>: LV95 and the LHN95 height.</summary>
    public static (double E, double N, double H) FromEcef(EcefPos p)
    {
        var g = Bessel.ToGeodetic(p - DatumShift);
        if (Math.Abs(FromCentre(Rad(g.LonDeg))) >= MaxFromCentre) OutsideDomain?.Invoke(p);
        var (e, n) = FromBessel(Rad(g.LatDeg), Rad(g.LonDeg));
        return (e, n, g.H - BesselGeoid(e, n));
    }

    /// <summary>
    /// The LHN95 height of an ECEF point alone (#1480): <see cref="FromEcef"/>'s H, to the bit, but asked
    /// anywhere: a height needs no projection, so the sliver on Bern's antimeridian is no error here
    /// (its geoid is the grid's edge value, as for every far point).
    /// </summary>
    public static double Lhn95Height(EcefPos p)
    {
        var g = Bessel.ToGeodetic(p - DatumShift);
        var (e, n) = FromBessel(Rad(g.LatDeg), Rad(g.LonDeg));
        return g.H - BesselGeoid(e, n);
    }

    /// <summary>LV95 and an LHN95 height to latitude, longitude and ellipsoidal height on WGS84.</summary>
    public static Geodetic ToWgs84(double e, double n, double hLhn95) => Ellipsoid.Wgs84.ToGeodetic(ToEcef(e, n, hLhn95));

    /// <summary>The inverse of <see cref="ToWgs84"/>.</summary>
    public static (double E, double N, double H) FromWgs84(Geodetic g) => FromEcef(Ellipsoid.Wgs84.ToEcef(g));

    /// <summary>
    /// The meridian convergence at an LV95 place (#1504): the bearing of true (WGS84) north on the LV95 grid there,
    /// radians clockwise from grid north. 0 on Bern's meridian (to 0.0007°, the datums' normals), ~+0.93° at Geneva,
    /// ~−2° at Scuol. A heading stored from grid north less this is one from true north.
    /// A few microseconds: per object at load, never per vertex.
    /// </summary>
    public static double GridConvergence(double e, double n, double hLhn95 = 500)
    {
        var o = ToEcef(e, n, hLhn95);
        var g = Ellipsoid.Wgs84.ToGeodetic(o);
        double lat = Rad(g.LatDeg), lon = Rad(g.LonDeg);
        var up = new EcefPos(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        var north = new EcefPos(-Math.Sin(lat) * Math.Cos(lon), -Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat));
        // grid east: a metre's step along the grid, laid flat; grid north: up × east
        var east = ToEcef(e + 1, n, hLhn95) - o;
        double along = Dot(east, up);
        east = new EcefPos(east.X - up.X * along, east.Y - up.Y * along, east.Z - up.Z * along);
        var gridNorth = new EcefPos(up.Y * east.Z - up.Z * east.Y, up.Z * east.X - up.X * east.Z, up.X * east.Y - up.Y * east.X);
        return Math.Atan2(Dot(north, east), Dot(north, gridNorth));
    }

    /// <summary><see cref="GridConvergence"/> in degrees: what a stored heading in degrees from grid north is turned by.</summary>
    public static double GridConvergenceDeg(double e, double n, double hLhn95 = 500) => GridConvergence(e, n, hLhn95) * 180 / Math.PI;

    private static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>
    /// The ellipsoidal height a latitude and longitude with no height of their own are taken at: the
    /// Swiss Plateau's ground (~450 m LHN95 plus ~50 m of geoid). The two datums' normals differ, so
    /// the height moves the LV95 point a little: <see cref="HorizontalPerMetre"/> per metre off.
    /// </summary>
    public const double GroundH = 500;

    /// <summary>How far an LV95 point moves per metre of ellipsoidal height it is converted at: measured 2.6e-5 at Bern and 2.8e-5 at Brest, so a summit taken at the Plateau's height lands ~10 cm off (the Matterhorn, 4 km higher): pass the height when it is known.</summary>
    public const double HorizontalPerMetre = 3e-5;

    /// <summary>
    /// Latitude and longitude on WGS84 / ETRS89 (degrees, GPS, OSM, IGN) to LV95, rigorously (#1443): the
    /// one way in for coordinates from outside, valid however far from Bern. <see cref="SwissProjection"/>'s
    /// polynomials are off by 10 m at Paris and 276 m at Brest.
    /// </summary>
    public static (double E, double N) ToLv95(double latDeg, double lonDeg, double hEllipsoid = GroundH)
    {
        var (e, n, _) = FromWgs84(new Geodetic(latDeg, lonDeg, hEllipsoid));
        return (e, n);
    }

    /// <summary>LV95 and an LHN95 height to latitude and longitude on WGS84 (degrees): the inverse of <see cref="ToLv95"/>.</summary>
    public static (double LatDeg, double LonDeg) ToLatLon(double e, double n, double hLhn95 = GroundH - 50)
    {
        var g = ToWgs84(e, n, hLhn95);
        return (g.LatDeg, g.LonDeg);
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;
}
