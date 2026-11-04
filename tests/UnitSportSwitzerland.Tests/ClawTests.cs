using Godot;
using UnitSport.Interiors.Games.Claw;
using UnitSport.Items;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>The claw machine (#1326): a coin to play, steering inside the box, the drop, a grab's luck by place, the prize out of the chute, the restock, the wire.</summary>
public class ClawTests
{
    private readonly ITestOutputHelper _out;
    public ClawTests(ITestOutputHelper output) => _out = output;

    private const double Dt = 1.0 / 120;

    private static ClawGame Game(int seed = 1)
    {
        var g = new ClawGame(1.0f, 1.0f, seed);
        g.Sit(5, 0);
        return g;
    }

    private static double Run(ClawGame g, double from, double seconds, Action? each = null)
    {
        double now = from;
        for (double t = 0; t < seconds; t += Dt)
        {
            each?.Invoke();
            g.Step(Dt, now += Dt);
        }
        return now;
    }

    [Fact]
    public void The_box_is_stocked_clear_of_the_chute()
    {
        var g = Game();
        Assert.Equal(ClawGame.Stock, g.Pile.Count);
        foreach (var (at, item) in g.Pile)
        {
            Assert.False(at.X < -g.Hx + 0.24f && at.Y > g.Hz - 0.24f, $"a prize in the chute's corner at {at}");
            Assert.Contains(item, ClawGame.Kinds);
        }
    }

    [Fact]
    public void No_coin_no_claw_and_it_stays_in_its_box()
    {
        var g = Game();
        Assert.NotNull(g.Act(0, ClawGame.ActDrop, System.Array.Empty<int>(), 0, new Random(1)));
        Assert.Null(g.Act(0, ClawGame.ActCoin, System.Array.Empty<int>(), 0, new Random(1)));
        Assert.NotNull(g.Act(0, ClawGame.ActCoin, System.Array.Empty<int>(), 0, new Random(1)));
        Run(g, 0, 5, () => g.Input(0, ClawGame.Pack(new Vector2(1, -1))));
        Assert.True(g.ClawAt.X <= g.Hx + 1e-4f && g.ClawAt.Y >= -g.Hz - 1e-4f, $"inside the box: {g.ClawAt}");
    }

    [Fact]
    public void Time_runs_out_and_it_drops_by_itself()
    {
        var g = Game();
        g.Act(0, ClawGame.ActCoin, System.Array.Empty<int>(), 0, new Random(1));
        double now = Run(g, 0, ClawGame.Time + 0.5);
        Assert.NotEqual(ClawGame.Phase.Steer, g.State);
        Run(g, now, 10);
        Assert.Equal(ClawGame.Phase.Idle, g.State);
    }

    [Fact]
    public void Right_over_a_prize_it_wins_often_off_it_rarely()
    {
        int OverWins(float off)
        {
            int wins = 0;
            for (int seed = 0; seed < 60; seed++)
            {
                var g = Game(seed);
                g.Act(0, ClawGame.ActCoin, System.Array.Empty<int>(), 0, new Random(1));
                // straight to a prize (off by `off`), and drop
                var target = g.Pile[0].At + new Vector2(off, 0);
                g.ClawAt = target;
                g.Act(0, ClawGame.ActDrop, System.Array.Empty<int>(), 0, new Random(1));
                Run(g, 0, 15);
                wins += g.Won;
                if (g.Won > 0) Assert.Contains(g.Payouts, p => p.Peer == 5 && ClawGame.Kinds.Contains(p.Item));
            }
            return wins;
        }
        int over = OverWins(0), off = OverWins(0.06f), far = OverWins(0.3f);
        _out.WriteLine($"wins of 60: over {over}, 6 cm off {off}, far {far}");
        Assert.InRange(over, 20, 45);
        Assert.InRange(off, 2, 25);
        Assert.True(off < over, "nearer is better");
    }

    [Fact]
    public void The_pile_restocks_when_it_runs_low()
    {
        var g = Game();
        while (g.Pile.Count >= ClawGame.Restock) g.Pile.RemoveAt(0);
        g.Act(0, ClawGame.ActCoin, System.Array.Empty<int>(), 0, new Random(1));
        g.Act(0, ClawGame.ActDrop, System.Array.Empty<int>(), 0, new Random(1));
        Run(g, 0, 15);
        Assert.True(g.Pile.Count >= ClawGame.Restock, $"restocked: {g.Pile.Count}");
    }

    [Fact]
    public void A_bot_plays_it_the_way_a_player_would()
    {
        var g = Game();
        var rng = new Random(3);
        double now = 0, bots = 0;
        int goes = 0;
        while (goes < 20 && now < 2000)
        {
            now += Dt;
            if ((bots -= Dt) <= 0)
            {
                bots = 0.1;
                if (g.Bot(0, now, rng) is { } move && g.Act(0, move.Action, move.Args, now, rng) == null && move.Action == ClawGame.ActCoin) goes++;
            }
            g.Step(Dt, now);
        }
        _out.WriteLine($"{goes} goes, {g.Won} prizes");
        Assert.InRange(g.Won, 2, 16);
    }

    [Fact]
    public void The_state_and_poses_pack_and_unpack()
    {
        var g = Game();
        g.Act(0, ClawGame.ActCoin, System.Array.Empty<int>(), 0, new Random(1));
        Run(g, 0, 1, () => g.Input(0, ClawGame.Pack(new Vector2(0.5f, 0.5f))));
        var copy = new ClawGame();
        copy.Unpack(g.Pack());
        Assert.Equal(g.State, copy.State);
        Assert.Equal(g.Pile.Count, copy.Pile.Count);
        Assert.True(copy.ClawAt.DistanceTo(g.ClawAt) < 1e-5f);
    }
}
