namespace UnitSport.Tools.RoadGen.Geometry;

/// <summary>How aggressively a polyline is turned into a fluid alignment.</summary>
/// <param name="DesignRadius">Preferred corner radius in metres. Comes from the road class.</param>
/// <param name="MinRadius">Below this a corner is left sharp rather than distorted.</param>
/// <param name="SpiralRatio">Spiral length as a fraction of radius (Ls = ratio·R).</param>
/// <param name="SimplifyTolerance">
/// How far a vertex may be from its chord and still be dropped, in metres. This is the knob
/// that decides what counts as "the road's shape" versus "how the road was sampled".
/// </param>
/// <param name="LegShare">Fraction of a straight leg one corner may consume at each end.</param>
/// <param name="MaxOffset">
/// How far the smoothed alignment may depart from the polyline it came from, in metres.
///
/// <para>
/// This is a safety bound, not a style knob. Heights are carried over from the original line,
/// so wherever smoothing moves a road across a slope it takes a height from slightly the wrong
/// place — and swissALTI3D really does drop tens of metres between adjacent cells beside an
/// alpine path. Measured over 121 tiles without this bound: mean error 0.07 m, but a worst case
/// of 20.6 m on a footpath whose corner had been cut by 2.9 m. Capping the departure at half
/// the road's own width keeps the smoothed centreline inside the original carriageway, which is
/// the strongest guarantee available without re-draping.
/// </para>
/// </param>
public sealed record CurveStyle(
    double DesignRadius,
    double MinRadius,
    double SpiralRatio = 0.25,
    double SimplifyTolerance = 0.6,
    double LegShare = 0.45,
    double MaxOffset = 3.0);

/// <summary>
/// Turns a surveyed polyline into a G2 alignment of straights, clothoids and arcs.
///
/// <para>
/// The construction at each corner is the standard highway one — <b>spiral · arc · spiral</b>,
/// often written SCS. Approaching a bend the curvature ramps linearly from zero to 1/R along
/// a clothoid, holds at 1/R round the arc, then ramps back to zero. That is what a driver
/// actually does with the steering wheel, and it is why the result reads as a road rather
/// than as a chain of tangent circles.
/// </para>
///
/// <para>
/// The radius is not a free choice: it has to fit in the straight legs either side. The
/// solver bisects for the largest radius whose tangent distance fits the space available,
/// so a sweeping motorway curve gets its 400 m and an alpine hairpin quietly gets 12 m,
/// from the same code and without hand-tuning.
/// </para>
/// </summary>
public static class AlignmentBuilder
{
    /// <summary>Below this deflection a vertex is not a corner, it is survey noise.</summary>
    private const double MinDeflection = 0.5 * Math.PI / 180.0;

    /// <summary>
    /// Past this the line doubles back on itself. Real alpine hairpins get close, but a true
    /// reversal is a data spike, and rounding it would invent a road that is not there.
    /// </summary>
    private const double MaxDeflection = 178.0 * Math.PI / 180.0;

    /// <summary>
    /// The smallest radius the solver will fall back to before giving up and leaving a vertex
    /// sharp.
    ///
    /// <para>
    /// This exists because treating the class's <see cref="CurveStyle.MinRadius"/> as a hard
    /// stop is a mistake that looks reasonable and is not: refusing to round a corner does not
    /// leave it at the minimum radius, it leaves it at <i>zero</i> radius, which is tighter
    /// than anything the refusal was protecting against. The bisection already returns the
    /// largest radius that fits, so a corner with room still gets the class minimum or better;
    /// this only governs what happens when there is no room at all.
    /// </para>
    /// </summary>
    private const double HardMinRadius = 0.25;

