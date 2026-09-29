using Godot;

namespace UnitSport.Player;

/// <summary>
/// What a shot or a strike can hit, as opposed to what the body moves with.
///
/// <para>
/// Movement keeps its capsule — a plane taxiing on an 11 m box would snag every slope of a 1 m
/// terrain lattice, and a parked one starts inside the hillside (see <see cref="Plane.ParkedBox"/>).
/// But a capsule 2.2 m across is not a plane with an 11 m wingspan, a rotor 10 m across or a bike
/// 1.75 m long, and rounds through the wing went through nothing. So every drawn machine also
/// carries one of these: a box fitted to its mesh (<see cref="Avatar.MeshBounds"/>), parented to
/// the VISUAL so it banks, pitches, flips and lands with it for free.
/// </para>
///
/// <para>
/// An <see cref="Area3D"/> on its own layer (<see cref="Layer"/>), monitorable and not monitoring:
/// it collides with nothing and nothing collides with it, so no movement anywhere changes. A ray
/// that wants it sets <c>CollideWithAreas = true</c> and includes <see cref="Layer"/> in its mask,
/// then resolves the hit to its body with <see cref="BodyOf"/>.
/// </para>
/// </summary>
public partial class Hurtbox : Area3D
{
    /// <summary>Physics layer 8, used by nothing else.</summary>
    public const uint Layer = 1u << 7;

    /// <summary>
    /// Fits a hurtbox to everything drawn under <paramref name="visual"/> and adds it there.
    /// Null when nothing is drawn yet (a headless peer builds no meshes).
    /// </summary>
    public static Hurtbox? Fit(Node3D visual)
    {
        var box = Avatar.MeshBounds.Of(visual);
        if (box.Size.LengthSquared() < 1e-4f) return null;
        var h = new Hurtbox
        {
            Name = "Hurtbox",
            CollisionLayer = Layer,
            CollisionMask = 0,
            Monitoring = false,
            Monitorable = true,
        };
        h.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size }, Position = box.GetCenter() });
        visual.AddChild(h);
        return h;
    }

    /// <summary>The body a ray hit belongs to: itself, or a hurtbox's owner.</summary>
    public static Node? BodyOf(GodotObject? hit)
    {
        if (hit is not Hurtbox h) return hit as Node;
        for (Node? n = h.GetParent(); n != null; n = n.GetParent())
            if (n is PhysicsBody3D) return n;
        return null;
    }
}
