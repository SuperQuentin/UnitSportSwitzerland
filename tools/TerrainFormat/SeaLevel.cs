namespace UnitSport.Terrain.Format;

/// <summary>
/// A height above sea level (the geoid) against the ellipsoidal height of WGS84 (#1443 G2, B2 #1506). The
/// build stages work above sea level, where a lake is one number; the leaf writer converts to the
/// ellipsoidal heights the cube tiles hold. Each source brings the model its heights were measured in.
/// </summary>
public interface ISeaLevel
{
    /// <summary>The ellipsoidal height of sea level at a latitude and longitude (degrees): add it to a height above sea level.</summary>
    double Undulation(double latDeg, double lonDeg);
}

/// <summary>
/// Switzerland's sea level: LHN95 through the rigorous chain of <see cref="SwissGeodesy"/> (Bessel, its
/// CHGeo2004 grid, the datum shift), the model swissALTI3D's heights are in. Held at the grid's edge outside
/// it, as <see cref="SwissGeodesy.BesselGeoid"/> is (G1 took French IGN69 heights this way too); G3 brings
/// EGM2008 and the French RAF20.
/// </summary>
public sealed class SwissSeaLevel : ISeaLevel
{
    public static readonly SwissSeaLevel Instance = new();

    public double Undulation(double latDeg, double lonDeg)
    {
        // the ellipsoidal height of the point whose LHN95 height is 0; the two datums' normals are a few
        // arc-seconds apart, so the offset is the same at any height to well under a millimetre
        var p = Ellipsoid.Wgs84.ToEcef(latDeg * Math.PI / 180, lonDeg * Math.PI / 180, 0);
        return -SwissGeodesy.Lhn95Height(p);
    }
}
