namespace UnitSport.Terrain.Format;

/// <summary>
/// A point in earth-centred, earth-fixed coordinates (#1430), metres: X towards latitude 0 /
/// longitude 0, Y towards longitude 90° east, Z towards the north pole. Doubles: the step at the
/// Earth's radius is about a nanometre, at Neptune's distance a millimetre.
/// </summary>
public readonly record struct EcefPos(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double DistanceTo(EcefPos o)
    {
        double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static EcefPos operator +(EcefPos a, EcefPos b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static EcefPos operator -(EcefPos a, EcefPos b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public override string ToString() => $"ECEF {X:F3}/{Y:F3}/{Z:F3}";
}

/// <summary>
/// Latitude and longitude in degrees and the height above the ellipsoid in metres (#1430).
/// Ellipsoidal, not above sea level: in Switzerland the two differ by ~50 m (<see cref="SwissGeodesy"/>).
/// </summary>
public readonly record struct Geodetic(double LatDeg, double LonDeg, double H)
{
    public override string ToString() => $"{LatDeg:F7}° {LonDeg:F7}° h {H:F2}";
}

/// <summary>
/// An ellipsoid of revolution and the conversions between geodetic coordinates on it and ECEF
/// (#1430): <see cref="Wgs84"/> for the Earth in space, <see cref="Bessel1841"/> under LV95.
/// </summary>
public sealed class Ellipsoid
{
    /// <summary>WGS84 (NIMA TR8350.2). ETRS89's GRS80 differs only in f, by 0.1 mm at the pole: not told apart.</summary>
    public static readonly Ellipsoid Wgs84 = new(6_378_137.0, 298.257223563);

    /// <summary>Bessel 1841, the ellipsoid of CH1903+ and so of LV95.</summary>
    public static readonly Ellipsoid Bessel1841 = new(6_377_397.155, 299.1528128);

    /// <summary>Equatorial radius, metres.</summary>
    public double A { get; }

    /// <summary>Flattening.</summary>
    public double F { get; }

    /// <summary>Polar radius, metres.</summary>
    public double B { get; }

    /// <summary>First eccentricity squared.</summary>
    public double E2 { get; }

    /// <summary>Second eccentricity squared, e² / (1 − e²).</summary>
    public double Ep2 { get; }

    public Ellipsoid(double a, double inverseFlattening)
    {
        A = a;
        F = 1.0 / inverseFlattening;
        B = a * (1.0 - F);
        E2 = F * (2.0 - F);
        Ep2 = E2 / (1.0 - E2);
    }

    /// <summary>Radius of curvature in the prime vertical at a latitude (radians).</summary>
    public double PrimeVerticalRadius(double lat)
    {
        double s = Math.Sin(lat);
        return A / Math.Sqrt(1.0 - E2 * s * s);
    }

    public EcefPos ToEcef(Geodetic g) => ToEcef(g.LatDeg * Math.PI / 180.0, g.LonDeg * Math.PI / 180.0, g.H);

    /// <summary>Latitude and longitude in radians, height in metres.</summary>
    public EcefPos ToEcef(double lat, double lon, double h)
    {
        double n = PrimeVerticalRadius(lat), cl = Math.Cos(lat);
        return new((n + h) * cl * Math.Cos(lon), (n + h) * cl * Math.Sin(lon), (n * (1.0 - E2) + h) * Math.Sin(lat));
    }

    /// <summary>
    /// Bowring's method, iterated: two rounds already reach a few micrometres anywhere from the
    /// Earth's centre region out past the moon; it runs until the latitude stops moving.
    /// </summary>
    public Geodetic ToGeodetic(EcefPos p)
    {
        double rho = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        double lon = Math.Atan2(p.Y, p.X);
        if (rho < 1e-9)
            return new(p.Z >= 0 ? 90.0 : -90.0, 0.0, Math.Abs(p.Z) - B);

        double beta = Math.Atan2(p.Z * A, rho * B), lat = 0.0;
        for (int i = 0; i < 8; i++)
        {
            double sb = Math.Sin(beta), cb = Math.Cos(beta);
            double next = Math.Atan2(p.Z + Ep2 * B * sb * sb * sb, rho - E2 * A * cb * cb * cb);
            bool done = Math.Abs(next - lat) < 1e-15;
            lat = next;
            if (done) break;
            beta = Math.Atan2((1.0 - F) * Math.Sin(lat), Math.Cos(lat));
        }

        double sl = Math.Sin(lat);
        // stable at every latitude, the poles included
        double h = rho * Math.Cos(lat) + p.Z * sl - A * Math.Sqrt(1.0 - E2 * sl * sl);
        return new(lat * 180.0 / Math.PI, lon * 180.0 / Math.PI, h);
    }
}
