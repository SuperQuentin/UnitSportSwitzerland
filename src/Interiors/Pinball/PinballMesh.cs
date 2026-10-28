using Godot;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// A triangle soup with vertex colours for the pinball cabinet's parts (#1325): boxes, slabs along a
/// line, cylinders, flat polygons and strips. Built once per table and shared (the interior material
/// draws it in every style: vertex alpha 0.2-0.5 lights itself, 1 is lit by the room).
/// </summary>
internal sealed class PinballMesh
{
    public readonly List<Vector3> V = new();
    public readonly List<Color> C = new();
    /// <summary>Every triangle both ways round: for small parts seen from any side (the flipper, the ball).</summary>
    public bool TwoSided;

    public static Color Col(float r, float g, float b) => new Color(r, g, b).SrgbToLinear();
    public static Color Lit(Color c) => c with { A = 0.35f };

    public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col)
    {
        V.Add(a); V.Add(b); V.Add(c);
        C.Add(col); C.Add(col); C.Add(col);
        if (TwoSided)
        {
            V.Add(a); V.Add(c); V.Add(b);
            C.Add(col); C.Add(col); C.Add(col);
        }
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
    {
        Tri(a, b, c, col);
        Tri(a, c, d, col);
    }

    /// <summary>An oriented box: centre, half sizes, basis.</summary>
    public void Box(Vector3 centre, Vector3 half, Basis basis, Color col, bool bottom = false)
    {
        Vector3 P(float x, float y, float z) => centre + basis * new Vector3(x * half.X, y * half.Y, z * half.Z);
        var side = (col * 0.86f) with { A = col.A };
        Quad(P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), col);
        Quad(P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), side);
        Quad(P(-1, -1, 1), P(-1, 1, 1), P(1, 1, 1), P(1, -1, 1), side);
        Quad(P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1), (side * 0.95f) with { A = col.A });
        Quad(P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(1, 1, -1), (side * 0.95f) with { A = col.A });
        if (bottom) Quad(P(-1, -1, -1), P(-1, -1, 1), P(1, -1, 1), P(1, -1, -1), (side * 0.7f) with { A = col.A });
    }

    public void Box(Vector3 min, Vector3 max, Color col, bool bottom = false) =>
        Box((min + max) / 2, (max - min) / 2, Basis.Identity, col, bottom);

    /// <summary>A vertical cylinder from <paramref name="y0"/> to <paramref name="y1"/>.</summary>
    public void Cylinder(Vector3 at, float r, float y0, float y1, Color col, int sides = 12, bool top = true)
    {
        var side = (col * 0.85f) with { A = col.A };
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            var p0 = at + new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r);
            var p1 = at + new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
            Quad(p0 + Vector3.Up * y0, p1 + Vector3.Up * y0, p1 + Vector3.Up * y1, p0 + Vector3.Up * y1, side);
            if (top) Tri(at + Vector3.Up * y1, p0 + Vector3.Up * y1, p1 + Vector3.Up * y1, col);
        }
    }

    /// <summary>A flat disc facing up at height <paramref name="y"/>.</summary>
    public void Disc(Vector3 at, float r, Color col, int sides = 12)
    {
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            Tri(at, at + new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r), at + new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r), col);
        }
    }

    /// <summary>A flat ring facing up.</summary>
    public void Ring(Vector3 at, float r0, float r1, Color col, int sides = 16)
    {
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            Vector3 D(float a, float r) => at + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
            Quad(D(a0, r0), D(a0, r1), D(a1, r1), D(a1, r0), col);
        }
    }

    /// <summary>A slab standing on the line a-b (in the XZ plane at <paramref name="y0"/>): thick, up to <paramref name="y1"/>.</summary>
    public void Slab(Vector3 a, Vector3 b, float thick, float y0, float y1, Color col)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-5f) return;
        var x = d / len;
        var basis = new Basis(x, Vector3.Up, x.Cross(Vector3.Up));
        Box((a + b) / 2 + Vector3.Up * ((y0 + y1) / 2), new Vector3(len / 2 + thick / 2, (y1 - y0) / 2, thick / 2), basis, col);
    }

    /// <summary>A rod between two points, a square section <paramref name="r"/> across.</summary>
    public void Rod(Vector3 a, Vector3 b, float r, Color col)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-5f) return;
        var y = d / len;
        var x = Mathf.Abs(y.Dot(Vector3.Up)) > 0.9f ? Vector3.Right : y.Cross(Vector3.Up).Normalized();
        var z = x.Cross(y);
        Box((a + b) / 2, new Vector3(r, len / 2, r), new Basis(x, y, z), col);
    }

    public ArrayMesh Build()
    {
        Core.ShowcaseTrace.Mark();
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = V.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = C.ToArray();
        var mesh = new ArrayMesh();
        if (V.Count > 0) mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
