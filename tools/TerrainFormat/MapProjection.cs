namespace UnitSport.Terrain.Format;

/// <summary>
/// The oblique stereographic projection on WGS84 (EPSG method 9809, the "double stereographic": the ellipsoid onto
/// Gauss's conformal sphere, then the sphere's stereographic) centred on a place (#1514, G2 C5). What a map and a GPX
/// track lay the Earth flat with, decided with the user: conformal (a shape and an angle are true at every point, so a
/// map turns and zooms like a picture and a track's corners are its own), true north up at the centre, scale 1 there
/// and 1 + d²/4R² at d metres away (60 ppm at 100 km, 0.5 % at 900 km). Never the world frame: a picture's plane.
///
/// <para>
/// x east, y north, metres from the centre. <see cref="Forward"/> is closed form; <see cref="Inverse"/> iterates the
/// latitude off the isometric one (to 1e-12 rad, 3-4 steps). The LV95 map of MapSetup keeps LV95 instead, its own
/// projection, also conformal.
/// </para>
/// </summary>
public sealed class MapProjection
{
    private readonly double _k0, _e, _e2, _n, _c, _r2, _sinChi0, _cosChi0, _lambda0, _g, _h, _chi0;

    /// <summary>The centre's latitude and longitude, degrees.</summary>
    public double LatDeg { get; }
    public double LonDeg { get; }

    public MapProjection(double latDeg, double lonDeg) : this(latDeg, lonDeg, Ellipsoid.Wgs84, 1) { }

    /// <summary>On another ellipsoid with a scale at the centre: EPSG's worked example (RD New, Bessel) pins the formulas.</summary>
    public MapProjection(double latDeg, double lonDeg, Ellipsoid ell, double k0)
    {
        LatDeg = latDeg;
        LonDeg = lonDeg;
        _k0 = k0;
        _e2 = ell.E2;
        _e = Math.Sqrt(_e2);
        double phi0 = latDeg * Math.PI / 180, sin0 = Math.Sin(phi0), cos0 = Math.Cos(phi0);
        _lambda0 = lonDeg * Math.PI / 180;
        double w = 1 - _e2 * sin0 * sin0;
        double rho0 = ell.A * (1 - _e2) / Math.Pow(w, 1.5), nu0 = ell.A / Math.Sqrt(w);
        double r = Math.Sqrt(rho0 * nu0) * k0;
        _n = Math.Sqrt(1 + _e2 * Math.Pow(cos0, 4) / (1 - _e2));
        double s1 = (1 + sin0) / (1 - sin0), s2 = (1 - _e * sin0) / (1 + _e * sin0);
        double w1 = Math.Pow(s1 * Math.Pow(s2, _e), _n);
        double sinChi = (w1 - 1) / (w1 + 1);
        _c = (_n + sin0) * (1 - sinChi) / ((_n - sin0) * (1 + sinChi));
        double w2 = _c * w1;
        _chi0 = Math.Asin((w2 - 1) / (w2 + 1));
        _sinChi0 = Math.Sin(_chi0);
        _cosChi0 = Math.Cos(_chi0);
        _r2 = 2 * r;
        _g = _r2 * Math.Tan(Math.PI / 4 - _chi0 / 2);
        _h = 2 * _r2 * Math.Tan(_chi0) + _g;
    }

    /// <summary>The projection centred on a place given in ECEF (its geodetic latitude and longitude).</summary>
    public static MapProjection At(EcefPos p)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(p);
        return new MapProjection(g.LatDeg, g.LonDeg);
    }

    /// <summary>Metres east and north of the centre on the map plane.</summary>
    public (double X, double Y) Forward(double latDeg, double lonDeg)
    {
        double phi = latDeg * Math.PI / 180, sin = Math.Sin(phi);
        double dLambda = _n * NormaliseLon(lonDeg * Math.PI / 180 - _lambda0);
        double sa = (1 + sin) / (1 - sin), sb = (1 - _e * sin) / (1 + _e * sin);
        double w = _c * Math.Pow(sa * Math.Pow(sb, _e), _n);
        double sinChi = (w - 1) / (w + 1), cosChi = Math.Sqrt(Math.Max(0, 1 - sinChi * sinChi));
        double cosL = Math.Cos(dLambda);
        double b = 1 + sinChi * _sinChi0 + cosChi * _cosChi0 * cosL;
        return (_r2 * cosChi * Math.Sin(dLambda) / b, _r2 * (sinChi * _cosChi0 - cosChi * _sinChi0 * cosL) / b);
    }

    /// <summary><see cref="Forward(double, double)"/> of an ECEF place (its height ignored: the plane is the ellipsoid's).</summary>
    public (double X, double Y) Forward(EcefPos p)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(p);
        return Forward(g.LatDeg, g.LonDeg);
    }

    /// <summary>The latitude and longitude (degrees) at a map point.</summary>
    public (double LatDeg, double LonDeg) Inverse(double x, double y)
    {
        double i = Math.Atan(x / (_h + y));
        double j = Math.Atan(x / (_g - y)) - i;
        double chi = _chi0 + 2 * Math.Atan((y - x * Math.Tan(j / 2)) / _r2);
        double dLambda = j + 2 * i;
        double lambda = dLambda / _n + _lambda0;
        double sinChi = Math.Sin(chi);
        double psi = 0.5 * Math.Log((1 + sinChi) / (_c * (1 - sinChi))) / _n;
        double phi = 2 * Math.Atan(Math.Exp(psi)) - Math.PI / 2;
        for (int k = 0; k < 12; k++)
        {
            double s = Math.Sin(phi);
            double psiI = Math.Log(Math.Tan(phi / 2 + Math.PI / 4) * Math.Pow((1 - _e * s) / (1 + _e * s), _e / 2));
            double next = phi - (psiI - psi) * Math.Cos(phi) * (1 - _e2 * s * s) / (1 - _e2);
            bool done = Math.Abs(next - phi) < 1e-12;
            phi = next;
            if (done) break;
        }
        return (phi * 180 / Math.PI, NormaliseLon(lambda) * 180 / Math.PI);
    }

    /// <summary>
    /// The map's scale at a map point: plane metres to one metre on the ellipsoid there (1 at the centre). From the
    /// sphere's stereographic, 1 + d²/4R² to the 2e-6 the Gauss sphere departs from the ellipsoid over 1000 km.
    /// </summary>
    public double ScaleAt(double x, double y) => _k0 * (1 + (x * x + y * y) / (_r2 * _r2));

    /// <summary>
    /// Grid north at a map point from true north, degrees clockwise (the map's convergence): a bearing read off the
    /// map is the true one less this. 0 on the centre's meridian.
    /// </summary>
    public double ConvergenceAt(double x, double y)
    {
        const double d = 1.0;
        var (lat, lon) = Inverse(x, y);
        var (nx, ny) = Forward(lat + d / 111_000.0, lon);
        return Math.Atan2(nx - x, ny - y) * 180 / Math.PI;
    }

    private static double NormaliseLon(double rad)
    {
        while (rad > Math.PI) rad -= 2 * Math.PI;
        while (rad < -Math.PI) rad += 2 * Math.PI;
        return rad;
    }
}
