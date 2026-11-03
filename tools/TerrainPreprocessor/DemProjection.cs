namespace UnitSport.Tools.Preprocessor;

/// <summary>The national grids the neighbours' elevation models come in (#936, #965).</summary>
public enum DemCrs
{
    /// <summary>France, IGN: RGF93 / Lambert-93 (EPSG:2154).</summary>
    Lambert93,
    /// <summary>Germany (Baden-Württemberg), South Tyrol: ETRS89 / UTM zone 32N (EPSG:25832).</summary>
    Utm32,
    /// <summary>Austria, BEV: ETRS89 / LAEA Europe (EPSG:3035).</summary>
    Laea3035,
}

/// <summary>
/// LV95 to the neighbours' grids, rigorously: swisstopo's inverse oblique Mercator (Bessel),
/// CH1903+ -> ETRS89 by the three-parameter shift, then the target projection on GRS80. Each
/// matches GDAL (EPSG:2056 -> 2154 / 25832 / 3035) to well under a millimetre around Switzerland;
/// <c>SwissProjection</c>'s approximate formulas are off by 1-2.5 m at the borders, which shows as a
/// step at a terrain seam.
/// </summary>
public static class DemProjection
{
    public static DemCrs Parse(string s) => s.ToLowerInvariant() switch
    {
        "lambert93" or "l93" or "2154" or "epsg:2154" => DemCrs.Lambert93,
        "utm32" or "25832" or "epsg:25832" => DemCrs.Utm32,
        "laea" or "3035" or "epsg:3035" => DemCrs.Laea3035,
        _ => throw new ArgumentException($"unknown DEM grid '{s}' (lambert93, utm32, laea)"),
    };

    public static (double X, double Y) FromLv95(DemCrs crs, double e, double n)
    {
        var (phi, lam) = Lv95ToEtrs89(e, n);
        return crs switch
        {
            DemCrs.Lambert93 => Lambert93(phi, lam),
            DemCrs.Utm32 => Utm(phi, lam, Deg(9)),
            _ => Laea3035(phi, lam),
        };
    }

    // ---- LV95 -> ETRS89 ----------------------------------------------------------------------------

    private const double BesselA = 6377397.155, BesselE2 = 0.006674372230614;
    private static readonly double BesselE = Math.Sqrt(BesselE2);
    private static readonly double Phi0 = Deg(46 + 57 / 60.0 + 8.66 / 3600), Lam0 = Deg(7 + 26 / 60.0 + 22.5 / 3600);
    private static readonly double SphereR = BesselA * Math.Sqrt(1 - BesselE2) / (1 - BesselE2 * Math.Pow(Math.Sin(Phi0), 2));
    private static readonly double Alpha = Math.Sqrt(1 + BesselE2 / (1 - BesselE2) * Math.Pow(Math.Cos(Phi0), 4));
    private static readonly double B0 = Math.Asin(Math.Sin(Phi0) / Alpha);
    private static readonly double K = Math.Log(Math.Tan(Math.PI / 4 + B0 / 2)) - Alpha * Math.Log(Math.Tan(Math.PI / 4 + Phi0 / 2))
        + Alpha * BesselE / 2 * Math.Log((1 + BesselE * Math.Sin(Phi0)) / (1 - BesselE * Math.Sin(Phi0)));

    private const double A = 6378137.0, F = 1 / 298.257222101;
    private static readonly double E2 = 2 * F - F * F, E = Math.Sqrt(E2);

    /// <summary>LV95 metres -> ETRS89 (GRS80) latitude and longitude in radians.</summary>
    public static (double Phi, double Lam) Lv95ToEtrs89(double e, double n)
    {
        double lb = (e - 2600000) / SphereR;
        double bb = 2 * (Math.Atan(Math.Exp((n - 1200000) / SphereR)) - Math.PI / 4);
        double b = Math.Asin(Math.Cos(B0) * Math.Sin(bb) + Math.Sin(B0) * Math.Cos(bb) * Math.Cos(lb));
        double l = Math.Atan(Math.Sin(lb) / (Math.Cos(B0) * Math.Cos(lb) - Math.Sin(B0) * Math.Tan(bb)));
        double lam = Lam0 + l / Alpha, phi = b;
        for (int i = 0; i < 6; i++)
        {
            double s = (Math.Log(Math.Tan(Math.PI / 4 + b / 2)) - K) / Alpha
                + BesselE * Math.Log(Math.Tan(Math.PI / 4 + Math.Asin(BesselE * Math.Sin(phi)) / 2));
            phi = 2 * Math.Atan(Math.Exp(s)) - Math.PI / 2;
        }

        // Bessel -> ECEF, shift, ECEF -> GRS80 (height 0: its effect on position is far below a cm)
        double nb = BesselA / Math.Sqrt(1 - BesselE2 * Math.Sin(phi) * Math.Sin(phi));
        double x = nb * Math.Cos(phi) * Math.Cos(lam) + 674.374;
        double y = nb * Math.Cos(phi) * Math.Sin(lam) + 15.056;
        double z = nb * (1 - BesselE2) * Math.Sin(phi) + 405.346;
        lam = Math.Atan2(y, x);
        double p = Math.Sqrt(x * x + y * y);
        phi = Math.Atan2(z, p * (1 - E2));
        for (int i = 0; i < 6; i++)
        {
            double ng = A / Math.Sqrt(1 - E2 * Math.Sin(phi) * Math.Sin(phi));
            double h = p / Math.Cos(phi) - ng;
            phi = Math.Atan2(z, p * (1 - E2 * ng / (ng + h)));
        }
        return (phi, lam);
    }

