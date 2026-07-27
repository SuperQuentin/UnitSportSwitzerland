namespace UnitSport.Gpx;

/// <summary>
/// WGS84 (GPS) to LV95 conversion using swisstopo's approximate formulas.
///
/// Accurate to well under a metre inside Switzerland, which is far tighter than consumer
/// GPS itself, so the rigorous transformation would buy nothing here.
/// </summary>
public static class SwissProjection
{
    /// <summary>Converts geographic degrees to LV95 easting/northing in metres.</summary>
    public static (double E, double N) ToLv95(double latDeg, double lonDeg)
    {
        // auxiliary values: arc seconds relative to the Bern reference, in units of 10000"
        double phi = (latDeg * 3600.0 - 169028.66) / 10000.0;
        double lam = (lonDeg * 3600.0 - 26782.5) / 10000.0;

        double e = 2600072.37
                   + 211455.93 * lam
                   - 10938.51 * lam * phi
                   - 0.36 * lam * phi * phi
                   - 44.54 * lam * lam * lam;

        double n = 1200147.07
                   + 308807.95 * phi
                   + 3745.25 * lam * lam
                   + 76.63 * phi * phi
                   - 194.56 * lam * lam * phi
                   + 119.79 * phi * phi * phi;

        return (e, n);
    }

    /// <summary>Inverse of <see cref="ToLv95"/>, for reporting positions back as GPS.</summary>
    public static (double Lat, double Lon) ToWgs84(double e, double n)
    {
        double y = (e - 2600000.0) / 1000000.0;
        double x = (n - 1200000.0) / 1000000.0;

        double lon = 2.6779094
                     + 4.728982 * y
                     + 0.791484 * y * x
                     + 0.1306 * y * x * x
                     - 0.0436 * y * y * y;

        double lat = 16.9023892
                     + 3.238272 * x
                     - 0.270978 * y * y
                     - 0.002528 * x * x
                     - 0.0447 * y * y * x
                     - 0.0140 * x * x * x;

        return (lat * 100.0 / 36.0, lon * 100.0 / 36.0);
    }
}
