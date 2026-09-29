using UnitSport.Player;

namespace UnitSport.Gpx;

/// <summary>One recorded fix, already projected to LV95.</summary>
public readonly record struct TrackPoint(
    double E, double N, double Elevation, double Seconds, double Distance);

/// <summary>
/// A parsed GPX track ready for playback: positions in LV95, plus cumulative time and
/// distance so the runner can be sampled by either.
/// </summary>
public sealed class GpxTrack
{
    public required string Name { get; init; }
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
        Name = Name, Points = Points, HasTiming = HasTiming, ElevationIsSurface = ElevationIsSurface,
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
    public (double E, double N, double Elevation, double Speed, double Distance) Sample(double seconds)
    {
        if (Points.Count == 0) return (0, 0, 0, 0, 0);
        if (Points.Count == 1)
            return (Points[0].E, Points[0].N, Points[0].Elevation, 0, 0);

        seconds = Math.Clamp(seconds, 0, Duration);
        var here = At(seconds);

        double a = Math.Max(0, seconds - SpeedWindow);
        double b = Math.Min(Duration, seconds + SpeedWindow);
        double speed = b - a > 1e-3 ? (At(b).Distance - At(a).Distance) / (b - a) : 0;

        return (here.E, here.N, here.Elevation, speed, here.Distance);
    }

    /// <summary>Linear interpolation of the recorded values at a time.</summary>
    private (double E, double N, double Elevation, double Distance) At(double seconds)
    {
        int i = FindSegment(seconds);
        var a = Points[i];
        var b = Points[i + 1];

        double span = b.Seconds - a.Seconds;
        double t = span > 1e-6 ? (seconds - a.Seconds) / span : 0;

        return (
            a.E + (b.E - a.E) * t,
            a.N + (b.N - a.N) * t,
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