    // ---- Lambert-93 (conformal conic, 2 standard parallels) ----------------------------------------

    private static readonly double L93Lam0 = Deg(3);
    private static readonly double L93N, L93F, L93Rho0;

    static DemProjection()
    {
        double p1 = Deg(44), p2 = Deg(49), p0 = Deg(46.5);
        L93N = (Math.Log(M(p1)) - Math.Log(M(p2))) / (Math.Log(T(p1)) - Math.Log(T(p2)));
        L93F = M(p1) / (L93N * Math.Pow(T(p1), L93N));
        L93Rho0 = A * L93F * Math.Pow(T(p0), L93N);
    }

    private static double M(double p) => Math.Cos(p) / Math.Sqrt(1 - E2 * Math.Sin(p) * Math.Sin(p));
    private static double T(double p) =>
        Math.Tan(Math.PI / 4 - p / 2) / Math.Pow((1 - E * Math.Sin(p)) / (1 + E * Math.Sin(p)), E / 2);

    public static (double X, double Y) Lambert93(double phi, double lam)
    {
        double rho = A * L93F * Math.Pow(T(phi), L93N);
        double th = L93N * (lam - L93Lam0);
        return (700000 + rho * Math.Sin(th), 6600000 + L93Rho0 - rho * Math.Cos(th));
    }

    // ---- UTM (transverse Mercator, Krüger series to n^3: sub-mm within a zone) --------------------

    private static readonly double Tn = F / (2 - F);
    private static readonly double TA = A / (1 + Tn) * (1 + Tn * Tn / 4 + Math.Pow(Tn, 4) / 64);
    private static readonly double[] TAlpha =
    {
        Tn / 2 - 2 * Tn * Tn / 3 + 5 * Math.Pow(Tn, 3) / 16,
        13 * Tn * Tn / 48 - 3 * Math.Pow(Tn, 3) / 5,
        61 * Math.Pow(Tn, 3) / 240,
    };

    public static (double X, double Y) Utm(double phi, double lam, double lam0)
    {
        const double K0 = 0.9996;
        double c = 2 * Math.Sqrt(Tn) / (1 + Tn);
        double t = Math.Sinh(Math.Atanh(Math.Sin(phi)) - c * Math.Atanh(c * Math.Sin(phi)));
        double xi = Math.Atan(t / Math.Cos(lam - lam0));
        double eta = Math.Atanh(Math.Sin(lam - lam0) / Math.Sqrt(1 + t * t));
        double x = eta, y = xi;
        for (int j = 1; j <= 3; j++)
        {
            x += TAlpha[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            y += TAlpha[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
        }
        return (500000 + K0 * TA * x, K0 * TA * y);
    }

    // ---- LAEA Europe (ellipsoidal Lambert azimuthal equal-area, Snyder) ---------------------------

    private static readonly double LPhi0 = Deg(52), LLam0 = Deg(10);
    private static readonly double Qp = Q(Math.PI / 2), Beta1 = Math.Asin(Q(LPhi0) / Qp), Rq = A * Math.Sqrt(Qp / 2);
    private static readonly double LD = A * (Math.Cos(LPhi0) / Math.Sqrt(1 - E2 * Math.Sin(LPhi0) * Math.Sin(LPhi0))) / (Rq * Math.Cos(Beta1));

    private static double Q(double p)
    {
        double s = Math.Sin(p);
        return (1 - E2) * (s / (1 - E2 * s * s) - 1 / (2 * E) * Math.Log((1 - E * s) / (1 + E * s)));
    }

    public static (double X, double Y) Laea3035(double phi, double lam)
    {
        double beta = Math.Asin(Q(phi) / Qp);
        double dl = lam - LLam0;
        double b = Rq * Math.Sqrt(2 / (1 + Math.Sin(Beta1) * Math.Sin(beta) + Math.Cos(Beta1) * Math.Cos(beta) * Math.Cos(dl)));
        return (4321000 + b * LD * Math.Cos(beta) * Math.Sin(dl),
                3210000 + b / LD * (Math.Cos(Beta1) * Math.Sin(beta) - Math.Sin(Beta1) * Math.Cos(beta) * Math.Cos(dl)));
    }

    private static double Deg(double d) => d * Math.PI / 180;
}
