namespace UnitSport.Tools.RoadGen.Synthesis;

using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// A symmetric traceless 2×2 tensor, stored as the two independent components.
///
/// <para>
/// This representation is the reason tensor fields are used for street layout instead of plain
/// direction fields. A direction field cannot be averaged — halfway between "north" and "east"
/// is ambiguous, and blending 0° with 180° cancels to nothing even though they are the same
/// street direction. Encoding the direction as a double angle makes the field a genuine vector
/// space: fields simply add, weighted sums behave, and the 180° ambiguity disappears because
/// θ and θ+π map to the same tensor.
/// </para>
///
/// <para>
/// Each tensor carries two orthogonal directions at once — the major and minor eigenvectors —
/// which is exactly the structure of a street grid: avenues one way, cross streets the other.
/// </para>
/// </summary>
public readonly record struct Tensor(double A, double B)
{
    public static readonly Tensor Zero = new(0, 0);

    public double Magnitude => Math.Sqrt(A * A + B * B);

    /// <summary>Direction of the major eigenvector. The double angle is undone here.</summary>
    public double MajorAngle => 0.5 * Math.Atan2(B, A);

    public Vec2 Major => Vec2.FromHeading(MajorAngle);
    public Vec2 Minor => Vec2.FromHeading(MajorAngle + Math.PI / 2);

    public static Tensor operator +(Tensor a, Tensor b) => new(a.A + b.A, a.B + b.B);
    public static Tensor operator *(Tensor t, double s) => new(t.A * s, t.B * s);

    /// <summary>A field whose major eigenvector points along <paramref name="heading"/>.</summary>
    public static Tensor FromHeading(double heading, double magnitude = 1.0) =>
        new(magnitude * Math.Cos(2 * heading), magnitude * Math.Sin(2 * heading));
}

public abstract class BasisField
{
    public Vec2 Centre { get; init; }
    /// <summary>Falloff per square metre. 0 makes the field global.</summary>
    public double Decay { get; init; }
    public double Weight { get; init; } = 1.0;

    protected double Falloff(Vec2 p) =>
        Decay <= 0 ? 1.0 : Math.Exp(-Decay * p.DistanceSquaredTo(Centre));

    public abstract Tensor At(Vec2 p);
}

/// <summary>A regular grid of streets at a fixed orientation.</summary>
public sealed class GridField : BasisField
{
    public double Heading { get; init; }
    public override Tensor At(Vec2 p) => Tensor.FromHeading(Heading, Weight * Falloff(p));
}

/// <summary>
/// Streets radiating from a point, with ring roads around it. Village centres, roundabouts and
/// anything grown around a church square come out of this one.
/// </summary>
public sealed class RadialField : BasisField
{
    public override Tensor At(Vec2 p)
    {
        var d = p - Centre;
        if (d.LengthSquared < 1e-9) return Tensor.Zero;
        return Tensor.FromHeading(d.Heading, Weight * Falloff(p));
    }
}

/// <summary>
/// Aligns streets to the terrain: the major eigenvector runs along the contour, so roads
/// traverse a slope rather than climbing straight up it, and the minor eigenvector gives the
/// connecting streets that do climb.
///
/// <para>
/// Weighting by slope is what makes this behave: on flat ground the field contributes almost
/// nothing and whatever grid or radial field is present wins, while on a steep face it
/// dominates and the layout turns into switchbacks by itself. That mirrors how alpine
/// settlements are actually laid out, and it is the one basis field a Swiss terrain project
/// really wants.
/// </para>
/// </summary>
public sealed class TerrainField : BasisField
{
    public required Func<Vec2, double> Height { get; init; }
    public double SampleDistance { get; init; } = 8.0;
    /// <summary>Slope at which this field reaches full strength.</summary>
    public double FullStrengthGrade { get; init; } = 0.20;

    public override Tensor At(Vec2 p)
    {
        double h = SampleDistance;
        double dx = (Height(p + new Vec2(h, 0)) - Height(p - new Vec2(h, 0))) / (2 * h);
        double dy = (Height(p + new Vec2(0, h)) - Height(p - new Vec2(0, h))) / (2 * h);

        var gradient = new Vec2(dx, dy);
        double grade = gradient.Length;
        if (grade < 1e-6) return Tensor.Zero;

        double strength = Math.Clamp(grade / FullStrengthGrade, 0, 1);
        return Tensor.FromHeading(gradient.Perp.Heading, Weight * strength * Falloff(p));
    }
}

public sealed class TensorField
{
    public List<BasisField> Fields { get; } = new();

    /// <summary>
    /// Small isotropic noise keeps the layout from looking stamped. Applied as a rotation of
    /// the blended tensor rather than as a position jitter, so streets stay straight but the
    /// grid drifts the way a real one does.
    /// </summary>
    public double NoiseAmplitude { get; init; }
    public double NoiseScale { get; init; } = 300.0;

    public Tensor At(Vec2 p)
    {
        var sum = Tensor.Zero;
        foreach (var field in Fields) sum += field.At(p);

        if (NoiseAmplitude > 1e-9 && sum.Magnitude > 1e-9)
        {
            double n = ValueNoise(p.X / NoiseScale, p.Y / NoiseScale);
            sum = Tensor.FromHeading(sum.MajorAngle + n * NoiseAmplitude, sum.Magnitude);
        }

        return sum;
    }

    /// <summary>Cheap smooth value noise in [-1, 1]; no dependencies, deterministic.</summary>
    private static double ValueNoise(double x, double y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = x - xi, yf = y - yi;
        double u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);

        double Hash(int a, int b)
        {
            unchecked
            {
                int h = a * 374761393 + b * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0x7fffffff) / (double)0x3fffffff - 1.0;
            }
        }

        double top = Hash(xi, yi) * (1 - u) + Hash(xi + 1, yi) * u;
        double bottom = Hash(xi, yi + 1) * (1 - u) + Hash(xi + 1, yi + 1) * u;
        return top * (1 - v) + bottom * v;
    }
}