    /// <summary>
    /// An alignment that follows the polyline exactly — one straight per span, hard corners.
    ///
    /// <para>
    /// For geometry that must not move: a bridge's piers and a tunnel's carve mask were both
    /// derived from the surveyed plan-view line, so rounding its corners would leave the bore
    /// beside its own hole in the terrain. This still gives the junction solver the real
    /// headings to trim against, which is all it needs.
    /// </para>
    /// </summary>
    public static Alignment Polygonal(IReadOnlyList<Vec2> raw)
    {
        var pts = Polyline.Dedupe(raw);
        var pieces = new List<GeometryPiece>();

        for (int i = 1; i < pts.Count; i++)
        {
            var span = pts[i] - pts[i - 1];
            pieces.Add(GeometryPiece.Line(pts[i - 1], span.Heading, span.Length));
        }

        return new Alignment(pieces);
    }

    public static Alignment FromPolyline(IReadOnlyList<Vec2> raw, CurveStyle style)
    {
        var pts = Polyline.Dedupe(raw);
        if (pts.Count >= 3 && style.SimplifyTolerance > 0)
            pts = Polyline.Simplify(pts, style.SimplifyTolerance);

        var pieces = new List<GeometryPiece>();
        if (pts.Count < 2) return new Alignment(pieces);

        if (pts.Count == 2)
        {
            var dir0 = (pts[1] - pts[0]);
            pieces.Add(GeometryPiece.Line(pts[0], dir0.Heading, dir0.Length));
            return new Alignment(pieces);
        }

        var cursor = pts[0];
        double heading = (pts[1] - pts[0]).Heading;

        for (int i = 1; i < pts.Count - 1; i++)
        {
            var u = (pts[i] - pts[i - 1]).Normalized();
            var v = (pts[i + 1] - pts[i]).Normalized();
            double signedTurn = Angles.Delta(u.Heading, v.Heading);
            double deflection = Math.Abs(signedTurn);

            double available = style.LegShare * Math.Min(
                pts[i].DistanceTo(pts[i - 1]), pts[i + 1].DistanceTo(pts[i]));

            var corner = deflection < MinDeflection || deflection > MaxDeflection
                ? null
                : SolveCorner(deflection, available, style);

            if (corner is null)
            {
                // No radius fits, the turn is below the noise floor, or the line doubles back
                // on itself. The vertex stays sharp — but it still has to be EMITTED. Simply
                // skipping it leaves the cursor travelling on the old heading, and the
                // alignment walks away from its own polyline: measured 271 m of drift on a
                // synthesised network before this was handled.
                RunTo(pieces, ref cursor, ref heading, pts[i]);
                heading = v.Heading;
                continue;
            }

            var (radius, spiralLength, tangentDistance, tau) = corner.Value;
            double sign = Math.Sign(signedTurn);
            double curvature = sign / radius;
            double rate = curvature / spiralLength;

            // straight up to the tangent-to-spiral point
            RunTo(pieces, ref cursor, ref heading, pts[i] - u * tangentDistance);

            // spiral in: curvature 0 → ±1/R
            if (spiralLength > 1e-9)
            {
                pieces.Add(GeometryPiece.Spiral(cursor, heading, 0, rate, spiralLength));
                cursor = pieces[^1].EndPoint;
                heading = pieces[^1].EndHeading;
            }

            // circular arc holding ±1/R
            double arcLength = radius * Math.Max(0, deflection - 2 * tau);
            if (arcLength > 1e-9)
            {
                pieces.Add(GeometryPiece.Arc(cursor, heading, curvature, arcLength));
                cursor = pieces[^1].EndPoint;
                heading = pieces[^1].EndHeading;
            }

            // spiral out: curvature ±1/R → 0
            if (spiralLength > 1e-9)
            {
                pieces.Add(GeometryPiece.Spiral(cursor, heading, curvature, -rate, spiralLength));
                cursor = pieces[^1].EndPoint;
                heading = pieces[^1].EndHeading;
            }
        }

        RunTo(pieces, ref cursor, ref heading, pts[^1]);
        return new Alignment(pieces);
    }

