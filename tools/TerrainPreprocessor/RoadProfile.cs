using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The smoothed long profile of a draped road (#1419). A centreline draped on the 1 m DTM every 4 m
/// follows every hummock of the ground under it; a real road is graded over tens of metres. Each draped
/// line is averaged along its arc length with a Gaussian of a class-dependent width
/// (<see cref="SigmaFor"/>), before <c>RoadExtractor.LimitGrade</c>.
///
/// <para>
/// <b>Fixed points.</b> The line's two ends and every anchor (an interior vertex where another line ends: a
/// junction on it) keep their draped height exactly: each run between them is extended past its ends by an odd
/// reflection about the end point, so the average at the end is the end itself and a straight grade passes
/// through unchanged. Junction heights, bridge and tunnel approach deltas (measured at the line's ends) and the
/// tile seams (the profile is computed on the whole line, then sampled per tile piece) stay as they were.
/// </para>
///
/// <para>
/// <b>Limits.</b> The change is capped: at most <see cref="MaxLower"/> under the drape (a crest is cut, never
/// past the drape offset by much, and a road on a cliff lip is not pulled down the face) and at most
/// <see cref="MaxRaise"/> over it (a sag is filled).
/// </para>
/// </summary>
public static class RoadProfile
{
    /// <summary>Metres: the most the smoothing lowers a point under its draped height (the drape lies 0.35 m over the ground).</summary>
    public const double MaxLower = 0.5;

    /// <summary>Metres: the most it raises one.</summary>
    public const double MaxRaise = 1.0;

    /// <summary>
    /// The Gaussian's standard deviation along the road, metres; 0 = not smoothed. About a quarter of the
    /// window it averages over: 40 m for a motorway or a railway line, 16 m for a footpath.
    /// </summary>
    public static double SigmaFor(RoadClass cls, RoadFlags flags)
    {
        if ((flags & (RoadFlags.Stairs | RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) return 0;
        if (RoadFormat.IsAerial(cls) || RoadFormat.IsWall(cls) || RoadFormat.IsWatercourse(cls)) return 0;
        if (cls == RoadClass.Railway)
            return (flags & (RoadFlags.RackRailway | RoadFlags.Funicular)) != 0 ? 4 : 10;
        if ((flags & RoadFlags.MountainHiking) != 0) return 3;
        return cls switch
        {
            RoadClass.Motorway or RoadClass.Expressway => 10,
            RoadClass.Ramp or RoadClass.Major or RoadClass.Road => 7.5,
            RoadClass.Minor or RoadClass.Lane => 6,
            RoadClass.Track => 5,
            RoadClass.Bisse => 0,   // dug to a near-constant fall already
            _ => 4,                 // paths, links, squares
        };
    }

    /// <summary>
    /// The change to add to each draped height <paramref name="y"/> at arc length <paramref name="along"/>
    /// (increasing, metres): the smoothed profile minus the drape, capped. Zero at both ends and at every
    /// <paramref name="anchor"/>.
    /// </summary>
    public static double[] Delta(double[] along, double[] y, double sigma, bool[]? anchor = null)
    {
        int n = y.Length;
        var delta = new double[n];
        if (sigma <= 0 || n < 3) return delta;
        int start = 0;
        for (int i = 1; i < n; i++)
        {
            if (i < n - 1 && (anchor == null || !anchor[i])) continue;
            SmoothRun(along, y, start, i, sigma, delta);
            start = i;
        }
        for (int i = 0; i < n; i++) delta[i] = Math.Clamp(delta[i], -MaxLower, MaxRaise);
        return delta;
    }

    /// <summary>The smoothing of points a..b (both fixed), written into <paramref name="delta"/>[a+1..b-1].</summary>
    private static void SmoothRun(double[] s, double[] y, int a, int b, double sigma, double[] delta)
    {
        if (b - a < 2) return;
        double len = s[b] - s[a];
        // three sigma at most half the run: the kernel reaches past one end only, so both ends stay exact
        double sg = Math.Min(sigma, len / 6);
        if (sg < 0.5) return;
        double reach = 3 * sg, inv = 1 / (2 * sg * sg);
        double ya = y[a], yb = y[b], s0 = s[a], s1 = s[b];
        int lo = a;
        for (int i = a + 1; i < b; i++)
        {
            double si = s[i];
            while (s[lo] < si - reach && lo < i) lo++;
            double sw = 0, swy = 0;
            // the run's own points, trapezoid weights
            for (int j = lo; j <= b && s[j] <= si + reach; j++)
            {
                double w = Weight(s, j, a, b) * Math.Exp(-(s[j] - si) * (s[j] - si) * inv);
                sw += w;
                swy += w * y[j];
            }
            // odd reflections about the two ends (the end point itself is not doubled)
            if (si - s0 < reach)
                for (int j = a + 1; j <= b; j++)
                {
                    double d = 2 * s0 - s[j] - si;   // the mirrored position's offset
                    if (d < -reach) break;
                    double w = Weight(s, j, a, b) * Math.Exp(-d * d * inv);
                    sw += w;
                    swy += w * (2 * ya - y[j]);
                }
            if (s1 - si < reach)
                for (int j = b - 1; j >= a; j--)
                {
                    double d = 2 * s1 - s[j] - si;
                    if (d > reach) break;
                    double w = Weight(s, j, a, b) * Math.Exp(-d * d * inv);
                    sw += w;
                    swy += w * (2 * yb - y[j]);
                }
            if (sw > 0) delta[i] = swy / sw - y[i];
        }
    }

    /// <summary>
    /// The trapezoid weight of point j in the run a..b. An end point sits in the middle of the reflected run, so
    /// it carries a whole span (its own half and its mirror's), which keeps a straight grade exact.
    /// </summary>
    private static double Weight(double[] s, int j, int a, int b) =>
        j == a ? s[a + 1] - s[a] : j == b ? s[b] - s[b - 1] : (s[j + 1] - s[j - 1]) * 0.5;

    /// <summary>Linear interpolation of <paramref name="v"/> (given at increasing <paramref name="s"/>) at <paramref name="at"/>, clamped to the ends.</summary>
    public static double At(double[] s, double[] v, double at)
    {
        int n = s.Length;
        if (n == 0) return 0;
        if (at <= s[0]) return v[0];
        if (at >= s[n - 1]) return v[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (s[mid] <= at) lo = mid; else hi = mid;
        }
        double span = s[hi] - s[lo];
        return span <= 1e-12 ? v[lo] : v[lo] + (v[hi] - v[lo]) * (at - s[lo]) / span;
    }
}
