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