    /// <summary>
    /// Appends a straight from the cursor to <paramref name="target"/>, measured by projection
    /// onto the current heading so a hair of accumulated drift cannot make the piece longer
    /// than the leg it sits in.
    /// </summary>
    private static void RunTo(List<GeometryPiece> pieces, ref Vec2 cursor, ref double heading, Vec2 target)
    {
        double run = (target - cursor).Dot(Vec2.FromHeading(heading));
        if (run <= 1e-6) return;

        pieces.Add(GeometryPiece.Line(cursor, heading, run));
        cursor = pieces[^1].EndPoint;
        heading = pieces[^1].EndHeading;
    }

    /// <summary>
    /// Largest radius whose spiral-arc-spiral fits inside <paramref name="available"/> metres
    /// of tangent, found by bisection. Returns null when even the minimum radius does not fit.
    /// </summary>
    private static (double Radius, double SpiralLength, double TangentDistance, double Tau)?
        SolveCorner(double deflection, double available, CurveStyle style)
    {
        if (available <= 1e-3) return null;

        // both constraints grow monotonically with the radius, so one bisection satisfies both
        bool Fits(in (double Radius, double SpiralLength, double TangentDistance, double Tau, double Offset) c)
            => c.TangentDistance <= available && c.Offset <= style.MaxOffset;

        double floor = Math.Min(style.MinRadius, HardMinRadius);
        var atFloor = Evaluate(floor, deflection, style.SpiralRatio);
        if (!Fits(atFloor)) return null;   // no room for any curve that stays inside the bound

        var atDesign = Evaluate(style.DesignRadius, deflection, style.SpiralRatio);
        if (Fits(atDesign)) return Drop(atDesign);

        double lo = floor, hi = style.DesignRadius;
        var best = atFloor;
        for (int iter = 0; iter < 40 && hi - lo > 1e-4; iter++)
        {
            double mid = 0.5 * (lo + hi);
            var trial = Evaluate(mid, deflection, style.SpiralRatio);
            if (Fits(trial)) { best = trial; lo = mid; }
            else hi = mid;
        }
        return Drop(best);
    }

    private static (double, double, double, double) Drop(
        (double Radius, double SpiralLength, double TangentDistance, double Tau, double Offset) c)
        => (c.Radius, c.SpiralLength, c.TangentDistance, c.Tau);

    /// <summary>
    /// Tangent distance for a spiral-arc-spiral of the given radius.
    ///
    /// <para>
    /// The spiral shifts the circular arc inwards from the tangent by <c>p</c> and pushes its
    /// start along the tangent by <c>k</c>; the classic result is
    /// <c>T = k + (R + p)·tan(Δ/2)</c>. Both offsets are read straight off an integrated
    /// spiral rather than from the usual series approximations, which only hold for small
    /// spiral angles and quietly lose accuracy exactly where alpine roads live.
    /// </para>
    /// </summary>
    private static (double Radius, double SpiralLength, double TangentDistance, double Tau, double Offset)
        Evaluate(double radius, double deflection, double spiralRatio)
    {
        // the two spirals may not consume more than the whole deflection, or no arc is left
        double spiralLength = Math.Min(spiralRatio * radius, 0.9 * radius * deflection);
        double tau = spiralLength / (2 * radius);

        double p, k;
        if (spiralLength > 1e-9)
        {
            var probe = GeometryPiece.Spiral(Vec2.Zero, 0, 0, 1.0 / (radius * spiralLength), spiralLength);
            var end = probe.EndPoint;
            p = end.Y - radius * (1 - Math.Cos(tau));
            k = end.X - radius * Math.Sin(tau);
        }
        else
        {
            p = 0;
            k = 0;
        }

        double tangent = k + (radius + p) * Math.Tan(deflection / 2);

        // External distance: how far the corner vertex sits from the nearest point of the curve,
        // which is exactly how far the smoothed line departs from the polyline it replaces.
        double offset = (radius + p) * (1.0 / Math.Cos(deflection / 2) - 1.0) + p;

        return (radius, spiralLength, tangent, tau, offset);
    }
}
