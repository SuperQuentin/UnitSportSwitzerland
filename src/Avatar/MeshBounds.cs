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
}
