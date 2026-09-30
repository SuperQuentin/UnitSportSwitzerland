using System;
using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The bounds of what is actually drawn, so a collision or hit box is measured from the mesh
/// instead of typed in beside it — a hand-typed box drifts the first time the model changes.
/// </summary>
public static class MeshBounds
{
    /// <summary>
    /// Every mesh at or under <paramref name="root"/>, merged into <paramref name="root"/>'s own
    /// space. A child branch whose node name is in <paramref name="skip"/> is left out (a rotor disc
    /// is not something a fuselage rests on). Empty when nothing is drawn.
    /// </summary>
    public static Aabb Of(Node3D root, params string[] skip)
    {
        Aabb? box = root is MeshInstance3D { Mesh: { } own } ? own.GetAabb() : null;
        Walk(root, Transform3D.Identity, skip, ref box);
        return box ?? new Aabb();
    }

    private static void Walk(Node node, Transform3D toRoot, string[] skip, ref Aabb? box)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is not Node3D n || Array.IndexOf(skip, n.Name.ToString()) >= 0) continue;
            var t = toRoot * n.Transform;
            if (n is MeshInstance3D { Mesh: { } mesh })
            {
                var b = t * mesh.GetAabb();
                box = box is { } acc ? acc.Merge(b) : b;
            }
            Walk(n, t, skip, ref box);
        }
    }

    /// <summary>
    /// The drawn shape as two boxes in <paramref name="root"/>'s space: everything below
    /// <paramref name="cutFraction"/> of the height (a car's body, a bike's frame) and everything
    /// above it (the cabin, the rider). One box around a car is as wide as its body up to the roof,
    /// so a door mirror sticks out of the cabin box and the roof pillars of two cars side by side
    /// stay a hand apart; two boxes follow the silhouette. Built from the vertices, so it is exact
    /// for whatever the mesh builder drew. Empty boxes when nothing is drawn.
    /// </summary>
    public static (Aabb Lower, Aabb Upper) Split(Node3D root, float cutFraction, params string[] skip)
    {
        var all = Of(root, skip);
        if (all.Size.LengthSquared() < 1e-6f) return (new Aabb(), new Aabb());
        float cut = all.Position.Y + all.Size.Y * cutFraction;
        Aabb? lower = null, upper = null;
        void Add(Vector3 p)
        {
            var point = new Aabb(p, Vector3.Zero);
            if (p.Y <= cut) lower = lower is { } l ? l.Expand(p) : point;
            if (p.Y >= cut) upper = upper is { } u ? u.Expand(p) : point;
        }
        Visit(root, Transform3D.Identity, skip, Add);
        // each half reaches the cut, so the two boxes meet with no gap between them
        var lo = lower ?? all;
        var hi = upper ?? all;
        lo = lo.Expand(new Vector3(lo.Position.X, cut, lo.Position.Z));
        hi = hi.Expand(new Vector3(hi.Position.X, cut, hi.Position.Z));
        return (lo, hi);
    }

    private static void Visit(Node3D root, Transform3D toRoot, string[] skip, Action<Vector3> add)
    {
        if (root is MeshInstance3D { Mesh: { } own }) AddVertices(own, toRoot, add);
        VisitChildren(root, toRoot, skip, add);
    }

    private static void VisitChildren(Node node, Transform3D toRoot, string[] skip, Action<Vector3> add)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is not Node3D n || Array.IndexOf(skip, n.Name.ToString()) >= 0) continue;
            var t = toRoot * n.Transform;
            if (n is MeshInstance3D { Mesh: { } mesh }) AddVertices(mesh, t, add);
            VisitChildren(n, t, skip, add);
        }
    }

    private static void AddVertices(Mesh mesh, Transform3D t, Action<Vector3> add)
    {
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
            foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array()) add(t * v);
        }
    }
}
