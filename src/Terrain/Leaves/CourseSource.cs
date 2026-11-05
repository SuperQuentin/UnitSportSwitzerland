using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A fixture course's coordinates (#1509, G2 B5): metres east (a) and north (b) of its start, on the orthographic
/// plane tangent at the start's latitude and longitude (<see cref="WorkPlane"/>, as a work frame's), heights above
/// Switzerland's sea level. A course is placed at a latitude and longitude, not an LV95 point. Its metres are true at
/// its own height (<see cref="HeightM"/>, the start's ground): a plane's metres are the ellipsoid's, and 1,200 m up a
/// metre on the ground spans (R + h) / R of one there, 2×10⁻⁴ (half a metre on the hairpin's 3 km). Within a few km
/// of the start they are true to a few ppm.
/// </summary>
public sealed class CourseSource : ISourceProjection
{
    /// <summary>The Earth's mean radius, for the height's scale (another radius changes it by under a ppm).</summary>
    private const double EarthRadiusM = 6_371_008.8;

    private readonly WorkPlane _plane;
    private readonly double _scale;

    public double StartLatDeg { get; }
    public double StartLonDeg { get; }

    /// <summary>The height above sea level its metres are true at.</summary>
    public double HeightM { get; }

    public CourseSource(double startLatDeg, double startLonDeg, double heightM = 0)
    {
        (StartLatDeg, StartLonDeg, HeightM) = (startLatDeg, startLonDeg, heightM);
        _plane = new WorkPlane(new EnuFrame(new Geodetic(startLatDeg, startLonDeg, 0)));
        _scale = EarthRadiusM / (EarthRadiusM + heightM);
    }

    public string Name => "course";

    public ISeaLevel SeaLevel => SwissSeaLevel.Instance;

    public (double LatDeg, double LonDeg) ToLatLon(double a, double b) => _plane.FromPlane(a * _scale, b * _scale);

    public (double A, double B) FromLatLon(double latDeg, double lonDeg)
    {
        var (x, y) = _plane.ToPlane(latDeg, lonDeg);
        return (x / _scale, y / _scale);
    }
}
