using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Two-bone inverse kinematics: given where a shoulder and a hand are, find the elbow.
///
/// <para>
/// Used for every limb on every figure, because posing joints by hand does not survive contact
/// with motion. A knee keyframed to look right at one crank angle is wrong at the next, and a
/// foot placed by eye slides along the ground. Solving instead means the constraint that matters
/// — the bones keep their length while the hand or foot goes where the world says — holds at
/// every instant for free.
/// </para>
/// </summary>
public static class Limb
{
    /// <summary>
    /// The middle joint, placed so both bones keep their length and the limb bends toward
    /// <paramref name="bendHint"/>.
    ///
    /// <para>
    /// The joint lies on the circle where the two bone spheres intersect, so there are always
    /// infinitely many solutions and the hint is what picks one. Without it a knee is as likely
    /// to fold backwards as forwards — geometrically fine, anatomically a horror.
    /// </para>
    /// </summary>
    public static Vector3 Solve(Vector3 root, Vector3 target, float upper, float lower,
        Vector3 bendHint)
    {
        var to = target - root;
        float distance = to.Length();

        // Fully extended: there is no bend to compute, and the sphere intersection degenerates.
        // Shy of the limit rather than at it, so the tubes never come out zero-length.
        float reach = upper + lower - 0.001f;
        if (distance >= reach) return root + to.Normalized() * upper;
        if (distance < 1e-4f) return root + bendHint.Normalized() * upper;

        var direction = to / distance;

        // distance along root→target to the plane where the two spheres meet
        float along = (distance * distance + upper * upper - lower * lower) / (2f * distance);
        float radius = Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));

        // only the part of the hint across the limb axis can choose a solution
        var bend = bendHint - direction * bendHint.Dot(direction);
        if (bend.LengthSquared() < 1e-6f)
        {
            var fallback = Mathf.Abs(direction.Dot(Vector3.Up)) > 0.95f
                ? new Vector3(0, 0, 1) : Vector3.Up;
            bend = fallback - direction * fallback.Dot(direction);
        }

        return root + direction * along + bend.Normalized() * radius;
    }

    /// <summary>
    /// <see cref="Solve"/> for a limb that dances (#2055): when the hint lies nearly along the limb it hardly
    /// chooses a side, and the plain solve then flips the joint to the far side of the axis in one frame (an elbow
    /// popping through the arm). Here the hint's share across the axis fades the bend into <paramref name="fallback"/>
    /// instead, so the joint swings round over a few frames and never jumps.
    /// </summary>
    public static Vector3 SolveSmooth(Vector3 root, Vector3 target, float upper, float lower, Vector3 bendHint, Vector3 fallback)
    {
        var to = target - root;
        float distance = to.Length();
        float reach = upper + lower - 0.001f;
        if (distance >= reach) return root + to.Normalized() * upper;
        if (distance < 1e-4f) return root + bendHint.Normalized() * upper;

        var direction = to / distance;
        float along = (distance * distance + upper * upper - lower * lower) / (2f * distance);
        float radius = Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));

        var across = bendHint - direction * bendHint.Dot(direction);
        float share = bendHint.Length() < 1e-6f ? 0f : across.Length() / bendHint.Length();
        float k = Mathf.Clamp(share / 0.8f, 0f, 1f);
        k = k * k * (3f - 2f * k);
        k *= k;
        // the fallback: a few directions an elbow likes (the caller's, down, back, to the caller's side), each across the axis
        // and each counting by how far across it is, so none that lies along the limb can flip it
        var fb = Across(fallback.Normalized(), direction) + Across(Vector3.Down, direction)
            + Across(new Vector3(0f, 0f, -1f), direction) + Across(new Vector3(fallback.X < 0f ? -1f : 1f, 0f, 0f), direction);
        if (fb.LengthSquared() < 1e-8f || across.LengthSquared() < 1e-10f)
            return fb.LengthSquared() < 1e-8f ? Solve(root, target, upper, lower, bendHint) : root + direction * along + fb.Normalized() * radius;
        // turn from the hint's side to the fallback's round the limb (a blend of the two would pass through nothing and flip)
        var u1 = across.Normalized();
        var u2 = fb.Normalized();
        float turn = Mathf.Atan2(direction.Dot(u1.Cross(u2)), u1.Dot(u2));
        var bend = u1.Rotated(direction, turn * (1f - k));
        return root + direction * along + bend.Normalized() * radius;
    }

    private static Vector3 Across(Vector3 v, Vector3 axis)
    {
        var a = v - axis * v.Dot(axis);
        return a * a.Length();
    }
}
