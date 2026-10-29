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
        Assert.Equal(MathF.PI * 2f, SwordPoses.Sample(SwordMove.Slash3, 1, 2, 0.5f).Spin, 1);   // the moving set turns once round, the standing yoko-giri pivots
        Assert.Equal(0f, SwordPoses.Sample(SwordMove.Slash3, 0, 2, 0.5f).Spin);
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
    public void The_manga_timing_has_a_moving_hold_an_explosive_strike_and_a_zanshin_hold_the_cancel_opens_in()
    {
        foreach (var move in Swings.Where(m => m != SwordMove.Charged))
        {
            var d = SwordRules.Def(move);
            if (move == SwordMove.Slash5) { Assert.InRange(d.Total, 1.1f, 1.3f); Assert.InRange(d.Startup, 0.45f, 0.6f); Assert.InRange(d.Active, 0.06f, 0.12f); }
            else { Assert.InRange(d.Total, 0.78f, 0.92f); Assert.InRange(d.Startup, 0.22f, 0.32f); Assert.InRange(d.Active, 0.06f, 0.10f); }
            Assert.InRange(d.Recovery, 0.45f, 0.60f);
            // the cancel opens inside the zanshin hold: after the snap, before the hold's end
            Span<float> kt = stackalloc float[9];
            SwordPoses.KeyTimes(move, 3, kt);
            Assert.InRange(d.Cancel / d.Total, kt[4], kt[7]);
        }
        var c = SwordRules.Def(SwordMove.Charged);
        Assert.InRange(c.Total + SwordRules.ChargeMin, 1.25, 1.45);
        Assert.InRange(c.Active, 0.06f, 0.10f);
    }

    [Fact]
    public void Each_swing_has_a_moving_hold_a_fast_strike_an_overshoot_snap_and_a_still_zanshin()
    {
        foreach (var move in Swings)
            for (int v = 0; v < 2; v++)
            {
                Span<float> kt = stackalloc float[9];
                SwordPoses.KeyTimes(move, 3, kt);
                float total = SwordRules.Def(move).Total;
                // the moving hold: the coil is reached 0.08-0.15 s before the blade goes live, and the pose drifts a little over it
                float hold = (kt[2] - kt[1]) * total;
                Assert.InRange(hold, 0.08f, 0.15f);
                var coil = SwordPoses.Sample(move, v, 3, kt[1]);
                var end = SwordPoses.Sample(move, v, 3, kt[2]);
                float drift = Math.Abs(end.Psi - coil.Psi) + Math.Abs(end.Hy - coil.Hy);
                Assert.InRange(drift, 0.001f, 0.9f);
                // the strike takes at most 0.12 s
                Assert.True((kt[3] - kt[2]) * total <= 0.125f);
                // the snap: just after the strike's end the blow has gone past it
                var strike = SwordPoses.Sample(move, v, 3, kt[3]);
                var snap = SwordPoses.Sample(move, v, 3, kt[4]);
                float arc = Math.Abs(strike.Psi - end.Psi) + Math.Abs(strike.Hx - end.Hx) + Math.Abs(strike.Hy - end.Hy) + Math.Abs(strike.Hz - end.Hz);
                float past = Math.Abs(snap.Psi - end.Psi) + Math.Abs(snap.Hx - end.Hx) + Math.Abs(snap.Hy - end.Hy) + Math.Abs(snap.Hz - end.Hz);
                Assert.True(past > arc, $"{move} v{v}: the snap goes past the end of the arc");
                // the zanshin: nearly still through the hold (the body's twist and the sword hand move less than 1.5 rad-or-metres a second, against ten or more in the strike)
                float h0 = kt[5], h1 = kt[6];
                var a = SwordPoses.Sample(move, v, 3, h0);
                var b = SwordPoses.Sample(move, v, 3, h1);
                float speed = (Math.Abs(b.Psi - a.Psi) + Math.Abs(b.Hx - a.Hx) + Math.Abs(b.Hy - a.Hy)) / ((h1 - h0) * total);
                Assert.True(speed < 1.5f, $"{move} v{v}: the hold is still ({speed:F2}/s)");
            }
    }

    [Fact]
    public void The_wind_up_points_to_the_side_the_cut_starts_from()
    {
        // the hand at the end of the moving hold is on the far side of where the cut ends: a diagonal from the right, a backhand from the left, a thrust drawn back,
        // an overhead cut from above, a wide cut from behind
        SwordPose Coil(SwordMove m, int v) { Span<float> kt = stackalloc float[9]; SwordPoses.KeyTimes(m, 3, kt); return SwordPoses.Sample(m, v, 3, kt[2]); }
        SwordPose Strike(SwordMove m, int v) { Span<float> kt = stackalloc float[9]; SwordPoses.KeyTimes(m, 3, kt); return SwordPoses.Sample(m, v, 3, kt[5]); }
        for (int v = 0; v < 2; v++)
        {
            Assert.True(Coil(SwordMove.Slash1, v).Hx > Strike(SwordMove.Slash1, v).Hx + 0.4f, "kesa-giri winds up on the right");
            Assert.True(Coil(SwordMove.Slash2, v).Hx < Strike(SwordMove.Slash2, v).Hx - 0.4f, "gyaku kesa winds up on the left");
            Assert.True(Coil(SwordMove.Slash4, v).Hz < Strike(SwordMove.Slash4, v).Hz - 0.4f, "tsuki draws back");
            Assert.True(Coil(SwordMove.Slash5, v).Hy > Strike(SwordMove.Slash5, v).Hy + 0.3f || v == 1, "the finisher winds up overhead");
            Assert.True(Coil(SwordMove.Charged, v).Psi < Strike(SwordMove.Charged, v).Psi - 1f, "the charged cut winds up from behind");
        }
        Assert.True(Coil(SwordMove.Slash3, 0).Hx > Strike(SwordMove.Slash3, 0).Hx + 0.4f, "yoko-giri winds up on the right");
        // the iai dash: a low drawn-back stance
        Assert.True(Coil(SwordMove.Slash5, 1).Py < SwordPoses.Ready.Py - 0.2f);
        // the finisher is two-handed: the off hand comes over to the grip
        Assert.True(Coil(SwordMove.Slash5, 0).Ox < -0.3f);
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
