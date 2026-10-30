using System.Numerics;
using UnitSport.Avatar;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The closed fist's grip on the sword's handle (#1371, src/Avatar/HandGrip.cs, linked in): the one description the fist mesh and the sword's placement share.</summary>
public class HandGripTests
{
    private const float Tolerance = 0.003f;   // 3 mm

    private static Vector3[] Ring(int finger)
    {
        var pts = new Vector3[HandGrip.Segments + 1];
        HandGrip.RingPoints(finger, pts);
        return pts;
    }

    [Fact]
    public void The_handle_axis_passes_through_every_fingers_ring_centre()
    {
        for (int i = 0; i < 4; i++)
            Assert.True(HandGrip.DistanceToAxis(HandGrip.RingCentre(i)) < 0.0005f, $"finger {i}");
        Assert.True(HandGrip.DistanceToAxis(HandGrip.Centre) < 0.0005f);
    }

    [Fact]
    public void Every_finger_segment_lies_against_the_handles_surface_within_3_mm()
    {
        for (int i = 0; i < 4; i++)
        {
            var pts = Ring(i);
            for (int k = 0; k < HandGrip.Segments; k++)
            {
                // the middle of a segment is the finger's centreline: half a finger's thickness from the handle's surface
                var mid = (pts[k] + pts[k + 1]) * 0.5f;
                float inner = HandGrip.DistanceToAxis(mid) - HandGrip.FingerThick * 0.5f;
                Assert.True(Math.Abs(inner - HandGrip.HandleRadius) < Tolerance, $"finger {i} segment {k}: {inner * 1000:F1} mm from the axis, the handle is {HandGrip.HandleRadius * 1000:F1}");
                // and along the whole segment nothing is nearer than the surface: no finger passes through the handle
                for (float t = 0f; t <= 1f; t += 0.1f)
                    Assert.True(HandGrip.DistanceToAxis(Vector3.Lerp(pts[k], pts[k + 1], t)) - HandGrip.FingerThick * 0.5f > HandGrip.HandleRadius - 0.001f);
            }
        }
    }

    [Fact]
    public void The_fingers_wrap_most_of_the_way_round_and_end_on_the_far_side()
    {
        var pts = Ring(1);
        var c = HandGrip.RingCentre(1);
        var f = HandGrip.Finger;
        // from the knuckle (at the bottom, the palm's side) round over the top
        float top = Vector3.Dot(pts[HandGrip.Segments * 2 / 3] - c, new Vector3(0, 0, 1));
        Assert.True(top > HandGrip.HandleRadius, "a finger passes over the handle's far side");
        Assert.InRange(HandGrip.Wrap * 180f / MathF.PI, 200f, 280f);
        _ = f;
    }

    [Fact]
    public void The_thumb_closes_over_the_index_and_middle_fingers_without_touching_the_handle()
    {
        var th = new Vector3[3];
        HandGrip.ThumbPoints(th);
        float over = HandGrip.RingRadius + HandGrip.FingerThick * 0.5f + HandGrip.ThumbRadius;
        Assert.True(Math.Abs(HandGrip.DistanceToAxis(th[1]) - over) < Tolerance);
        Assert.True(Math.Abs(HandGrip.DistanceToAxis(th[2]) - over) < Tolerance);
        // the thumb lies along the handle from the index finger's ring to the middle finger's
        Assert.True(Math.Abs(Vector3.Dot(th[1] - th[2], HandGrip.Axis) - (HandGrip.Offset(0) - HandGrip.Offset(1))) < Tolerance);
        for (float t = 0f; t <= 1f; t += 0.05f)
            Assert.True(HandGrip.DistanceToAxis(Vector3.Lerp(th[0], th[1], t)) > HandGrip.HandleRadius + HandGrip.ThumbRadius - 0.001f);
    }

    [Fact]
    public void The_palm_does_not_intersect_the_handle()
    {
        var (centre, half) = HandGrip.PalmBox;
        float BoxDistance(Vector3 p)
        {
            var q = Vector3.Abs(p - centre) - half;
            return Vector3.Max(q, Vector3.Zero).Length() + Math.Min(Math.Max(q.X, Math.Max(q.Y, q.Z)), 0f);
        }
        // along the whole handle, as far as it reaches either side of the fist
        for (float s = -0.2f; s <= 0.12f; s += 0.005f)
        {
            var onAxis = HandGrip.Centre + HandGrip.Axis * s;
            Assert.True(BoxDistance(onAxis) > HandGrip.HandleRadius - 0.001f, $"the handle at {s:F3} m is inside the palm ({BoxDistance(onAxis) * 1000:F1} mm from it)");
        }
    }

    [Fact]
    public void The_handle_runs_diagonally_across_the_palm_and_the_blade_points_forward_off_the_forearm()
    {
        // the handle's axis against the hand's own direction (the forearm's): 30 to 45 degrees, the blade forward and across
        float off = MathF.Acos(HandGrip.Axis.Y) * 180f / MathF.PI;
        Assert.InRange(off, 30f, 45f);
        // the fingers leave perpendicular to the axis, in the palm's plane
        Assert.True(Math.Abs(Vector3.Dot(HandGrip.Axis, HandGrip.Finger)) < 1e-5f);
        Assert.Equal(0f, HandGrip.Axis.Z);
        // the four fingers stack along the handle, a finger's width apart, the index at the blade's side
        for (int i = 0; i < 3; i++)
            Assert.InRange(HandGrip.Offset(i) - HandGrip.Offset(i + 1), HandGrip.FingerWide, HandGrip.FingerWide * 1.3f);
        Assert.True(HandGrip.Offset(0) > 0f && HandGrip.Offset(3) < 0f);
    }
}
