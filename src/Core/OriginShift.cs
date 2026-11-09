using Godot;

namespace UnitSport.Core;

/// <summary>
/// How world space moved when the origin did (#185): applied to anything that was in the old
/// world space, it gives the same place in the new one.
///
/// <para>
/// A rigid transform, not a plain offset, although today the rotation is always the identity:
/// the round world (#187) turns the frame a little at each shift, and everything that handles a
/// shift through <see cref="Point"/>, <see cref="Direction"/> and <see cref="Apply"/> then keeps
/// working unchanged. Code that only adds <see cref="Transform"/>.Origin would not.
/// </para>
/// </summary>
public readonly struct OriginShift
{
    public readonly Transform3D Transform;

    public OriginShift(Transform3D transform) => Transform = transform;

    public static OriginShift Identity => new(Transform3D.Identity);

    public bool IsIdentity => Transform.IsEqualApprox(Transform3D.Identity);

    /// <summary>A position in the old world space, in the new one.</summary>
    public Vector3 Point(Vector3 p) => Transform * p;

    /// <summary>A direction, velocity or normal: turned, never moved.</summary>
    public Vector3 Direction(Vector3 v) => Transform.Basis * v;

    /// <summary>A placement (a node's global transform, a door's frame) in the new world space.</summary>
    public Transform3D Apply(Transform3D t) => Transform * t;

    /// <summary>
    /// An axis-aligned box in the new world space: the smallest one holding the old box's eight corners moved
    /// (#1512). Exact while the shift does not turn; under a turn it grows by the turn's sine times the box's size
    /// (0.05 mm on a 1 m box per 3 km re-anchor), so a box kept across many turning shifts is better built again
    /// from what it bounds.
    /// </summary>
    public Aabb Apply(Aabb box)
    {
        var a = box.Position;
        var b = box.End;
        var first = Transform * a;
        var min = first;
        var max = first;
        for (int k = 1; k < 8; k++)
        {
            var corner = Transform * new Vector3((k & 1) != 0 ? b.X : a.X, (k & 2) != 0 ? b.Y : a.Y, (k & 4) != 0 ? b.Z : a.Z);
            min = min.Min(corner);
            max = max.Max(corner);
        }
        return new Aabb(min, max - min);
    }

    /// <summary>
    /// How far the shift turns headings about the world's up, radians (#1430 H3): what a heading kept as
    /// an angle (a view yaw, a flight heading) adds. 0 for every flat ground shift.
    /// </summary>
    public float Yaw
    {
        get
        {
            var f = Transform.Basis * Vector3.Forward;
            return Mathf.Atan2(-f.X, -f.Z);
        }
    }

    /// <summary>
    /// Whether the shift turns the world at all (an anchor frame's, #1430 H3). Exactly: a ground shift's basis is
    /// the identity bit for bit, and the earth world's turn under a 20 m stress shift (3·10⁻⁶ rad, #1512) is below
    /// <c>IsEqualApprox</c>'s tolerance but still a turn the velocities must follow.
    /// </summary>
    public bool Turns => Transform.Basis != Basis.Identity;

    /// <summary>The way back: from the new world space to the old.</summary>
    public OriginShift Inverse => new(Transform.AffineInverse());

    /// <summary>This shift, then <paramref name="next"/>.</summary>
    public OriginShift Then(OriginShift next) => new(next.Transform * Transform);

    public override string ToString() => Transform.Origin.ToString();
}

/// <summary>
/// A Node3D that stays at the identity while the origin moves, and whose children hold world
/// positions: the children are moved instead of it (<see cref="OriginShifter"/>). A manager that
/// parents world objects under itself must be one, or the shift moves the manager and leaves
/// its children's local coordinates as large as before. A plain Node3D joins the
/// <see cref="OriginShifter.ContainerGroup"/> group instead.
/// </summary>
public interface IOriginContainer { }

/// <summary>
/// A node that keeps world positions somewhere other than its own transform (a cache, a ring of
/// samples, a shader uniform) and must move them when the origin does. Called on the main thread,
/// after every node has been moved, for every node in the tree that implements it.
/// </summary>
public interface IOriginShiftAware
{
    void OnOriginShifted(OriginShift shift);
}
