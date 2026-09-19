namespace UnitSport.Gpx;

/// <summary>
/// WGS84 (GPS) to LV95, forwarding to the shared implementation in the format library.
///
/// <para>
/// The formulas moved to <see cref="UnitSport.Terrain.Format.SwissProjection"/> when the
/// preprocessor needed them too — French IGN data arrives in WGS84 and has to land on the same
/// lattice. This alias stays so the GPX code reads as it did.
/// </para>
/// </summary>
public static class SwissProjection
{
    public static (double E, double N) ToLv95(double latDeg, double lonDeg) =>
        Terrain.Format.SwissProjection.ToLv95(latDeg, lonDeg);

    public static (double Lat, double Lon) ToWgs84(double e, double n) =>
        Terrain.Format.SwissProjection.ToWgs84(e, n);
}
