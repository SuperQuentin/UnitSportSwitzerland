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
                int spin = Floats(SwordPoses.Ready).Length - 1;   // Spin is the last field: it keeps its turn through the settle
                for (int i = 0; i < ready.Length - 1; i++)
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

    private static readonly SwordMove[] Swings = { SwordMove.Slash1, SwordMove.Slash2, SwordMove.Slash3, SwordMove.Slash4, SwordMove.Slash5, SwordMove.Charged };

    [Fact]
    public void The_new_timing_is_semi_realistic_and_the_blade_is_live_inside_the_strike()
    {
        foreach (var move in Swings.Where(m => m != SwordMove.Charged))
        {
            var d = SwordRules.Def(move);
            if (move == SwordMove.Slash5) { Assert.InRange(d.Total, 1.1f, 1.3f); Assert.InRange(d.Startup, 0.45f, 0.6f); }
            else { Assert.InRange(d.Total, 0.78f, 0.92f); Assert.InRange(d.Startup, 0.22f, 0.32f); }
            Assert.InRange(d.Active, 0.12f, 0.18f);
            Assert.InRange(d.Recovery, 0.40f, 0.55f);
            // the cancel opens late in the follow-through: past the end of the strike by a good part of the recovery, before it ends
            Assert.InRange(d.Cancel, d.Startup + d.Active + d.Recovery * 0.4f, d.Total - 0.05f);
        }
        // the charged blow, with its minimum hold, is about 1.3 s
        var c = SwordRules.Def(SwordMove.Charged);
        Assert.InRange(c.Total + SwordRules.ChargeMin, 1.25, 1.45);
    }

    [Fact]
    public void Velocity_is_continuous_through_every_key_so_nothing_stops_mid_move()
    {
        foreach (var move in Swings.Append(SwordMove.Plunge).Append(SwordMove.Dodge))
            for (int v = 0; v < 2; v++)
            {
                var (_, tA, tS, tF) = SwordPoses.Times(move, 3);
                float tU = tF + (1f - tF) * 0.55f;
                foreach (float key in new[] { tA, tS, tF, tU })
                {
                    if (key < 0.02f) continue;
                    const float e = 0.002f;
                    var before = Floats(SwordPoses.Sample(move, v, 3, key - e));
                    var at = Floats(SwordPoses.Sample(move, v, 3, key));
                    var after = Floats(SwordPoses.Sample(move, v, 3, key + e));
                    for (int i = 0; i < at.Length - 1; i++)   // Spin is the body's own turn, clamped
                    {
                        float vl = (at[i] - before[i]) / e, vr = (after[i] - at[i]) / e;
                        // the speed on both sides agrees (the lags and the weight shift are smooth too): no kink, no stop-and-start
                        Assert.True(Math.Abs(vl - vr) < 0.25f * Math.Max(1f, Math.Abs(vl) + Math.Abs(vr)) + 0.6f, $"{move} v{v} key {key:F2} field {i}: {vl} vs {vr}");
                    }
                }
            }
    }

    [Fact]
    public void A_chained_swing_flows_out_of_the_one_before_without_a_pop_and_without_the_stance()
    {
        // swing 2 begins 0.60 s into swing 1 (the cancel window): the pose at its first moment is where swing 1 was
        var prev = new SwordPoses.Chain(SwordMove.Slash1, 0, 3, 0.60f / SwordRules.Def(SwordMove.Slash1).Total);
        var was = Floats(SwordPoses.Sample(SwordMove.Slash1, 0, 3, prev.Progress));
        var first = Floats(SwordPoses.SampleChain(SwordMove.Slash2, 0, 4, 0f, prev));
        for (int i = 0; i < was.Length - 1; i++) Assert.Equal(was[i], first[i], 3);
        // not the stance
        var ready = Floats(SwordPoses.Ready);
        Assert.True(Enumerable.Range(0, ready.Length - 1).Sum(i => Math.Abs(first[i] - ready[i])) > 0.3f, "the chain starts away from the stance");
        // no jump from frame to frame across the whole hand-over
        var last = first;
        float total = SwordRules.Def(SwordMove.Slash2).Total;
        for (float s = 0.005f; s <= 0.5f; s += 0.005f)
        {
            var now = Floats(SwordPoses.SampleChain(SwordMove.Slash2, 0, 4, s / total, prev));
            for (int i = 0; i < now.Length - 1; i++) Assert.True(Math.Abs(now[i] - last[i]) < 0.12f, $"a pop in field {i} at {s}");
            last = now;
        }
        // and it is the swing's own pose once the wind-up has taken over
        var own = Floats(SwordPoses.Sample(SwordMove.Slash2, 0, 4, 0.5f));
        var chained = Floats(SwordPoses.SampleChain(SwordMove.Slash2, 0, 4, 0.5f, prev));
        Assert.Equal(own, chained);
        // the chain packs into the pose's bits and back
        var back = SwordPoses.UnpackChain(SwordPoses.PackChain(prev));
        Assert.Equal(prev.Move, back.Move);
        Assert.Equal(prev.Variant, back.Variant);
        Assert.Equal(prev.Seed, back.Seed);
        Assert.Equal(prev.Progress, back.Progress, 1);
        Assert.Equal(SwordMove.None, SwordPoses.UnpackChain(0).Move);
    }

    [Fact]
    public void The_hips_lead_and_the_body_follows_in_order()
    {
        // through the strike the hips' twist-driven channels move first: the chest's lag is behind the hips', the arm's behind the chest's
        var (_, tA, tS, _) = SwordPoses.Times(SwordMove.Slash1, 3);
        float total = SwordRules.Def(SwordMove.Slash1).Total;
        float p = (tA + tS) * 0.5f;
        var now = SwordPoses.Sample(SwordMove.Slash1, 0, 3, p);
        var later = SwordPoses.Sample(SwordMove.Slash1, 0, 3, p + 0.03f / total);
        // the weight shift: the pelvis has dropped and moved to the front foot by the strike's end, the back heel is up
        var end = SwordPoses.Sample(SwordMove.Slash1, 0, 3, tS);
        Assert.True(end.Py < SwordPoses.Ready.Py - 0.1f);
        Assert.True(end.Px < -0.04f);
        Assert.True(end.RearY > SwordPoses.Ready.RearY + 0.03f);
        Assert.NotEqual(now.Psi, later.Psi);
        Assert.True(SwordPoses.ChestLag < SwordPoses.ArmLag && SwordPoses.ArmLag < SwordPoses.HeadLag);
        Assert.InRange(SwordPoses.ChestLag, 0.03f, 0.07f);
    }

    [Fact]
    public void Slashes_twist_the_chest_sixty_to_ninety_degrees_between_the_wind_up_and_the_follow_through()
    {
        foreach (var move in new[] { SwordMove.Slash1, SwordMove.Slash2 })
            for (int v = 0; v < 2; v++)
            {
                var (_, tA, _, tF) = SwordPoses.Times(move, 3);
                float swing = Math.Abs(SwordPoses.Sample(move, v, 3, tF + 0.05f).Psi - SwordPoses.Sample(move, v, 3, tA).Psi) * 180f / MathF.PI;
                Assert.InRange(swing, 60f, 100f);
            }
    }
}
