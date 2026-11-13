namespace UnitSport.Terrain.Format;

/// <summary>
/// How a source's own coordinates name places on the Earth (#1443 G2, B2 #1506): every source enters the
/// build through one of these, both ways and rigorously, and its heights through its <see cref="SeaLevel"/>.
/// LV95 survives only here, as the notation swisstopo's files are written in.
/// </summary>
public interface ISourceProjection
{
    string Name { get; }

    /// <summary>The model the source's heights are above.</summary>
    ISeaLevel SeaLevel { get; }

    /// <summary>A source coordinate pair to latitude and longitude (degrees).</summary>
    (double LatDeg, double LonDeg) ToLatLon(double a, double b);

    /// <summary>The inverse of <see cref="ToLatLon"/>.</summary>
    (double A, double B) FromLatLon(double latDeg, double lonDeg);
}

/// <summary>swisstopo's LV95 (EPSG:2056): easting and northing, rigorous through <see cref="SwissGeodesy"/>.</summary>
public sealed class Lv95Source : ISourceProjection
{
    public static readonly Lv95Source Instance = new();

    public string Name => "LV95";

    public ISeaLevel SeaLevel => SwissSeaLevel.Instance;

    /// <summary>
    /// LV95 to latitude and longitude at sea level. The datums' normals differ, so the height moves the place
    /// by 2.6-2.8 cm a km (<see cref="SwissGeodesy.ToLatLon"/>); a source with heights converts through
    /// <see cref="ToGeodetic"/> instead.
    /// </summary>
    public (double LatDeg, double LonDeg) ToLatLon(double a, double b) => SwissGeodesy.ToLatLon(a, b, 0);

    public (double A, double B) FromLatLon(double latDeg, double lonDeg)
    {
        var (e, n) = SwissGeodesy.ToLv95(latDeg, lonDeg, SwissSeaLevel.Instance.Undulation(latDeg, lonDeg));
        return (e, n);
    }

    /// <summary>An LV95 point with its LHN95 height, exactly where swisstopo puts it.</summary>
    public static Geodetic ToGeodetic(double e, double n, double hLhn95) => SwissGeodesy.ToWgs84(e, n, hLhn95);
}

/// <summary>Latitude and longitude as they are (OSM, Copernicus, Natural Earth): a = latitude, b = longitude.</summary>
public sealed class GeographicSource(ISeaLevel seaLevel) : ISourceProjection
{
    public string Name => "geographic";

    public ISeaLevel SeaLevel { get; } = seaLevel;

    public (double LatDeg, double LonDeg) ToLatLon(double a, double b) => (a, b);

    public (double A, double B) FromLatLon(double latDeg, double lonDeg) => (latDeg, lonDeg);
}

/// <summary>
/// IGN's RGF93 / Lambert-93 (EPSG:2154): the conformal conic on GRS80 with standard parallels 44° and 49°,
/// origin 46.5° N 3° E at 700,000 / 6,600,000. RGF93 and GRS80 are taken as WGS84 (centimetres apart). The
/// inverse iterates the conformal latitude to 1e-12 rad.
/// </summary>
public sealed class Lambert93Source(ISeaLevel seaLevel) : ISourceProjection
{
    private const double A = 6_378_137.0, F = 1 / 298.257222101;
    private static readonly double E2 = F * (2 - F), E = Math.Sqrt(E2);
    private static readonly double Lam0 = 3 * Math.PI / 180;
    private static readonly double N, Fc, Rho0;

    static Lambert93Source()
    {
        double p1 = 44 * Math.PI / 180, p2 = 49 * Math.PI / 180, p0 = 46.5 * Math.PI / 180;
        N = (Math.Log(M(p1)) - Math.Log(M(p2))) / (Math.Log(T(p1)) - Math.Log(T(p2)));
        Fc = M(p1) / (N * Math.Pow(T(p1), N));
        Rho0 = A * Fc * Math.Pow(T(p0), N);
    }

    public string Name => "Lambert-93";

    public ISeaLevel SeaLevel { get; } = seaLevel;

    private static double M(double p) => Math.Cos(p) / Math.Sqrt(1 - E2 * Math.Sin(p) * Math.Sin(p));

    private static double T(double p) =>
        Math.Tan(Math.PI / 4 - p / 2) / Math.Pow((1 - E * Math.Sin(p)) / (1 + E * Math.Sin(p)), E / 2);

    public (double A, double B) FromLatLon(double latDeg, double lonDeg)
    {
        double phi = latDeg * Math.PI / 180, lam = lonDeg * Math.PI / 180;
        double rho = A * Fc * Math.Pow(T(phi), N);
        double th = N * (lam - Lam0);
        return (700_000 + rho * Math.Sin(th), 6_600_000 + Rho0 - rho * Math.Cos(th));
    }

    public (double LatDeg, double LonDeg) ToLatLon(double a, double b)
    {
        double dx = a - 700_000, dy = Rho0 - (b - 6_600_000);
        double rho = Math.Sqrt(dx * dx + dy * dy);
        double th = Math.Atan2(dx, dy);
        double t = Math.Pow(rho / (A * Fc), 1 / N);
        double phi = Math.PI / 2 - 2 * Math.Atan(t);
        for (int i = 0; i < 20; i++)
        {
            double s = E * Math.Sin(phi);
            double next = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - s) / (1 + s), E / 2));
            if (Math.Abs(next - phi) < 1e-12) { phi = next; break; }
            phi = next;
        }
        return (phi * 180 / Math.PI, (th / N + Lam0) * 180 / Math.PI);
    }
}
