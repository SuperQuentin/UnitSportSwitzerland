using System.Numerics;

namespace UnitSport.Avatar;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// The grip of a closed fist on a sword's handle (#1371): ONE description of where the handle is in the hand and where each finger wraps round
/// it, used both to build the fist (<c>HumanMeshBuilder.Hand</c>) and to place the sword (<c>MountsFor</c>), so the two cannot drift apart.
/// Coordinates are the hand's own, measured from the wrist: x along its thumb side, y along the hand (the forearm's direction), z towards the palm's
/// inside (the way the fingers curl). The handle lies diagonally across the palm, from the index finger's base towards the heel of the hand; the four
/// fingers sit stacked along it, each curling round it in a ring perpendicular to its axis, the thumb closing over the index and middle fingers.
/// </summary>
public static class HandGrip
{
    /// <summary>The handle's radius, metres (the item mesh's grip tube).</summary>
    public const float HandleRadius = 0.013f;
    /// <summary>A finger's width along the handle and its thickness round it.</summary>
    public const float FingerWide = 0.0145f, FingerThick = 0.019f;
    /// <summary>The thumb's radius.</summary>
    public const float ThumbRadius = 0.0085f;
    /// <summary>Angle of the handle's axis from the hand's thumb side towards its direction, degrees: the handle runs diagonally across the palm.</summary>
    public const float DiagonalDeg = 50f;
    /// <summary>Radius of a finger's ring: the handle plus half a finger.</summary>
    public const float RingRadius = HandleRadius + FingerThick * 0.5f;
    /// <summary>Segments a finger's ring is built in (a polygon whose flat sides are tangent to the finger's circle).</summary>
    public const int Segments = 6;
    /// <summary>How far round the handle a finger wraps, radians (about 240 degrees).</summary>
    public const float Wrap = 4.2f;
    /// <summary>Along the handle (authored mesh z) where the middle of the four fingers sits: the guard stands a little above the index finger.</summary>
    public const float HandleAtFingers = 0.005f;

    private static readonly float Phi = DiagonalDeg * MathF.PI / 180f;

    /// <summary>The handle's axis (towards the blade): thumb side and forward.</summary>
    public static Vector3 Axis => new(MathF.Cos(Phi), MathF.Sin(Phi), 0f);

    /// <summary>The direction the fingers leave the knuckles in: perpendicular to the axis in the palm's plane.</summary>
    public static Vector3 Finger => new(-MathF.Sin(Phi), MathF.Cos(Phi), 0f);

    /// <summary>The knuckle row's middle: past the palm's end, a hair above its plane so the handle clears the palm.</summary>
    public static Vector3 Knuckles => new(0f, 0.082f, 0.006f);

    /// <summary>The ring centre at the middle of the four fingers: the handle's axis passes through it.</summary>
    public static Vector3 Centre => Knuckles + new Vector3(0f, 0f, RingRadius);

    /// <summary>Where along the handle (towards the blade) finger <paramref name="i"/> (0 index, 3 little) sits from the middle, metres.</summary>
    public static float Offset(int i) => (1.5f - i) * 0.0168f;

    /// <summary>The ring centre of finger <paramref name="i"/>.</summary>
    public static Vector3 RingCentre(int i) => Centre + Axis * Offset(i);

    /// <summary>
    /// The points a finger's ring is built through: <see cref="Segments"/> + 1 of them, from the knuckle round the handle, the vertices of a polygon
    /// whose flat sides touch the finger's own circle (so a straight box between two of them lies at <see cref="RingRadius"/> from the axis).
    /// </summary>
    public static void RingPoints(int i, Span<Vector3> points)
    {
        float step = Wrap / Segments, vertexRadius = RingRadius / MathF.Cos(step * 0.5f);
        var c = RingCentre(i);
        var f = Finger;
        var n = new Vector3(0f, 0f, 1f);
        for (int k = 0; k <= Segments; k++)
        {
            float a = k * step;
            points[k] = c + (f * MathF.Sin(a) - n * MathF.Cos(a)) * vertexRadius;
        }
    }

    /// <summary>The thumb's path: from its root on the palm's thumb side, over the index finger, over the middle finger. Three points.</summary>
    public static void ThumbPoints(Span<Vector3> points)
    {
        float over = RingRadius + FingerThick * 0.5f + ThumbRadius;
        var f = Finger;
        var n = new Vector3(0f, 0f, 1f);
        const float a = 3.6f;
        var dir = f * MathF.Sin(a) - n * MathF.Cos(a);
        points[0] = new Vector3(0.026f, 0.048f, 0.012f);
        points[1] = RingCentre(0) + dir * over;
        points[2] = RingCentre(1) + dir * over;
    }

    /// <summary>The distance from a point to the handle's axis (a line through <see cref="Centre"/> along <see cref="Axis"/>).</summary>
    public static float DistanceToAxis(Vector3 p)
    {
        var d = p - Centre;
        var along = Axis * Vector3.Dot(d, Axis);
        return (d - along).Length();
    }

    /// <summary>The palm's box in hand coordinates (centre and half extents), for the clearance check: the 0.068 x 0.072 x 0.028 box <c>Hand</c> builds.</summary>
    public static (Vector3 Centre, Vector3 Half) PalmBox => (new Vector3(0f, 0.042f, 0f), new Vector3(0.034f, 0.036f, 0.014f));
}
