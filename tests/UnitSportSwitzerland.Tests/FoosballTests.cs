using Godot;
using UnitSport.Interiors.Games.Foosball;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>Table football (#1324): the rods' reach, blocking and kicking, lifting, goals, who runs which rod, bots playing a game, the wire.</summary>
public class FoosballTests
{
    private readonly ITestOutputHelper _out;
    public FoosballTests(ITestOutputHelper output) => _out = output;

    private const double Dt = 1.0 / 120;

    /// <summary>A game in play with two humans who do nothing, the ball set by hand.</summary>
    private static FoosballGame Game(Vector2 ball, Vector2 v)
    {
        var g = new FoosballGame();
        g.Sit(1, 0);
        g.Sit(2, 1);
        double now = 0;
        while (!g.InPlay) g.Step(Dt, now += Dt);
        g.BallAt = ball;
        g.BallV = v;
        return g;
    }

    private static void Run(FoosballGame g, double seconds, Action? each = null)
    {
        for (double t = 0; t < seconds; t += Dt)
        {
            each?.Invoke();
            g.Step(Dt, 100 + t);
        }
    }

    [Fact]
    public void Every_figure_stays_on_the_pitch_at_either_end_of_its_rod()
    {
        for (int rod = 0; rod < 8; rod++)
            foreach (float s in new[] { -1f, 1f })
                for (int k = 0; k < FoosballGame.Figures[rod]; k++)
                {
                    float z = FoosballGame.FigureZ(rod, k) + s * FoosballGame.SlideRange(rod);
                    Assert.InRange(Mathf.Abs(z), 0, FoosballGame.HalfZ - FoosballGame.FootHalfZ);
                }
    }

    [Fact]
    public void A_hanging_figure_stops_the_ball_and_a_lifted_one_lets_it_pass()
    {
        // blue's attack rod (2) at x -0.21: its middle figure at z 0
        var g = Game(new Vector2(-0.1f, 0), new Vector2(-1.5f, 0));
        Run(g, 0.3);
        Assert.True(g.BallAt.X > -0.21f, $"stopped in front of the figure (x {g.BallAt.X:F3})");

        var h = Game(new Vector2(-0.1f, 0), new Vector2(-1.5f, 0));
        Run(h, 0.3, () => h.Input(1, FoosballGame.Pack(0, false, true)));
        Assert.True(h.BallAt.X < -0.21f, $"passed under the lifted rod (x {h.BallAt.X:F3})");
    }

    [Fact]
    public void A_kick_shoots_the_ball_up_the_table()
    {
        // red's attack (rod 5, x 0.21), its middle figure at z 0: the ball just in front of it
        var g = Game(new Vector2(0.235f, 0), Vector2.Zero);
        g.Input(0, FoosballGame.Pack(0, true, false));
        Run(g, 0.06);
        _out.WriteLine($"after the kick: v {g.BallV}");
        Assert.True(g.BallV.X > 1.5f, $"the ball shot towards blue's goal ({g.BallV.X:F2} m/s)");
    }

    [Fact]
    public void A_ball_into_the_goal_scores_and_is_served_again()
    {
        var g = Game(new Vector2(0.5f, 0.02f), new Vector2(3f, 0));
        // blue's goalie out of the way
        g.Input(1, FoosballGame.Pack(1, false, true));
        bool scored = false;
        Run(g, 0.3, () => scored |= g.Goals[0] == 1);
        Assert.True(scored, "red scored");
        Run(g, 3);
        Assert.True(g.InPlay, "the ball was served again");
        Assert.Contains("1 : 0", g.Call);
    }

    [Fact]
    public void Four_players_split_the_rods_two_play_them_all()
    {
        var g = new FoosballGame();
        g.Sit(1, 0);
        g.Sit(2, 1);
        Assert.Equal(0, g.SeatOfRod(5));
        Assert.Equal(1, g.SeatOfRod(2));
        g.Sit(3, 2);
        g.Sit(4, 3);
        Assert.Equal(0, g.SeatOfRod(0));
        Assert.Equal(0, g.SeatOfRod(1));
        Assert.Equal(2, g.SeatOfRod(3));
        Assert.Equal(2, g.SeatOfRod(5));
        Assert.Equal(3, g.SeatOfRod(2));
        Assert.Equal(1, g.SeatOfRod(7));
    }

    private int _dead;
    private bool _wasDead;

    [Fact]
    public void Two_bots_play_a_game_to_ten()
    {
        var g = new FoosballGame();
        g.Sit(FoosballGame.BotBase, 0);
        g.Sit(FoosballGame.BotBase - 1, 1);
        var rng = new Random(3);
        double now = 0, bots = 0;
        int steps = 0;
        while (g.Winner < 0 && now < 1800)
        {
            now += Dt;
            steps++;
            // the server asks its bots ten times a second
            if ((bots -= Dt) <= 0)
            {
                bots = 0.1;
                g.Bot(0, now, rng);
                g.Bot(1, now, rng);
            }
            g.Step(Dt, now);
            if (g.Call.StartsWith("Dead") && !_wasDead) _dead++;
            _wasDead = g.Call.StartsWith("Dead");
        }
        _out.WriteLine($"{g.Call} after {now / 60:F1} min, {_dead} dead balls");
        Assert.True(g.Winner >= 0, $"a winner within half an hour ({g.Goals[0]} : {g.Goals[1]})");
        Assert.InRange(now, 30, 1800);
    }

    [Fact]
    public void The_state_and_poses_pack_and_unpack()
    {
        var g = Game(new Vector2(0.1f, -0.05f), new Vector2(0.4f, 0.2f));
        g.Input(0, FoosballGame.Pack(0.5f, false, false));
        Run(g, 0.2);
        var copy = new FoosballGame();
        copy.Unpack(g.Pack());
        Assert.Equal(g.Seats, copy.Seats);
        Assert.Equal(g.Call, copy.Call);
        Assert.True(copy.BallAt.DistanceTo(g.BallAt) < 1e-5f);
        for (int i = 0; i < 8; i++)
        {
            Assert.InRange(copy.Slide[i] - g.Slide[i], -1e-3f, 1e-3f);
            Assert.InRange(copy.Angle[i] - g.Angle[i], -1e-3f, 1e-3f);
        }
    }
}
