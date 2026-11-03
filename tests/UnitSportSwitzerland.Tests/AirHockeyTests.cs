using Godot;
using UnitSport.Interiors.Games.AirHockey;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>Air hockey (#1326): mallets kept in their halves, the puck off the rails and the mallets, goals and serves, bots to seven, the wire.</summary>
public class AirHockeyTests
{
    private readonly ITestOutputHelper _out;
    public AirHockeyTests(ITestOutputHelper output) => _out = output;

    private const double Dt = 1.0 / 120;

    private static AirHockeyGame Game()
    {
        var g = new AirHockeyGame();
        g.Sit(1, 0);
        g.Sit(2, 1);
        double now = 0;
        while (!g.InPlay) g.Step(Dt, now += Dt);
        return g;
    }

    private static void Run(AirHockeyGame g, double seconds, Action? each = null)
    {
        for (double t = 0; t < seconds; t += Dt)
        {
            each?.Invoke();
            g.Step(Dt, 100 + t);
        }
    }

    [Fact]
    public void A_mallet_stays_in_its_own_half_and_on_the_table()
    {
        var g = Game();
        Run(g, 2, () => { g.Input(0, AirHockeyGame.Pack(new Vector2(1, 1))); g.Input(1, AirHockeyGame.Pack(new Vector2(-1, -1))); });
        Assert.True(g.MalletAt[0].Y < 0 && g.MalletAt[1].Y > 0, "each in its half");
        Assert.True(Mathf.Abs(g.MalletAt[0].X) <= g.Hx - AirHockeyGame.Mallet + 1e-4f);
    }

    [Fact]
    public void A_mallet_driven_into_the_puck_shoots_it_up_the_table()
    {
        var g = Game();
        g.PuckAt = new Vector2(0, -0.3f);
        g.PuckV = Vector2.Zero;
        g.MalletAt[0] = new Vector2(0, -0.6f);
        Run(g, 0.2, () => g.Input(0, AirHockeyGame.Pack(new Vector2(0, 1))));
        _out.WriteLine($"puck {g.PuckV}");
        Assert.True(g.PuckV.Y > 2f, $"the puck flies up the table ({g.PuckV.Y:F2} m/s)");
    }

    [Fact]
    public void Rails_bounce_and_the_goal_scores()
    {
        var g = Game();
        g.PuckAt = new Vector2(0.3f, 0);
        g.PuckV = new Vector2(2, 0);
        Run(g, 0.2);
        Assert.True(g.PuckV.X < 0, "off the side rail");

        var h = Game();
        h.MalletAt[1] = new Vector2(0.4f, 0.5f);
        h.PuckAt = new Vector2(0, 0.5f);
        h.PuckV = new Vector2(0, 3);
        bool scored = false;
        Run(h, 0.5, () => scored |= h.Goals[0] == 1);
        Assert.True(scored, "into the +z goal: a goal for seat 0");
        Run(h, 2.5);
        Assert.True(h.InPlay && h.PuckAt.Y > 0, "served in the half of who conceded");
    }

    [Fact]
    public void Two_bots_play_to_seven()
    {
        var g = new AirHockeyGame();
        g.Sit(AirHockeyGame.BotBase, 0);
        g.Sit(AirHockeyGame.BotBase - 1, 1);
        var rng = new Random(5);
        double now = 0, bots = 0;
        while (g.Winner < 0 && now < 1200)
        {
            now += Dt;
            if ((bots -= Dt) <= 0)
            {
                bots = 0.1;
                g.Bot(0, now, rng);
                g.Bot(1, now, rng);
            }
            g.Step(Dt, now);
        }
        _out.WriteLine($"{g.Call} after {now / 60:F1} min");
        Assert.True(g.Winner >= 0, $"a winner within 20 minutes ({g.Goals[0]} : {g.Goals[1]})");
    }

    [Fact]
    public void The_state_and_poses_pack_and_unpack()
    {
        var g = Game();
        Run(g, 0.3, () => g.Input(0, AirHockeyGame.Pack(new Vector2(0.5f, 0.2f))));
        var copy = new AirHockeyGame();
        copy.Unpack(g.Pack());
        Assert.Equal(g.Call, copy.Call);
        Assert.True(copy.PuckAt.DistanceTo(g.PuckAt) < 1e-5f && copy.MalletAt[0].DistanceTo(g.MalletAt[0]) < 1e-5f);
    }
}
