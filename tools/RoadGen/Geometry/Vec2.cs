namespace UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// A point in the plan view, in metres (LV95 easting/northing, or tile-local — the code
/// never cares which as long as one run is consistent).
///
/// <para>
/// Road geometry is solved in 2D on purpose. Height comes from draping onto the terrain
/// afterwards, so curvature, offsets and junction polygons are all plan-view problems.
/// Trying to solve them in 3D would make every intersection test depend on a height model
/// that is not even the same one the road was surveyed against.
/// </para>
/// </summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    public double LengthSquared => X * X + Y * Y;
    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>Rotated 90° counter-clockwise. The left-hand side of a line running along this.</summary>
    public Vec2 Perp => new(-Y, X);

    public double Heading => Math.Atan2(Y, X);

    public Vec2 Normalized()
    {
        double len = Length;
        return len < 1e-12 ? new Vec2(1, 0) : new Vec2(X / len, Y / len);
    }

    public double Dot(Vec2 b) => X * b.X + Y * b.Y;

    /// <summary>2D cross product (z of the 3D cross). Sign tells you which way a corner turns.</summary>
    public double Cross(Vec2 b) => X * b.Y - Y * b.X;

    public double DistanceTo(Vec2 b) => (this - b).Length;
    public double DistanceSquaredTo(Vec2 b) => (this - b).LengthSquared;

    public static Vec2 FromHeading(double radians) => new(Math.Cos(radians), Math.Sin(radians));

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => a + (b - a) * t;

    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({X:F2}, {Y:F2})");
}

public static class Angles
{
    /// <summary>Wraps to (-π, π]. Every heading difference in this tool goes through here.</summary>
    public static double Normalize(double radians)
    {
        double a = Math.IEEERemainder(radians, 2 * Math.PI);
        if (a <= -Math.PI) a += 2 * Math.PI;
        if (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }

    /// <summary>Signed turn from <paramref name="from"/> to <paramref name="to"/>, in (-π, π].</summary>
    public static double Delta(double from, double to) => Normalize(to - from);
}
