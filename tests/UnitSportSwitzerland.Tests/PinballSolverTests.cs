using Godot;
using UnitSport.Interiors.Pinball;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>Pinball's solver on its four tables (#1325): no tunnelling, flipper shots, bumpers, ramps, the plunger, scoops, robot play.</summary>
public class PinballSolverTests
{
    private readonly ITestOutputHelper _out;
    public PinballSolverTests(ITestOutputHelper output) => _out = output;

    /// <summary>Every named shot of every table (ramps, orbits, scoops, banks, bumpers, spinners) is made by some flipper timing.</summary>
    [Fact]
    public void Every_shot_is_reachable_from_the_flippers()
    {
        var missing = new List<string>();
        foreach (var t in PinballLayouts.All())
        {
            var got = PinballTests.Reached(t);
            _out.WriteLine($"== {t.Name}: " + string.Join(" | ", t.Shots.Select(s => got.TryGetValue(s.Name, out var how) ? $"{s.Name} ({how})" : $"MISSING {s.Name}")));
            missing.AddRange(t.Shots.Where(s => !got.ContainsKey(s.Name)).Select(s => $"{t.Name}: {s.Name}"));
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void A_fast_ball_never_passes_through_a_wall()
    {
        var t = PinballLayouts.Get(0);
        foreach (float angle in new[] { 170f, 185f, 200f, 225f })
        {
            var s = new PinballSolver(t);
            var b = s.Serve();
            b.P = new Vector2(-0.10f, 0.55f);
            // 15 m/s, 15 mm a step: more than the ball's radius, into the left wall
            b.V = Vector2.FromAngle(Mathf.DegToRad(angle)) * 15f;
            for (int i = 0; i < 40; i++)
            {
                s.Tick();
                Assert.True(b.P.X > PinballLayouts.Left + PinballTable.BallRadius * 0.9f, $"through the wall at {angle}°: {b.P}");
            }
        }
    }

    [Fact]
    public void A_cradled_ball_flipped_goes_up_the_table()
    {
        foreach (var t in PinballLayouts.All())
            for (int side = 0; side < 2; side++)
            {
                var s = new PinballSolver(t);
                var fl = t.Flippers[side];
                var b = s.Serve();
                b.P = fl.Pivot + Vector2.FromAngle(fl.Up) * 0.035f + new Vector2(0, 0.03f);
                float top = 0;
                for (int i = 0; i < 2000; i++)
                {
                    bool held = i < 1200 || i >= 1380 && i < 1680;
                    if (side == 0) s.LeftHeld = held; else s.RightHeld = held;
                    s.Tick();
                    if (i == 1199) Assert.True(b.V.Length() < 0.05f, $"{t.Name}: cradled ({b.P}, {b.V})");
                    if (s.Balls.Count > 0) top = Math.Max(top, b.Pos3.Y);
                }
                Assert.True(top > 0.5f, $"{t.Name} side {side}: the shot reached y {top:F2}");
            }
    }

    [Fact]
    public void A_ball_left_alone_drains_between_the_flippers()
    {
        var t = PinballLayouts.Get(1);
        var s = new PinballSolver(t);
        var b = s.Serve();
        b.P = new Vector2(PinballLayouts.Xc, 0.40f);
        bool drained = false;
        for (int i = 0; i < 4000 && !drained; i++)
        {
            s.Tick();
            drained = s.Events.Any(e => e.Kind == PinballSolver.EventKind.Drain);
        }
        Assert.True(drained);
        Assert.Empty(s.Balls);
    }

    [Fact]
    public void A_pop_bumper_kicks_the_ball_away_and_closes_its_switch()
    {
        var t = PinballLayouts.Get(0);
        var bumper = t.Discs.First(d => d.Kind == PinballTable.DiscKind.Bumper);
        var s = new PinballSolver(t);
        var b = s.Serve();
        b.P = bumper.C + new Vector2(0, -0.06f);
        b.V = new Vector2(0, 0.4f);
        float fastest = 0;
        for (int i = 0; i < 300; i++)
        {
            s.Tick();
            fastest = Math.Max(fastest, b.V.Length());
        }
        Assert.Contains(s.Events, e => e.Kind == PinballSolver.EventKind.Switch && e.Id == bumper.Switch);
        Assert.True(fastest >= PinballSolver.BumperKick * 0.95f, $"kicked to {fastest:F2} m/s");
    }

    [Theory]
    [InlineData(1.0f, false)]
    [InlineData(2.5f, true)]
    public void A_ramp_is_made_only_fast_enough(float speed, bool made)
    {
        var t = PinballLayouts.Get(3);
        int sw = t.SwitchIndex("Slalom");
        var s = new PinballSolver(t);
        var b = s.Serve();
        b.P = new Vector2(0.08f, 0.50f);
        b.V = new Vector2(0, speed);
        for (int i = 0; i < 3000 && s.Balls.Count > 0; i++) s.Tick();
        Assert.Equal(made, s.Events.Any(e => e.Kind == PinballSolver.EventKind.Switch && e.Id == sw));
        if (!made) Assert.Contains(s.Events, e => e.Kind == PinballSolver.EventKind.RampFail);
    }

    [Fact]
    public void A_full_plunge_goes_round_the_arch_and_a_soft_one_reaches_the_top_lanes()
    {
        foreach (var t in PinballLayouts.All())
        {
            bool lanesHit = false;
            for (float pull = 0.1f; pull <= 0.3f; pull += 0.02f)
            {
                var s = new PinballSolver(t);
                s.Serve();
                s.Run(300);
                Assert.True(s.BallOnPlunger(), $"{t.Name}: the ball waits on the plunger");
                s.Pull = pull;
                s.Run(400);
                s.Pull = 0;
                s.Run(3000);
                lanesHit |= s.Events.Any(e => e.Kind == PinballSolver.EventKind.Switch && t.Switches[e.Id].Kind == SwitchKind.TopLane);
                Assert.Contains(s.Events, e => e.Kind == PinballSolver.EventKind.Launch);
            }
            Assert.True(lanesHit, $"{t.Name}: a soft plunge reaches a top lane");
            var full = new PinballSolver(t);
            var ball = full.Serve();
            full.Run(300);
            full.Pull = 1;
            full.Run(400);
            full.Pull = 0;
            float leftmost = 1;
            for (int i = 0; i < 1500 && full.Balls.Count > 0; i++)
            {
                full.Tick();
                leftmost = Math.Min(leftmost, ball.P.X);
            }
            Assert.True(leftmost < 0f, $"{t.Name}: the full plunge comes round the arch ({leftmost:F2})");
        }
    }

    [Fact]
    public void A_scoop_holds_the_ball_until_released()
    {
        var t = PinballLayouts.Get(0);
        var scoop = t.Scoops[0];
        var s = new PinballSolver(t);
        var b = s.Serve();
        b.P = scoop.C + new Vector2(0, -0.04f);
        b.V = new Vector2(0, 0.5f);
        s.Run(500);
        Assert.Equal(0, b.Held);
        Assert.Contains(s.Events, e => e.Kind == PinballSolver.EventKind.Switch && e.Id == scoop.Switch);
        s.Run(2000);
        Assert.Equal(0, b.Held);
        s.Release(0, s.Step + 10);
        s.Run(250);
        Assert.True((b.P - scoop.C).Length() > 0.03f, $"kicked out of the scoop ({b.P})");
        s.Run(50);
        Assert.Equal(-1, b.Held);
    }

    /// <summary>A ball rolling down near a flipper gets flipped: the robot that plays the tables below.</summary>
    internal static void Robot(PinballGame g, int ms)
    {
        bool left = false, right = false;
        foreach (var b in g.Solver.Balls)
            if (b.Ramp < 0 && b.Held < 0 && b.P.Y < 0.2f && b.V.Y < -0.05f)
            {
                if (b.P.X < PinballLayouts.Xc) left = true;
                else right = true;
            }
        g.Button(0, left);
        g.Button(1, right);
        if (g.Solver.BallOnPlunger()) g.Solver.Pull = ms % 1500 < 600 ? 0.7f : 0f;
    }

    /// <summary>Minutes of robot play on every table: no ball ever leaves the table or stalls, and games end.</summary>
    [Fact]
    public void Robot_play_keeps_every_ball_on_the_table()
    {
        foreach (var t in PinballLayouts.All())
        {
            var g = new PinballGame(t, 7);
            g.Start();
            int still = 0, drains = 0;
            for (int ms = 0; ms < 120_000 && g.Rules.State == PinballRules.Phase.Playing; ms++)
            {
                Robot(g, ms);
                g.Run(1);
                foreach (var b in g.Solver.Balls)
                    Assert.True(Mathf.Abs(b.P.X) < PinballTable.Width / 2 + 0.002f && b.P.Y < PinballTable.Length + 0.002f, $"{t.Name}: a ball off the table at {b.P}");
                drains += g.Outbox.Count(e => e.Kind == PinballSolver.EventKind.Drain);
                g.Outbox.Clear();
                var first = g.Solver.Balls.FirstOrDefault(b => b.Held < 0 && b.Ramp < 0);
                still = first != null && first.V.LengthSquared() < 1e-6f && !g.Solver.BallOnPlunger() && first.P.Y > 0.2f ? still + 1 : 0;
                Assert.True(still < 8000, $"{t.Name}: a ball stuck at {first?.P}");
            }
            _out.WriteLine($"{t.Name}: {g.Rules.Players[0].Score} points, {drains} drains, {g.Rules.State}, {g.Time:F0} s");
            Assert.True(drains > 0);
        }
    }

    [Fact]
    public void The_same_inputs_give_the_same_game()
    {
        string Play()
        {
            var g = new PinballGame(PinballLayouts.Get(2), 3);
            g.Start();
            for (int ms = 0; ms < 20_000; ms++)
            {
                Robot(g, ms);
                g.Run(1);
            }
            return string.Join(";", g.Solver.Balls.Select(b => $"{b.P.X:R},{b.P.Y:R}")) + $"/{g.Rules.Players[0].Score}";
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void The_servers_mirror_scores_the_same_from_the_events()
    {
        foreach (var t in PinballLayouts.All())
        {
            var g = new PinballGame(t, 11);
            g.Start();
            var mirror = new PinballRules(g.Table, 11);
            mirror.Start(0);
            for (int ms = 0; ms < 90_000 && g.Rules.State == PinballRules.Phase.Playing; ms++)
            {
                Robot(g, ms);
                g.Run(1);
                // 20 Hz batches, through the wire format
                if (ms % 50 == 49) PinballGame.Mirror(mirror, PinballGame.UnpackEvents(PinballGame.PackEvents(g.TakeOutbox())), g.Time);
            }
            PinballGame.Mirror(mirror, g.TakeOutbox(), g.Time);
            _out.WriteLine($"{t.Name}: {g.Rules.Players[0].Score} / mirror {mirror.Players[0].Score}");
            Assert.True(g.Rules.Players[0].Score > 0);
            Assert.Equal(g.Rules.Players[0].Score, mirror.Players[0].Score);
            Assert.Equal(g.Rules.Players[0].Ball, mirror.Players[0].Ball);
            Assert.Equal(g.Rules.State, mirror.State);
        }
    }
}
