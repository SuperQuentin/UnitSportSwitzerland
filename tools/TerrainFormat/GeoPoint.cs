namespace UnitSport.Terrain.Format;

/// <summary>
/// A position on the Earth as the region-wide files carry it in a cube region (#1507, G2 B3 row 8): WGS84 latitude
/// and longitude (degrees) and the ellipsoidal height (metres). The LV95 build's files leave it out (null, not
/// written); a cube region's copies (<c>--cube-region-files</c>) carry it beside the LV95 fields, which stay until D.
/// </summary>
public readonly record struct GeoPoint(double Lat, double Lon, double H)
{
    /// <summary>An LV95 point at its LHN95 height, rigorously (<see cref="SwissGeodesy"/>).</summary>
    public static GeoPoint FromLv95(double e, double n, double hLhn95)
    {
        var g = SwissGeodesy.ToWgs84(e, n, hLhn95);
        return new GeoPoint(g.LatDeg, g.LonDeg, g.H);
    }

    public EcefPos ToEcef() => Ellipsoid.Wgs84.ToEcef(new Geodetic(Lat, Lon, H));

    /// <summary>Back to LV95 E, N and the LHN95 height.</summary>
    public (double E, double N, double H) ToLv95() => SwissGeodesy.FromWgs84(new Geodetic(Lat, Lon, H));

    /// <summary>A grid bearing (degrees clockwise from LV95's grid north) at an LV95 point as a true bearing, 0..360.</summary>
    public static double TrueBearing(double gridDeg, double e, double n, double hLhn95) =>
        ((gridDeg - SwissGeodesy.GridConvergenceDeg(e, n, hLhn95)) % 360 + 360) % 360;
}
