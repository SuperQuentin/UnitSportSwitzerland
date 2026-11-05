using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A producer's own coordinates on one leaf (#1509, G2 B5): the generated world's LV95, a fixture course's metres
/// from its start. A lattice vertex's source position (<see cref="SourceAt"/>) is bilinear between control points
/// every <see cref="ControlStep"/> vertices (~19 m), each taken through the work plane and the source's projection
/// rigorously (the mapping bends ~10⁻⁷ a metre: under a micrometre between them); a record's point goes the other
/// way rigorously (<see cref="Local"/>), so lattice and records agree. Heights are above the source's sea level,
/// which must be the frames' (<see cref="LeafFrames.SeaLevel"/>).
///
/// <para>
/// A source's coordinates are metres (LV95, a course): directions are carried by a one-metre step. Its notation
/// is two-dimensional: a point is placed by its latitude and longitude at sea level, whatever its height (LV95's
/// normals lean 2.6-2.8 cm a km of height against WGS84's), the same for the lattice and the records.
/// </para>
/// </summary>
public sealed class SourceOnLeaf
{
    public const int ControlStep = 16;

    private readonly double[] _a, _b;
    private readonly int _side;

    public CubeKey Leaf { get; }
    public LeafTransform Transform { get; }
    public WorkFrame Frame => Transform.Frame;
    public ISourceProjection Source { get; }

    /// <summary>Vertices a side of the leaf's lattice.</summary>
    public int Side => Transform.Cells + 1;

    public SourceOnLeaf(LeafFrames frames, CubeKey leaf, ISourceProjection source)
    {
        Leaf = leaf;
        Source = source;
        Transform = frames.TransformOf(leaf);
        _side = Transform.Cells / ControlStep + 1;
        _a = new double[_side * _side];
        _b = new double[_side * _side];
        for (int cj = 0; cj < _side; cj++)
            for (int ci = 0; ci < _side; ci++)
            {
                var (x, y) = Transform.LatticeToPlane(ci * ControlStep, cj * ControlStep);
                var (lat, lon) = Frame.Plane.FromPlane(x, y);
                (_a[ci + cj * _side], _b[ci + cj * _side]) = source.FromLatLon(lat, lon);
            }
    }

    /// <summary>The source position of lattice point (i, j), 0..cells.</summary>
    public (double A, double B) SourceAt(double i, double j)
    {
        double fi = i / ControlStep, fj = j / ControlStep;
        int ci = Math.Clamp((int)fi, 0, _side - 2), cj = Math.Clamp((int)fj, 0, _side - 2);
        double u = fi - ci, v = fj - cj;
        int k = ci + cj * _side;
        return (Bilinear(_a, k, u, v), Bilinear(_b, k, u, v));
    }

    /// <summary>
    /// Bilinear in a control cell, exact on its rows and columns: a vertex on a control line (a leaf's edge among them)
    /// reads the same values from either side, so neighbouring leaves give a shared edge vertex the same position.
    /// </summary>
    private double Bilinear(double[] g, int k, double u, double v)
    {
        if (v == 0) return Lerp(g[k], g[k + 1], u);
        if (v == 1) return Lerp(g[k + _side], g[k + _side + 1], u);
        return Lerp(Lerp(g[k], g[k + 1], u), Lerp(g[k + _side], g[k + _side + 1], u), v);
    }

    private static double Lerp(double a, double b, double t) => t == 0 ? a : t == 1 ? b : a + (b - a) * t;

    /// <summary>The corners' source positions: the leaf's box in the source (its edges bow a few mm).</summary>
    public (double MinA, double MinB, double MaxA, double MaxB) Box()
    {
        double minA = double.MaxValue, minB = double.MaxValue, maxA = double.MinValue, maxB = double.MinValue;
        for (int k = 0; k < _a.Length; k++)
        {
            int ci = k % _side, cj = k / _side;
            if (ci != 0 && ci != _side - 1 && cj != 0 && cj != _side - 1) continue;
            minA = Math.Min(minA, _a[k]); maxA = Math.Max(maxA, _a[k]);
            minB = Math.Min(minB, _b[k]); maxB = Math.Max(maxB, _b[k]);
        }
        return (minA, minB, maxA, maxB);
    }

    /// <summary>A source point on the work plane.</summary>
    public (double X, double Y) PlaneOf(double a, double b)
    {
        var (lat, lon) = Source.ToLatLon(a, b);
        return Frame.Plane.ToPlane(lat, lon);
    }

    /// <summary>A source point and its height above sea level in the leaf's anchor frame.</summary>
    public (double X, double Y, double Z) Local(double a, double b, double hSea)
    {
        var (x, y) = PlaneOf(a, b);
        return Transform.PlaneToLocal(x, y, hSea);
    }

    /// <summary>A direction in the source (a east, b north) at a point as one in the anchor frame (x east, z south).</summary>
    public (double Dx, double Dz) DirectionToLocal(double a, double b, double da, double db, double hSea)
    {
        double length = Math.Sqrt(da * da + db * db);
        if (length == 0) return (0, 0);
        var (x0, y0) = PlaneOf(a, b);
        var (x1, y1) = PlaneOf(a + da / length, b + db / length);
        return Transform.DirectionToLocal(x0, y0, x1 - x0, y1 - y0, hSea);
    }

    /// <summary>Whether a source point lies in this leaf.</summary>
    public bool Holds(double a, double b) => LeafOf(Source, a, b) == Leaf;

    /// <summary>The leaf a source point lies in.</summary>
    public static CubeKey LeafOf(ISourceProjection source, double a, double b)
    {
        var (lat, lon) = source.ToLatLon(a, b);
        return LeafFrames.LeafAt(lat, lon);
    }
}
