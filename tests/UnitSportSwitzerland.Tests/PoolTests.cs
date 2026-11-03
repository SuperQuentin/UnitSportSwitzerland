using Godot;
using UnitSport.Interiors.Games.Pool;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>Pool (#1324): the balls' solver (pots, stun, draw, follow, the break) and eight-ball's rules (fouls, groups, the 8, bots).</summary>
public class PoolTests
{
    private readonly ITestOutputHelper _out;
    public PoolTests(ITestOutputHelper output) => _out = output;

    private static readonly (float Hx, float Hz) Cloth = PoolGame.Cloth(1.4f, 2.7f);

    private static PoolSolver Empty()
    {
        var s = new PoolSolver(Cloth.Hx, Cloth.Hz);
        for (int i = 0; i < PoolSolver.Balls; i++) s.On[i] = false;
        return s;
    }

    private static float Angle(Vector2 from, Vector2 to) => PoolGame.AngleTo(from, to) / 1000f * Mathf.Pi / 180f;

    [Fact]
    public void The_rack_is_tight_and_on_the_cloth()
    {
        var s = new PoolSolver(Cloth.Hx, Cloth.Hz);
        s.Rack(new Random(3));
        for (int i = 0; i < PoolSolver.Balls; i++)
        {
            Assert.True(s.On[i]);
            Assert.True(Mathf.Abs(s.P[i].X) < Cloth.Hx && Mathf.Abs(s.P[i].Y) < Cloth.Hz, $"ball {i} off the cloth");
            for (int j = i + 1; j < PoolSolver.Balls; j++)
                Assert.True(s.P[i].DistanceTo(s.P[j]) >= 2 * PoolSolver.R - 1e-4f, $"balls {i} and {j} overlap");
        }
    }

    [Fact]
    public void A_straight_shot_pots_into_the_corner()
    {
        var s = Empty();
        var corner = new Vector2(Cloth.Hx, Cloth.Hz);
        var dir = (corner - Vector2.Zero).Normalized();
        s.On[1] = true;
        s.P[1] = corner - dir * 0.5f;
        s.On[0] = true;
        s.P[0] = s.P[1] - dir * 0.4f;
        s.Strike(Angle(s.P[0], s.P[1]), 2.5f, 0);
        s.Run();
        Assert.False(s.On[1], "the 1 went into the corner pocket");
        Assert.True(s.On[0], "the cue ball stayed up");
    }

    [Theory]
    [InlineData(0f, -0.03f, 0.1f)]    // stun: the cue ball stops near where it hit (the cloth gives it a little roll first)
    [InlineData(-0.8f, -9f, -0.1f)]   // draw: it comes back
    [InlineData(0.8f, 0.1f, 9f)]      // follow: it goes on after the object ball
    public void Spin_decides_where_the_cue_ball_goes(float follow, float min, float max)
    {
        var s = Empty();
        s.On[0] = s.On[1] = true;
        s.P[0] = new Vector2(0, -0.5f);
        s.P[1] = new Vector2(0, -0.2f);
        s.Strike(0, 3f, follow);
        // where the cue ball is before the object ball comes back off the far cushion
        s.RunTo(0.6f);
        float moved = s.P[0].Y - (-0.2f + -2 * PoolSolver.R);
        _out.WriteLine($"follow {follow}: the cue ball ends {moved:F3} m past the contact");
        Assert.InRange(moved, min, max);
    }

    [Fact]
    public void The_break_spreads_the_rack_and_keeps_every_ball_on_or_in_a_pocket()
    {
        var g = new PoolGame(Cloth.Hx, Cloth.Hz, 5);
        var s = g.Table;
        PoolGame.Strike(s, (PoolGame.AngleTo(s.P[0], new Vector2(0, Cloth.Hz * 0.5f)), 950, 0));
        s.Run();
        float spread = 0;
        for (int i = 1; i < PoolSolver.Balls; i++)
            if (s.On[i])
            {
                Assert.True(Mathf.Abs(s.P[i].X) <= Cloth.Hx && Mathf.Abs(s.P[i].Y) <= Cloth.Hz, $"ball {i} off the cloth");
                spread += s.P[i].DistanceTo(new Vector2(0, Cloth.Hz * 0.5f));
            }
        _out.WriteLine($"spread {spread:F2} m, {s.Time:F1} s");
        Assert.True(spread > 4f, "the balls spread out");
        Assert.True(s.Time < PoolSolver.MaxTime);
    }

    /// <summary>A game with two seats, broken, the table set by hand.</summary>
    private static PoolGame Set(params (int Ball, float X, float Z)[] balls)
    {
        var g = new PoolGame(Cloth.Hx, Cloth.Hz);
        g.Sit(1, 0);
        g.Sit(2, 1);
        for (int i = 0; i < PoolSolver.Balls; i++) g.Table.On[i] = false;
        foreach (var (b, x, z) in balls)
        {
            g.Table.On[b] = true;
            g.Table.P[b] = new Vector2(x, z);
        }
        g.State = PoolGame.Phase.Play;
        g.InHand = false;
        g.Up = 0;
        return g;
    }

    private static string? Shoot(PoolGame g, int seat, Vector2 at, int power = 300, int follow = 0) =>
        g.Act(seat, PoolGame.ActShoot, new[] { PoolGame.AngleTo(g.Table.P[0], at), power, follow }, g.ShotEnd + 1, new Random(1));

    [Fact]
    public void A_scratch_gives_the_opponent_ball_in_hand()
    {
        var corner = new Vector2(Cloth.Hx, Cloth.Hz);
        var g = Set((0, 0.2f, 0.6f), (3, -0.3f, -0.6f), (11, 0.3f, -0.8f));
        Assert.Null(Shoot(g, 0, corner, 400));
        Assert.Contains("Foul", g.Call);
        Assert.Equal(1, g.Up);
        Assert.True(g.InHand);
        Assert.True(g.Table.On[0], "the cue ball is back for the ball in hand");
    }

    [Fact]
    public void A_pot_on_the_open_table_takes_the_group_and_keeps_the_turn()
    {
        var corner = new Vector2(Cloth.Hx, Cloth.Hz);
        var dir = corner.Normalized();
        var b = corner - dir * 0.4f;
        var cue = b - dir * 0.35f;
        var g = Set((0, cue.X, cue.Y), (12, b.X, b.Y), (2, -0.3f, -0.8f), (8, -0.3f, 0.2f));
        Assert.Null(Shoot(g, 0, b, 250));
        Assert.False(g.Table.On[12], g.Call);
        Assert.Equal(PoolGame.Group.Stripes, g.Groups[0]);
        Assert.Equal(PoolGame.Group.Solids, g.Groups[1]);
        Assert.Equal(0, g.Up);
    }

    [Fact]
    public void The_eight_before_the_group_loses()
    {
        var corner = new Vector2(Cloth.Hx, Cloth.Hz);
        var dir = corner.Normalized();
        var b = corner - dir * 0.4f;
        var cue = b - dir * 0.35f;
        var g = Set((0, cue.X, cue.Y), (8, b.X, b.Y), (2, -0.3f, -0.8f), (12, -0.3f, 0.3f));
        g.Groups[0] = PoolGame.Group.Solids;
        g.Groups[1] = PoolGame.Group.Stripes;
        Assert.Null(Shoot(g, 0, b, 250));
        Assert.Equal(PoolGame.Phase.Over, g.State);
        Assert.Equal(1, g.Winner);
    }

    [Fact]
    public void The_eight_after_the_group_wins()
    {
        var corner = new Vector2(Cloth.Hx, Cloth.Hz);
        var dir = corner.Normalized();
        var b = corner - dir * 0.4f;
        var cue = b - dir * 0.35f;
        var g = Set((0, cue.X, cue.Y), (8, b.X, b.Y), (12, -0.3f, 0.3f));
        g.Groups[0] = PoolGame.Group.Solids;
        g.Groups[1] = PoolGame.Group.Stripes;
        Assert.Null(Shoot(g, 0, b, 250));
        Assert.Equal(PoolGame.Phase.Over, g.State);
        Assert.Equal(0, g.Winner);
    }

    [Fact]
    public void Wrong_turn_and_moving_balls_are_refused()
    {
        var g = Set((0, 0, -0.5f), (1, 0, 0.3f));
        Assert.NotNull(g.Act(1, PoolGame.ActShoot, new[] { 0, 300, 0 }, 10, new Random(1)));
        Assert.Null(g.Act(0, PoolGame.ActShoot, new[] { 0, 300, 0 }, 10, new Random(1)));
        Assert.NotNull(g.Act(g.Up, PoolGame.ActShoot, new[] { 0, 300, 0 }, 10.1, new Random(1)));
    }

    [Fact]
    public void Two_bots_play_a_game_to_its_end()
    {
        for (int seed = 0; seed < 6; seed++)
        {
            var g = new PoolGame(Cloth.Hx, Cloth.Hz, seed);
            g.Sit(-1, 0);
            g.Sit(-2, 1);
            var rng = new Random(seed);
            double now = 0;
            int shots = 0, pots = 0;
            while (g.State != PoolGame.Phase.Over && shots < 200)
            {
                now = Math.Max(now, g.ShotEnd + PoolGame.BotThink + 0.01);
                var move = g.Bot(g.Up, now, rng);
                Assert.NotNull(move);
                int on = 0;
                for (int i = 1; i < PoolSolver.Balls; i++) if (g.Table.On[i]) on++;
                Assert.Null(g.Act(g.Up, move!.Value.Action, move.Value.Args, now, rng));
                if (move.Value.Action == PoolGame.ActShoot)
                {
                    shots++;
                    for (int i = 1; i < PoolSolver.Balls; i++) if (g.Table.On[i]) on--;
                    pots += on;
                }
            }
            _out.WriteLine($"seed {seed}: {shots} shots, {pots} pots, {g.Call}");
            Assert.Equal(PoolGame.Phase.Over, g.State);
            Assert.True(pots >= 8, "the bots pot their way to the 8");
        }
    }

    [Fact]
    public void The_state_packs_and_unpacks()
    {
        var g = Set((0, 0, -0.5f), (1, 0, 0.3f), (9, 0.2f, 0.4f));
        Shoot(g, 0, new Vector2(0, 0.3f));
        var copy = new PoolGame(Cloth.Hx, Cloth.Hz);
        copy.Unpack(g.Pack());
        Assert.Equal(g.Seats, copy.Seats);
        Assert.Equal(g.ShotSeq, copy.ShotSeq);
        Assert.Equal(g.Call, copy.Call);
        for (int i = 0; i < PoolSolver.Balls; i++)
        {
            Assert.Equal(g.Table.On[i], copy.Table.On[i]);
            Assert.Equal(g.Table.P[i], copy.Table.P[i]);
            Assert.Equal(g.Before.P[i], copy.Before.P[i]);
        }
        // a peer replaying the shot from the numbers ends where the server's table rests
        var replay = new PoolSolver(Cloth.Hx, Cloth.Hz);
        replay.CopyFrom(copy.Before);
        PoolGame.Strike(replay, copy.Shot);
        replay.Run();
        for (int i = 0; i < PoolSolver.Balls; i++)
            if (replay.On[i]) Assert.True(replay.P[i].DistanceTo(copy.Table.P[i]) < 1e-4f);
    }
}
