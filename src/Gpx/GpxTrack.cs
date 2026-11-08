using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx;

/// <summary>
/// One recorded fix on its track's <see cref="GpxTrack.Plane"/>: X metres east and Y metres north of the
/// track's centre (#1514, G2 C5), never LV95. <see cref="Elevation"/> is above sea level (see
/// <see cref="TrackPlane.At"/>); <see cref="Distance"/> is true metres along the track (see <see cref="TrackPlane.Length"/>).
/// </summary>
public readonly record struct TrackPoint(
    double X, double Y, double Elevation, double Seconds, double Distance);

/// <summary>
/// A parsed GPX track ready for playback: positions on its own map plane (<see cref="Plane"/>, an oblique
/// stereographic centred on the track), plus cumulative time and distance so the runner can be sampled by
/// either. A world position is <c>origin.ToWorld(track.At(x, y, elevation))</c>.
/// </summary>
public sealed class GpxTrack
{
    public required string Name { get; init; }

    /// <summary>
    /// The plane the points lie on (#1514): conformal, true north up and scale 1 at its centre (the middle of
    /// the track's latitude/longitude box), 15 ppm at 50 km from it. Smoothing, distances and road matching
    /// all run on it; <see cref="At"/> takes a point back to the Earth.
    /// </summary>
    public required MapProjection Plane { get; init; }
    public required IReadOnlyList<TrackPoint> Points { get; init; }

    /// <summary>True when the file carried usable timestamps.</summary>
    public required bool HasTiming { get; init; }

    /// <summary>
    /// True when <see cref="TrackPoint.Elevation"/> is the road surface rather than a GPS reading.
    ///
    /// <para>
    /// A recorded track is draped onto the terrain at playback, because its own elevation is
    /// noisy. A road-matched one must NOT be: the road already carries a surveyed deck height on
    /// a bridge and a bore height in a tunnel, and re-draping throws both away — dropping the
    /// runner into the gorge the bridge spans and walking them over the mountain the tunnel goes
    /// through.
    /// </para>
    /// </summary>
    public bool ElevationIsSurface { get; init; }

    /// <summary>
    /// What the recording was made as, so playback can put the right avatar on the course
    /// instead of always running one. Read from the GPX <c>&lt;type&gt;</c> element; a file with
    /// no type, or one naming an activity we do not model a rig for, plays as a runner - the
    /// original behaviour, so nothing regresses for the tracks this never mattered for.
    /// </summary>
    public RideKind Kind { get; init; } = RideKind.OnFoot;

    /// <summary>
    /// Where the nose pointed at each fix (Godot yaw, radians), when the recording carries it.
    /// A track only says where the vehicle went; for a drifting car that is not where it pointed,
    /// and the drift is the whole point of watching it.
    /// </summary>
    public IReadOnlyList<float>? Yaw { get; init; }

    public GpxTrack WithYaw(IReadOnlyList<float> yaw) => new()
    {
        Name = Name, Plane = Plane, Points = Points, HasTiming = HasTiming, ElevationIsSurface = ElevationIsSurface,
        Kind = Kind, MinElevation = MinElevation, MaxElevation = MaxElevation, Ascent = Ascent, Yaw = yaw,
    };

