using Godot;

namespace UnitSport.Farming;

/// <summary>
/// One convex piece of a cell's footprint inside a field outline (#494): a trapezoid between two
/// x positions, counter-clockwise (A, B along the lower bound, C, D along the upper one). The flags
/// say which sides lie on the cell's own sides (the neighbour decides a wall there).
/// </summary>
public struct FieldPiece
{
    public Vector2 A, B, C, D;
    public byte Sides;
    public const byte South = 1, East = 2, North = 4, West = 8;
}

/// <summary>A straight piece of a field outline, chunk-local metres (u east, v north).</summary>
public readonly record struct OutlineSeg(Vector2 P, Vector2 Q)
{
    public float MinU => Math.Min(P.X, Q.X);
    public float MaxU => Math.Max(P.X, Q.X);
    public float MinV => Math.Min(P.Y, Q.Y);
    public float MaxV => Math.Max(P.Y, Q.Y);
    /// <summary>v on the segment's line at <paramref name="u"/> (not vertical).</summary>
    public float VAt(float u) => P.Y + (u - P.X) / (Q.X - P.X) * (Q.Y - P.Y);
}

/// <summary>
/// The geometry the field drawing needs (#494, <c>docs/notes/farming/fields-runtime.md</c>): a cell
/// cut to a field's outline as trapezoids (even-odd over every ring, holes cut out, concave outlines
/// fine), segments cut to a square, lines cut to a convex piece. Plain math, worker thread.
/// </summary>
public static class FieldClip
{
    private const float Eps = 1e-4f;

    /// <summary>
    /// The part of the square [u0, u1] × [v0, v1] inside the outline, as trapezoids appended to
    /// <paramref name="pieces"/>. <paramref name="stripe"/> must hold every segment whose u range
    /// overlaps the square's (any v: the parity is counted from below). <paramref name="xs"/> and
    /// <paramref name="hits"/> are scratch lists.
    /// </summary>
    public static void Cut(float u0, float v0, float u1, float v1, List<OutlineSeg> stripe, List<FieldPiece> pieces,
        List<float> xs, List<(float V, int Seg)> hits)
    {
        xs.Clear();
        xs.Add(u0);
        xs.Add(u1);
        foreach (var s in stripe)
        {
            if (s.MaxU <= u0 || s.MinU >= u1 || s.MaxU - s.MinU < Eps) continue;
            if (s.P.X > u0 && s.P.X < u1) xs.Add(s.P.X);
            if (s.Q.X > u0 && s.Q.X < u1) xs.Add(s.Q.X);
            // where it crosses the square's lower and upper sides
            AddCross(s, v0, u0, u1, xs);
            AddCross(s, v1, u0, u1, xs);
        }
        xs.Sort();
        for (int i = 0; i + 1 < xs.Count; i++)
        {
            float a = xs[i], b = xs[i + 1];
            if (b - a < Eps) continue;
            float m = (a + b) * 0.5f;
            hits.Clear();
            bool inside = false;
            for (int k = 0; k < stripe.Count; k++)
            {
                var s = stripe[k];
                if (!(s.MinU < m && s.MaxU > m)) continue;
                float v = s.VAt(m);
                if (v <= v0) inside = !inside;
                else if (v < v1) hits.Add((v, k));
            }
            hits.Sort((x, y) => x.V.CompareTo(y.V));
            int lower = -1;   // -1: the square's lower side
            for (int h = 0; h <= hits.Count; h++)
            {
                int bound = h < hits.Count ? hits[h].Seg : -2;   // -2: the upper side
                if (inside) Emit(a, b, u0, u1, v0, v1, lower, bound, stripe, pieces);
                inside = !inside;
                lower = bound;
            }
        }
    }

    private static void AddCross(in OutlineSeg s, float v, float u0, float u1, List<float> xs)
    {
        if ((s.P.Y > v) == (s.Q.Y > v)) return;
        float u = s.P.X + (v - s.P.Y) / (s.Q.Y - s.P.Y) * (s.Q.X - s.P.X);
        if (u > u0 && u < u1) xs.Add(u);
    }

