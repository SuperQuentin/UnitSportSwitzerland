namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// An arm's axis from its junction's node outward (#818), LV95: the street's alignment as a polyline. Stations are metres
/// along it from the node; past either end it runs on straight. Offsets across it are metres to the right of a driver
/// arriving (<see cref="RightAt"/>), as a <see cref="Section"/> counts them.
/// </summary>
public sealed class ArmAxis
{
    private readonly Vec2[] _points;
    private readonly double[] _at;

    public ArmAxis(IReadOnlyList<Vec2> points)
    {
        var kept = new List<Vec2> { points[0] };
        for (int k = 1; k < points.Count; k++)
            if (points[k].DistanceTo(kept[^1]) > 1e-6) kept.Add(points[k]);
        if (kept.Count < 2) throw new ArgumentException("an axis needs two distinct points", nameof(points));
        _points = kept.ToArray();
        _at = new double[_points.Length];
        for (int k = 1; k < _points.Length; k++) _at[k] = _at[k - 1] + _points[k].DistanceTo(_points[k - 1]);
    }

    /// <summary>A straight axis from <paramref name="from"/> along <paramref name="heading"/> (tests, a link without alignment).</summary>
    public static ArmAxis Straight(Vec2 from, double heading, double length = 200) => new([from, from + Vec2.FromHeading(heading) * length]);

    public double Length => _at[^1];

    public Vec2 PointAt(double s)
    {
        int k = Segment(s);
        return _points[k] + DirOf(k) * (s - _at[k]);
    }

    /// <summary>The unit direction outward, away from the node.</summary>
    public Vec2 DirAt(double s) => DirOf(Segment(s));

    /// <summary>The unit vector to the right of a driver arriving (driving toward the node), the sections' positive offsets.</summary>
    public Vec2 RightAt(double s) => DirAt(s).Perp;

    /// <summary>The point at station <paramref name="s"/>, <paramref name="offset"/> to the right of a driver arriving.</summary>
    public Vec2 At(double s, double offset) => PointAt(s) + RightAt(s) * offset;

    /// <summary>The station of the axis point nearest <paramref name="p"/> (within its length).</summary>
    public double StationOf(Vec2 p)
    {
        double best = double.MaxValue, result = 0;
        for (int k = 0; k + 1 < _points.Length; k++)
        {
            var a = _points[k];
            var ab = _points[k + 1] - a;
            double len2 = ab.LengthSquared;
            double t = len2 < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
            double d = p.DistanceSquaredTo(a + ab * t);
            if (d < best) { best = d; result = _at[k] + t * (_at[k + 1] - _at[k]); }
        }
        return result;
    }

    private Vec2 DirOf(int k) => (_points[k + 1] - _points[k]) / (_at[k + 1] - _at[k]);

    private int Segment(double s)
    {
        if (s <= 0) return 0;
        if (s >= _at[^1]) return _points.Length - 2;
        int lo = 0, hi = _at.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_at[mid] <= s) lo = mid; else hi = mid;
        }
        return Math.Min(lo, _points.Length - 2);
    }
}
