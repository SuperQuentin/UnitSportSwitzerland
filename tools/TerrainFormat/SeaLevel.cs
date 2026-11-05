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
public sealed class SwissSeaLevel : IGriddedSeaLevel
{
    public static readonly SwissSeaLevel Instance = new();

    /// <summary>
    /// <see cref="Undulation"/> in its two parts (#1507): the Bessel height of the WGS84 ellipsoid's point
    /// (smooth), negated, and the point's LV95 position, where <see cref="Grid"/> reads CHGeo2004. Their
    /// sum is <see cref="Undulation"/> to the bit (<c>LeafTransformTests</c>).
    /// </summary>
    public (double Smooth, double A, double B) Split(double latDeg, double lonDeg)
    {
        var p = Ellipsoid.Wgs84.ToEcef(latDeg * Math.PI / 180, lonDeg * Math.PI / 180, 0);
        // SwissGeodesy.Lhn95Height's steps, kept apart
        var g = Ellipsoid.Bessel1841.ToGeodetic(p - SwissGeodesy.DatumShift);
        var (e, n) = SwissGeodesy.FromBessel(g.LatDeg * Math.PI / 180.0, g.LonDeg * Math.PI / 180.0);
        return (-g.H, e, n);
    }

    public double Grid(double a, double b) => SwissGeodesy.BesselGeoid(a, b);

    public double Undulation(double latDeg, double lonDeg)
    {
        // the ellipsoidal height of the point whose LHN95 height is 0; the two datums' normals are a few
        // arc-seconds apart, so the offset is the same at any height to well under a millimetre
        var p = Ellipsoid.Wgs84.ToEcef(latDeg * Math.PI / 180, lonDeg * Math.PI / 180, 0);
        return -SwissGeodesy.Lhn95Height(p);
    }
}

/// <summary>
/// A sea level that is a smooth part plus a grid read at a smooth position (#1507): Switzerland's is the
/// Bessel ellipsoid's height (smooth) plus CHGeo2004, bilinear in a 20 km grid of LV95 and kinked at its
/// lines. <see cref="LeafTransform"/> interpolates the smooth parts between its control points and reads
/// the grid at every point, so the kinks stay where they are: interpolating the undulation itself across
/// a grid line is off by up to ~1.5 mm.
/// </summary>
public interface IGriddedSeaLevel : ISeaLevel
{
    /// <summary>The smooth part and the grid position: <c>Undulation = Smooth + Grid(A, B)</c>.</summary>
    (double Smooth, double A, double B) Split(double latDeg, double lonDeg);

    double Grid(double a, double b);
}
