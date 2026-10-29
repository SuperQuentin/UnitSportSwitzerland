using System.Runtime.InteropServices;
using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The sword's whole-body keyframe tables (#1371, src/Combat/SwordPoses.cs, linked in).</summary>
public class SwordPosesTests
{
    private static readonly SwordMove[] Blows =
        { SwordMove.Slash1, SwordMove.Slash2, SwordMove.Slash3, SwordMove.Slash4, SwordMove.Slash5, SwordMove.Charged, SwordMove.Plunge };

    private static float[] Floats(SwordPose p) => MemoryMarshal.Cast<SwordPose, float>(MemoryMarshal.CreateSpan(ref p, 1)).ToArray();

    [Fact]
    public void Every_moves_phases_are_ordered_inside_its_timing_windows()
    {
        foreach (var move in Blows.Append(SwordMove.Dodge))
            for (int seed = 0; seed < SwordPoses.Seeds; seed++)
            {
                var (zero, a, s, f) = SwordPoses.Times(move, seed);
                Assert.True(zero <= a && a <= s && s < f && f < 1f, $"{move} seed {seed}: {zero} {a} {s} {f}");
                if (move != SwordMove.Dodge)
                {
                    // the strike starts where the machine's startup ends, within the jitter's few per cent
                    var d = SwordRules.Def(move);
                    Assert.InRange(a, d.Startup / d.Total * 0.95f - 0.001f, d.Startup / d.Total * 1.05f + 0.001f);
                    Assert.Equal((d.Startup + d.Active) / d.Total, s, 4);
                }
            }
    }

    [Fact]
    public void Each_swing_starts_and_ends_in_the_stance_and_is_finite_all_along()
    {
        foreach (var move in Blows.Where(m => m != SwordMove.Plunge))
            for (int v = 0; v < SwordPoses.Variants; v++)
            {
                var start = Floats(SwordPoses.Sample(move, v, 3, 0f));
                var end = Floats(SwordPoses.Sample(move, v, 3, 1f));
                var ready = Floats(SwordPoses.Ready);
                for (int i = 0; i < ready.Length; i++)
                {
                    Assert.Equal(ready[i], start[i], 3);
                    Assert.Equal(ready[i], end[i], 3);
                }
                for (float p = 0; p <= 1f; p += 0.01f)
                    Assert.All(Floats(SwordPoses.Sample(move, v, 3, p)), x => Assert.True(float.IsFinite(x)));
            }
    }

    [Fact]
    public void The_strike_is_faster_than_the_wind_up_and_the_settle_overshoots_a_little()
    {
        var move = SwordMove.Slash1;
        var (_, a, s, _) = SwordPoses.Times(move, 3);
        float StrikeSpeed() => Math.Abs(SwordPoses.Sample(move, 0, 3, s).Psi - SwordPoses.Sample(move, 0, 3, a).Psi) / (s - a);
        float WindSpeed() => Math.Abs(SwordPoses.Sample(move, 0, 3, a).Psi - SwordPoses.Sample(move, 0, 3, 0f).Psi) / a;
        Assert.True(StrikeSpeed() > 2f * WindSpeed(), "the blow whips");
        // ease-in: the wind-up starts slow
        float early = Math.Abs(SwordPoses.Sample(move, 0, 3, a * 0.2f).Psi - SwordPoses.Sample(move, 0, 3, 0f).Psi);
        float late = Math.Abs(SwordPoses.Sample(move, 0, 3, a).Psi - SwordPoses.Sample(move, 0, 3, a * 0.8f).Psi);
        Assert.True(late > early * 3f);
        // overshoot: somewhere in the settle the chest's twist passes the stance's on the far side
        var ready = SwordPoses.Ready.Psi;
        bool passed = false;
        var over = SwordPoses.Sample(move, 0, 3, 0.7f).Psi;
        for (float p = 0.7f; p <= 1f; p += 0.005f)
            if ((SwordPoses.Sample(move, 0, 3, p).Psi - ready) * (over - ready) < 0f) passed = true;
        Assert.True(passed, "the settle overshoots the stance");
    }

