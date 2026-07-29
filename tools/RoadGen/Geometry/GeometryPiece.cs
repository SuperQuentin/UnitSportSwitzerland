namespace UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// One primitive of a road alignment: a run of constant curvature *rate*.
///
/// <para>
/// This single record covers all three shapes a road is built from, which is why it is the
/// only primitive in the tool:
/// </para>
/// <list type="bullet">
///   <item>curvature 0, rate 0 → a straight</item>
///   <item>curvature k, rate 0 → a circular arc of radius 1/k</item>
///   <item>rate ≠ 0 → a clothoid (Euler spiral), curvature linear in arc length</item>
/// </list>
///
/// <para>
/// That is deliberately the same decomposition ASAM OpenDRIVE uses for its reference line
/// (line / arc / spiral), because it is the one real road surveying and road design use.
/// A clothoid is the curve a vehicle traces when the steering wheel is turned at a constant
/// rate, so it is what "fluid" actually means for a road: curvature never jumps.
/// </para>
/// </summary>
/// <param name="Start">Start point.</param>
/// <param name="Heading">Start heading in radians.</param>
/// <param name="Curvature">Curvature at s = 0, signed (positive turns left).</param>
/// <param name="CurvatureRate">d(curvature)/ds. Zero for lines and arcs.</param>
/// <param name="Length">Arc length.</param>
public sealed record GeometryPiece(
    Vec2 Start,
    double Heading,
    double Curvature,
    double CurvatureRate,
    double Length)
{
    public GeometryKind Kind =>
        Math.Abs(CurvatureRate) > 1e-12 ? GeometryKind.Spiral
        : Math.Abs(Curvature) > 1e-12 ? GeometryKind.Arc
        : GeometryKind.Line;

    public double CurvatureAt(double s) => Curvature + CurvatureRate * s;

    /// <summary>
    /// Heading is the exact integral of curvature — no numerical work needed, because
    /// curvature is linear in s by construction.
    /// </summary>
    public double HeadingAt(double s) => Heading + Curvature * s + 0.5 * CurvatureRate * s * s;

    /// <summary>
    /// Position, by integrating (cos θ, sin θ) with Simpson's rule.
    ///
    /// <para>
    /// The alternative is the Fresnel integrals in closed form, which need a rational
    /// approximation with its own error bounds and sign conventions. Integrating the
    /// heading — which is exact — is simpler to read and simpler to trust, and the step
    /// count is chosen from the total turn so a gentle motorway curve costs almost nothing
    /// and a tight hairpin gets the subdivision it needs.
    /// </para>
    /// </summary>
    public Vec2 PointAt(double s)
    {
        s = Math.Clamp(s, 0, Length);
        if (s <= 0) return Start;

        // one Simpson panel per ~3° of turn, always even, always at least 2
        double turn = Math.Abs(HeadingAt(s) - Heading);
        int n = (int)Math.Ceiling(turn / 0.05) * 2;
        n = Math.Clamp(n, 2, 512);
        if (n % 2 != 0) n++;

        double h = s / n;
        double sumX = Math.Cos(Heading) + Math.Cos(HeadingAt(s));
        double sumY = Math.Sin(Heading) + Math.Sin(HeadingAt(s));

        for (int i = 1; i < n; i++)
        {
            double weight = i % 2 == 0 ? 2.0 : 4.0;
            double theta = HeadingAt(i * h);
            sumX += weight * Math.Cos(theta);
            sumY += weight * Math.Sin(theta);
        }

        return Start + new Vec2(sumX, sumY) * (h / 3.0);
    }

    public Vec2 EndPoint => PointAt(Length);
    public double EndHeading => HeadingAt(Length);
    public double EndCurvature => CurvatureAt(Length);

    public static GeometryPiece Line(Vec2 start, double heading, double length) =>
        new(start, heading, 0, 0, length);

    public static GeometryPiece Arc(Vec2 start, double heading, double curvature, double length) =>
        new(start, heading, curvature, 0, length);

    public static GeometryPiece Spiral(Vec2 start, double heading, double curvature,
        double curvatureRate, double length) =>
        new(start, heading, curvature, curvatureRate, length);
}

public enum GeometryKind { Line, Arc, Spiral }
