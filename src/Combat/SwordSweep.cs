using System.Numerics;

namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>A capsule: the segment <see cref="A"/>-<see cref="B"/> thickened by <see cref="R"/>.</summary>
public readonly record struct Capsule(Vector3 A, Vector3 B, float R);

/// <summary>
/// Where a blade touches what it hits (#1371): the blade is a thin capsule from the hilt to the tip, swept between the samples of its
/// path (the same samples the trail is drawn from), tested against each target's own shape. A blow lands when the blade reaches the body, not
/// when the body is within some reach and cone of the swinger: out of reach is a clean miss. Pure, so the owner's client, the server and the
/// unit tests run the same geometry.
/// </summary>
public static class SwordSweep
{
    /// <summary>The blade's thickness: a 5 cm radius.</summary>
    public const float BladeRadius = 0.05f;
    /// <summary>A player's or a bot's body: a cylinder 0.42 m in radius and 1.85 m tall (as <c>PlayerHits</c>), as a capsule of that radius.</summary>
    public const float BodyRadius = 0.42f, BodyHeight = 1.85f;
    /// <summary>Substeps between two samples of the path: a fast swing cannot tunnel through a post.</summary>
    public const int Substeps = 6;

    /// <summary>The shortest distance between the segments <paramref name="p1"/>-<paramref name="q1"/> and <paramref name="p2"/>-<paramref name="q2"/>.</summary>
    public static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 on1)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-9f && e <= 1e-9f) { s = t = 0f; }
        else if (a <= 1e-9f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-9f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                s = denom > 1e-9f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        on1 = p1 + d1 * s;
        var c2 = p2 + d2 * t;
        return Vector3.Distance(on1, c2);
    }

    /// <summary>The body of a player or a bot standing at <paramref name="feet"/>: one capsule up the body.</summary>
    public static int BodyShape(Vector3 feet, Span<Capsule> into)
    {
        into[0] = new Capsule(feet + new Vector3(0, BodyRadius, 0), feet + new Vector3(0, BodyHeight - BodyRadius, 0), BodyRadius);
        return 1;
    }

    /// <summary>
    /// The training dummy at <paramref name="feet"/> turned <paramref name="yaw"/> about Y: its post, its straw torso, its sack head and its
    /// crossbar with the straw hands (the shapes of <c>TrainingDummy.Body</c>), not a cylinder round them.
    /// </summary>
    public static int DummyShape(Vector3 feet, float yaw, Span<Capsule> into)
    {
        float s = MathF.Sin(yaw), c = MathF.Cos(yaw);
        Vector3 Local(float x, float y, float z) => feet + new Vector3(c * x + s * z, y, -s * x + c * z);
        into[0] = new Capsule(Local(0, 0.18f, 0), Local(0, 1.50f, 0), 0.07f);          // the post
        into[1] = new Capsule(Local(0, 0.95f, 0), Local(0, 1.17f, 0), 0.26f);          // the torso
        into[2] = new Capsule(Local(0, 1.62f, 0), Local(0, 1.74f, 0), 0.16f);          // the head
        into[3] = new Capsule(Local(-0.47f, 1.30f, 0), Local(0.47f, 1.30f, 0), 0.06f); // the arms
        return 4;
    }

    /// <summary>
    /// Sweeps the blade from (<paramref name="a0"/>, <paramref name="b0"/>) (hilt, tip) to (<paramref name="a1"/>, <paramref name="b1"/>) against
    /// <paramref name="shape"/>: the first substep at which the blade's capsule is within <paramref name="slack"/> of touching one of its capsules.
    /// <paramref name="t"/> is how far along the sweep (0..1) and <paramref name="contact"/> the point on the blade there.
    /// </summary>
    public static bool Sweep(Vector3 a0, Vector3 b0, Vector3 a1, Vector3 b1, ReadOnlySpan<Capsule> shape, float slack, out Vector3 contact, out float t)
    {
        for (int i = 0; i <= Substeps; i++)
        {
            float u = i / (float)Substeps;
            var a = Vector3.Lerp(a0, a1, u);
            var b = Vector3.Lerp(b0, b1, u);
            foreach (var cap in shape)
                if (SegmentDistance(a, b, cap.A, cap.B, out var on) <= BladeRadius + cap.R + slack)
                {
                    contact = on;
                    t = u;
                    return true;
                }
        }
        contact = default;
        t = 0f;
        return false;
    }
}