    /// <summary>The recorded nose yaw at a playback time, interpolated the short way round; null if none.</summary>
    public float? SampleYaw(double seconds)
    {
        if (Yaw == null || Points.Count == 0) return null;
        int lo = 0, hi = Points.Count - 1;
        if (seconds <= Points[0].Seconds) return Yaw[0];
        if (seconds >= Points[hi].Seconds) return Yaw[hi];
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (Points[mid].Seconds <= seconds) lo = mid; else hi = mid;
        }
        float a = Yaw[lo], b = Yaw[hi];
        if (float.IsNaN(a)) return float.IsNaN(b) ? null : b;
        if (float.IsNaN(b)) return a;
        float t = (float)((seconds - Points[lo].Seconds) / Math.Max(1e-6, Points[hi].Seconds - Points[lo].Seconds));
        return a + Godot.Mathf.Wrap(b - a, -Godot.Mathf.Pi, Godot.Mathf.Pi) * t;
    }

    /// <summary>The place at a plane point and an elevation above sea level (<see cref="TrackPlane.At"/>).</summary>
    public GlobalPos At(double x, double y, double elevation) => Plane.At(x, y, elevation);

    /// <summary>A place on this track's plane (its height left out).</summary>
    public (double X, double Y) PlaneOf(GlobalPos p) => Plane.Forward(p.Ecef);

    /// <summary>
    /// The LV95 box (min E, min N, max E, max N) holding the track and <paramref name="pad"/> metres round it:
    /// which LV95 tiles to load while the tiles are LV95 (until G2 D).
    /// </summary>
    public (double MinE, double MinN, double MaxE, double MaxN) Lv95Bounds(double pad)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Points)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return TrackPlane.Lv95Box(Plane, minX - pad, minY - pad, maxX + pad, maxY + pad);
    }

    public double Duration => Points.Count == 0 ? 0 : Points[^1].Seconds;
    public double Length => Points.Count == 0 ? 0 : Points[^1].Distance;

    public double MinElevation { get; init; }
    public double MaxElevation { get; init; }

    /// <summary>Total metres climbed, ignoring the jitter typical of GPS elevation.</summary>
    public double Ascent { get; init; }

    /// <summary>
    /// Speed is averaged over this many seconds either side of the sample point.
    ///
    /// A recording with roughly one fix per second has a couple of metres of GPS jitter
    /// between consecutive points, which read as a large instantaneous speed: differencing
    /// a single 1-second segment reports a jog as a sprint and never settles. Averaging
    /// over a window gives the pace a human would recognise.
    /// </summary>
    private const double SpeedWindow = 6.0;

    /// <summary>
    /// Interpolates position, elevation, smoothed speed and distance at a playback time.
    /// Times outside the track clamp to its ends.
    /// </summary>
    public (double X, double Y, double Elevation, double Speed, double Distance) Sample(double seconds)
    {
        if (Points.Count == 0) return (0, 0, 0, 0, 0);
        if (Points.Count == 1)
            return (Points[0].X, Points[0].Y, Points[0].Elevation, 0, 0);

        seconds = Math.Clamp(seconds, 0, Duration);
        var here = At(seconds);

        double a = Math.Max(0, seconds - SpeedWindow);
        double b = Math.Min(Duration, seconds + SpeedWindow);
        double speed = b - a > 1e-3 ? (At(b).Distance - At(a).Distance) / (b - a) : 0;

        return (here.X, here.Y, here.Elevation, speed, here.Distance);
    }

    /// <summary>Linear interpolation of the recorded values at a time.</summary>
    private (double X, double Y, double Elevation, double Distance) At(double seconds)
    {
        int i = FindSegment(seconds);
        var a = Points[i];
        var b = Points[i + 1];

        double span = b.Seconds - a.Seconds;
        double t = span > 1e-6 ? (seconds - a.Seconds) / span : 0;

        return (
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Elevation + (b.Elevation - a.Elevation) * t,
            a.Distance + (b.Distance - a.Distance) * t);
    }

    /// <summary>Index of the segment containing <paramref name="seconds"/>.</summary>
    private int FindSegment(double seconds)
    {
        int lo = 0, hi = Points.Count - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Points[mid].Seconds <= seconds) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}

