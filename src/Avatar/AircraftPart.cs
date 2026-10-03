using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// A moving part: geometry authored in the aircraft's frame, stored in the hinge node's own frame. The
/// node sits at <paramref name="pivotAuth"/> (flipped) turned by <paramref name="basis"/>.
/// </summary>
public sealed class AircraftPart
{
    public readonly MeshScratch S = new();
    private readonly Vector3 _pivot;
    private readonly Basis _basis, _inverse;

    public AircraftPart(Vector3 pivotAuth, Basis basis)
    {
        _pivot = AircraftMeshBuilder.Flip(pivotAuth);
        _basis = basis;
        _inverse = basis.Inverse();
    }

    /// <summary>Authored point of the aircraft to the scratch's coordinates (flipped again by Build).</summary>
    public Vector3 P(Vector3 auth) => AircraftMeshBuilder.Flip(_inverse * (AircraftMeshBuilder.Flip(auth) - _pivot));

    public Vector3[] P(Vector3[] ring)
    {
        var r = new Vector3[ring.Length];
        for (int i = 0; i < r.Length; i++) r[i] = P(ring[i]);
        return r;
    }

    public void Loft(Vector3[][] rings, Color[] edges, Color cap)
    {
        var local = new Vector3[rings.Length][];
        for (int i = 0; i < rings.Length; i++) local[i] = P(rings[i]);
        S.Loft(local, edges, cap);
    }

    public void Tube(Vector3 a, Vector3 b, float ra, float rb, Color c, int sides = 8) => S.Tube(P(a), P(b), ra, rb, c, sides);

    public void Box(Vector3 centre, Vector3 size, Color c) => S.Box(P(centre), size, c);

    /// <summary>A box turned by an authored <paramref name="orientation"/>, carried into the part's frame like its points.</summary>
    public void Box(Vector3 centre, Vector3 size, Color c, Basis orientation) =>
        S.Box(P(centre), size, c, Turn * _inverse * Turn * orientation);

    /// <summary>The half turn about Y between authored and node space (its own inverse).</summary>
    private static readonly Basis Turn = new(new Vector3(-1, 0, 0), Vector3.Up, new Vector3(0, 0, -1));

    public Node3D ToNode(string name, Material body, Material glass)
    {
        var node = new Node3D { Name = name, Transform = new Transform3D(_basis, _pivot) };
        var mi = new MeshInstance3D { Name = name + "Mesh", Mesh = S.Build() };
        node.AddChild(mi);
        MeshScratch.Paint(mi, body, glass);
        return node;
    }
}