    [Fact]
    public void The_jitter_is_deterministic_bounded_and_changes_with_the_seed()
    {
        for (int seed = 0; seed < SwordPoses.Seeds; seed++)
            for (int ch = 0; ch < 3; ch++)
            {
                float j = SwordPoses.Jitter(seed, ch);
                Assert.Equal(j, SwordPoses.Jitter(seed, ch));
                Assert.InRange(j, -1f, 1f);
            }
        Assert.Equal(Floats(SwordPoses.Sample(SwordMove.Slash2, 1, 5, 0.4f)), Floats(SwordPoses.Sample(SwordMove.Slash2, 1, 5, 0.4f)));
        Assert.NotEqual(Floats(SwordPoses.Sample(SwordMove.Slash2, 1, 5, 0.4f)), Floats(SwordPoses.Sample(SwordMove.Slash2, 1, 6, 0.4f)));
        // a few degrees, not a different move
        var a = SwordPoses.Sample(SwordMove.Slash1, 0, 1, 0.45f);
        var b = SwordPoses.Sample(SwordMove.Slash1, 0, 9, 0.45f);
        Assert.InRange(Math.Abs(a.Psi - b.Psi), 0f, 0.4f);
    }

    [Fact]
    public void The_two_sets_differ_and_the_swings_have_their_own_arcs()
    {
        for (int m = 0; m < 5; m++)
            Assert.NotEqual(Floats(SwordPoses.Sample(Blows[m], 0, 2, 0.45f)), Floats(SwordPoses.Sample(Blows[m], 1, 2, 0.45f)));
        for (int i = 0; i < 5; i++)
            for (int j = i + 1; j < 5; j++)
                Assert.NotEqual(Floats(SwordPoses.Sample(Blows[i], 0, 2, 0.4f)), Floats(SwordPoses.Sample(Blows[j], 0, 2, 0.4f)));
        // the spin slash and the charged release turn the body once round
        Assert.Equal(MathF.PI * 2f, SwordPoses.Sample(SwordMove.Slash3, 0, 2, 0.5f).Spin, 1);
        Assert.Equal(MathF.PI * 2f, SwordPoses.Sample(SwordMove.Charged, 0, 2, 0.5f).Spin, 1);
        Assert.Equal(0f, SwordPoses.Sample(SwordMove.Slash1, 0, 2, 0.5f).Spin);
        // the finisher jumps (the hips above the stance) before it slams down below it
        Assert.True(SwordPoses.Sample(SwordMove.Slash5, 0, 2, 0.15f).Py > SwordPoses.Ready.Py);
        Assert.True(SwordPoses.Sample(SwordMove.Slash5, 0, 2, 0.6f).Py < SwordPoses.Ready.Py - 0.15f);
    }

    [Fact]
    public void The_lunge_is_a_short_step_that_finishes_and_stays_inside_one_metre_two()
    {
        foreach (var move in new[] { SwordMove.Slash1, SwordMove.Slash2, SwordMove.Slash3, SwordMove.Slash4, SwordMove.Slash5, SwordMove.Charged })
            for (int v = 0; v < 2; v++)
            {
                float m = SwordPoses.Lunge(move, v);
                Assert.InRange(m, 0.3f, 1.2f);
                float last = 0f;
                for (float p = 0f; p <= 1f; p += 0.02f)
                {
                    float d = SwordPoses.LungeDone(move, p);
                    Assert.True(d >= last - 1e-6f, "the step never goes back");
                    last = d;
                }
                Assert.Equal(1f, last, 3);
            }
        Assert.Equal(0f, SwordPoses.Lunge(SwordMove.Plunge, 0));
        Assert.True(SwordPoses.Lunge(SwordMove.Slash4, 1) > SwordPoses.Lunge(SwordMove.Slash4, 0));
    }

    [Fact]
    public void Dodges_read_their_direction_and_the_code_round_trips()
    {
        Assert.Equal(SwordPoses.DodgeRoll, SwordPoses.DodgeVariant(1f, 0f));
        Assert.Equal(SwordPoses.DodgeBack, SwordPoses.DodgeVariant(-1f, 0f));
        Assert.Equal(SwordPoses.DodgeLeft, SwordPoses.DodgeVariant(0f, -1f));
        Assert.Equal(SwordPoses.DodgeRight, SwordPoses.DodgeVariant(0f, 1f));
        for (int v = 0; v < 4; v++)
            for (int s = 0; s < 16; s++)
            {
                int code = SwordPoses.Code(v, s);
                Assert.Equal(v, SwordPoses.VariantOf(code));
                Assert.Equal(s, SwordPoses.SeedOf(code));
                Assert.InRange(code, 0, 63);
            }
        Assert.Equal(1, SwordPoses.VariantFor(true));
        Assert.Equal(0, SwordPoses.VariantFor(false));
    }
}