/// <summary>
/// A GPX track's map plane (#1514, G2 C5): the <see cref="MapProjection"/> centred on it, the true length of a
/// step on it, the way back to the Earth, and the crossings to the LV95 sources (the road and terrain tiles,
/// LV95 until G2 D). Godot-free: <c>GpxTrackPlaneTests</c> links it.
/// </summary>
public static class TrackPlane
{
    /// <summary>
    /// The plane centred on the middle of a track's latitude/longitude box (degrees). A track crossing the
    /// antimeridian would centre on the far side of the Earth; none does.
    /// </summary>
    public static MapProjection Around(IReadOnlyList<double> lats, IReadOnlyList<double> lons)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
        for (int i = 0; i < lats.Count; i++)
        {
            minLat = Math.Min(minLat, lats[i]); maxLat = Math.Max(maxLat, lats[i]);
            minLon = Math.Min(minLon, lons[i]); maxLon = Math.Max(maxLon, lons[i]);
        }
        return new MapProjection((minLat + maxLat) * 0.5, (minLon + maxLon) * 0.5);
    }

    /// <summary>
    /// True metres on the ellipsoid of a straight step on the plane: its plane length over the plane's scale at
    /// the step's middle. The scale is 1 at the centre and 1 + d²/4R² at d (15 ppm at 50 km): small against GPS
    /// noise, but free, and it keeps a long track's length and speed the Earth's. The height's own stretch
    /// (1.6e-7 per metre, 80 ppm at 500 m) is left out, as LV95's was.
    /// </summary>
    public static double Length(MapProjection plane, double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        return Math.Sqrt(dx * dx + dy * dy) / plane.ScaleAt((x0 + x1) * 0.5, (y0 + y1) * 0.5);
    }

    /// <summary>
    /// The place at a plane point and an elevation above sea level. A GPX <c>&lt;ele&gt;</c> is a height above
    /// the geoid (EGM96 for most recorders); it is taken as the game's height above sea level, LHN95 (what
    /// <see cref="GlobalPos.Alt"/>, <see cref="GlobalPos.FromLv95"/> and a flat frame's world Y are), as the LV95
    /// track always did: the two geoids differ by a metre or two over Switzerland, below a GPS's own height
    /// error. Built on WGS84 at a guessed ellipsoidal height, then moved along the plumb line to that LHN95
    /// height (<see cref="GlobalPos.WithAlt"/>, exact). A few microseconds.
    /// </summary>
    public static GlobalPos At(this MapProjection plane, double x, double y, double elevation)
    {
        var (lat, lon) = plane.Inverse(x, y);
        // ~50 m of geoid over Switzerland; WithAlt takes the rest off along the plumb line
        var guess = GlobalPos.FromEcef(Ellipsoid.Wgs84.ToEcef(new Geodetic(lat, lon, elevation + 50)));
        return guess.WithAlt(elevation);
    }

    /// <summary>
    /// An LV95 point of a Swiss source (a road vertex) on the plane, at its LHN95 height
    /// <paramref name="hLhn95"/> (the height moves the point 3e-5 m per metre: pass it when known).
    /// </summary>
    public static (double X, double Y) FromLv95(this MapProjection plane, double e, double n, double hLhn95)
    {
        var (lat, lon) = SwissGeodesy.ToLatLon(e, n, hLhn95);
        return plane.Forward(lat, lon);
    }

    /// <summary>The LV95 point under a plane point, for a tile key only (to ~10 cm: the Plateau's height is assumed).</summary>
    public static (double E, double N) Lv95Of(this MapProjection plane, double x, double y)
    {
        var (lat, lon) = plane.Inverse(x, y);
        return SwissGeodesy.ToLv95(lat, lon);
    }

    /// <summary>
    /// The LV95 box (min E, min N, max E, max N) holding a plane box: its four corners in LV95. The two maps turn
    /// against each other by a couple of degrees at most and bend its edges by micrometres, so the corners hold it.
    /// </summary>
    public static (double MinE, double MinN, double MaxE, double MaxN) Lv95Box(
        MapProjection plane, double minX, double minY, double maxX, double maxY)
    {
        double e0 = double.MaxValue, n0 = double.MaxValue, e1 = double.MinValue, n1 = double.MinValue;
        foreach (var (x, y) in new[] { (minX, minY), (maxX, minY), (minX, maxY), (maxX, maxY) })
        {
            var (e, n) = plane.Lv95Of(x, y);
            e0 = Math.Min(e0, e); e1 = Math.Max(e1, e);
            n0 = Math.Min(n0, n); n1 = Math.Max(n1, n);
        }
        return (e0, n0, e1, n1);
    }
}