    private static void Emit(float a, float b, float u0, float u1, float v0, float v1, int lower, int upper,
        List<OutlineSeg> stripe, List<FieldPiece> pieces)
    {
        float la = lower < 0 ? v0 : Math.Clamp(stripe[lower].VAt(a), v0, v1);
        float lb = lower < 0 ? v0 : Math.Clamp(stripe[lower].VAt(b), v0, v1);
        float ua = upper < 0 ? v1 : Math.Clamp(stripe[upper].VAt(a), v0, v1);
        float ub = upper < 0 ? v1 : Math.Clamp(stripe[upper].VAt(b), v0, v1);
        if (ua - la < Eps && ub - lb < Eps) return;
        byte sides = 0;
        if (lower < 0) sides |= FieldPiece.South;
        if (upper < 0) sides |= FieldPiece.North;
        if (a <= u0 + Eps) sides |= FieldPiece.West;
        if (b >= u1 - Eps) sides |= FieldPiece.East;
        pieces.Add(new FieldPiece { A = new(a, la), B = new(b, lb), C = new(b, ub), D = new(a, ua), Sides = sides });
    }

    /// <summary>A whole square as one piece, every side a cell side.</summary>
    public static FieldPiece Square(float u0, float v0, float u1, float v1) => new()
    {
        A = new(u0, v0), B = new(u1, v0), C = new(u1, v1), D = new(u0, v1),
        Sides = FieldPiece.South | FieldPiece.East | FieldPiece.North | FieldPiece.West,
    };

    /// <summary>Liang-Barsky: the segment cut to the square, false when it misses it.</summary>
    public static bool ClipToSquare(Vector2 p, Vector2 q, float u0, float v0, float u1, float v1, out Vector2 a, out Vector2 b)
    {
        float t0 = 0, t1 = 1;
        var d = q - p;
        a = b = default;
        if (!Edge(-d.X, p.X - u0, ref t0, ref t1) || !Edge(d.X, u1 - p.X, ref t0, ref t1)
            || !Edge(-d.Y, p.Y - v0, ref t0, ref t1) || !Edge(d.Y, v1 - p.Y, ref t0, ref t1)) return false;
        a = p + d * t0;
        b = p + d * t1;
        return true;
    }

    private static bool Edge(float pp, float qq, ref float t0, ref float t1)
    {
        if (MathF.Abs(pp) < 1e-9f) return qq >= 0;
        float t = qq / pp;
        if (pp < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
        else { if (t < t0) return false; if (t < t1) t1 = t; }
        return true;
    }

    /// <summary>Cyrus-Beck: the line <paramref name="o"/> + t <paramref name="d"/> inside the piece, as [t0, t1].</summary>
    public static bool LineInPiece(in FieldPiece pc, Vector2 o, Vector2 d, out float t0, out float t1)
    {
        t0 = -1e9f;
        t1 = 1e9f;
        return Side(pc.A, pc.B, o, d, ref t0, ref t1) && Side(pc.B, pc.C, o, d, ref t0, ref t1)
            && Side(pc.C, pc.D, o, d, ref t0, ref t1) && Side(pc.D, pc.A, o, d, ref t0, ref t1) && t1 > t0;
    }

    private static bool Side(Vector2 e0, Vector2 e1, Vector2 o, Vector2 d, ref float t0, ref float t1)
    {
        var e = e1 - e0;
        if (e.LengthSquared() < 1e-10f) return true;
        var n = new Vector2(-e.Y, e.X);   // inward for a counter-clockwise piece
        float num = (o - e0).Dot(n), den = d.Dot(n);
        if (MathF.Abs(den) < 1e-9f) return num >= 0;
        float t = -num / den;
        if (den > 0) t0 = Math.Max(t0, t);
        else t1 = Math.Min(t1, t);
        return t1 > t0;
    }

    /// <summary>A point inside the (counter-clockwise, convex) piece.</summary>
    public static bool Inside(in FieldPiece pc, Vector2 p) =>
        Left(pc.A, pc.B, p) && Left(pc.B, pc.C, p) && Left(pc.C, pc.D, p) && Left(pc.D, pc.A, p);

    private static bool Left(Vector2 e0, Vector2 e1, Vector2 p) => (e1 - e0).Cross(p - e0) >= -1e-5f;
}
