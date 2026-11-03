namespace UnitSport.Trainer;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A training course's elevation profile (#901): the heights along the route by distance, so
/// that the grade under the rider, the climbing left and the distance left are all cheap to
/// read. Built from the GPX snapped onto the roads (road-surface heights: bridges and tunnels
/// hold), or from the raw GPX elevations when it would not snap.
/// </summary>
public sealed class CourseProfile
{
    private readonly double[] _distance;
    private readonly double[] _height;

    /// <summary>Climbing still ahead from each point (sum of the rises after it).</summary>
    private readonly double[] _ascentAfter;

    public CourseProfile(IReadOnlyList<(double Distance, double Height)> points)
    {
        if (points.Count < 2) throw new ArgumentException("a course needs at least two points", nameof(points));
        _distance = new double[points.Count];
        _height = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            _distance[i] = points[i].Distance;
            _height[i] = points[i].Height;
        }
        _ascentAfter = new double[points.Count];
        for (int i = points.Count - 2; i >= 0; i--)
            _ascentAfter[i] = _ascentAfter[i + 1] + Math.Max(0, _height[i + 1] - _height[i]);
    }

    public double Length => _distance[^1] - _distance[0];
    public double Ascent => _ascentAfter[0];

    /// <summary>The height at <paramref name="s"/> metres along (clamped to the course).</summary>
    public double HeightAt(double s)
    {
        s += _distance[0];
        if (s <= _distance[0]) return _height[0];
        if (s >= _distance[^1]) return _height[^1];
        int i = Array.BinarySearch(_distance, s);
        if (i >= 0) return _height[i];
        i = ~i;   // first point past s
        double span = _distance[i] - _distance[i - 1];
        double t = span > 1e-9 ? (s - _distance[i - 1]) / span : 0;
        return _height[i - 1] + (_height[i] - _height[i - 1]) * t;
    }

    /// <summary>
    /// The grade at <paramref name="s"/> as a fraction, measured over <paramref name="window"/>
    /// metres centred there (shrunk at the ends). Over a window and not between two points,
    /// because GPS heights, and even mapped ones, step by decimetres every few metres: point to
    /// point, a steady 4% climb reads as a saw from -10% to +20%, which a trainer would play out
    /// as the resistance hammering.
    /// </summary>
    public double GradeAt(double s, double window = 40)
    {
        double from = Math.Max(0, s - window / 2), to = Math.Min(Length, s + window / 2);
        if (to - from < 1) return 0;
        return (HeightAt(to) - HeightAt(from)) / (to - from);
    }

    /// <summary>Climbing still to do from <paramref name="s"/>, metres.</summary>
    public double AscentLeft(double s)
    {
        if (s <= 0) return Ascent;
        if (s >= Length) return 0;
        double abs = s + _distance[0];
        int i = Array.BinarySearch(_distance, abs);
        if (i < 0) i = ~i;   // the next point; the partial rise to it is counted below
        double rise = Math.Max(0, _height[i] - HeightAt(s));
        return rise + _ascentAfter[i];
    }
}

/// <summary>
/// What the trainer is told (#901): the course's grade, scaled by the rider's
/// <b>trainer difficulty</b> (as Zwift's: 50% halves every climb and descent, 100% is the real
/// road), and sent only when it has changed enough and not more than once a second. FTMS
/// trainers acknowledge every write and some queue them; a write per frame backs them up for
/// seconds, which is felt as the hill arriving late.
/// </summary>
public sealed class GradeFeed
{
    public const double MinInterval = 1.0;      // s between writes
    public const double MinChange = 0.002;      // 0.2%
    public const double MaxGrade = 0.25;        // what FTMS trainers accept; the wire's range is wider

    private double _lastSentAt = double.NegativeInfinity;

    /// <summary>The grade last sent, after the difficulty (null before the first).</summary>
    public double? Sent { get; private set; }

    /// <summary>0..1, the share of the road's grade the trainer plays.</summary>
    public double Difficulty { get; set; } = 0.5;

    /// <summary>The grade to send now for a road grade of <paramref name="road"/> at <paramref name="now"/> s, or null to send nothing.</summary>
    public double? Next(double road, double now)
    {
        double grade = Math.Clamp(road * Math.Clamp(Difficulty, 0, 1), -MaxGrade, MaxGrade);
        grade = Math.Round(grade, 3);
        if (now - _lastSentAt < MinInterval) return null;
        if (Sent is { } was && Math.Abs(grade - was) < MinChange) return null;
        _lastSentAt = now;
        Sent = grade;
        return grade;
    }

    /// <summary>A new trainer or a new course: the next call sends whatever it is.</summary>
    public void Reset()
    {
        _lastSentAt = double.NegativeInfinity;
        Sent = null;
    }
}
