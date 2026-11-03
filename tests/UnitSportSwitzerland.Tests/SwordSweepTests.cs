using System.Numerics;
using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The blade's swept capsule against the targets' own shapes (#1371, src/Combat/SwordSweep.cs, linked in).</summary>
public class SwordSweepTests
{
    private static Capsule Body(float x, float z)
    {
        Span<Capsule> s = stackalloc Capsule[4];
        SwordSweep.BodyShape(new Vector3(x, 0, z), s);
        return s[0];
    }

    [Fact]
    public void A_blade_whose_tip_just_reaches_the_body_hits_it_and_five_centimetres_short_does_not()
    {
        // the blade lies along +X from the hilt at 0.1 m to the tip; the body's axis stands 2 m away with a 0.42 m radius and the blade's 5 cm thickness
        var body = new[] { Body(2f, 0f) };
        float touch = 2f - SwordSweep.BodyRadius - SwordSweep.BladeRadius;
        bool Hits(float tip) => SwordSweep.Sweep(new Vector3(0.1f, 1f, 0), new Vector3(tip, 1f, 0), new Vector3(0.1f, 1f, 0), new Vector3(tip, 1f, 0), body, 0f, out _, out _);
        Assert.True(Hits(touch + 0.01f), "just inside the tip");
        Assert.False(Hits(touch - 0.05f), "5 cm outside");
        // the slack the server allows lets a body 0.3 m further still be claimed, no more
        Assert.True(SwordSweep.Sweep(new Vector3(0.1f, 1f, 0), new Vector3(touch - 0.2f, 1f, 0), new Vector3(0.1f, 1f, 0), new Vector3(touch - 0.2f, 1f, 0), body, 0.3f, out _, out _));
        Assert.False(SwordSweep.Sweep(new Vector3(0.1f, 1f, 0), new Vector3(touch - 0.5f, 1f, 0), new Vector3(0.1f, 1f, 0), new Vector3(touch - 0.5f, 1f, 0), body, 0.3f, out _, out _));
    }

    [Fact]
    public void A_blade_above_or_below_the_body_misses_it()
    {
        var body = new[] { Body(1f, 0f) };
        Assert.False(SwordSweep.Sweep(new Vector3(0.1f, 2.4f, 0), new Vector3(1.4f, 2.4f, 0), new Vector3(0.1f, 2.4f, 0), new Vector3(1.4f, 2.4f, 0), body, 0f, out _, out _));
        Assert.False(SwordSweep.Sweep(new Vector3(0.1f, -0.6f, 0), new Vector3(1.4f, -0.6f, 0), new Vector3(0.1f, -0.6f, 0), new Vector3(1.4f, -0.6f, 0), body, 0f, out _, out _));
    }

    [Fact]
    public void A_spin_slash_reaches_a_target_behind_where_it_started()
    {
        // the blade circles the body once at 1.2 m reach, a full turn over 16 samples; a target stands behind the start
        var body = new[] { Body(0f, 1.5f) };   // the start faces -Z
        bool hit = false;
        Vector3 Tip(float a) => new(1.3f * MathF.Sin(a), 1f, -1.3f * MathF.Cos(a));
        Vector3 Hilt(float a) => new(0.1f * MathF.Sin(a), 1f, -0.1f * MathF.Cos(a));
        for (int i = 0; i < 16 && !hit; i++)
        {
            float a0 = MathF.Tau * i / 16f, a1 = MathF.Tau * (i + 1) / 16f;
            hit = SwordSweep.Sweep(Hilt(a0), Tip(a0), Hilt(a1), Tip(a1), body, 0f, out _, out _);
        }
        Assert.True(hit, "the full turn covers the back");
        // a quarter turn from the start does not
        bool early = false;
        for (int i = 0; i < 4; i++)
        {
            float a0 = MathF.Tau * i / 16f, a1 = MathF.Tau * (i + 1) / 16f;
            early |= SwordSweep.Sweep(Hilt(a0), Tip(a0), Hilt(a1), Tip(a1), body, 0f, out _, out _);
        }
        Assert.False(early);
    }

    [Fact]
    public void The_fastest_swing_cannot_tunnel_through_the_dummys_post()
    {
        // two samples 1.8 m apart (far more than any real swing's step): the blade passes the 7 cm post between them
        Span<Capsule> shape = stackalloc Capsule[4];
        int n = SwordSweep.DummyShape(new Vector3(0.9f, 0, 0), 0f, shape);
        // the blade is 0.9 m of steel held across the post's height, moving along X from -0.2 to 1.6 m; the post stands at x 0.9 and the torso round it:
        // only the thin post is in the way at the hilt-to-tip height of 0.3 m (below the torso)
        var a0 = new Vector3(-0.2f, 0.3f, -0.4f);
        var b0 = new Vector3(-0.2f, 0.3f, 0.4f);
        var a1 = new Vector3(1.6f, 0.3f, -0.4f);
        var b1 = new Vector3(1.6f, 0.3f, 0.4f);
        Assert.True(SwordSweep.Sweep(a0, b0, a1, b1, shape[..n], 0f, out var c, out float t));
        Assert.InRange(c.X, 0.7f, 1.1f);
        Assert.InRange(t, 0.3f, 0.7f);
    }

    [Fact]
    public void The_dummy_is_its_own_shape_and_not_a_cylinder_round_it()
    {
        var shape = new Capsule[4];
        int n = SwordSweep.DummyShape(new Vector3(0, 0, 0), 0f, shape);
        // beside the crossbar's end the straw hand is there, but out at the corner of a 0.55 m cylinder at shoulder height beside the torso, nothing is
        bool Touches(Vector3 p) => SwordSweep.Sweep(p, p + new Vector3(0, 0.05f, 0), p, p + new Vector3(0, 0.05f, 0), shape.AsSpan(0, n), 0f, out _, out _);
        Assert.True(Touches(new Vector3(0.45f, 1.30f, 0f)), "the crossbar's straw hand");
        Assert.False(Touches(new Vector3(0.40f, 0.60f, 0f)), "air beside the post below the torso");
        Assert.True(Touches(new Vector3(0f, 1.05f, -0.3f)), "the torso");
        // turned a quarter, the crossbar points along Z
        int m = SwordSweep.DummyShape(new Vector3(0, 0, 0), MathF.PI / 2f, shape);
        Assert.True(SwordSweep.Sweep(new Vector3(0f, 1.30f, 0.45f), new Vector3(0f, 1.35f, 0.45f), new Vector3(0f, 1.30f, 0.45f), new Vector3(0f, 1.35f, 0.45f), shape.AsSpan(0, m), 0f, out _, out _));
    }

    [Fact]
    public void The_segment_distance_is_exact_for_crossing_parallel_and_end_cases()
    {
        Assert.Equal(1f, SwordSweep.SegmentDistance(new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(1, 1, -1), new Vector3(1, 1, 1), out _), 4);
        Assert.Equal(2f, SwordSweep.SegmentDistance(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 2, 0), new Vector3(1, 2, 0), out _), 4);
        Assert.Equal(1f, SwordSweep.SegmentDistance(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(3, 0, 0), out _), 4);
    }
}
